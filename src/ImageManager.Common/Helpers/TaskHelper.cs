namespace ImageManager.Common.Helpers;

/// <summary>
/// Provides safe fire-and-forget task execution with error logging.
/// Use this instead of raw <c>_ = Task.Run(...)</c> to avoid silent exception drops.
/// </summary>
public static class TaskHelper
{
    /// <summary>
    /// Fire-and-forget a background task with automatic exception logging.
    /// <see cref="OperationCanceledException"/> is silently ignored (expected on cancellation).
    /// All other exceptions are logged via <see cref="AppLogger.Error"/>.
    /// </summary>
    /// <param name="taskFactory">Async operation to run on the thread pool.</param>
    /// <param name="context">Human-readable description for error logs (e.g. "PageManager.TrimCache").</param>
    public static void FireAndForget(Func<Task> taskFactory, string context)
    {
        _ = Task.Run(async () =>
        {
            try { await taskFactory(); }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AppLogger.Error($"[FireAndForget:{context}] {ex}");
            }
        });
    }

    /// <summary>
    /// Fire-and-forget an action on the thread pool with automatic exception logging.
    /// </summary>
    public static void FireAndForget(Action action, string context)
    {
        _ = Task.Run(() =>
        {
            try { action(); }
            catch (Exception ex)
            {
                AppLogger.Error($"[FireAndForget:{context}] {ex}");
            }
        });
    }
}
