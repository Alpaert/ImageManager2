namespace ImageManager.Common.Constants;

public static class FileTypeConstants
{
    public static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp"
    };

    public static readonly HashSet<string> AllMediaExtensions;

    static FileTypeConstants()
    {
        AllMediaExtensions = new HashSet<string>(
            ImageExtensions,
            StringComparer.OrdinalIgnoreCase
        );
    }

    public static bool IsImageFile(string path)
        => ImageExtensions.Contains(Path.GetExtension(path));

    public static bool IsMediaFile(string path)
        => AllMediaExtensions.Contains(Path.GetExtension(path));
}
