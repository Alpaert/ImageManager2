using ImageManager.Common.Constants;

namespace ImageManager.Common.Helpers;

public static class PathHelper
{
    public static bool IsImageFile(string path)
    {
        return FileTypeConstants.IsImageFile(path);
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
