using ImageManager.Core.Models;
using ImageManager.Core.Services;

namespace ImageManager.App.Services;

public sealed class FileSortSnapshotBuilder
{
    public async Task<FileSortSnapshot> BuildAsync(
        IReadOnlyList<string> paths,
        string? rootFolder,
        bool recursive,
        int mutationVersion,
        IImageMetaRepository repository,
        bool useFolderQuery,
        IReadOnlyDictionary<string, ImageMeta>? cachedMetadata = null,
        FileSortSnapshot? previous = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(repository);
        cancellationToken.ThrowIfCancellationRequested();

        if (previous?.Covers(paths, rootFolder, recursive, mutationVersion) == true)
            return previous;

        IReadOnlyList<ImageSortMetadata> metadata;
        if (useFolderQuery && !string.IsNullOrWhiteSpace(rootFolder))
            metadata = await repository.GetSortMetadataByFolderAsync(rootFolder!, recursive, cancellationToken)
                .ConfigureAwait(false);
        else
            metadata = await repository.GetSortMetadataByPathsAsync(paths, cancellationToken).ConfigureAwait(false);

        var byPath = metadata.ToDictionary(item => item.FilePath, StringComparer.OrdinalIgnoreCase);
        var entries = new FileSortEntry[paths.Count];
        for (var index = 0; index < paths.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = paths[index];
            byPath.TryGetValue(path, out var persisted);
            ImageMeta? cached = null;
            if (cachedMetadata is not null)
                cachedMetadata.TryGetValue(path, out cached);
            var oldEntry = default(FileSortEntry);
            previous?.TryGet(path, out oldEntry);

            var fileSize = persisted is { FileSize: > 0 } ? persisted.FileSize
                : cached?.FileSize > 0 ? cached.FileSize : oldEntry.FileSize;
            var lastWriteTicks = persisted is { LastWriteTicks: > 0 } ? persisted.LastWriteTicks
                : cached?.LastWriteTicks > 0 ? cached.LastWriteTicks : oldEntry.LastWriteTicks;
            var width = persisted is { Width: > 0 } ? persisted.Width
                : cached?.Width > 0 ? cached.Width : oldEntry.Width;
            var height = persisted is { Height: > 0 } ? persisted.Height
                : cached?.Height > 0 ? cached.Height : oldEntry.Height;

            // A single FileInfo supplies both fallback fields. Resolution intentionally
            // remains unknown when the lightweight metadata is missing.
            if (fileSize <= 0 || lastWriteTicks <= 0)
            {
                try
                {
                    var fileInfo = new FileInfo(path);
                    if (fileSize <= 0 && fileInfo.Exists) fileSize = fileInfo.Length;
                    if (lastWriteTicks <= 0 && fileInfo.Exists) lastWriteTicks = fileInfo.LastWriteTimeUtc.Ticks;
                }
                catch { }
            }

            var known = FileSortKnownFields.None;
            if (fileSize > 0) known |= FileSortKnownFields.FileSize;
            if (lastWriteTicks > 0) known |= FileSortKnownFields.LastWriteTicks;
            var pixelCount = width > 0 && height > 0 ? (long)width * height : 0;
            if (pixelCount > 0) known |= FileSortKnownFields.Resolution;
            entries[index] = new FileSortEntry(
                path,
                Path.GetFileName(path),
                Path.GetDirectoryName(path) ?? string.Empty,
                fileSize,
                lastWriteTicks,
                width,
                height,
                pixelCount,
                known);
        }

        return new FileSortSnapshot(rootFolder, recursive, mutationVersion, entries);
    }
}
