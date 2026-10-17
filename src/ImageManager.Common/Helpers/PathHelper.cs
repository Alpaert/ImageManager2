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

    public static bool IsPathInside(string path, string parent)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(parent)) return false;

        try
        {
            var candidate = NormalizePathForComparison(path);
            var root = NormalizePathForComparison(parent);
            if (string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase)) return true;

            var separator = root.EndsWith(Path.DirectorySeparatorChar) || root.EndsWith(Path.AltDirectorySeparatorChar)
                ? string.Empty
                : Path.DirectorySeparatorChar.ToString();
            return candidate.StartsWith(root + separator, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static bool IsSafeCopyOrMoveDestination(string source, string destination)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(destination)) return false;

        try
        {
            var sourcePath = NormalizePathForComparison(source);
            var destinationPath = NormalizePathForComparison(destination);
            return !IsPathInside(destinationPath, sourcePath);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string NormalizePathForComparison(string path)
    {
        if (path.Length == 2 && path[1] == Path.VolumeSeparatorChar)
            return path;
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
