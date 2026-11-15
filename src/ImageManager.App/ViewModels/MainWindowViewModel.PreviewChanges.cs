using ImageManager.Common.Constants;
using ImageManager.Common.Helpers;

namespace ImageManager.App.ViewModels;

public partial class MainWindowViewModel
{
    private readonly object _previewChangesGate = new();
    private readonly HashSet<string> _previewChangedPaths = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _previewChangesCts;
    private bool _previewChangesOverflow;

    private void OnFolderFileChanged(object sender, FileSystemEventArgs e) => QueuePreviewInvalidation(e.FullPath);

    private void QueuePreviewInvalidation(string path)
    {
        _folderDetailsService?.Invalidate(path);
        CancellationToken token;
        lock (_previewChangesGate)
        {
            if (_detailsDisposed) return;
            if (_previewChangedPaths.Count < 256) _previewChangedPaths.Add(path);
            else _previewChangesOverflow = true;
            _previewChangesCts?.Cancel();
            _previewChangesCts?.Dispose();
            _previewChangesCts = new();
            token = _previewChangesCts.Token;
        }
        RunBackgroundSafe("preview-content-change", token, async ct =>
        {
            await Task.Delay(500, ct);
            string[] changed;
            bool overflow;
            lock (_previewChangesGate)
            {
                ct.ThrowIfCancellationRequested();
                changed = _previewChangedPaths.ToArray();
                overflow = _previewChangesOverflow;
                _previewChangedPaths.Clear();
                _previewChangesOverflow = false;
            }
            // Later file events may cancel the debounce, but must not discard this drained batch.
            // Navigation/shutdown still cancel the running work through the folder lifetime.
            ct = CurrentFolderWorkToken;

            var targets = new HashSet<string>(changed.Where(FileTypeConstants.IsMediaFile), StringComparer.OrdinalIgnoreCase);
            var directories = changed.Select(Path.GetDirectoryName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            await _dispatcher.InvokeAsync(() =>
            {
                if (ct.IsCancellationRequested || _detailsDisposed) return;
                var materialized = Images.Concat(ContinuousImageItemSource?.GetAllCachedItems() ?? []);
                foreach (var item in materialized)
                    if (overflow || (item.IsMusic && directories.Contains(Path.GetDirectoryName(item.FilePath))))
                        targets.Add(item.FilePath);
                if (!DetailsPane.IsFolder && DetailsPane.CurrentPath is { } current &&
                    (overflow || (FileTypeConstants.IsMusicFile(current) && directories.Contains(Path.GetDirectoryName(current)))))
                    targets.Add(current);
            });

            await Task.Run(() =>
            {
                foreach (var target in targets)
                {
                    ct.ThrowIfCancellationRequested();
                    _thumbCache.InvalidateThumbnail(target);
                }
            }, ct);
            await _dispatcher.InvokeAsync(() =>
            {
                if (ct.IsCancellationRequested || _detailsDisposed) return;
                var items = GetMaterializedThumbnailItems(targets).ToList();
                foreach (var item in items)
                {
                    item.ThumbnailVersion = null;
                    item.ThumbnailData = null;
                    item.IsLoaded = false;
                    item.HasNoPreview = false;
                }
                if (DetailsPane.IsFolder && DetailsPane.CurrentPath is { } folder)
                    RefreshFolderDetails(folder);
                else RefreshDetailsForFiles(targets, reloadThumbnail: true);
                _pageManager.LoadThumbnailsForItems(items);
                foreach (var card in FolderItems)
                {
                    if (card.CoverMediaPath is not { } cover || !targets.Contains(cover)) continue;
                    card.ThumbnailData = null;
                    _ = LoadFolderCardThumbnailAsync(card, CurrentFolder,
                        Volatile.Read(ref _folderThumbnailRequestVersion), _folderThumbnailDecodeWidth, CurrentFolderWorkToken);
                }
            });
        });
    }

    private void StopPreviewChangeNotifications()
    {
        lock (_previewChangesGate)
        {
            _previewChangesCts?.Cancel();
            _previewChangesCts?.Dispose();
            _previewChangesCts = null;
            _previewChangedPaths.Clear();
            _previewChangesOverflow = false;
        }
    }
}
