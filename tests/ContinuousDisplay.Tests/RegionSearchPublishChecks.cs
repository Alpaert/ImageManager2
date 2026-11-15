using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using ImageManager.App.Controls;
using ImageManager.App.Helpers;
using ImageManager.App.Models;
using ImageManager.App.Services;
using ImageManager.App.ViewModels;
using ImageManager.App.Views;
using ImageManager.Core.Models;
using ImageManager.Core.Services;
using ImageManager.Infrastructure.Caching;
using ImageManager.Infrastructure.Data;
using ImageManager.Infrastructure.Data.Repositories;
using ImageManager.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

internal static class RegionSearchPublishChecks
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "ImageManager-RegionPublish", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var manifest = Environment.GetEnvironmentVariable("IMAGEMANAGER_TEST_REGION_PUBLISH_FIXTURE");
        var real = string.IsNullOrWhiteSpace(manifest) ? null : RealFixture.Load(manifest);
        if (real is not null)
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var paths = Enumerable.Range(0, 24).Select(i => Path.Combine(root, $"{i}.png")).ToList();
        if (real is not null)
            paths = real.Paths;
        else
        {
            using var bitmap = new SKBitmap(100, 100);
            bitmap.Erase(SKColors.SkyBlue);
            using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            foreach (var path in paths)
                File.WriteAllBytes(path, png.ToArray());
        }

        var repo = DispatchProxy.Create<IImageMetaRepository, MetadataProxy>();
        var messenger = new WeakReferenceMessenger();
        var dispatcher = new AvaloniaDispatcher();
        var cache = new ThumbnailCacheService(new MediaProcessorFactory(null!), Path.Combine(root, "cache"));
        var page = new PageManager(cache, null!, metaRepo: repo);
        var search = new FixtureSearch(paths);
        using var sscd = real is null ? null : new SscdEmbeddingService(real.ModelsRoot);
        using var pixai = real is null ? null : new PixaiTagService();
        IRegionSearchService regionSearch = search;
        if (real is not null)
        {
            var factory = new ReadOnlyFixtureConnection(real.Database);
            var embeddings = new ImageEmbeddingRepository(factory, new CharacterTagSuppressionRepository(factory));
            regionSearch = new RegionSearchService(embeddings, embeddings, sscd!, pixai!, real.ModelsRoot);
        }
        using var services = new ServiceCollection().AddSingleton(page).AddSingleton(regionSearch).BuildServiceProvider();
        typeof(ImageManager.App.App).GetProperty("Services")!.SetValue(null, services);
        typeof(ImageManager.App.App).GetProperty("UI")!.SetValue(null, dispatcher);
        var tags = new TagSearchEngine(repo, messenger, ImagePaging.Default);
        var vm = new MainWindowViewModel(null!, null!, repo, null!, null!, null!, null!, cache, page,
            tags, null!, null!, null!, messenger, dispatcher, regionSearchService: regionSearch);
        vm.WaterfallMode = "Horizontal";
        typeof(MainWindowViewModel).GetField("_allFiles", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, paths);
        typeof(MainWindowViewModel).GetField("_displayFilteredFiles", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, paths);
        var window = new TestMainWindow { DataContext = vm, Width = 1000, Height = 700, ShowInTaskbar = false };
        vm.ContinuousDisplayRefreshRequested += (Func<int, Task>)typeof(MainWindow)
            .GetMethod("ApplyDisplayModeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate(typeof(Func<int, Task>), window);

        try
        {
            window.Show();
            Pump(window);
            foreach (var mode in new[] { ImageDisplayMode.Paged, ImageDisplayMode.Continuous })
            {
                Wait(window, vm.TrySetDisplayModeAsync(mode), $"switch to {mode}");
                if (real is not null)
                {
                    vm.BeginRegionSearchSession(0);
                    foreach (var searchMode in real.Modes)
                    {
                        Wait(window, vm.QueueRegionSearchAsync(paths[0], real.Region, searchMode, immediate: true),
                            $"{mode} real ONNX {searchMode}", TimeSpan.FromSeconds(120));
                        Require(vm.IsShowingSearchResult && vm.ActiveFileList.Count > 0
                            && vm.ActiveFileList.All(path => paths.Contains(path, StringComparer.OrdinalIgnoreCase))
                            && vm.ActiveFileList.Any(path => !string.Equals(path, paths[0], StringComparison.OrdinalIgnoreCase)),
                            $"{mode}: real results did not reach the active gallery: {vm.RegionSearchStatus} (gallery: {vm.StatusText})");
                        Require(vm.StatusText.Contains("向量覆盖"), $"{mode}: real search never reported completion: {vm.StatusText}");
                        Require(vm.HasRegionSearchResults && !vm.IsRegionSearchBusy,
                            $"{mode}: inline session did not finish publishing real results");
                        Require(mode == ImageDisplayMode.Paged
                            ? vm.Images.Any(item => vm.ActiveFileList.Contains(item.FilePath))
                            : vm.ContinuousImageItemSource?.Count == vm.ActiveFileList.Count && vm.ContinuousDisplayGeometrySnapshot is not null,
                            $"{mode}: real result layout did not materialize");
                        Console.WriteLine($"RegionPublish verified {mode} {searchMode}: {vm.StatusText}");
                    }
                    Wait(window, vm.ExitRegionSearchSessionAsync(), $"{mode} exit real search");
                    continue;
                }
                for (var repeat = 0; repeat < 2; repeat++)
                {
                    Wait(window, vm.SearchRegionAsync(paths[0], new ImageRegion(10, 10, 40, 40),
                        RegionSearchMode.SimilarContent), $"{mode} publish {repeat}");
                    Require(vm.IsShowingSearchResult && vm.ActiveFileList.SequenceEqual(paths.Skip(1).Take(4)),
                        $"{mode}: results did not reach the active gallery");
                    Require(vm.StatusText.Contains("向量覆盖 24/24"), $"{mode}: search never reported completion: {vm.StatusText}");
                    Require(mode == ImageDisplayMode.Paged
                        ? vm.Images.Any(item => item.FilePath == paths[1])
                        : vm.ContinuousImageItemSource?.Count == 4 && vm.ContinuousDisplayGeometrySnapshot is not null,
                        $"{mode}: expected result layout did not materialize");
                }

                search.WaitForCancel = true;
                var canceled = vm.SearchRegionAsync(paths[0], null, RegionSearchMode.SimilarContent);
                Wait(window, search.Entered.Task, $"{mode} service entered");
                vm.StopSearchCommand.Execute(null);
                Wait(window, canceled, $"{mode} canceled");
                Require(vm.ActiveFileList.SequenceEqual(paths.Skip(1).Take(4)), $"{mode}: canceled request replaced results");
                search.WaitForCancel = false;
                Wait(window, vm.SearchRegionAsync(paths[0], null, RegionSearchMode.SimilarContent), $"{mode} after cancel");
            }
        }
        finally
        {
            window.Close();
        }
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Wait(Window window, Task task, string phase, TimeSpan? timeout = null)
    {
        Console.WriteLine($"RegionPublish START {phase}");
        var watch = Stopwatch.StartNew();
        while (!task.IsCompleted && watch.Elapsed < (timeout ?? TimeSpan.FromSeconds(8)))
        {
            Pump(window);
            Thread.Sleep(5);
        }
        Require(task.IsCompleted, $"Region result publication stalled: {phase}");
        task.GetAwaiter().GetResult();
        Pump(window);
        Console.WriteLine($"RegionPublish DONE {phase} ({watch.ElapsedMilliseconds} ms)");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed record RealFixture(string Database, string ModelsRoot, List<string> Paths, ImageRegion? Region,
        RegionSearchMode[] Modes)
    {
        public static RealFixture Load(string manifest)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(manifest))!;
            var fixture = JsonSerializer.Deserialize<RealFixture>(File.ReadAllText(manifest),
                new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })
                ?? throw new InvalidDataException("Missing region publish fixture.");
            string Input(string path)
            {
                var full = Path.GetFullPath(path, directory);
                var relative = Path.GetRelativePath(directory, full);
                Require(relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}")
                    && !Path.IsPathRooted(relative) && File.Exists(full), "Fixture inputs must be files inside the fixture directory.");
                return full;
            }
            Require(fixture.Paths.Count is >= 2 and <= 24 && fixture.Modes.Length > 0, "Invalid real fixture size or modes.");
            return fixture with
            {
                Database = Input(fixture.Database),
                ModelsRoot = Path.GetFullPath(fixture.ModelsRoot, directory),
                Paths = fixture.Paths.Select(Input).ToList()
            };
        }
    }

    private sealed class ReadOnlyFixtureConnection(string database) : IDbContextFactory
    {
        public SqliteConnection CreateConnection(int commandTimeout = 30)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = database,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
                DefaultTimeout = commandTimeout
            }.ToString());
            connection.Open();
            return connection;
        }
    }

    private sealed class FixtureSearch(List<string> paths) : IRegionSearchService
    {
        public bool WaitForCancel { get; set; }
        public TaskCompletionSource Entered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<RegionSearchResponse> SearchAsync(string imagePath, ImageRegion? region,
            IReadOnlyCollection<string> candidates, RegionSearchMode mode, int limit, CancellationToken ct = default)
        {
            Require(candidates.Count == 24 && mode == RegionSearchMode.SimilarContent, "Unexpected search request.");
            Console.WriteLine("RegionPublish service entered");
            if (WaitForCancel)
            {
                Entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, ct); }
                finally { Entered = new(TaskCreationOptions.RunContinuationsAsynchronously); }
            }
            Console.WriteLine("RegionPublish service returned");
            return new RegionSearchResponse(paths.Skip(1).Take(4).Select(path => new SimilaritySearchResult(path, .8f)).ToArray(), 24, 24);
        }
    }
}
