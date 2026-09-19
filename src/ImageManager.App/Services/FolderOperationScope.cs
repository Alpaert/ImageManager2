namespace ImageManager.App.Services;

/// <summary>Immutable target captured before showing a folder operation dialog.</summary>
internal sealed class FolderOperationScope
{
    private readonly string? _folder;
    private readonly bool _recursive;
    private readonly string[] _files;

    private FolderOperationScope(string? folder, bool recursive, string[] files)
        => (_folder, _recursive, _files) = (folder, recursive, files);

    public static FolderOperationScope ForFolder(string path, bool recursive) =>
        new(Normalize(path), recursive, []);

    public static FolderOperationScope ForFiles(IEnumerable<string> paths) =>
        new(null, false, paths.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());

    public bool Overlaps(FolderOperationScope other)
    {
        if (_folder == null) return _files.Any(other.ContainsFile);
        if (other._folder == null) return other._files.Any(ContainsFile);
        return Same(_folder, other._folder)
            || (_recursive && IsDescendant(other._folder, _folder))
            || (other._recursive && IsDescendant(_folder, other._folder));
    }

    private bool ContainsFile(string file) => _folder == null
        ? _files.Contains(file, StringComparer.OrdinalIgnoreCase)
        : _recursive ? IsDescendant(file, _folder) : Same(Normalize(Path.GetDirectoryName(file)!), _folder);

    private static bool IsDescendant(string path, string folder) =>
        path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
