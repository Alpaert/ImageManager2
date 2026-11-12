using ImageManager.Common.Helpers;
using ImageManager.Core.Models;
using ImageManager.Core.Services;

namespace ImageManager.App.Services;

/// <summary>Resumable cleanup for one immutable folder/file-list snapshot.</summary>
internal sealed class FolderMetadataCleanup(
    IImageMetaRepository repository, string folder, bool recursive, IReadOnlyCollection<string> existingFiles)
{
    private readonly HashSet<string> _existingFiles = new(existingFiles, StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<ImageSortMetadata>? _rows;
    private int _nextIndex;

    public async Task RunAsync(CancellationToken cancellationToken, Action ensureCurrent)
    {
        ensureCurrent();
        cancellationToken.ThrowIfCancellationRequested();
        // This projection does not hydrate ImageMeta or its Tags collection.
        _rows ??= await repository.GetSortMetadataByFolderAsync(folder, recursive, cancellationToken).ConfigureAwait(false);
        for (; _nextIndex < _rows.Count; _nextIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ensureCurrent();
            var path = _rows[_nextIndex].FilePath;
            if (_existingFiles.Contains(path) || File.Exists(path)) continue;

            // Check again just before a write. A single non-cancellable UPDATE may finish
            // after interaction resumes; the next record must not start after cancellation.
            cancellationToken.ThrowIfCancellationRequested();
            ensureCurrent();
            if (File.Exists(path)) continue;
            try { await repository.SetFolderIdAsync(path, 0L).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { AppLogger.Warn($"CleanMeta unlink failed: {path} — {ex.Message}"); }
        }
        cancellationToken.ThrowIfCancellationRequested();
    }
}
