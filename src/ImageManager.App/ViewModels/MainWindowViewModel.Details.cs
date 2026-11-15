using ImageManager.Common.Helpers;
using ImageManager.App.Models;
using ImageManager.Core.Services;

namespace ImageManager.App.ViewModels;

public partial class MainWindowViewModel
{
    public DetailPaneViewModel DetailsPane { get; }

    private string? _preferredDetailFilePath;
    private string? _preferredDetailFolderPath;
    private string? _regionDetailFilePath;
    private readonly IFolderDetailsService? _folderDetailsService;
    private int _detailsSelectionRefreshQueued;
    private bool _detailsDisposed;
    private long _detailsSettingsVersion;
    private long _detailsSettingsSavedVersion;
    private Task _detailsSettingsSaveTask = Task.CompletedTask;

    /// <summary>Called after a gallery gesture commits selection, never at drag press.</summary>
    public void FocusDetailFile(string path)
    {
        if (IsRegionSearchActive)
        {
            if (DetailsPane.IsOpen && IsFileSelected(path))
                _regionDetailFilePath = path;
            QueueDetailsSelectionRefresh();
            return;
        }

        _preferredDetailFolderPath = null;
        if (IsFileSelected(path)) _preferredDetailFilePath = path;
        QueueDetailsSelectionRefresh();
    }

    public void FocusDetailFolder(string? path)
    {
        _preferredDetailFolderPath = path;
        QueueDetailsSelectionRefresh();
    }

    /// <summary>Only called after a complete result snapshot has been committed, never on a page change.</summary>
    internal void ReconcileSelectionWithPublishedResults()
    {
        var valid = new HashSet<string>(ActiveFileList, StringComparer.OrdinalIgnoreCase);
        var changed = _selectedFilePaths.RemoveWhere(path => !valid.Contains(path)) > 0;
        _pendingSelectionRestorePaths?.IntersectWith(valid);
        if (changed) ApplySelectionToRealizedItems();
        QueueDetailsSelectionRefresh();
    }

    public IReadOnlyList<string> GetValidSelectedFilePaths()
    {
        if (IsDisplayFilterBusy) return Array.Empty<string>();
        var valid = new HashSet<string>(ActiveFileList, StringComparer.OrdinalIgnoreCase);
        return _selectedFilePaths.Where(valid.Contains).ToArray();
    }

    public void RefreshFolderDetails(string path)
    {
        _folderDetailsService?.Invalidate(path);
        if (DetailsPane.IsFolder && StringComparer.OrdinalIgnoreCase.Equals(DetailsPane.CurrentPath, path))
            DetailsPane.Refresh(reloadThumbnail: true);
    }

    private void QueueDetailsSelectionRefresh()
    {
        if (_detailsDisposed || Interlocked.Exchange(ref _detailsSelectionRefreshQueued, 1) != 0)
            return;
        _dispatcher.Post(() =>
        {
            Interlocked.Exchange(ref _detailsSelectionRefreshQueued, 0);
            if (!_detailsDisposed) UpdateDetailsSelection();
        });
    }

    private void UpdateDetailsSelection()
    {
        if (IsDisplayFilterBusy) return;
        var files = ActiveFileList;
        bool IsValid(string? path) => path is not null && IsFileSelected(path) &&
            files.Contains(path, StringComparer.OrdinalIgnoreCase);

        string? path;
        if (IsRegionSearchActive)
        {
            if (!DetailsPane.IsOpen || !IsValid(_regionDetailFilePath))
                _regionDetailFilePath = null;
            path = _regionDetailFilePath;
        }
        else
        {
            path = IsValid(_preferredDetailFilePath) ? _preferredDetailFilePath :
                IsValid(DetailsPane.CurrentPath) ? DetailsPane.CurrentPath :
                files.FirstOrDefault(_selectedFilePaths.Contains);
            _preferredDetailFilePath = path;
        }

        OnPropertyChanged(nameof(SelectedFilePaths));
        if (path is null)
        {
            if (IsRegionSearchActive)
            {
                DetailsPane.Show(null, null, _pageManager.ThumbnailDecodeWidth, _selectedFilePaths.Count);
                return;
            }

            var card = FolderItems.FirstOrDefault(item => item.IsSelected &&
                StringComparer.OrdinalIgnoreCase.Equals(item.FolderPath, _preferredDetailFolderPath));
            var folder = card?.FolderPath ?? (!IsShowingSearchResult && !string.IsNullOrWhiteSpace(CurrentFolder) ? CurrentFolder : null);
            if (folder != null)
            {
                var coverPath = card?.CoverMediaPath ?? _allFiles
                    .Where(file => StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(file)?.TrimEnd('\\', '/'), folder.TrimEnd('\\', '/')))
                    .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                DetailsPane.ShowFolder(folder, card?.ThumbnailData, coverPath);
            }
            else DetailsPane.Show(null, null, _pageManager.ThumbnailDecodeWidth, _selectedFilePaths.Count);
            return;
        }

        // Only already materialized models; enumerating the item source creates every item.
        var pagedSource = Images.FirstOrDefault(item =>
            string.Equals(item.FilePath, path, StringComparison.OrdinalIgnoreCase));
        var continuousSource = ContinuousImageItemSource?.GetAllCachedItems().FirstOrDefault(item =>
            string.Equals(item.FilePath, path, StringComparison.OrdinalIgnoreCase));
        var source = IsPagedDisplay ? pagedSource ?? continuousSource : continuousSource ?? pagedSource;
        DetailsPane.Show(path, source, _pageManager.ThumbnailDecodeWidth, _selectedFilePaths.Count);
    }

    /// <summary>Invalidate only the active detail, leaving gallery selection and layout alone.</summary>
    public void RefreshDetailsForFiles(IEnumerable<string>? affectedPaths = null, bool reloadThumbnail = false)
    {
        var path = DetailsPane.CurrentPath;
        if (_detailsDisposed || path is null ||
            (affectedPaths is not null && !affectedPaths.Contains(path, StringComparer.OrdinalIgnoreCase)))
            return;
        _dispatcher.Post(() =>
        {
            if (_detailsDisposed || !string.Equals(path, DetailsPane.CurrentPath, StringComparison.OrdinalIgnoreCase))
                return;
            if (DetailsPane.IsFolder)
            {
                _folderDetailsService?.Invalidate(path);
                DetailsPane.Refresh();
                return;
            }
            if (!IsFileSelected(path) || !ActiveFileList.Contains(path, StringComparer.OrdinalIgnoreCase))
                UpdateDetailsSelection();
            else
                DetailsPane.Refresh(reloadThumbnail);
        });
    }

    private void SyncDetailsPaneSettings()
    {
        DetailsPane.PreferredWidth = AppSettings.DetailsPaneWidth;
        AppSettings.DetailsPaneWidth = DetailsPane.PreferredWidth;
        if (!IsRegionSearchActive)
            DetailsPane.IsOpen = AppSettings.DetailsPaneOpen;
        QueueDetailsSelectionRefresh();
    }

    internal void SetRegionDetailsPaneOpen(bool open)
    {
        _regionDetailFilePath = null;
        DetailsPane.Show(null, null, _pageManager.ThumbnailDecodeWidth, _selectedFilePaths.Count);
        DetailsPane.IsOpen = open;
    }

    public void SetDetailsPaneOpen(bool open)
    {
        if (_detailsDisposed || (DetailsPane.IsOpen == open && AppSettings.DetailsPaneOpen == open)) return;
        if (open && !DetailsPane.IsOpen)
        {
            // Bind the current gallery source before starting a reopened request.
            // Otherwise the old hidden path can start a duplicate cold thumbnail load.
            DetailsPane.Show(null, null, _pageManager.ThumbnailDecodeWidth, _selectedFilePaths.Count);
            DetailsPane.IsOpen = true;
            UpdateDetailsSelection();
        }
        else
        {
            DetailsPane.IsOpen = open;
        }
        AppSettings.DetailsPaneOpen = open;
        QueueDetailsSettingsSave();
    }

    public void SetDetailsPaneWidth(double width)
    {
        if (_detailsDisposed) return;
        DetailsPane.PreferredWidth = width;
        if (AppSettings.DetailsPaneWidth == DetailsPane.PreferredWidth) return;
        AppSettings.DetailsPaneWidth = DetailsPane.PreferredWidth;
        QueueDetailsSettingsSave();
    }

    private void QueueDetailsSettingsSave()
    {
        _detailsSettingsVersion++;
        if (_detailsSettingsSaveTask.IsCompleted)
            _detailsSettingsSaveTask = SaveDetailsSettingsLoopAsync();
    }

    private async Task SaveDetailsSettingsLoopAsync()
    {
        try
        {
            while (_detailsSettingsSavedVersion != _detailsSettingsVersion)
            {
                var version = _detailsSettingsVersion;
                await Task.Delay(250);
                if (version != _detailsSettingsVersion) continue;
                await SaveSettingsAsync();
                _detailsSettingsSavedVersion = version;
            }
        }
        catch (Exception ex)
        {
            // No unobserved task failure; a close flush retries a failed save.
            AppLogger.Error($"保存预览栏设置失败: {ex.Message}");
            await _dispatcher.InvokeAsync(() => StatusText = "预览栏设置保存失败，关闭前将重试");
        }
    }

    public async Task FlushDetailsPaneSettingsAsync()
    {
        await _detailsSettingsSaveTask;
        if (_detailsSettingsSavedVersion == _detailsSettingsVersion) return;
        await SaveSettingsAsync();
        _detailsSettingsSavedVersion = _detailsSettingsVersion;
    }

    private void ShutdownDetailsPane()
    {
        if (_detailsDisposed) return;
        _detailsDisposed = true;
        DetailsPane.Dispose();
    }
}
