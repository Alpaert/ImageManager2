namespace ImageManager.Core.Services;

/// <summary>
/// Abstraction over UI-thread dispatcher so Infrastructure services
/// can marshal callbacks to the UI thread without depending on Avalonia.
/// </summary>
public interface IDispatcher
{
    /// <summary>Post an action to the UI thread (fire-and-forget).</summary>
    void Post(Action action);

    /// <summary>Invoke an action on the UI thread asynchronously.</summary>
    Task InvokeAsync(Action action);

    /// <summary>Invoke an async operation on the UI thread.</summary>
    Task InvokeAsync(Func<Task> callback);
}
