using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ImageManager.App.Services;
using ImageManager.Common.Constants;
using ImageManager.Core.Models;
using ImageManager.Core.Services;

namespace ImageManager.App.ViewModels;

/// <summary>UI-owned detail state with one running read and one replaceable pending request.</summary>
public sealed class DetailPaneViewModel(IDetailPaneDataLoader loader, IDispatcher dispatcher,
    Func<ImageViewItem, bool>? isSourceLoadPending = null)
    : ObservableObject, IDisposable
{
    private readonly object _gate = new();
    private bool _isOpen = true, _disposed, _running;
    private double _preferredWidth = 312;
    private long _generation;
    private int _decodeWidth = 256, _selectionCount;
    private int? _preferredDecodeWidth;
    private string? _folderCoverMediaPath;
    private WeakReference<byte[]>? _folderCardCover;
    private CancellationTokenSource? _active;
    private Pending? _pending;
    private WeakReference<ImageViewItem>? _source;
    private bool _waitingForSource;
    private Timer? _sourceWatch;
    private string? _retryToken;
    private MediaPreviewResult? _preview;
    private sealed record Pending(long Generation, DetailPaneLoadRequest Request, TaskCompletionSource? Completion);

    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            lock (_gate)
            {
                if (_disposed || !SetProperty(ref _isOpen, value)) return;
                Invalidate();
                ReleaseOwner();
                Unsubscribe();
                _folderCardCover = null;
                _preview = null;
                _retryToken = null;
                PreviewStatusText = null;
                IsLoading = IsPreviewLoading = IsSavingPreview = false;
                if (value && CurrentPath != null) { CreateOwner(); Queue(!IsFolder || _folderCoverMediaPath != null); }
                Notify();
            }
        }
    }
    public double PreferredWidth
    {
        get => _preferredWidth;
        set => SetProperty(ref _preferredWidth, double.IsFinite(value) ? Math.Clamp(value, 248, 520) : 312);
    }
    public string? CurrentPath { get; private set; }
    public bool IsFolder { get; private set; }
    public bool HasItem => CurrentPath != null;
    public ImageViewItem? ThumbnailItem { get; private set; }
    public IReadOnlyList<string> Tags { get; private set; } = Array.Empty<string>();
    public string FileName => CurrentPath == null ? "未选择文件" :
        IsFolder ? Path.GetFileName(Path.TrimEndingDirectorySeparator(CurrentPath)) is { Length: > 0 } name
            ? name : CurrentPath : Path.GetFileName(CurrentPath);
    public string FormatText => IsFolder ? "文件夹" :
        CurrentPath == null ? "未知" : Path.GetExtension(CurrentPath).TrimStart('.').ToUpperInvariant();
    public string DimensionsText { get; private set; } = "未知";
    public string FileSizeText { get; private set; } = "未知";
    public string ModifiedText { get; private set; } = "未知";
    public string FolderPath => CurrentPath == null ? string.Empty : Path.GetDirectoryName(CurrentPath) ?? string.Empty;
    public bool IsMusic => !IsFolder && CurrentPath != null && FileTypeConstants.IsMusicFile(CurrentPath);
    public bool IsVideo => !IsFolder && CurrentPath != null && FileTypeConstants.IsVideoFile(CurrentPath);
    public bool CanEditTags => HasItem && !IsFolder && !IsMusic;
    public bool IsLoading { get; private set; }
    public bool IsPreviewLoading { get; private set; }
    public bool IsSavingPreview { get; private set; }
    public bool CanRetryPreview => HasItem && !IsFolder && !IsPreviewLoading &&
        (_preview?.State == MediaPreviewState.Failed ||
         (_preview?.Persistence == PreviewPersistence.MemoryOnly && _retryToken == null));
    public bool CanRetrySave => _retryToken != null && !IsLoading && !IsSavingPreview;
    public string? ErrorText { get; private set; }
    public string? PreviewStatusText { get; private set; }
    public string? ArtworkSourceText => _preview?.Origin switch
    {
        ArtworkOrigin.VideoFrame => "视频封面帧",
        ArtworkOrigin.Embedded => "内嵌封面",
        ArtworkOrigin.Sidecar => "外置封面",
        ArtworkOrigin.FolderImage => "文件夹图片",
        _ => null
    };
    public FolderDetailsSnapshot? Folder { get; private set; }
    public string FolderFileCount => FormatFolderCount(Folder?.FileCount);
    public string FolderMediaCount => FormatFolderCount(Folder?.MediaCount);
    public string FolderDirectoryCount => FormatFolderCount(Folder?.DirectoryCount);

    private string FormatFolderCount(long? count) => count == null || Folder?.State == FolderStatisticsState.Unavailable
        ? "未知" : FolderTotalPrefix + count.Value.ToString("N0");
    private string FolderTotalPrefix => Folder?.State == FolderStatisticsState.Scanning ? "至少 " : string.Empty;
    public string SelectionHint => IsFolder ? "当前文件夹" :
        _selectionCount > 1 ? $"已选择 {_selectionCount} 项 · 当前文件" : "当前文件";

    public void Show(string? path, ImageViewItem? source, int decodeWidth, int selectionCount)
    {
        lock (_gate)
        {
            if (_disposed) return;
            path = string.IsNullOrWhiteSpace(path) ? null : path;
            var previousDecodeWidth = _decodeWidth;
            _decodeWidth = Math.Max(1, decodeWidth);
            _selectionCount = Math.Max(0, selectionCount);
            if (!IsFolder && StringComparer.OrdinalIgnoreCase.Equals(CurrentPath, path))
            {
                var wasWaiting = _waitingForSource;
                BindSource(source);
                if (IsOpen && path != null && ThumbnailItem?.ThumbnailData is not { Length: > 0 } &&
                    ((_preferredDecodeWidth == null && previousDecodeWidth != _decodeWidth) ||
                     (wasWaiting && !_waitingForSource))) Queue(true);
                OnPropertyChanged(nameof(SelectionHint));
                return;
            }
            ResetTarget(path, isFolder: false);
            if (IsOpen && path != null)
            {
                CreateOwner();
                if (source != null && StringComparer.OrdinalIgnoreCase.Equals(source.FilePath, path))
                    Tags = source.Tags.ToArray();
                BindSource(source);
                Queue(loader.UsesVersionedPreview || ThumbnailItem?.ThumbnailData == null);
            }
            Notify();
        }
    }

    public void ShowFolder(string path, byte[]? cover = null, string? coverMediaPath = null)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (IsFolder && StringComparer.OrdinalIgnoreCase.Equals(CurrentPath, path))
            {
                bool changedCover = !StringComparer.OrdinalIgnoreCase.Equals(_folderCoverMediaPath, coverMediaPath);
                bool newCardCover = cover is { Length: > 0 } && (_folderCardCover == null ||
                    !_folderCardCover.TryGetTarget(out var previousCover) || !ReferenceEquals(previousCover, cover));
                _folderCoverMediaPath = coverMediaPath;
                if (IsOpen && ThumbnailItem != null)
                {
                    if (newCardCover)
                    {
                        _folderCardCover = new(cover!);
                        SetBytes(cover!);
                    }
                    else if (changedCover) ThumbnailItem.ThumbnailData = null;
                    if (changedCover)
                        Queue(cover is not { Length: > 0 } && coverMediaPath != null, loadMetadata: IsLoading);
                    else if (newCardCover && IsPreviewLoading)
                        Queue(false, loadMetadata: IsLoading);
                }
                return;
            }

            ResetTarget(path, isFolder: true);
            _folderCoverMediaPath = coverMediaPath;
            if (IsOpen)
            {
                CreateOwner();
                if (cover is { Length: > 0 })
                {
                    _folderCardCover = new(cover);
                    SetBytes(cover);
                }
                Queue(cover is not { Length: > 0 } && coverMediaPath != null);
            }
            Notify();
        }
    }

    private void ResetTarget(string? path, bool isFolder)
    {
        Invalidate();
        Unsubscribe();
        ReleaseOwner();
        CurrentPath = path;
        IsFolder = isFolder;
        Folder = null;
        _folderCoverMediaPath = null;
        _folderCardCover = null;
        _preview = null;
        _retryToken = null;
        Tags = Array.Empty<string>();
        DimensionsText = FileSizeText = ModifiedText = "未知";
        ErrorText = PreviewStatusText = null;
        IsLoading = IsPreviewLoading = IsSavingPreview = false;
    }

    public void RetryPreview() => Refresh(reloadThumbnail: true);

    public void SetPreviewDecodeWidth(int width)
    {
        lock (_gate)
        {
            if (_disposed || width == _preferredDecodeWidth) return;
            var previousWidth = _preferredDecodeWidth ?? _decodeWidth;
            _preferredDecodeWidth = width;
            if (!IsOpen || CurrentPath == null || width == previousWidth ||
                (IsFolder && _folderCoverMediaPath == null)) return;

            // Existing covers stay visible while a new size is resolved. A settled
            // metadata read is retained, and an unfinished one is requeued only when needed.
            Queue(true, loadMetadata: IsLoading);
        }
    }

    public Task RetrySaveAsync()
    {
        lock (_gate)
        {
            if (_disposed || !IsOpen || IsFolder || CurrentPath == null || !CanRetrySave)
                return Task.CompletedTask;
            return Queue(false, _retryToken);
        }
    }

    public void Refresh(bool reloadThumbnail = false)
    {
        lock (_gate)
        {
            if (_disposed || !IsOpen || CurrentPath == null) return;
            if (!IsFolder && reloadThumbnail)
            {
                if (!_waitingForSource) Unsubscribe();
                if (ThumbnailItem != null) ThumbnailItem.ThumbnailData = null;
                _preview = null;
                _retryToken = null;
            }
            // A metadata invalidation must not lose an unfinished thumbnail request.
            Queue(IsFolder ? _folderCoverMediaPath != null && (reloadThumbnail ||
                ThumbnailItem?.ThumbnailData is not { Length: > 0 }) :
                reloadThumbnail || IsPreviewLoading || (_preview == null &&
                    ThumbnailItem?.ThumbnailData is not { Length: > 0 }));
        }
    }

    private Task Queue(bool thumbnail, string? retryToken = null, bool loadMetadata = true)
    {
        Invalidate();
        // PageManager already owns this decode. Wait for its bytes instead of racing it.
        var completion = retryToken == null ? null : new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var width = _preferredDecodeWidth ?? _decodeWidth;
        _pending = new Pending(_generation, new(CurrentPath!, width, thumbnail && !_waitingForSource,
            IsFolder, retryToken,
            loadMetadata && retryToken == null, _folderCoverMediaPath),
            completion);
        IsSavingPreview = retryToken != null;
        IsLoading = loadMetadata && !IsSavingPreview;
        IsPreviewLoading = thumbnail && (IsFolder || loader.UsesVersionedPreview);
        if (IsLoading) ErrorText = null;
        if (IsPreviewLoading && !IsFolder) PreviewStatusText = IsVideo ? "正在读取视频封面…" :
            IsMusic ? "正在读取音乐封面…" : "正在读取预览…";
        if (IsSavingPreview) PreviewStatusText = "正在保存封面…";
        Notify();
        if (!_running)
        {
            _running = true;
            _ = Task.Run(PumpAsync);
        }
        return completion?.Task ?? Task.CompletedTask;
    }

    private async Task PumpAsync()
    {
        while (true)
        {
            Pending work;
            CancellationTokenSource cancellation;
            lock (_gate)
            {
                if (_pending == null) { _running = false; return; }
                work = _pending;
                _pending = null;
                _active = cancellation = new CancellationTokenSource();
            }
            DetailPaneData? result = null;
            MediaPreviewResult? publishedPreview = null;
            string? error = null;
            var token = cancellation.Token;
            try
            {
                result = await loader.LoadAsync(work.Request, preview => dispatcher.InvokeAsync(() =>
                {
                    lock (_gate)
                    {
                        if (!CanPublish(work, token)) return;
                        if (!IsPreviewResultCurrent(work, preview)) return;
                        ApplyPreview(preview);
                        publishedPreview = preview;
                        Notify();
                    }
                }), progress =>
                {
                    try
                    {
                        dispatcher.Post(() =>
                        {
                            lock (_gate)
                            {
                                if (!CanPublish(work, token) || !IsLoading ||
                                    !work.Request.IsFolder || progress.State != FolderStatisticsState.Scanning ||
                                    !StringComparer.OrdinalIgnoreCase.Equals(work.Request.Path, progress.Path)) return;
                                Folder = progress;
                                FileSizeText = FolderTotalPrefix + FormatSize(progress.TotalBytes);
                                Notify();
                            }
                        });
                    }
                    catch (InvalidOperationException) { /* Dispatcher has shut down. */ }
                }, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception ex) { error = $"无法读取文件信息：{ex.Message}"; }
            try
            {
                await dispatcher.InvokeAsync(() =>
                {
                    lock (_gate)
                    {
                        if (!CanPublish(work, cancellation.Token)) return;
                        if (work.Request.RetryToken != null)
                        {
                            IsSavingPreview = false;
                            if (result?.Media is { } saved && !IsPreviewResultCurrent(work, saved))
                                ApplySaveResult(new(MediaPreviewState.Failed, Error: "源文件已变化，请重新读取预览"), null);
                            else
                                ApplySaveResult(result?.Media, error);
                            Notify();
                            return;
                        }
                        if (result != null)
                        {
                            if (work.Request.LoadMetadata) ApplyMetadata(result);
                            if (result.Media != null && !ReferenceEquals(publishedPreview, result.Media) &&
                                IsPreviewResultCurrent(work, result.Media))
                                ApplyPreview(result.Media);
                            if (result.Media == null && ThumbnailItem != null && work.Request.LoadThumbnail &&
                                result.ThumbnailData is { Length: > 0 } bytes &&
                                (!work.Request.LoadMetadata || ThumbnailItem.ThumbnailData == null))
                                SetBytes(bytes);
                        }
                        else if (work.Request.LoadMetadata) ErrorText = error;
                        IsLoading = false;
                        IsPreviewLoading = work.Request.LoadThumbnail == false && _waitingForSource &&
                            loader.UsesVersionedPreview;
                        if (ThumbnailItem != null)
                        {
                            ThumbnailItem.IsLoading = _waitingForSource;
                            ThumbnailItem.HasNoPreview = !_waitingForSource && ThumbnailItem.ThumbnailData == null;
                        }
                        Notify();
                    }
                }).ConfigureAwait(false);
            }
            catch (Exception) { /* Dispatcher can stop during application shutdown. */ }
            finally
            {
                lock (_gate) { if (ReferenceEquals(_active, cancellation)) _active = null; }
                cancellation.Dispose();
                work.Completion?.TrySetResult();
            }
        }
    }

    private bool CanPublish(Pending work, CancellationToken token) =>
        !_disposed && IsOpen && !token.IsCancellationRequested && work.Generation == _generation &&
        work.Request.IsFolder == IsFolder && StringComparer.OrdinalIgnoreCase.Equals(CurrentPath, work.Request.Path);

    private bool IsPreviewResultCurrent(Pending work, MediaPreviewResult preview)
    {
        if (preview.Version is not { } version) return true;
        var path = work.Request.IsFolder ? work.Request.FolderCoverMediaPath : work.Request.Path;
        return path != null && loader.IsKnownPreviewVersionCurrent(path, version);
    }

    private void ApplyPreview(MediaPreviewResult result)
    {
        if (IsFolder)
        {
            if (result is { State: MediaPreviewState.Ready, Data.Length: > 0 }) SetBytes(result.Data);
            else if (ThumbnailItem != null) ThumbnailItem.ThumbnailData = null;
            IsPreviewLoading = false;
            return;
        }
        // State can outlive the visible bitmap, so do not hold an extra reference to its encoded bytes.
        _preview = result with { Data = null };
        _retryToken = result.RetryToken;
        IsPreviewLoading = false;
        if (result is { State: MediaPreviewState.Ready, Data.Length: > 0 })
            SetBytes(result.Data);
        else if (ThumbnailItem != null)
            ThumbnailItem.ThumbnailData = null;
        PreviewStatusText = result.State switch
        {
            MediaPreviewState.Ready when result.Persistence == PreviewPersistence.MemoryOnly =>
                result.RetryToken == null ? "封面已显示，暂未保存。请重新读取预览后再试。" : "封面已显示，暂未保存",
            MediaPreviewState.NoArtwork => "没有音乐封面",
            MediaPreviewState.Failed => result.Error ?? "无法读取预览",
            _ => null
        };
    }

    private void ApplyMetadata(DetailPaneData result)
    {
        Tags = result.Tags.ToArray();
        DimensionsText = IsMusic ? "不适用" : result.Width > 0 && result.Height > 0
            ? $"{result.Width} × {result.Height}" : "未知";
        FileSizeText = result.FileSize is long size ? FormatSize(size) : "未知";
        ModifiedText = result.Modified?.ToString("yyyy-MM-dd HH:mm:ss") ?? "未知";
        Folder = result.Folder;
        if (Folder is { } folder)
        {
            if (folder.State != FolderStatisticsState.Unavailable)
                FileSizeText = FormatSize(folder.TotalBytes);
            ModifiedText = folder.ScannedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
        }
        ErrorText = Folder?.State switch
        {
            FolderStatisticsState.Partial => "部分目录或文件无法统计，已显示可读取结果。",
            FolderStatisticsState.Unavailable => Folder.Error ?? "无法访问文件夹",
            _ => result.ErrorText
        };
    }

    private void ApplySaveResult(MediaPreviewResult? result, string? error)
    {
        if (result?.Persistence == PreviewPersistence.Saved)
        {
            _retryToken = null;
            if (_preview != null)
                _preview = _preview with { Persistence = PreviewPersistence.Saved, RetryToken = null };
            PreviewStatusText = "已保存封面";
            return;
        }
        _retryToken = result == null ? _retryToken : result.RetryToken;
        PreviewStatusText = result?.Error ?? error ?? "未能保存封面，请重新读取预览";
        if (_retryToken == null && _preview != null)
            _preview = _preview with { State = MediaPreviewState.Failed };
    }

    private void BindSource(ImageViewItem? source)
    {
        if (!IsOpen || CurrentPath == null) return;
        Unsubscribe();
        if (source == null || !StringComparer.OrdinalIgnoreCase.Equals(source.FilePath, CurrentPath)) return;
        TryUseSourceBytes(source);
        _source = new(source);
        source.PropertyChanged += SourceChanged;
        bool waitingForImage = !loader.UsesVersionedPreview ||
            (!IsMusic && !IsVideo && source.ThumbnailData is not { Length: > 0 });
        if (waitingForImage && ThumbnailItem?.ThumbnailData is not { Length: > 0 } && source.IsLoading)
        {
            _waitingForSource = true;
            // The source is deliberately weak: an evicted card must not be pinned by this pane.
            // GC is the only signal when eviction produces no property changes.
            _sourceWatch = new Timer(_ =>
            {
                try { dispatcher.Post(CheckSourceWait); }
                catch (InvalidOperationException) { /* Dispatcher has shut down. */ }
            }, null, 250, 250);
        }
    }
    private void SourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(ImageViewItem.ThumbnailData) or nameof(ImageViewItem.ThumbnailVersion)
            or nameof(ImageViewItem.IsLoading))) return;
        if (sender is not ImageViewItem source) return;
        var generation = _generation;
        dispatcher.Post(() =>
        {
            lock (_gate)
            {
                if (!_disposed && IsOpen && generation == _generation && _source != null &&
                    _source.TryGetTarget(out var current) && ReferenceEquals(current, source))
                {
                    if (!TryUseSourceBytes(source)) CheckSourceWait();
                }
            }
        });
    }
    private bool TryUseSourceBytes(ImageViewItem source)
    {
        if (source.ThumbnailData is not { Length: > 0 } bytes) return false;
        if (loader.UsesVersionedPreview) return false;
        SetBytes(bytes);
        return true;
    }
    private void CheckSourceWait()
    {
        lock (_gate)
        {
            if (_disposed || !IsOpen || !_waitingForSource) return;
            if (_source != null && _source.TryGetTarget(out var source))
            {
                if (source.ThumbnailData is { Length: > 0 })
                {
                    if (loader.UsesVersionedPreview)
                    {
                        EndSourceWait();
                        Queue(true);
                    }
                    else
                        TryUseSourceBytes(source);
                    return;
                }
                // IsLoading can remain true on a retained card after its viewport request was cancelled.
                // The host checks actual queued/running work, including cancelled native work not yet exited.
                if (source.IsLoading && (isSourceLoadPending == null || isSourceLoadPending(source))) return;
            }
            EndSourceWait();
            if (CurrentPath != null && ThumbnailItem?.ThumbnailData is not { Length: > 0 }) Queue(true);
        }
    }
    private void EndSourceWait()
    {
        _waitingForSource = false;
        _sourceWatch?.Dispose();
        _sourceWatch = null;
    }
    private void SetBytes(byte[] bytes)
    {
        if (ThumbnailItem == null) return;
        EndSourceWait();
        ThumbnailItem.ThumbnailData = bytes;
        ThumbnailItem.HasNoPreview = false;
        ThumbnailItem.IsLoading = false;
    }
    private void Unsubscribe()
    {
        EndSourceWait();
        if (_source != null && _source.TryGetTarget(out var source)) source.PropertyChanged -= SourceChanged;
        _source = null;
    }
    private void CreateOwner() => ThumbnailItem = new ImageViewItem { FilePath = CurrentPath!, FileName = FileName, IsLoading = true };
    private void ReleaseOwner()
    {
        if (ThumbnailItem != null) ThumbnailItem.ThumbnailData = null;
        ThumbnailItem = null;
    }
    private void Invalidate()
    {
        ++_generation;
        _pending?.Completion?.TrySetResult();
        _pending = null;
        _active?.Cancel();
    }
    private void Notify() => OnPropertyChanged(string.Empty);
    private static string FormatSize(long size) => size >= 1024 * 1024 ? $"{size / (1024d * 1024):0.##} MB" : size >= 1024 ? $"{size / 1024d:0.##} KB" : $"{size} B";
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            Invalidate();
            Unsubscribe();
            ReleaseOwner();
            _preview = null;
            _retryToken = null;
            IsLoading = IsPreviewLoading = IsSavingPreview = false;
            Notify();
        }
    }
}
