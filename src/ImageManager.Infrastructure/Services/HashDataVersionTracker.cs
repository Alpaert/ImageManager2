using ImageManager.Core.Services;

namespace ImageManager.Infrastructure.Services;

/// <summary>
/// Thread-safe, monotonically increasing tracker for hash and metadata data versions.
/// </summary>
public sealed class HashDataVersionTracker : IHashDataVersionTracker
{
    private long _version;

    public long CurrentVersion => Interlocked.Read(ref _version);

    public long IncrementVersion() => Interlocked.Increment(ref _version);
}
