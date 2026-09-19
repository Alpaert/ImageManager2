namespace ImageManager.Common.Constants;

public static class FileTypeConstants
{
    public sealed record FileTypeDefinition(string Id, string DisplayName,
        IReadOnlySet<string> Extensions, bool SupportsDimensions, bool SupportsThumbnails);
    public static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp"
    };

    public static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".flv", ".webm", ".m4v"
    };

    public static readonly HashSet<string> AllMediaExtensions;

    public static IReadOnlyList<FileTypeDefinition> SupportedTypes { get; } =
    [
        new("image", "图片", ImageExtensions, true, true),
        new("video", "视频", VideoExtensions, true, true)
    ];

    public static FileTypeDefinition? GetFileType(string path)
    {
        var extension = Path.GetExtension(path);
        return SupportedTypes.FirstOrDefault(type => type.Extensions.Contains(extension));
    }

    static FileTypeConstants()
    {
        AllMediaExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in SupportedTypes) AllMediaExtensions.UnionWith(type.Extensions);
    }

    public static bool IsImageFile(string path)
        => ImageExtensions.Contains(Path.GetExtension(path));

    public static bool IsVideoFile(string path)
        => VideoExtensions.Contains(Path.GetExtension(path));

    public static bool IsMediaFile(string path)
        => AllMediaExtensions.Contains(Path.GetExtension(path));
}
