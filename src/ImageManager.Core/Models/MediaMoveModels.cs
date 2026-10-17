namespace ImageManager.Core.Models;

public enum MediaMoveItemStatus
{
    Success,
    Failed,
    Skipped,
    Cancelled,
    CompensationFailed
}

public sealed record MediaMoveItemResult(
    string SourcePath,
    string? TargetPath,
    MediaMoveItemStatus Status,
    string? Error = null);

public sealed record MediaMoveBatchResult(
    IReadOnlyList<MediaMoveItemResult> Items,
    int TotalCount,
    int SuccessCount,
    int FailedCount,
    int SkippedCount,
    int CancelledCount,
    int CompensationFailedCount)
{
    public bool HasFailures => FailedCount > 0 || CompensationFailedCount > 0;
}

public sealed record MediaMoveProgress(
    int Processed,
    int Total,
    string CurrentStage,
    string? CurrentPath = null);

public sealed record MediaMoveRequest(
    IReadOnlyList<string> SourcePaths,
    string TargetDirectory,
    string? OperationId = null);

public sealed record DirectoryMoveRequest(
    string SourceDirectory,
    string TargetParentDirectory,
    string? OperationId = null);
