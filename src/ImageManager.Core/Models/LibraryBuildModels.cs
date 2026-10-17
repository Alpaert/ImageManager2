namespace ImageManager.Core.Models;

public enum LibraryBuildTarget
{
    Artist,
    Character
}

public record LibraryBuildRequest(
    string ReferenceDirectory,
    LibraryBuildTarget Target,
    int BatchSize = 8);

public record LibraryBuildProgress(
    int CurrentIndex,
    int TotalCount,
    int SkippedCount,
    int BuiltCount,
    int FailedCount,
    string CurrentItemName,
    string Stage,
    long OperationId = 0);

public record LibraryBuildResult(
    int TotalItems,
    int SkippedCount,
    int BuiltCount,
    int TotalInStore,
    bool Completed,
    bool Cancelled,
    string? ErrorMessage = null);
