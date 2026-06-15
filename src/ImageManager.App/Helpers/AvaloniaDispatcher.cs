using ImageManager.Core.Services;

namespace ImageManager.App.Helpers;

/// <summary>
/// Wraps Avalonia's <see cref="Avalonia.Threading.Dispatcher.UIThread"/> behind <see cref="IDispatcher"/>.
/// </summary>
public class AvaloniaDispatcher : IDispatcher
{
    public void Post(Action action)
        => Avalonia.Threading.Dispatcher.UIThread.Post(action);

    public Task InvokeAsync(Action action)
        => Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(action).GetTask();

    public Task InvokeAsync(Func<Task> callback)
        => Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(callback);
}
