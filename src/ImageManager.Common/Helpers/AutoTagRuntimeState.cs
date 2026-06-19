namespace ImageManager.Common.Helpers;

public static class AutoTagRuntimeState
{
    private static int _activeRuns;

    public static bool IsRunning => Volatile.Read(ref _activeRuns) > 0;

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
