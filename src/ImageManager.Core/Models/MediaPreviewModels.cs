namespace ImageManager.Core.Models;

public enum MediaPreviewState { Ready, NoArtwork, Failed }
public enum PreviewPersistence { Saved, MemoryOnly, NotApplicable }
public enum ArtworkOrigin { None, Image, VideoFrame, Embedded, Sidecar, FolderImage }

/// <summary>Encoded, display-sized bytes. Width/Height describe this image, not the source video.</summary>
public sealed record MediaPreviewResult(
    MediaPreviewState State,
    byte[]? Data = null,
    int Width = 0,
    int Height = 0,
    PreviewPersistence Persistence = PreviewPersistence.NotApplicable,
    ArtworkOrigin Origin = ArtworkOrigin.None,
    string? Version = null,
    string? RetryToken = null,
    string? Error = null);

public enum FolderStatisticsState { Complete, Partial, Unavailable, Scanning }

public sealed record FolderDetailsSnapshot(
    string Path,
    long DirectFileCount,
    long FileCount,
    long MediaCount,
    long DirectoryCount,
    long TotalBytes,
    FolderStatisticsState State,
    DateTimeOffset ScannedAt,
    long SkippedEntries = 0,
    string? Error = null);
