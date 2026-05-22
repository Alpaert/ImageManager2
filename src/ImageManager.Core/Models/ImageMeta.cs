namespace ImageManager.Core.Models;

public class ImageMeta
{
    public long Id { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public string? FileHash { get; set; }
    public string? PerceptualHash { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public long FileSize { get; set; }
    public long LastWriteTicks { get; set; }
    public long? FolderId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public int SystemRating { get; set; } = -1;  // -1=Unknown, 0=General, 1=Sensitive, 2=Questionable, 3=Explicit

    public List<TagCount> Tags { get; set; } = new();
}
