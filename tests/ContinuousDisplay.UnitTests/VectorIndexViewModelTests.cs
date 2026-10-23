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

    [Fact]
    public async Task BuildAsync_RebuildAndIncremental_ReportsProgressAndSetsProgressText()
    {
        var releaseBuild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progressApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var service = new FakeIndexService(_ => Task.FromResult(Statuses(10)));
        service.OnBuild = async (kind, rebuild, scope, progress, ct) =>
        {
            progress?.Report(new VectorIndexProgress(kind, Total: 10, Processed: 5, Generated: 4, Skipped: 1, Failed: 0, CurrentFile: @"C:\images\sample.png", IsPaused: false, Error: null));
            await releaseBuild.Task.WaitAsync(TimeSpan.FromSeconds(5));
        };
        var vm = new VectorIndexViewModel(service, new FakeClipService());
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.ProgressText) && vm.ProgressText.Contains("sample.png"))
                progressApplied.TrySetResult();
        };

        Task? buildTask = null;
        try
        {
            // Act 1: Build semantic (incremental, rebuild = false)
            buildTask = vm.BuildSemanticCommand.ExecuteAsync(null);

            // Wait until progress was actually applied to the VM properties
            await progressApplied.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Exact assertions while build is pending:
            Assert.True(vm.IsRunning);
            Assert.Equal(10, vm.ProgressMaximum);
            Assert.Equal(6, vm.ProgressValue); // Processed(5) + Skipped(1)
            Assert.Contains("6/10", vm.ProgressText);
            Assert.Contains("新增 4", vm.ProgressText);
            Assert.Contains("跳过 1", vm.ProgressText);
            Assert.Contains("失败 0", vm.ProgressText);
            Assert.Contains("sample.png", vm.ProgressText);

            // Release build to complete
            releaseBuild.TrySetResult();
            await buildTask.WaitAsync(TimeSpan.FromSeconds(5));

            // Completion assertions
            Assert.False(vm.IsRunning);
            Assert.Single(service.BuildCalls);
        }
        finally
        {
            releaseBuild.TrySetResult();
            if (buildTask != null) { try { await buildTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { } }
        }
        Assert.Equal((VectorIndexKind.Semantic, false, VectorIndexScope.All), service.BuildCalls[0]);
        Assert.Equal("Chinese-CLIP 索引处理完成", vm.ProgressText);

        // Act 2: Rebuild semantic (rebuild = true)
        service.OnBuild = null;
        await vm.RebuildSemanticCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, service.BuildCalls.Count);
        Assert.Equal((VectorIndexKind.Semantic, true, VectorIndexScope.All), service.BuildCalls[1]);
        Assert.Equal("Chinese-CLIP 索引处理完成", vm.ProgressText);
    }

    [Fact]
    public async Task BuildAsync_ActiveCancellation_SetsCancelledProgressText_AndAllowsNextRun()
    {
        var buildStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var buildCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var service = new FakeIndexService(_ => Task.FromResult(Statuses(10)));
        service.OnBuild = async (kind, rebuild, scope, progress, ct) =>
        {
            buildStarted.TrySetResult();
            await buildCanceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            throw new OperationCanceledException();
        };
        service.OnCancel = () => buildCanceled.TrySetResult();

        var vm = new VectorIndexViewModel(service, new FakeClipService());
        var task = vm.BuildSemanticCommand.ExecuteAsync(null);

        await buildStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(vm.IsRunning);

        // Cancel command
        vm.CancelCommand.Execute(null);
        await task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(vm.IsRunning);
        Assert.Equal("索引任务已取消，已完成部分会保留", vm.ProgressText);

        // Recovery: Next build succeeds
        service.OnBuild = null;
        await vm.BuildAtmosphereCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(vm.IsRunning);
        Assert.Equal("氛围 索引处理完成", vm.ProgressText);
    }

    [Fact]
    public async Task BuildAsync_ErrorOccurs_SetsFailedProgressText_AndAllowsNextRun()
    {
        var service = new FakeIndexService(_ => Task.FromResult(Statuses(10)));
        service.OnBuild = (kind, rebuild, scope, progress, ct) =>
            throw new InvalidOperationException("CLIP inference crashed");

        var vm = new VectorIndexViewModel(service, new FakeClipService());
        await vm.BuildSemanticCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(vm.IsRunning);
        Assert.Contains("索引任务失败: CLIP inference crashed", vm.ProgressText);

        // Recovery: Next run succeeds
        service.OnBuild = null;
        await vm.BuildColorCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(vm.IsRunning);
        Assert.Equal("颜色 索引处理完成", vm.ProgressText);
    }

    [Fact]
    public async Task RepairHashesAsync_Progress_Cancellation_And_ErrorRecovery()
    {
        var releaseRepair = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progressApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var service = new FakeIndexService(_ => Task.FromResult(Statuses(10)));
        service.OnRepairHashes = async (scope, progress, ct) =>
        {
            progress?.Report(new HashRepairProgress(Total: 25, Processed: 12, Generated: 10, Skipped: 3, Failed: 1, CurrentFile: @"C:\images\repair_target.png", Error: null));
            await releaseRepair.Task.WaitAsync(TimeSpan.FromSeconds(5));
        };

        var vm = new VectorIndexViewModel(service, new FakeClipService());
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.ProgressText) && vm.ProgressText.Contains("repair_target.png"))
                progressApplied.TrySetResult();
        };

        Task? repairTask = null;
        try
        {
            // Act 1: Run repair and verify intermediate progress
            repairTask = vm.RepairHashesCommand.ExecuteAsync(null);
            await progressApplied.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(vm.IsRunning);
            Assert.Equal(25, vm.ProgressMaximum);
            Assert.Equal(15, vm.ProgressValue); // Processed(12) + Skipped(3)
            Assert.Contains("15/25", vm.ProgressText);
            Assert.Contains("新增 10", vm.ProgressText);
            Assert.Contains("跳过 3", vm.ProgressText);
            Assert.Contains("失败 1", vm.ProgressText);
            Assert.Contains("repair_target.png", vm.ProgressText);

            releaseRepair.TrySetResult();
            await repairTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(vm.IsRunning);
            Assert.Equal("图片指纹补算完成", vm.ProgressText);
        }
        finally
        {
            releaseRepair.TrySetResult();
            if (repairTask != null) { try { await repairTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { } }
        }

        // Act 2: Cancellation
        var cancelTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.OnRepairHashes = async (scope, progress, ct) =>
        {
            await cancelTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            throw new OperationCanceledException();
        };
        service.OnCancel = () => cancelTcs.TrySetResult();

        var cancelTask = vm.RepairHashesCommand.ExecuteAsync(null);
        vm.CancelCommand.Execute(null);
        await cancelTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(vm.IsRunning);
        Assert.Equal("图片指纹补算已取消", vm.ProgressText);

        // Act 3: Error and recovery
        service.OnRepairHashes = (scope, progress, ct) =>
            throw new IOException("Hash disk read error");

        await vm.RepairHashesCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(vm.IsRunning);
        Assert.Contains("图片指纹补算失败: Hash disk read error", vm.ProgressText);

        // Recovery
        service.OnRepairHashes = null;
        await vm.RepairHashesCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(vm.IsRunning);
        Assert.Equal("图片指纹补算完成", vm.ProgressText);
    }

    [Fact]
    public void PauseAndResumeAndCancel_DelegatesToService()
    {
        var service = new FakeIndexService(_ => Task.FromResult(Statuses(0)));
        var vm = new VectorIndexViewModel(service, new FakeClipService());

        // Act 1: Pause
        vm.PauseCommand.Execute(null);
        Assert.True(service.PauseCalled);
        Assert.True(vm.IsPaused);
        Assert.Equal("已暂停", vm.ProgressText);

        // Act 2: Resume
        vm.ResumeCommand.Execute(null);
        Assert.True(service.ResumeCalled);
        Assert.False(vm.IsPaused);
        Assert.Equal("继续处理中...", vm.ProgressText);

        // Act 3: Cancel
        vm.CancelCommand.Execute(null);
        Assert.True(service.CancelCalled);
    }

    [Fact]
    public async Task IsRunning_BlocksConcurrentBuildOrFolderPicker()
    {
        var buildStarted = new TaskCompletionSource<bool>();
        var releaseBuild = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var service = new FakeIndexService(_ => Task.FromResult(Statuses(1)));
        service.OnBuild = async (kind, rebuild, scope, progress, ct) =>
        {
            buildStarted.TrySetResult(true);
            await releaseBuild.Task.WaitAsync(TimeSpan.FromSeconds(5));
        };

        var vm = new VectorIndexViewModel(service, new FakeClipService());
        int pickerCalls = 0;
        vm.FolderPicker = () =>
        {
            pickerCalls++;
            return Task.FromResult<string?>(@"C:\folder");
        };

        // Start first build in background
        var buildTask = vm.BuildSemanticCommand.ExecuteAsync(null);
        try
        {
            await buildStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // While running:
            Assert.True(vm.IsRunning);

            // Try second build: should return immediately without queuing another call
            await vm.BuildAtmosphereCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(service.BuildCalls);

            // Try folder picker: should return immediately without invoking picker
            await vm.SelectFolderCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, pickerCalls);

            // Try use all images: should return immediately
            await vm.UseAllImagesCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            // Release first build
            releaseBuild.TrySetResult(true);
            await buildTask.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.False(vm.IsRunning);
    }

    [Fact]
    public async Task ScopeDescription_And_FolderSelection_UpdatesCurrentScope()
    {
        var service = new FakeIndexService(_ => Task.FromResult(Statuses(0)));
        var vm = new VectorIndexViewModel(service, new FakeClipService());

        // Initial default: all images
        Assert.Equal("全部图库", vm.SelectedFolderDisplay);
        Assert.Equal("当前范围：全部图库", vm.ScopeDescription);

        // Pick folder
        vm.FolderPicker = () => Task.FromResult<string?>(@"C:\test_photos");
        await vm.SelectFolderCommand.ExecuteAsync(null);

        Assert.Equal(@"C:\test_photos", vm.SelectedFolderDisplay);
        Assert.Equal(@"当前范围：C:\test_photos（包含子文件夹）", vm.ScopeDescription);

        // Toggle include subfolders
        vm.IncludeSubfolders = false;
        Assert.Equal(@"当前范围：C:\test_photos（仅直属图片）", vm.ScopeDescription);

        // Switch back to all images
        await vm.UseAllImagesCommand.ExecuteAsync(null);
        Assert.Equal("全部图库", vm.SelectedFolderDisplay);
        Assert.Equal("当前范围：全部图库", vm.ScopeDescription);
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
        public List<(VectorIndexKind Kind, bool Rebuild, VectorIndexScope Scope)> BuildCalls { get; } = new();
        public bool PauseCalled { get; private set; }
        public bool ResumeCalled { get; private set; }
        public bool CancelCalled { get; private set; }
        public Func<VectorIndexKind, bool, VectorIndexScope, IProgress<VectorIndexProgress>?, CancellationToken, Task>? OnBuild { get; set; }
        public Func<VectorIndexScope, IProgress<HashRepairProgress>?, CancellationToken, Task>? OnRepairHashes { get; set; }
        public Action? OnCancel { get; set; }

        public Task<IReadOnlyList<VectorIndexStatus>> GetStatusesAsync(VectorIndexScope scope) => load(scope);
        public async Task<VectorIndexStatus> GetStatusAsync(VectorIndexKind kind, VectorIndexScope scope) =>
            (await load(scope)).First(status => status.Kind == kind);

        public async Task BuildAsync(VectorIndexKind kind, bool rebuild, VectorIndexScope scope,
            IProgress<VectorIndexProgress>? progress = null, CancellationToken ct = default)
        {
            BuildCalls.Add((kind, rebuild, scope));
            if (OnBuild != null)
                await OnBuild(kind, rebuild, scope, progress, ct);
        }

        public async Task RepairHashesAsync(VectorIndexScope scope, IProgress<HashRepairProgress>? progress = null,
            CancellationToken ct = default)
        {
            if (OnRepairHashes != null)
                await OnRepairHashes(scope, progress, ct);
        }

        public void Pause() => PauseCalled = true;
        public void Resume() => ResumeCalled = true;
        public void Cancel()
        {
            CancelCalled = true;
            OnCancel?.Invoke();
        }
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
