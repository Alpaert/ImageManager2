using ImageManager.Common.Helpers;
using Xunit;

namespace Infrastructure.IntegrationTests;

[CollectionDefinition("AutoTagRuntimeStateSerial", DisableParallelization = true)]
public class AutoTagRuntimeStateSerialCollectionDefinition { }

[Collection("AutoTagRuntimeStateSerial")]
public sealed class AutoTagRuntimeStateTests
{
    private static readonly object SyncLock = new();

    [Fact]
    public void Enter_SetsIsRunningToTrue_AndDisposingScopeResets()
    {
        lock (SyncLock)
        {
            Assert.False(AutoTagRuntimeState.IsRunning);

            using (AutoTagRuntimeState.Enter())
            {
                Assert.True(AutoTagRuntimeState.IsRunning);
            }

            Assert.False(AutoTagRuntimeState.IsRunning);
        }
    }

    [Fact]
    public void Scope_DisposeIsIdempotent()
    {
        lock (SyncLock)
        {
            var scope = AutoTagRuntimeState.Enter();
            try
            {
                Assert.True(AutoTagRuntimeState.IsRunning);
                scope.Dispose();
                scope.Dispose(); // second call must not decrement below 0
                Assert.False(AutoTagRuntimeState.IsRunning);
            }
            finally
            {
                scope.Dispose();
                Assert.False(AutoTagRuntimeState.IsRunning);
            }
        }
    }

    [Fact]
    public void MultiScope_OnlyBecomesIdleWhenLastScopeExits()
    {
        lock (SyncLock)
        {
            Assert.False(AutoTagRuntimeState.IsRunning);

            IDisposable? scope1 = null;
            IDisposable? scope2 = null;
            IDisposable? scope3 = null;
            try
            {
                scope1 = AutoTagRuntimeState.Enter();
                Assert.True(AutoTagRuntimeState.IsRunning);

                scope2 = AutoTagRuntimeState.Enter();
                scope3 = AutoTagRuntimeState.Enter();
                Assert.True(AutoTagRuntimeState.IsRunning);

                // Disposing first scope keeps state running
                scope1.Dispose();
                Assert.True(AutoTagRuntimeState.IsRunning);

                // Disposing second scope keeps state running
                scope2.Dispose();
                Assert.True(AutoTagRuntimeState.IsRunning);

                // Disposing third and final scope transitions to idle
                scope3.Dispose();
                Assert.False(AutoTagRuntimeState.IsRunning);
            }
            finally
            {
                scope1?.Dispose();
                scope2?.Dispose();
                scope3?.Dispose();
                Assert.False(AutoTagRuntimeState.IsRunning);
            }
        }
    }

    [Fact]
    public async Task WaitForIdleAsync_ResumesWhenStateBecomesIdle()
    {
        IDisposable? scope = null;
        try
        {
            scope = AutoTagRuntimeState.Enter();
            Assert.True(AutoTagRuntimeState.IsRunning);

            var waitTask = AutoTagRuntimeState.WaitForIdleAsync(CancellationToken.None);

            // While scope is open, waitTask is still waiting
            await Task.Delay(50);
            Assert.False(waitTask.IsCompleted);

            // Release scope
            scope.Dispose();

            await waitTask.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(waitTask.IsCompletedSuccessfully);
        }
        finally
        {
            scope?.Dispose();
            Assert.False(AutoTagRuntimeState.IsRunning);
        }
    }

    [Fact]
    public async Task WaitForIdleAsync_ActiveCancellationWhileWaiting_ThrowsOperationCanceledException()
    {
        IDisposable? scope = null;
        using var cts = new CancellationTokenSource();
        Task? waitTask = null;
        try
        {
            scope = AutoTagRuntimeState.Enter();

            // Start waiting actively with live token
            waitTask = AutoTagRuntimeState.WaitForIdleAsync(cts.Token);

            // Ensure wait loop was entered while scope is held
            await Task.Delay(50);
            Assert.False(waitTask.IsCompleted);

            // Trigger cancellation while active
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitTask.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            cts.Cancel();
            scope?.Dispose();
            if (waitTask != null)
            {
                try { await waitTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            }
            Assert.False(AutoTagRuntimeState.IsRunning);
        }
    }

    [Fact]
    public void Scope_ExceptionExit_DisposesCleanlyAndResetsState()
    {
        lock (SyncLock)
        {
            Assert.False(AutoTagRuntimeState.IsRunning);

            try
            {
                using (AutoTagRuntimeState.Enter())
                {
                    Assert.True(AutoTagRuntimeState.IsRunning);
                    throw new InvalidOperationException("Simulated exception inside processing scope");
                }
            }
            catch (InvalidOperationException)
            {
                // Expected
            }

            // Must reset to idle cleanly after exception exit
            Assert.False(AutoTagRuntimeState.IsRunning);
        }
    }
}
