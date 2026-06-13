namespace ImageManager.Common.Constants;

public static class FileTypeConstants
{
    public static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp"
    };

    public static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".flv", ".webm", ".m4v"
    };

    public static readonly HashSet<string> AllMediaExtensions;

    static FileTypeConstants()
    {
        AllMediaExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ext in ImageExtensions) AllMediaExtensions.Add(ext);
        foreach (var ext in VideoExtensions) AllMediaExtensions.Add(ext);
    }

    public static bool IsImageFile(string path)
        => ImageExtensions.Contains(Path.GetExtension(path));

    public static bool IsVideoFile(string path)
        => VideoExtensions.Contains(Path.GetExtension(path));

    public static bool IsMediaFile(string path)
        => AllMediaExtensions.Contains(Path.GetExtension(path));
}
