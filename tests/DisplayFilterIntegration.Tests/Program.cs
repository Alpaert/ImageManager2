using System.Reflection;
using CommunityToolkit.Mvvm.Messaging;
using ImageManager.App.Services;
using ImageManager.App.ViewModels;
using ImageManager.Core.Models;
using ImageManager.Core.Services;
using ImageManager.Infrastructure.Services;
using ImageManager.Infrastructure.Caching;

var messenger = new WeakReferenceMessenger();
var repository = DispatchProxy.Create<IImageMetaRepository, MetadataProxy>();
var fake = (MetadataProxy)(object)repository;
var cache = new ThumbnailCacheService(null!, Path.Combine(Path.GetTempPath(), "ImageManager-FilterTests"));
var page = new PageManager(cache, null!);
var search = new TagSearchEngine(repository, messenger, PageManager.PageSize);
var vm = new MainWindowViewModel(null!, null!, repository, null!, null!, null!, null!, cache, page,
    search, null!, null!, null!, messenger, new ImmediateDispatcher());

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
Console.WriteLine("All 5 display-filter integration groups passed.");

static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
static void SetFiles(MainWindowViewModel vm, List<string> files) => typeof(MainWindowViewModel).GetField("_allFiles", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(vm, files);
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
