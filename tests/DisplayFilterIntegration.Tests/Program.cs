using System.Reflection;
using CommunityToolkit.Mvvm.Messaging;
using ImageManager.App.Models;
using ImageManager.App.Services;
using ImageManager.App.ViewModels;
using ImageManager.Core.Models;
using ImageManager.Core.Services;
using ImageManager.Infrastructure.Video;
using ImageManager.Infrastructure.Services;
using ImageManager.Infrastructure.Caching;

var messenger = new WeakReferenceMessenger();
var repository = DispatchProxy.Create<IImageMetaRepository, MetadataProxy>();
var fake = (MetadataProxy)(object)repository;
var cache = new ThumbnailCacheService(null!, Path.Combine(Path.GetTempPath(), "ImageManager-FilterTests"));
var page = new PageManager(cache, null!);
var search = new TagSearchEngine(repository, messenger, ImagePaging.Default);
var vm = new MainWindowViewModel(null!, null!, repository, null!, null!, null!, null!, cache, page,
    search, null!, null!, null!, messenger, new ImmediateDispatcher());

var cacheFixtureDirectory = Path.Combine(Path.GetTempPath(), "ImageManager-CachedDimensions-" + Guid.NewGuid().ToString("N"));
try
{
    const string cachedVideo = @"Z:\cache-fixture\video.mp4";
    var dimensionCache = new ThumbnailCacheService(null!, cacheFixtureDirectory, 200);
    var currentWidthCache = new DiskThumbnailCache(cacheFixtureDirectory, 200);
    var otherWidthCache = new DiskThumbnailCache(cacheFixtureDirectory, 320);
    var originalFrames = new VideoOriginalFrameCacheService(cacheFixtureDirectory);
    var originalFramePath = originalFrames.GetOriginalFramePath(cachedVideo);
    Directory.CreateDirectory(Path.GetDirectoryName(originalFramePath)!);
    await File.WriteAllBytesAsync(originalFramePath, CreateJpegHeader(1920, 1080));

    otherWidthCache.SaveMeta(cachedVideo, 3840, 2160);
    currentWidthCache.SaveMeta(cachedVideo, 1, 1);
    Require(dimensionCache.TryResolveCachedDimensions(cachedVideo, 200) == (1920, 1080),
        "video original frame dimensions must win over invalid current and other-width metadata");

    currentWidthCache.SaveMeta(cachedVideo, 1280, 720);
    Require(dimensionCache.TryResolveCachedDimensions(cachedVideo, 200) == (1280, 720),
        "requested width metadata must win over the video original frame");

    File.Delete(Path.ChangeExtension(currentWidthCache.GetCacheFilePath(cachedVideo), ".json"));
    currentWidthCache.Save(cachedVideo, CreateJpegHeader(640, 360));
    Require(dimensionCache.TryResolveCachedDimensions(cachedVideo, 200) == (640, 360),
        "a target thumbnail without JSON metadata must be reused before the video original frame");

    var batchDimensions = await dimensionCache.GetCachedDimensionsAsync([cachedVideo]);
    Require(batchDimensions.TryGetValue(cachedVideo, out var batchSize) && batchSize == (640, 360),
        "batched cache resolution must inspect thumbnails without JSON metadata");
    Console.WriteLine("PASS cached video dimensions reuse target metadata and original frames without source access");
}
finally
{
    try { Directory.Delete(cacheFixtureDirectory, recursive: true); } catch { }
}

var firstPage = ImageDisplayRange.ForPage(0, ImagePaging.Default, 401);
var lastPage = ImageDisplayRange.ForPage(2, ImagePaging.Default, 401);
Require(firstPage == new ImageDisplayRange(0, 200), "first page range must be bounded by page size");
Require(lastPage == new ImageDisplayRange(400, 1), "last page range must be bounded by result count");
Require(ImageDisplayRange.ForPage(3, ImagePaging.Default, 401).IsEmpty, "out-of-range page must stay empty");
Require(ImagePaging.Clamp(99) == ImagePaging.Min &&
        ImagePaging.Clamp(501) == ImagePaging.Max &&
        ImagePaging.Clamp(0) == ImagePaging.Default &&
        ImagePaging.Clamp(-1) == ImagePaging.Default,
    "paging clamp must enforce the configured bounds");
Require(ImageDisplayRange.ForPage(0, 500, 401).Count == 401 &&
        ImageDisplayRange.ForPage(1, 500, 401).IsEmpty &&
        ImageDisplayRange.ForPage(0, 100, 401).Count == 100 &&
        ImageDisplayRange.ForPage(4, 100, 401).Count == 1 &&
        ImageDisplayRange.ForPage(5, 100, 401).IsEmpty,
    "configurable page size must produce correct page boundaries");
Console.WriteLine("PASS configurable paging bounds");
Require(vm.DisplayMode == ImageDisplayMode.Paged && vm.IsPagedDisplay, "paged display must remain the default");
Require(await vm.TrySetDisplayModeAsync(ImageDisplayMode.Continuous), "continuous mode must activate after virtualization is available");
Require(vm.DisplayMode == ImageDisplayMode.Continuous && !vm.IsPagedDisplay, "continuous mode must hide paged state");
Require(await vm.TrySetDisplayModeAsync(ImageDisplayMode.Paged), "paged mode must remain restorable");
Require(vm.DisplayMode == ImageDisplayMode.Paged && vm.IsPagedDisplay, "returning to paging must restore paged state");
Console.WriteLine("PASS display mode switching preserves paged fallback");

var emptyWindow = new ImageDisplayWindowManager();
var emptySnapshot = emptyWindow.UpdateVisibleRange(0, 10, 20);
Require(emptySnapshot.TotalCount == 0 && emptySnapshot.VisibleRange.IsEmpty && emptySnapshot.RetainedRange.IsEmpty,
    "empty display window must remain empty");

var window = new ImageDisplayWindowManager(100);
var frontSnapshot = window.UpdateVisibleRange(-10, 20, 15);
Require(frontSnapshot.VisibleRange == new ImageDisplayRange(0, 10),
    "front visible range must clamp to result bounds");
Require(frontSnapshot.RetainedRange == new ImageDisplayRange(0, 25),
    "front retained range must apply symmetric buffer within bounds");

var middleSnapshot = window.UpdateVisibleRange(40, 10, 15);
Require(middleSnapshot.VisibleRange == new ImageDisplayRange(40, 10) &&
        middleSnapshot.RetainedRange == new ImageDisplayRange(25, 40),
    "middle retained range must include symmetric buffer");

var tailSnapshot = window.UpdateVisibleRange(95, 20, int.MaxValue);
Require(tailSnapshot.VisibleRange == new ImageDisplayRange(95, 5) &&
        tailSnapshot.RetainedRange == new ImageDisplayRange(0, 100) &&
        tailSnapshot.RetainedRange.EndExclusive <= tailSnapshot.TotalCount,
    "tail retained range must clamp without overflow");
Require(window.IsIndexRetained(99) && !window.IsIndexRetained(100),
    "retained index check must use half-open bounds");
Console.WriteLine("PASS bounded display window clamps empty, front, middle, and tail ranges");

var geometryItems = new[]
{
    new ImageDisplayItemSize(1600, 900),
    new ImageDisplayItemSize(900, 1600),
    new ImageDisplayItemSize(800, 800),
    new ImageDisplayItemSize(0, 0), // Must use the 4:3 fallback safely.
    new ImageDisplayItemSize(double.NaN, 100)
};
var emptyGeometry = ImageDisplayGeometryIndex.Build([], ImageDisplayGeometryOptions.Default);
Require(emptyGeometry.Count == 0 && emptyGeometry.ExtentHeight == 0,
    "empty geometry index must have no extent height");
foreach (var mode in Enum.GetValues<ImageDisplayLayoutMode>())
{
    var geometry = ImageDisplayGeometryIndex.Build(geometryItems,
        new ImageDisplayGeometryOptions(mode, 640, 160, 1));
    Require(geometry.Count == geometryItems.Length && geometry.ExtentWidth > 0 && geometry.ExtentHeight > 0,
        $"{mode} geometry must expose a non-empty full extent");
    Require(geometry.TryGetItemRect(0, out var firstRect) && firstRect.IsValid,
        $"{mode} geometry must expose valid item rectangles");
    Require(geometry.TryGetItemRect(3, out var fallbackRect) && fallbackRect.IsValid,
        $"{mode} geometry must fall back from invalid dimensions");
    geometry.TryGetItemRect(geometry.Count / 2, out var middleRect);
    geometry.TryGetItemRect(geometry.Count - 1, out var tailRect);
    var top = geometry.QueryViewport(0, 10);
    var middle = geometry.QueryViewport(middleRect.Y, Math.Min(10, middleRect.Height));
    var tail = geometry.QueryViewport(tailRect.Y, Math.Min(10, tailRect.Height));
    Require(!top.IsEmpty && top.StartIndex >= 0 && top.EndExclusive <= geometry.Count,
        $"{mode} top viewport must stay in bounds");
    Require(!middle.IsEmpty && middle.StartIndex >= 0 && middle.EndExclusive <= geometry.Count,
        $"{mode} middle viewport must stay in bounds");
    Require(!tail.IsEmpty && tail.StartIndex >= 0 && tail.EndExclusive <= geometry.Count,
        $"{mode} tail viewport must stay in bounds");
    Require(middle.StartIndex <= geometry.Count / 2 && middle.EndExclusive > geometry.Count / 2 &&
            tail.StartIndex <= geometry.Count - 1 && tail.EndExclusive > geometry.Count - 1,
        $"{mode} viewport range must include the queried item index");
    Require(geometry.QueryViewport(double.NaN, 10).IsEmpty && geometry.QueryViewport(0, 0).IsEmpty,
        $"{mode} invalid viewport must be empty");
}
var narrowGrid = ImageDisplayGeometryIndex.Build(geometryItems,
    new ImageDisplayGeometryOptions(ImageDisplayLayoutMode.None, 100, 160, 1));
Require(narrowGrid.TryGetItemRect(0, out var narrowFirst) && narrowGrid.ExtentWidth >= narrowFirst.Right,
    "logical grid extent must include an item wider than a narrow container");

var paritySizes = new[]
{
    new ImageDisplayItemSize(1600, 900),
    new ImageDisplayItemSize(900, 1600),
    new ImageDisplayItemSize(800, 800)
};
var parityGrid = ImageDisplayGeometryIndex.Build(paritySizes,
    new ImageDisplayGeometryOptions(ImageDisplayLayoutMode.None, 900, 160, 1));
Require(parityGrid.TryGetItemRect(0, out var gridRect) && gridRect.Width == 170 && gridRect.Height == 170,
    "continuous grid cells must match paged width plus the template's 5px margins without phantom text height");
var parityVertical = ImageDisplayGeometryIndex.Build(paritySizes,
    new ImageDisplayGeometryOptions(ImageDisplayLayoutMode.Vertical, 900, 160, 1));
Require(parityVertical.TryGetItemRect(0, out var verticalRect) && verticalRect.Width == 180 && verticalRect.Height == 143.25,
    "continuous masonry geometry must match the paged column width and image-plus-chrome height");
Require(parityVertical.TryGetItemRect(1, out var secondVerticalRect) && secondVerticalRect.X == 180 &&
        parityVertical.ExtentWidth == 900 && secondVerticalRect.Right <= parityVertical.ExtentWidth,
    "continuous masonry columns must use the paged panel's edge-to-edge column positions");
var parityHorizontal = ImageDisplayGeometryIndex.Build(paritySizes,
    new ImageDisplayGeometryOptions(ImageDisplayLayoutMode.Horizontal, 900, 160, 1));
Require(parityHorizontal.TryGetItemRect(0, out var horizontalRect) && horizontalRect.Y == 0 && horizontalRect.Height > 0,
    "continuous justified geometry must preserve the paged horizontal row contract");
var multiRowHorizontal = ImageDisplayGeometryIndex.Build(
    Enumerable.Repeat(new ImageDisplayItemSize(1600, 900), 3).ToArray(),
    new ImageDisplayGeometryOptions(ImageDisplayLayoutMode.Horizontal, 900, 160, 1));
Require(multiRowHorizontal.TryGetItemRect(1, out var firstRowLastItem) &&
        multiRowHorizontal.TryGetItemRect(2, out var finalRowItem) &&
        Math.Abs(finalRowItem.Y - firstRowLastItem.Height) < 0.001 &&
        Math.Abs(finalRowItem.Height - firstRowLastItem.Height) < 0.001,
    "continuous justified final rows must inherit the paged panel's preceding row height");

var hasQuarterTurnRotation = typeof(VideoMetadataExtractor)
    .GetMethod("HasQuarterTurnRotation", BindingFlags.NonPublic | BindingFlags.Static)!;
Require((bool)hasQuarterTurnRotation.Invoke(null, ["displaymatrix: rotation of -90.00 degrees"])!,
    "display matrix quarter-turn rotations must produce portrait display dimensions");
Require((bool)hasQuarterTurnRotation.Invoke(null, ["\nrotate: 270\n"])!,
    "legacy rotate tags must produce portrait display dimensions");
Require(!(bool)hasQuarterTurnRotation.Invoke(null, ["displaymatrix: rotation of 180.00 degrees"])!,
    "half-turn rotations must keep the encoded width and height order");

var masonryItems = Enumerable.Range(0, 30)
    .Select(index => new ImageDisplayItemSize(index % 2 == 0 ? 1 : 100, index % 2 == 0 ? 100 : 1))
    .ToArray();
var masonryGeometry = ImageDisplayGeometryIndex.Build(masonryItems,
    new ImageDisplayGeometryOptions(ImageDisplayLayoutMode.Vertical, 640, 160, 1));
for (var index = 0; index < masonryGeometry.Count; index++)
{
    masonryGeometry.TryGetItemRect(index, out var rect);
    var viewport = masonryGeometry.QueryViewport(rect.Y, Math.Min(1, rect.Height));
    Require(viewport.StartIndex <= index && viewport.EndExclusive > index,
        "masonry viewport lookup must not omit an item with mixed aspect ratios");
}
Console.WriteLine("PASS logical geometry index covers layouts, viewport bounds, and invalid dimensions");

var continuousPaths = Enumerable.Range(0, 400).Select(index => $"continuous-{index}.jpg").ToArray();
var continuousSizes = Enumerable.Range(0, continuousPaths.Length)
    .Select(index => new ImageDisplayItemSize(index == 0 ? 0 : 1600, 900))
    .ToArray();
var continuousSource = new ContinuousImageItemSource(continuousPaths, continuousSizes, _ => new List<string> { "cached" });
Require(continuousSource.Count == continuousPaths.Length && continuousSource.CachedItemCount == 0,
    "continuous source must retain paths without materializing view items");
var invalidSizeItem = GetImageItem(continuousSource[0], "continuous source index 0");
Require(invalidSizeItem.Width == 1 && invalidSizeItem.Height == 900 && continuousSource.CachedItemCount == 1,
    "continuous source must lazily create items and safely fall back invalid dimensions");
var cachedViewportItems = continuousSource.GetCachedItems(new ImageDisplayRange(0, 3));
Require(cachedViewportItems.Count == 1 && ReferenceEquals(cachedViewportItems[0], invalidSizeItem) &&
        continuousSource.CachedItemCount == 1,
    "continuous viewport lookup must return only panel-materialized items without filling the range");
var retainedFirst = GetImageItem(continuousSource[110], "continuous source index 110");
var retainedSecond = GetImageItem(continuousSource[120], "continuous source index 120");
var evicted = GetImageItem(continuousSource[300], "continuous source index 300");
evicted.ThumbnailData = [1, 2, 3];
continuousSource.UpdateRetainedRange(new ImageDisplayRange(100, 30));
Require(continuousSource.CachedItemCount == 2 && continuousSource.TryGetCachedItem(110, out _) &&
        continuousSource.TryGetCachedItem(120, out _) && evicted.ThumbnailData is null,
    "continuous source must discard thumbnail state outside the retained range");
continuousSource.ClearCache();
Require(continuousSource.CachedItemCount == 0, "continuous cache clear must not mutate the fixed snapshot");
RequireThrows<NotSupportedException>(() => ((System.Collections.IList)continuousSource).Clear(),
    "continuous source must reject IList mutation");
RequireThrows<NotSupportedException>(() => continuousSource.GetEnumerator(),
    "continuous source must reject whole-result enumeration");
RequireThrows<NotSupportedException>(() => continuousSource.CopyTo(new object[continuousSource.Count], 0),
    "continuous source must reject whole-result copying");
Console.WriteLine("PASS continuous source lazily materializes and bounds retained view items");

var geometryBuilder = new ContinuousDisplayGeometryBuilder();
var geometryPaths = Enumerable.Range(0, ContinuousDisplayGeometryBuilder.BatchSize * 2 + 1)
    .Select(index => $"geometry-{index}.jpg")
    .ToArray();
var geometryBatches = new List<int>();
var geometrySnapshot = await geometryBuilder.BuildAsync(
    geometryPaths,
    new ImageDisplayGeometryOptions(ImageDisplayLayoutMode.Vertical, 800, 160, 1),
    (batch, _) =>
    {
        geometryBatches.Add(batch.Count);
        var dimensions = new Dictionary<string, (int Width, int Height)>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in batch.Where(path => path.EndsWith("0.jpg", StringComparison.Ordinal)))
            dimensions[path] = (1600, 900);
        return Task.FromResult(dimensions);
    },
    CancellationToken.None);
geometryPaths[0] = "mutated-after-build.jpg";
Require(geometryBatches.SequenceEqual([900, 900, 1]) &&
        geometrySnapshot.Paths.Count == 1801 && geometrySnapshot.Paths[0] == "geometry-0.jpg" &&
        geometrySnapshot.Sizes.Count == geometrySnapshot.Paths.Count &&
        geometrySnapshot.GeometryIndex.Count == geometrySnapshot.Paths.Count &&
        geometrySnapshot.DimensionHitCount == 181,
    "continuous geometry builder must batch, freeze input, and preserve aligned metadata");
using var cancelledGeometryBuild = new CancellationTokenSource();
cancelledGeometryBuild.Cancel();
try
{
    await geometryBuilder.BuildAsync(geometryPaths, ImageDisplayGeometryOptions.Default,
        (_, _) => throw new InvalidOperationException("cancelled builds must not call the loader"),
        cancelledGeometryBuild.Token);
    throw new InvalidOperationException("cancelled geometry build must throw");
}
catch (OperationCanceledException) { }
Console.WriteLine("PASS continuous geometry builder batches, snapshots, and observes cancellation");

SetDisplayFiles(vm, ["geometry-vm-1.jpg", "geometry-vm-2.jpg"]);
fake.Load = paths => Task.FromResult(paths.ToDictionary(path => path, _ => (1920, 1080)));
Require(await vm.PrepareContinuousDisplayGeometryAsync(800),
    "current continuous geometry request must publish a snapshot");
Require(vm.ContinuousDisplayGeometrySnapshot?.GeometryIndex.Count == 2 &&
        vm.ContinuousImageItemSource?.CachedItemCount == 0 && !vm.IsContinuousGeometryBuilding,
    "published continuous geometry must keep the item source lazy");
var reflowSource = vm.ContinuousImageItemSource!;
var reflowItem = GetImageItem(reflowSource[0], "continuous reflow source");
reflowItem.ThumbnailData = [1, 2, 3];
Require(await vm.ReflowContinuousDisplayGeometryAsync(1120),
    "width-only continuous reflow must publish a replacement geometry");
Require(ReferenceEquals(reflowSource, vm.ContinuousImageItemSource) &&
        vm.ContinuousDisplayGeometryIndex?.Options.ContainerWidth == 1120 &&
        reflowSource.CachedItemCount == 1 && reflowItem.ThumbnailData is { Length: 3 },
    "width-only reflow must preserve the item source and loaded thumbnail state");
var geometryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var releaseGeometry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
fake.Load = async paths =>
{
    geometryEntered.TrySetResult();
    await releaseGeometry.Task;
    return paths.ToDictionary(path => path, _ => (1920, 1080));
};
var staleGeometryRequest = vm.PrepareContinuousDisplayGeometryAsync(800);
await geometryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
InvokePrivate(vm, "InvalidateContinuousDisplayGeometry");
releaseGeometry.TrySetResult();
Require(!await staleGeometryRequest && vm.ContinuousDisplayGeometrySnapshot is null &&
        vm.ContinuousImageItemSource is null && !vm.IsContinuousGeometryBuilding,
    "invalidated continuous geometry must not publish when stale metadata work completes");
var deleteGeometryPaths = new List<string> { "geometry-delete-1.jpg", "geometry-delete-2.jpg" };
SetFiles(vm, deleteGeometryPaths.ToList());
SetDisplayFiles(vm, deleteGeometryPaths.ToList());
vm.Images = new();
fake.Load = paths => Task.FromResult(paths.ToDictionary(path => path, _ => (1920, 1080)));
Require(await vm.PrepareContinuousDisplayGeometryAsync(800),
    "continuous geometry must build before deletion invalidation is tested");
await vm.RemoveFilesFromViewAsync(new HashSet<string>(["geometry-delete-1.jpg"], StringComparer.OrdinalIgnoreCase));
Require(vm.ContinuousDisplayGeometrySnapshot is null && vm.ContinuousImageItemSource is null,
    "incremental deletion must invalidate the continuous display snapshot");
Console.WriteLine("PASS continuous geometry coordinator publishes current work and rejects stale work");

SetDisplayFiles(vm, ["selection-1.jpg", "selection-2.jpg", "selection-3.jpg"]);
vm.Images = new([new ImageViewItem { FilePath = "selection-1.jpg" }]);
vm.ReplaceSelectedFiles(["selection-2.jpg", "selection-3.jpg"]);
Require(vm.SelectedFilePaths.Count == 2 && !vm.Images[0].IsSelected,
    "path selection must not require the selected item to be materialized on the current page");
vm.Images = new([new ImageViewItem { FilePath = "selection-2.jpg" }]);
vm.ApplySelectionToRealizedItems();
Require(vm.Images[0].IsSelected && vm.GetSelectedRealizedItems().Count == 1,
    "path selection must project onto a newly realized page item");
Console.WriteLine("PASS path selection persists independently from realized page items");

SetFiles(vm, Enumerable.Range(0, 401).Select(i => $"image{i}.jpg").Append("clip.mp4").ToList());
vm.DisplayFilter = new("image");
Require(await Publish(vm), "publish type filter");
Paging(vm);
Require(vm.ActiveFileList.Count == 401 && vm.TotalPages == 3, "filtered count must drive paging");
Require(vm.DisplayFilterSourceCount == 402, "summary denominator must be unfiltered source");
Console.WriteLine("PASS type filtering drives published list and page count");

SetFiles(vm, ["second.mp4", "second.png"]);
Require(await Publish(vm), "publish folder replacement");
Require(vm.ActiveFileList.SequenceEqual(new[] { "second.png" }), "folder change must preserve criteria and replace snapshot");
vm.IsShowingSearchResult = true;
search.SearchResultFiles = ["rank2.png", "skip.mp4", "rank1.jpg"];
Require(await Publish(vm), "publish search");
Require(vm.ActiveFileList.SequenceEqual(new[] { "rank2.png", "rank1.jpg" }), "search rank order must be preserved");
vm.IsShowingSearchResult = false;
Require(await Publish(vm), "return from search");
Require(vm.ActiveFileList.SequenceEqual(new[] { "second.png" }), "back must rebuild from folder source");
Console.WriteLine("PASS source replacement and search return retain criteria and ranking");

var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
fake.Load = async paths => { entered.TrySetResult(); await release.Task; return paths.ToDictionary(p => p, _ => (1920, 1080)); };
SetFiles(vm, ["stale.jpg"]);
vm.DisplayFilter = new(null, MediaOrientation.Landscape);
var old = Publish(vm);
await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
SetFiles(vm, ["latest.mp4", "ignored.jpg"]);
vm.DisplayFilter = new("video");
Require(await Publish(vm), "new request");
release.TrySetResult();
Require(!await old, "cancelled request must not publish");
Require(vm.ActiveFileList.SequenceEqual(new[] { "latest.mp4" }), "old request must not overwrite latest source/options");
Require(!vm.IsDisplayFilterBusy, "latest request must clear busy state");
Console.WriteLine("PASS deterministic overlapping requests publish only latest result");

fake.Load = paths => Task.FromResult(new Dictionary<string, (int, int)>());
vm.DisplayFilter = new("image");
SetFiles(vm, ["only-video.mp4"]);
Require(await Publish(vm), "empty filtered result");
Paging(vm);
Require(vm.TotalPages == 0 && vm.IsDisplayFilterEmpty, "zero matches clear pages and expose reset state");
vm.DisplayFilter = new();
Require(await Publish(vm), "reset filter");
Require(vm.ActiveFileList.SequenceEqual(new[] { "only-video.mp4" }), "reset restores full supported source");
Console.WriteLine("PASS empty result and reset restore consistent state");
// Simulate a reset whose default-options publication is queued while the old snapshot is still visible.
SetFiles(vm, ["keep.jpg", "delete.mp4"]);
vm.DisplayFilter = new("video");
Require(await Publish(vm), "initial restricted snapshot");
vm.DisplayFilter = new();
vm.IsDisplayFilterBusy = true;
await vm.RemoveFilesFromViewAsync(new HashSet<string>(new[] { "delete.mp4" }, StringComparer.OrdinalIgnoreCase));
Require(vm.ActiveFileList.SequenceEqual(new[] { "keep.jpg" }) && vm.DisplayFilterSourceCount == 1 && !vm.IsDisplayFilterBusy,
    "delete during pending reset must complete publication and clear busy state");
Console.WriteLine("PASS delete reconciles a pending reset publication");
Console.WriteLine("All 8 display-filter integration groups passed.");

static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
static byte[] CreateJpegHeader(int width, int height) =>
[
    0xFF, 0xD8,
    0xFF, 0xC0,
    0x00, 0x08,
    0x08,
    (byte)(height >> 8), (byte)height,
    (byte)(width >> 8), (byte)width,
    0x00
];
static void RequireThrows<TException>(Action action, string message) where TException : Exception
{
    try { action(); }
    catch (TException) { return; }
    throw new InvalidOperationException(message);
}
static ImageViewItem GetImageItem(object? value, string message) =>
    value as ImageViewItem ?? throw new InvalidOperationException($"Expected ImageViewItem for {message}.");
static void SetFiles(MainWindowViewModel vm, List<string> files) => typeof(MainWindowViewModel).GetField("_allFiles", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(vm, files);
static void SetDisplayFiles(MainWindowViewModel vm, List<string> files) => typeof(MainWindowViewModel).GetField("_displayFilteredFiles", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(vm, files);
static void InvokePrivate(MainWindowViewModel vm, string methodName) => typeof(MainWindowViewModel).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, null);
static Task<bool> Publish(MainWindowViewModel vm) => (Task<bool>)typeof(MainWindowViewModel).GetMethod("UpdateDisplayFilterAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, [false])!;
static void Paging(MainWindowViewModel vm) => typeof(MainWindowViewModel).GetMethod("SetDisplayPaging", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, null);

public class MetadataProxy : DispatchProxy
{
    public Func<List<string>, Task<Dictionary<string, (int, int)>>> Load = _ => Task.FromResult(new Dictionary<string, (int, int)>());
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
    {
        "GetDimensionsByPathsAsync" => Load((List<string>)args![0]!),
        "GetTagMapByPathsAsync" => Task.FromResult(new Dictionary<string, List<string>>()),
        _ => throw new NotSupportedException(method.Name)
    };
}

public class ImmediateDispatcher : IDispatcher
{
    public void Post(Action action) => action();
    public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
    public Task InvokeAsync(Func<Task> callback) => callback();
    public Task<T> InvokeAsync<T>(Func<T> callback) => Task.FromResult(callback());
    public Task<T> InvokeAsync<T>(Func<Task<T>> callback) => callback();
}
