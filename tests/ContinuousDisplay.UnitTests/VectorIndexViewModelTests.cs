using System.Threading.Channels;
using ImageManager.App.ViewModels;
using ImageManager.Core.Models;
using ImageManager.Core.Services;
using Xunit;

namespace ContinuousDisplay.UnitTests;

public sealed class VectorIndexViewModelTests
{
    [Fact]
    public async Task Initialize_ReturnsToCallerWhileStatusServiceIsStillScanning()
    {
        using var releaseScan = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        int callerThread = 0;
        int scanningThread = 0;
        var service = new FakeIndexService(_ =>
        {
            scanningThread = Environment.CurrentManagedThreadId;
            entered.TrySetResult();
            if (!releaseScan.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Test scan was not released.");
            return Task.FromResult(Statuses(5));
        });
        var vm = new VectorIndexViewModel(service, new FakeClipService());
        var caller = new Thread(() =>
        {
            callerThread = Environment.CurrentManagedThreadId;
            try { returned.TrySetResult(vm.InitializeAsync()); }
            catch (Exception ex) { returned.TrySetException(ex); }
        }) { IsBackground = true };

        caller.Start();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var refresh = await returned.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(refresh.IsCompleted);
            Assert.NotEqual(callerThread, scanningThread);
        }
        finally
        {
            releaseScan.Set();
            var refresh = await returned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Contains("5", vm.SemanticStatus);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refresh_OlderScopeCompletionCannotOverwriteLatestScope(bool oldRequestFails)
    {
        var requests = Channel.CreateUnbounded<PendingRequest>();
        var service = new FakeIndexService(scope =>
        {
            var pending = new PendingRequest(scope);
            requests.Writer.TryWrite(pending);
            return pending.Completion.Task;
        });
        var vm = new VectorIndexViewModel(service, new FakeClipService());
        var firstRefresh = vm.InitializeAsync();
        var first = await requests.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(first.Scope.IsAll);

        vm.SelectedFolderPath = @"C:\Selected";
        var secondRefresh = vm.InitializeAsync();
        var second = await requests.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(vm.SelectedFolderPath, second.Scope.FolderPath);
        second.Completion.SetResult(Statuses(7));
        await secondRefresh;
        var latest = vm.SemanticStatus;

        if (oldRequestFails)
            first.Completion.SetException(new IOException("Old scope unavailable"));
        else
            first.Completion.SetResult(Statuses(99));
        await firstRefresh;

        Assert.Contains("7", vm.SemanticStatus);
        Assert.Equal(latest, vm.SemanticStatus);
        Assert.Equal(latest, vm.AtmosphereStatus);
        Assert.Equal(latest, vm.ColorStatus);
    }

    [Fact]
    public async Task Refresh_FailureIsDisplayedAndNextRefreshCanRecover()
    {
        int calls = 0;
        var service = new FakeIndexService(_ => ++calls == 1
            ? Task.FromException<IReadOnlyList<VectorIndexStatus>>(new IOException("Status unavailable"))
            : Task.FromResult(Statuses(0)));
        var vm = new VectorIndexViewModel(service, new FakeClipService());

        await vm.InitializeAsync();
        Assert.Contains("Status unavailable", vm.SemanticStatus);
        Assert.Equal(vm.SemanticStatus, vm.AtmosphereStatus);
        Assert.Equal(vm.SemanticStatus, vm.ColorStatus);
        Assert.Equal("空闲", vm.ProgressText);
        Assert.False(vm.IsRunning);

        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("当前范围没有已收录图片", vm.SemanticStatus);
    }

    private static IReadOnlyList<VectorIndexStatus> Statuses(int count) =>
        Enum.GetValues<VectorIndexKind>().Select(kind => new VectorIndexStatus(kind, count, count, 0)).ToArray();

    private sealed class PendingRequest(VectorIndexScope scope)
    {
        public VectorIndexScope Scope { get; } = scope;
        public TaskCompletionSource<IReadOnlyList<VectorIndexStatus>> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class FakeIndexService(Func<VectorIndexScope, Task<IReadOnlyList<VectorIndexStatus>>> load) : IVectorIndexService
    {
        public bool IsRunning => false;
        public Task<IReadOnlyList<VectorIndexStatus>> GetStatusesAsync(VectorIndexScope scope) => load(scope);
        public async Task<VectorIndexStatus> GetStatusAsync(VectorIndexKind kind, VectorIndexScope scope) =>
            (await load(scope)).First(status => status.Kind == kind);
        public Task BuildAsync(VectorIndexKind kind, bool rebuild, VectorIndexScope scope,
            IProgress<VectorIndexProgress>? progress = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task RepairHashesAsync(VectorIndexScope scope, IProgress<HashRepairProgress>? progress = null,
            CancellationToken ct = default) => Task.CompletedTask;
        public void Pause() { }
        public void Resume() { }
        public void Cancel() { }
    }

    private sealed class FakeClipService : IChineseClipService
    {
        public string ModelDirectory => "test-models";
        public void ValidateImageModelFiles() { }
        public void ValidateTextModelFiles() { }
        public Task<float[]> GetImageEmbeddingAsync(string imagePath, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<float[]> GetTextEmbeddingAsync(string text, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public void Dispose() { }
    }
}
