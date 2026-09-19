namespace ImageManager.Core.Models;

/// <summary>A file hashed outside the database transaction, with an optional missing old path.</summary>
public sealed record AutoTagFileRegistration(ImageMeta Metadata, long? MovedImageId = null, string? PreviousPath = null);

public sealed record AutoTagPreparationResult(
    List<(long Id, string FilePath)> Images, int Total, int Skipped, int Failed);
