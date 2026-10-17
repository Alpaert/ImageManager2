namespace ImageManager.Core.Models;

/// <summary>
/// Represents an optimistic-concurrency verified item for updating image hashes.
/// Matches on Id, ExpectedPath, ExpectedLastWriteTicks, and ExpectedFileSize.
/// If any check fails, the update will not match and will be reported as stale.
/// </summary>
public sealed record ImageHashUpdateItem(
    long Id,
    string ExpectedPath,
    long ExpectedLastWriteTicks,
    long ExpectedFileSize,
    string? FileHash,
    string? PerceptualHash,
    int? Width,
    int? Height,
    int HashStatus);

/// <summary>
/// Result of an ImageHash update batch operation.
/// </summary>
public sealed record ImageHashUpdateBatchResult(
    int UpdatedCount,
    int StaleCount,
    IReadOnlyList<long> StaleIds);
