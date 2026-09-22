using ImageManager.Common.Constants;

namespace ImageManager.App.Services;

/// <summary>Captures a selectable item in the folder/media browser.</summary>
public sealed record FolderOperationTarget(string Path, bool IsFolder);

/// <summary>
/// Pure path and enumeration helpers used by folder context-menu operations.
/// These methods only resolve a scope; they never mutate the file system.
/// </summary>
public static class FolderContentOperations
{
    /// <summary>
    /// Enumerates supported image and video files under <paramref name="path"/>.
    /// Inaccessible or missing directories produce an empty result so a stale
    /// folder card cannot make a context-menu command fail before confirmation.
    /// </summary>
    public static IReadOnlyList<string> EnumerateMediaFiles(string path, bool recursive)
    {
        if (string.IsNullOrWhiteSpace(path)) return [];

        try
        {
            var fullPath = Normalize(path);
            if (File.Exists(fullPath))
                return FileTypeConstants.IsMediaFile(fullPath) ? [fullPath] : [];
            if (!Directory.Exists(fullPath)) return [];

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                IgnoreInaccessible = true,
                ReturnSpecialDirectories = false,
                AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint
            };
            return Directory.EnumerateFiles(fullPath, "*", options)
                .Where(FileTypeConstants.IsMediaFile)
                .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                .ThenBy(file => file, StringComparer.Ordinal)
                .ToArray();
        }
        catch (ArgumentException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Returns whether <paramref name="path"/> is the same as, or a descendant
    /// of, <paramref name="parent"/>. Directory boundaries are significant:
    /// <c>C:\\Media2</c> is not inside <c>C:\\Media</c>.
    /// </summary>
    public static bool IsPathInside(string path, string parent)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(parent)) return false;

        try
        {
            var candidate = Normalize(path);
            var root = Normalize(parent);
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

    /// <summary>
    /// Validates a destination for copying or moving a folder. A folder cannot
    /// be copied/moved onto itself or into one of its descendants.
    /// </summary>
    public static bool IsSafeCopyOrMoveDestination(string source, string destination)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(destination)) return false;

        try
        {
            var sourcePath = Normalize(source);
            var destinationPath = Normalize(destination);
            return !IsPathInside(destinationPath, sourcePath);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Gets the number of supported media files in a folder scope.</summary>
    public static int GetDisplayCount(string path, bool recursive = false)
        => EnumerateMediaFiles(path, recursive).Count;

    private static string Normalize(string path)
    {
        // Path.GetFullPath("C:") is stable, but Path.GetFullPath("C:") after
        // trimming becomes "C:" which Windows interprets as the current
        // directory on that drive. Preserve that canonical drive-root token.
        if (path.Length == 2 && path[1] == Path.VolumeSeparatorChar)
            return path;
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
