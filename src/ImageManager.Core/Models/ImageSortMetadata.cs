namespace ImageManager.Core.Models;

/// <summary>Lightweight persisted fields used to build an in-memory file sorting snapshot.</summary>
public sealed record ImageSortMetadata
{
    public ImageSortMetadata()
    {
    }

    public ImageSortMetadata(
        string filePath,
        long fileSize,
        long lastWriteTicks,
        int width,
        int height)
    {
        FilePath = filePath;
        FileSize = fileSize;
        LastWriteTicks = lastWriteTicks;
        Width = width;
        Height = height;
    }

    public string FilePath { get; init; } = string.Empty;
    public long FileSize { get; init; }
    public long LastWriteTicks { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
}
