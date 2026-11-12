using System.Reflection;
using ImageManager.App.Services;
using ImageManager.Core.Models;
using ImageManager.Core.Services;
using Xunit;

namespace ContinuousDisplay.UnitTests;

public class IdleMaintenanceTests
{
    private sealed class Clock : TimeProvider
    {
        public long Now;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Now;
    }

    [Fact]
    public async Task FailureBackoffPreventsHotRetryAndNewFolderRequestResetsIt()
    {
        var clock = new Clock();
        using var scheduler = new IdleMaintenanceScheduler(clock);
        scheduler.Request();
        clock.Now = 5000;
        Assert.True(scheduler.TryStart(_ =>
        {
            Assert.Equal(TimeSpan.FromSeconds(30), scheduler.RecordFailure());
            return Task.FromResult(false);
        }, default, () => { }));
        await scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Now = 10000;
        scheduler.NotifyInteraction();
        clock.Now = 15000;
        Assert.False(scheduler.TryStart(_ => Task.FromResult(true), default, () => { }));
        Assert.Equal(TimeSpan.FromSeconds(20), scheduler.RemainingIdle);
        clock.Now = 35000;
        Assert.True(scheduler.TryStart(_ =>
        {
            Assert.Equal(TimeSpan.FromSeconds(60), scheduler.RecordFailure());
            return Task.FromResult(false);
        }, default, () => { }));
        await scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        scheduler.Request();
        using var staleRun = new CancellationTokenSource();
        staleRun.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => scheduler.RecordFailure(staleRun.Token));
        clock.Now += 5000;
        Assert.True(scheduler.TryStart(_ => Task.FromResult(true), default, () => { }));
        await scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(scheduler.HasPendingWork);
    }

    [Fact]
    public async Task RequiresFiveContinuousIdleSecondsAndDoesNotRepeatCompletedWork()
    {
        var clock = new Clock();
        using var scheduler = new IdleMaintenanceScheduler(clock);
        scheduler.Request();
        clock.Now = 4900;
        Assert.False(scheduler.TryStart(_ => Task.FromResult(true), default, () => { }));
        scheduler.NotifyInteraction();
        clock.Now = 9899;
        Assert.False(scheduler.TryStart(_ => Task.FromResult(true), default, () => { }));
        clock.Now = 9900;
        Assert.True(scheduler.TryStart(_ => Task.FromResult(true), default, () => { }));
        await scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        scheduler.NotifyInteraction();
        Assert.False(scheduler.HasPendingWork);
    }

    [Fact]
    public async Task InteractionCancelsOnlyAutomaticWorkAndWaitsForItsCurrentWriteBeforeRestart()
    {
        var clock = new Clock();
        using var scheduler = new IdleMaintenanceScheduler(clock);
        using var manual = new CancellationTokenSource();
        using var folder = new CancellationTokenSource();
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.Request();
        clock.Now = 5000;
        Assert.True(scheduler.TryStart(async ct =>
        {
            started.SetResult(ct);
            await finishWrite.Task;
            ct.ThrowIfCancellationRequested();
            return true;
        }, folder.Token, () => { }));
        var token = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        scheduler.NotifyInteraction();
        Assert.True(token.IsCancellationRequested);
        Assert.False(manual.IsCancellationRequested);
        Assert.False(folder.IsCancellationRequested);
        clock.Now = 10000;
        Assert.False(scheduler.TryStart(_ => Task.FromResult(true), default, () => { }));
        finishWrite.SetResult();
        await scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(scheduler.HasPendingWork);
        Assert.True(scheduler.TryStart(_ => Task.FromResult(true), default, () => { }));
        await scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(scheduler.HasPendingWork);
    }

    [Fact]
    public async Task BusyGateAndFolderInvalidationRemainPendingButShutdownDoesNotRestart()
    {
        var clock = new Clock();
        using var scheduler = new IdleMaintenanceScheduler(clock);
        scheduler.Request();
        clock.Now = 5000;
        var finished = 0;
        Assert.True(scheduler.TryStart(_ => Task.FromResult(false), default, () => finished++));
        await scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, finished);
        Assert.True(scheduler.HasPendingWork);

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var oldFolder = new CancellationTokenSource();
        Assert.True(scheduler.TryStart(async ct =>
        {
            started.SetResult();
            await release.Task;
            ct.ThrowIfCancellationRequested();
            return true;
        }, oldFolder.Token, () => finished++));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        oldFolder.Cancel();
        scheduler.Request();
        release.SetResult();
        await scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(scheduler.HasPendingWork);
        Assert.Equal(TimeSpan.FromSeconds(5), scheduler.RemainingIdle);
        scheduler.Dispose();
        clock.Now += 5000;
        Assert.False(scheduler.TryStart(_ => Task.FromResult(true), default, () => { }));
        Assert.False(scheduler.HasPendingWork);
    }

    [Fact]
    public async Task CleanupUsesLightweightScopedReadAndResumesAfterInFlightWrite()
    {
        var repository = DispatchProxy.Create<IImageMetaRepository, TestMetaProxy>();
        var proxy = (TestMetaProxy)(object)repository;
        var folder = Path.Combine(Path.GetTempPath(), "im-cleanup-" + Guid.NewGuid().ToString("N"));
        var paths = new[] { Path.Combine(folder, "a.png"), Path.Combine(folder, "b.png") };
        using var cancel = new CancellationTokenSource();
        var writes = new List<string>();
        var reads = 0;
        proxy.CustomHandler = (method, args) =>
        {
            if (method.Name == "GetByFolderAsync") throw new Xunit.Sdk.XunitException("Full metadata must not be loaded.");
            if (method.Name == "GetSortMetadataByFolderAsync")
            {
                reads++;
                Assert.Equal(folder, args![0]);
                Assert.False((bool)args[1]!);
                Assert.Equal(cancel.Token, args[2]);
                return Task.FromResult<IReadOnlyList<ImageSortMetadata>>(paths.Select(p => new ImageSortMetadata(p, 0, 0, 0, 0)).ToArray());
            }
            if (method.Name == "SetFolderIdAsync")
            {
                writes.Add((string)args![0]!);
                Assert.Equal(0L, args[1]);
                if (writes.Count == 1) cancel.Cancel(); // The already-started UPDATE still commits.
                return Task.CompletedTask;
            }
            return null;
        };
        var cleanup = new FolderMetadataCleanup(repository, folder, false, Array.Empty<string>());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cleanup.RunAsync(cancel.Token, () => { }));
        Assert.Equal(new[] { paths[0] }, writes);
        await cleanup.RunAsync(default, () => { });
        Assert.Equal(paths, writes);
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task CleanupDoesNotUnlinkReappearedFilesOrOldFolderGeneration()
    {
        var repository = DispatchProxy.Create<IImageMetaRepository, TestMetaProxy>();
        var proxy = (TestMetaProxy)(object)repository;
        var path = Path.Combine(Path.GetTempPath(), "im-reappeared-" + Guid.NewGuid().ToString("N") + ".png");
        var writes = 0;
        proxy.CustomHandler = (method, args) =>
        {
            if (method.Name == "GetSortMetadataByFolderAsync")
                return Task.FromResult<IReadOnlyList<ImageSortMetadata>>([new(path, 0, 0, 0, 0)]);
            if (method.Name == "SetFolderIdAsync") { writes++; return Task.CompletedTask; }
            return null;
        };
        try
        {
            var cleanup = new FolderMetadataCleanup(repository, Path.GetDirectoryName(path)!, true, Array.Empty<string>());
            var checks = 0;
            await cleanup.RunAsync(default, () =>
            {
                if (++checks == 3) File.WriteAllText(path, "reappeared"); // Right before write, after initial existence check.
            });
            Assert.Equal(0, writes);
            File.Delete(path);
            var oldGeneration = new FolderMetadataCleanup(repository, Path.GetDirectoryName(path)!, true, Array.Empty<string>());
            checks = 0;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldGeneration.RunAsync(default, () =>
            {
                if (++checks == 3) throw new OperationCanceledException();
            }));
            Assert.Equal(0, writes);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
