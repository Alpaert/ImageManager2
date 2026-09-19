namespace ImageManager.Common.Helpers;

public static class AutoTagRuntimeState
{
    private static int _activeRuns;

    public static bool IsRunning => Volatile.Read(ref _activeRuns) > 0;

    /// <summary>Background maintenance yields at file/batch boundaries, without holding database locks.</summary>
    public static async Task WaitForIdleAsync(CancellationToken ct)
    {
        while (IsRunning)
            await Task.Delay(100, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
    }

    public static IDisposable Enter()
    {
        Interlocked.Increment(ref _activeRuns);
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Interlocked.Decrement(ref _activeRuns);
        }
    }
}
