namespace ImageManager.Core.Services;

/// <summary>
/// Tracks data version increments for image hash and metadata updates,
/// enabling search snapshots and caches to invalidate deterministically.
/// </summary>
public interface IHashDataVersionTracker
{
    /// <summary>Gets the current monotonically increasing data version.</summary>
    long CurrentVersion { get; }

    /// <summary>Increments the version counter and returns the new version.</summary>
    long IncrementVersion();
}
