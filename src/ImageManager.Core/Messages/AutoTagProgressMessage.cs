namespace ImageManager.Core.Messages;

/// <summary>
/// Published by the auto-tag pipeline when progress updates.
/// Consumed by StatusBarViewModel and AutoTagReview UI.
/// </summary>
public sealed class AutoTagProgressMessage
{
    public string Phase { get; }
    public int Processed { get; }
    public int Total { get; }
    public string StatusText { get; }

    public AutoTagProgressMessage(string phase, int processed, int total, string statusText)
    {
        Phase = phase;
        Processed = processed;
        Total = total;
        StatusText = statusText;
    }
}
