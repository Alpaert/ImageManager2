namespace ImageManager.Common.Helpers;

public static class PathHelper
{
    private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp" };

    public static bool IsImageFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ImageExtensions.Contains(ext);
    }

    public static string GetNonConflictingPath(string targetPath)
    {
        if (!File.Exists(targetPath))
            return targetPath;

        var dir = Path.GetDirectoryName(targetPath) ?? Environment.CurrentDirectory;
        var name = Path.GetFileNameWithoutExtension(targetPath);
        var ext = Path.GetExtension(targetPath);

        int index = 1;
        string newPath;
        do
        {
            newPath = Path.Combine(dir, $"{name} ({index}){ext}");
            index++;
        } while (File.Exists(newPath));

        return newPath;
    }

    public static string NormalizeFolderPath(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
               + Path.DirectorySeparatorChar;
    }
}
