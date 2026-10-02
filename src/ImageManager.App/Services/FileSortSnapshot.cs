using ImageManager.App.ViewModels;

namespace ImageManager.App.Services;

[Flags]
public enum FileSortKnownFields
{
    None = 0,
    FileSize = 1,
    LastWriteTicks = 2,
    Resolution = 4
}

public readonly record struct FileSortEntry(
    string Path,
    string FileName,
    string Directory,
    long FileSize,
    long LastWriteTicks,
    int Width,
    int Height,
    long PixelCount,
    FileSortKnownFields KnownFields)
{
    public static FileSortEntry Unknown(string path) => new(
        path,
        System.IO.Path.GetFileName(path),
        System.IO.Path.GetDirectoryName(path) ?? string.Empty,
        0,
        0,
        0,
        0,
        0,
        FileSortKnownFields.None);
}

/// <summary>Immutable metadata keys for one published file-list content version.</summary>
public sealed class FileSortSnapshot
{
    private readonly IReadOnlyList<FileSortEntry> _entries;
    private readonly IReadOnlyDictionary<string, FileSortEntry> _byPath;

    public FileSortSnapshot(
        string? rootFolder,
        bool recursive,
        int mutationVersion,
        IEnumerable<FileSortEntry> entries)
    {
        RootFolder = rootFolder ?? string.Empty;
        Recursive = recursive;
        MutationVersion = mutationVersion;
        _entries = entries.ToArray();
        _byPath = _entries.ToDictionary(entry => entry.Path, StringComparer.OrdinalIgnoreCase);
    }

    public string RootFolder { get; }
    public bool Recursive { get; }
    public int MutationVersion { get; }
    public IReadOnlyList<FileSortEntry> Entries => _entries;
    public IReadOnlyDictionary<string, FileSortEntry> EntriesByPath => _byPath;

    public bool Covers(IEnumerable<string> paths, string? rootFolder, bool recursive, int mutationVersion)
    {
        if (MutationVersion != mutationVersion || Recursive != recursive ||
            !string.Equals(RootFolder, rootFolder ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            return false;
        return paths.All(path => _byPath.ContainsKey(path));
    }

    public bool TryGet(string path, out FileSortEntry entry) => _byPath.TryGetValue(path, out entry);

    public IReadOnlyDictionary<string, (int Width, int Height)> GetKnownDimensions(IEnumerable<string> paths)
    {
        var result = new Dictionary<string, (int Width, int Height)>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (_byPath.TryGetValue(path, out var entry) &&
                (entry.KnownFields & FileSortKnownFields.Resolution) != 0)
                result[path] = (entry.Width, entry.Height);
        }
        return result;
    }

    public List<string> SortPaths(
        IReadOnlyList<string> paths,
        ImageSortOrder order,
        bool folderGrouped,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entries = new FileSortEntry[paths.Count];
        for (var index = 0; index < paths.Count; index++)
        {
            var path = paths[index];
            entries[index] = _byPath.TryGetValue(path, out var entry) ? entry : FileSortEntry.Unknown(path);
        }

        var comparisonCount = 0;
        try
        {
            Array.Sort(entries, (left, right) =>
            {
                if ((++comparisonCount & 0xFFF) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                return FileSortComparer.Compare(left, right, order, folderGrouped);
            });
        }
        catch (InvalidOperationException exception)
            when (exception.InnerException is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();

        var result = new List<string>(entries.Length);
        foreach (var entry in entries)
            result.Add(entry.Path);
        return result;
    }
}

/// <summary>Pure in-memory ordering. This type deliberately has no filesystem APIs.</summary>
public static class FileSortComparer
{
    public static int Compare(
        FileSortEntry left,
        FileSortEntry right,
        ImageSortOrder order,
        bool folderGrouped)
    {
        if (folderGrouped)
        {
            var directory = string.Compare(left.Directory, right.Directory, StringComparison.OrdinalIgnoreCase);
            if (directory != 0)
                return directory;
        }

        var result = order switch
        {
            ImageSortOrder.FileNameAsc => string.Compare(left.FileName, right.FileName, StringComparison.OrdinalIgnoreCase),
            ImageSortOrder.FileNameDesc => string.Compare(right.FileName, left.FileName, StringComparison.OrdinalIgnoreCase),
            ImageSortOrder.ModifiedAsc => CompareMetric(left.LastWriteTicks, right.LastWriteTicks,
                (left.KnownFields & FileSortKnownFields.LastWriteTicks) != 0,
                (right.KnownFields & FileSortKnownFields.LastWriteTicks) != 0, false),
            ImageSortOrder.ModifiedDesc => CompareMetric(left.LastWriteTicks, right.LastWriteTicks,
                (left.KnownFields & FileSortKnownFields.LastWriteTicks) != 0,
                (right.KnownFields & FileSortKnownFields.LastWriteTicks) != 0, true),
            ImageSortOrder.FileSizeAsc => CompareMetric(left.FileSize, right.FileSize,
                (left.KnownFields & FileSortKnownFields.FileSize) != 0,
                (right.KnownFields & FileSortKnownFields.FileSize) != 0, false),
            ImageSortOrder.FileSizeDesc => CompareMetric(left.FileSize, right.FileSize,
                (left.KnownFields & FileSortKnownFields.FileSize) != 0,
                (right.KnownFields & FileSortKnownFields.FileSize) != 0, true),
            ImageSortOrder.ResolutionAsc => CompareMetric(left.PixelCount, right.PixelCount,
                (left.KnownFields & FileSortKnownFields.Resolution) != 0,
                (right.KnownFields & FileSortKnownFields.Resolution) != 0, false),
            ImageSortOrder.ResolutionDesc => CompareMetric(left.PixelCount, right.PixelCount,
                (left.KnownFields & FileSortKnownFields.Resolution) != 0,
                (right.KnownFields & FileSortKnownFields.Resolution) != 0, true),
            _ => 0
        };

        return result != 0
            ? result
            : string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase);
    }

    private static int CompareMetric(long left, long right, bool leftKnown, bool rightKnown, bool descending)
    {
        if (!leftKnown || !rightKnown)
            return leftKnown == rightKnown ? 0 : leftKnown ? -1 : 1;
        return descending ? right.CompareTo(left) : left.CompareTo(right);
    }
}
