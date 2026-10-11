using ImageManager.Core.Models;

namespace ImageManager.Core.Services;

/// <summary>
/// A short-lived query scope for co-occurring tags within one search result set.
/// Implementations may keep temporary database state until the session is disposed.
/// </summary>
public interface ICoTagQuerySession : IAsyncDisposable
{
    Task<IReadOnlyList<TagCount>> QueryAsync(
        string nameFilter,
        int limit = 300,
        CancellationToken cancellationToken = default);
}
