using ImageManager.Infrastructure.Data;
using Xunit;

namespace Infrastructure.IntegrationTests;

public sealed class MetadataWriteCoordinatorTests
{
    private sealed class DummyDbFactory : IDbContextFactory
    {
        public Microsoft.Data.Sqlite.SqliteConnection CreateConnection(int commandTimeout = 30) => throw new NotSupportedException();
    }

    [Fact]
    public async Task EnterAsync_AcquiresExclusiveLock_AndBlocksConcurrentHolders()
    {
        var factory = new DummyDbFactory();
        IDisposable? lease1 = null;
        IDisposable? lease2 = null;
        IDisposable? lease3 = null;
        Task<IDisposable>? task2 = null;
        Task<IDisposable>? task3 = null;

        try
        {
            // Act 1: Enter first lease
            lease1 = await MetadataWriteCoordinator.EnterAsync(factory);

            // Act 2: Directly call second and third EnterAsync without completing
            task2 = MetadataWriteCoordinator.EnterAsync(factory);
            task3 = MetadataWriteCoordinator.EnterAsync(factory);

            // Both subsequent enters must be blocked/uncompleted while lease1 is held
            Assert.False(task2.IsCompleted);
            Assert.False(task3.IsCompleted);

            // Act 3: Dispose first lease
            lease1.Dispose();
            lease1 = null;

            // Second caller now acquires lease
            lease2 = await task2.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(lease2);

            // Third caller must STILL be waiting while lease2 is held
            Assert.False(task3.IsCompleted);

            // Act 4: Dispose second lease
            lease2.Dispose();
            lease2 = null;

            // Third caller now acquires lease
            lease3 = await task3.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(lease3);
        }
        finally
        {
            lease1?.Dispose();
            lease2?.Dispose();
            lease3?.Dispose();
            await DrainLeaseTaskAsync(task2);
            await DrainLeaseTaskAsync(task3);
        }
    }

    [Fact]
    public async Task EnterAsync_CancellationWhileQueued_ThrowsAndLeavesLockIntact_AndAllowsSubsequentAcquisition()
    {
        var factory = new DummyDbFactory();
        using var cts = new CancellationTokenSource();
        IDisposable? lease1 = null;
        IDisposable? lease3 = null;
        Task<IDisposable>? queuedTask2 = null;
        Task<IDisposable>? queuedTask3 = null;

        try
        {
            // 1. Hold first lease
            lease1 = await MetadataWriteCoordinator.EnterAsync(factory);

            // 2. Queue second EnterAsync with an active CancellationToken
            queuedTask2 = MetadataWriteCoordinator.EnterAsync(factory, cts.Token);
            Assert.False(queuedTask2.IsCompleted);

            // 3. Cancel second request while actively queued
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queuedTask2.WaitAsync(TimeSpan.FromSeconds(5)));

            // 4. Assert first lease remains intact and exclusive
            queuedTask3 = MetadataWriteCoordinator.EnterAsync(factory);
            Assert.False(queuedTask3.IsCompleted);

            // 5. Release first lease; third request must acquire successfully
            lease1.Dispose();
            lease1 = null;

            lease3 = await queuedTask3.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(lease3);
        }
        finally
        {
            cts.Cancel();
            lease1?.Dispose();
            lease3?.Dispose();
            await DrainLeaseTaskAsync(queuedTask2);
            await DrainLeaseTaskAsync(queuedTask3);
        }
    }

    private static async Task DrainLeaseTaskAsync(Task<IDisposable>? task)
    {
        if (task == null) return;
        try
        {
            if (task.IsCompletedSuccessfully)
            {
                task.Result?.Dispose();
            }
            else if (!task.IsCompleted)
            {
                using var completedLease = await task.WaitAsync(TimeSpan.FromMilliseconds(500));
            }
        }
        catch { }
    }

    [Fact]
    public async Task EnterAsync_ConcurrentCallers_GuaranteesMaxConcurrencyOfOne()
    {
        var factory = new DummyDbFactory();
        int activeConcurrency = 0;
        int maxConcurrency = 0;

        var tasks = Enumerable.Range(0, 10).Select(async _ =>
        {
            using var lease = await MetadataWriteCoordinator.EnterAsync(factory);
            var current = Interlocked.Increment(ref activeConcurrency);

            // Record maximum observed concurrency
            int initialMax;
            do
            {
                initialMax = Volatile.Read(ref maxConcurrency);
                if (current <= initialMax)
                    break;
            } while (Interlocked.CompareExchange(ref maxConcurrency, current, initialMax) != initialMax);

            // Simulate small atomic work inside critical section
            await Task.Delay(10);

            Interlocked.Decrement(ref activeConcurrency);
        }).ToArray();

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, maxConcurrency);
        Assert.Equal(0, activeConcurrency);
    }

    [Fact]
    public async Task EnterAsync_DisposeIsIdempotent_ReleasesSemaphoreOnce()
    {
        var factory = new DummyDbFactory();
        var lease = await MetadataWriteCoordinator.EnterAsync(factory);

        // Multiple calls to Dispose do not throw or double-release
        lease.Dispose();
        lease.Dispose();
        lease.Dispose();

        // Immediately re-enter
        using var nextLease = await MetadataWriteCoordinator.EnterAsync(factory);
        Assert.NotNull(nextLease);
    }

    [Fact]
    public async Task EnterAsync_DifferentFactories_HaveIndependentLocks()
    {
        var factoryA = new DummyDbFactory();
        var factoryB = new DummyDbFactory();

        using var leaseA = await MetadataWriteCoordinator.EnterAsync(factoryA);

        // factoryB should NOT be blocked by factoryA
        var taskB = MetadataWriteCoordinator.EnterAsync(factoryB);
        using var leaseB = await taskB.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(leaseB);
    }
}
