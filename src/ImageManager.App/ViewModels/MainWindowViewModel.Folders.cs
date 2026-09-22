using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using ImageManager.App.Models;
using ImageManager.Common.Constants;
using ImageManager.Common.Helpers;

namespace ImageManager.App.ViewModels;

public partial class MainWindowViewModel
{
    private readonly SemaphoreSlim _folderThumbnailGate = new(4, 4);
    private readonly SemaphoreSlim _folderMetadataGate = new(8, 8);
    private int _folderThumbnailRequestVersion;
    private int _folderItemsRequestVersion;
    private int _folderNavigationVersion;
    private int _folderThumbnailDecodeWidth;
    [ObservableProperty]
    private ObservableCollection<FolderViewItem> _folderItems = new();

    public bool HasFolderItems => FolderItems.Count > 0;
    public bool ShowFolderSection => !ShowAllSubfolders && HasFolderItems;

    public IReadOnlyList<FolderTreeNode> FolderBreadcrumbs
    {
        get
        {
            if (string.IsNullOrWhiteSpace(CurrentFolder))
                return Array.Empty<FolderTreeNode>();

            var result = new List<FolderTreeNode>();
            var importedRoot = FindImportedRoot(CurrentFolder);
            var current = CurrentFolder;
            while (!string.IsNullOrWhiteSpace(current))
            {
                var node = FindNodeByPath(current) ?? new FolderTreeNode
                {
                    Path = current,
                    DisplayName = Path.GetFileName(current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                };
                result.Insert(0, node);
                if (importedRoot != null &&
                    string.Equals(NormalizePath(current), NormalizePath(importedRoot.Path), StringComparison.OrdinalIgnoreCase))
                    break;
                var parent = Directory.GetParent(current)?.FullName;
                if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                    break;
                current = parent ?? string.Empty;
            }
            return result;
        }
    }

    public bool HasBreadcrumbs => FolderBreadcrumbs.Count > 0;

    private FolderTreeNode? FindImportedRoot(string folderPath)
    {
        var normalizedFolder = NormalizePath(folderPath);
        return FolderTree
            .Where(node => IsSameOrDescendant(normalizedFolder, NormalizePath(node.Path)))
            .OrderByDescending(node => NormalizePath(node.Path).Length)
            .FirstOrDefault();
    }

    private static bool IsSameOrDescendant(string path, string root)
    {
        if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return false;
        return root.EndsWith(Path.DirectorySeparatorChar) ||
               root.EndsWith(Path.AltDirectorySeparatorChar) ||
               path.Length > root.Length &&
               (path[root.Length] == Path.DirectorySeparatorChar || path[root.Length] == Path.AltDirectorySeparatorChar);
    }

    partial void OnFolderItemsChanged(ObservableCollection<FolderViewItem> value)
    {
        OnPropertyChanged(nameof(HasFolderItems));
        OnPropertyChanged(nameof(ShowFolderSection));
        OnPropertyChanged(nameof(HasBreadcrumbs));
    }

    public async Task RefreshFolderItemsAsync(CancellationToken cancellationToken = default)
    {
        if (!cancellationToken.CanBeCanceled)
            cancellationToken = CurrentFolderWorkToken;
        var folder = CurrentFolder;
        var requestVersion = Interlocked.Increment(ref _folderItemsRequestVersion);
        Interlocked.Increment(ref _folderThumbnailRequestVersion);
        await PublishFolderItemsAsync(new ObservableCollection<FolderViewItem>());
        if (ShowAllSubfolders || string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            await PublishFolderBreadcrumbsAsync();
            return;
        }

        var directories = await Task.Run(() =>
        {
            try
            {
                return Directory.EnumerateDirectories(folder)
                    .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch
            {
                return new List<string>();
            }
        }, cancellationToken);

        if (!IsCurrentFolderRequest(folder, requestVersion))
            return;

        var items = directories.Select(directory => new FolderViewItem
        {
            FolderPath = directory,
            DisplayName = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        }).ToList();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (IsCurrentFolderRequest(folder, requestVersion))
                FolderItems = new ObservableCollection<FolderViewItem>(items);
        }).GetTask();

        if (!IsCurrentFolderRequest(folder, requestVersion))
            return;

        _folderThumbnailDecodeWidth = GetFolderThumbnailDecodeWidth();
        _ = PopulateFolderMetadataAsync(items, folder, requestVersion, cancellationToken);
        await PublishFolderBreadcrumbsAsync();
    }

    private async Task PopulateFolderMetadataAsync(
        IReadOnlyList<FolderViewItem> items,
        string folder,
        int requestVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.WhenAll(items.Select(item => PopulateFolderMetadataAsync(
                item, folder, requestVersion, cancellationToken)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AppLogger.Warn($"Folder card metadata load failed: {ex.Message}");
        }
    }

    private async Task PopulateFolderMetadataAsync(
        FolderViewItem item,
        string folder,
        int requestVersion,
        CancellationToken cancellationToken)
    {
        var enteredGate = false;
        try
        {
            await _folderMetadataGate.WaitAsync(cancellationToken);
            enteredGate = true;
            if (!IsCurrentFolderRequest(folder, requestVersion))
                return;
            var media = await Task.Run(() => EnumerateDirectMedia(item.FolderPath), cancellationToken);
            if (!IsCurrentFolderRequest(folder, requestVersion))
                return;

            var coverMediaPath = media.FirstOrDefault();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!IsCurrentFolderRequest(folder, requestVersion))
                    return;
                item.MediaCount = media.Count;
                item.CoverMediaPath = coverMediaPath;
            }).GetTask();

            if (!string.IsNullOrWhiteSpace(coverMediaPath) && IsCurrentFolderRequest(folder, requestVersion))
                _ = LoadFolderCardThumbnailAsync(item, folder,
                    Volatile.Read(ref _folderThumbnailRequestVersion),
                    _folderThumbnailDecodeWidth, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AppLogger.Warn($"Folder card metadata failed: {item.FolderPath} | {ex.Message}");
        }
        finally
        {
            if (enteredGate)
                _folderMetadataGate.Release();
        }
    }

    private bool IsCurrentFolderRequest(string folder, int requestVersion) =>
        requestVersion == Volatile.Read(ref _folderItemsRequestVersion) &&
        !ShowAllSubfolders &&
        string.Equals(folder, CurrentFolder, StringComparison.OrdinalIgnoreCase);

    private Task PublishFolderItemsAsync(ObservableCollection<FolderViewItem> items) =>
        Dispatcher.UIThread.InvokeAsync(() => FolderItems = items).GetTask();

    private Task PublishFolderBreadcrumbsAsync() =>
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            OnPropertyChanged(nameof(FolderBreadcrumbs));
            OnPropertyChanged(nameof(HasBreadcrumbs));
        }).GetTask();

    private static List<string> EnumerateDirectMedia(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly)
                .Where(FileTypeConstants.IsMediaFile)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    public Task RefreshFolderCardThumbnailsForCurrentScaleAsync()
    {
        var decodeWidth = GetFolderThumbnailDecodeWidth();
        if (decodeWidth == _folderThumbnailDecodeWidth || FolderItems.Count == 0)
            return Task.CompletedTask;

        _folderThumbnailDecodeWidth = decodeWidth;
        var requestVersion = Interlocked.Increment(ref _folderThumbnailRequestVersion);
        foreach (var item in FolderItems)
            item.ThumbnailData = null;
        return LoadFolderCardThumbnailsAsync(FolderItems.ToList(), CurrentFolder, requestVersion, decodeWidth, CurrentFolderWorkToken);
    }

    private int GetFolderThumbnailDecodeWidth()
    {
        var desired = Math.Max(120, ThumbnailBaseWidth);
        return desired switch
        {
            <= 180 => 160,
            <= 280 => 240,
            <= 400 => 320,
            _ => 480
        };
    }

    private Task LoadFolderCardThumbnailsAsync(
        IReadOnlyList<FolderViewItem> items,
        string folder,
        int requestVersion,
        int decodeWidth,
        CancellationToken cancellationToken)
    {
        return Task.WhenAll(items.Select(item => LoadFolderCardThumbnailAsync(
            item, folder, requestVersion, decodeWidth, cancellationToken)));
    }

    private async Task LoadFolderCardThumbnailAsync(
        FolderViewItem item,
        string folder,
        int requestVersion,
        int decodeWidth,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(item.CoverMediaPath))
            return;

        var enteredGate = false;
        try
        {
            await _folderThumbnailGate.WaitAsync(cancellationToken);
            enteredGate = true;
            if (requestVersion != Volatile.Read(ref _folderThumbnailRequestVersion) ||
                !string.Equals(folder, CurrentFolder, StringComparison.OrdinalIgnoreCase))
                return;

            await Dispatcher.UIThread.InvokeAsync(() => item.IsLoadingThumbnail = true).GetTask();
            // Cache probing includes synchronous disk I/O; keep it off the UI
            // continuation while a large folder grid is being populated.
            var result = await Task.Run(
                () => _thumbCache.GetOrCreateThumbnailAsync(item.CoverMediaPath, decodeWidth, cancellationToken),
                cancellationToken);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!cancellationToken.IsCancellationRequested &&
                    requestVersion == Volatile.Read(ref _folderThumbnailRequestVersion) &&
                    string.Equals(folder, CurrentFolder, StringComparison.OrdinalIgnoreCase))
                    item.ThumbnailData = result.Data;
            }).GetTask();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AppLogger.Warn($"Folder card thumbnail failed: {item.FolderPath} | {ex.Message}");
        }
        finally
        {
            Dispatcher.UIThread.Post(() => item.IsLoadingThumbnail = false);
            if (enteredGate)
                _folderThumbnailGate.Release();
        }
    }

    public async Task OpenFolderFromCardAsync(FolderViewItem item)
    {
        if (item == null || !Directory.Exists(item.FolderPath))
        {
            StatusText = "文件夹不存在或已被移动";
            await RefreshFolderItemsAsync();
            return;
        }

        await OpenFolderWithTreeSyncAsync(item.FolderPath);
    }

    public Task OpenFolderFromCardAsync(string folderPath)
    {
        var item = FolderItems.FirstOrDefault(candidate =>
            string.Equals(candidate.FolderPath, folderPath, StringComparison.OrdinalIgnoreCase));
        return item != null
            ? OpenFolderFromCardAsync(item)
            : OpenFolderPathAsync(folderPath);
    }

    private async Task OpenFolderPathAsync(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            StatusText = "文件夹不存在或已被移动";
            return;
        }
        await OpenFolderWithTreeSyncAsync(folderPath);
    }

    public async Task OpenBreadcrumbAsync(FolderTreeNode breadcrumb)
    {
        if (breadcrumb == null || string.IsNullOrWhiteSpace(breadcrumb.Path))
            return;
        await OpenFolderWithTreeSyncAsync(breadcrumb.Path);
    }

    private async Task OpenFolderWithTreeSyncAsync(string folderPath)
    {
        // Loading the browsing surface has priority. Tree expansion can involve
        // filesystem enumeration, so it runs concurrently and does not delay a
        // double-click or breadcrumb navigation.
        var navigationVersion = Interlocked.Increment(ref _folderNavigationVersion);
        bool IsCurrentNavigation() =>
            navigationVersion == Volatile.Read(ref _folderNavigationVersion) &&
            string.Equals(CurrentFolder, folderPath, StringComparison.OrdinalIgnoreCase);

        AppSettings.LastFolder = folderPath;
        var loadTask = LoadFolderAsync(folderPath);
        var treeTask = ExpandAndHighlightFolderAsync(folderPath, syncSelection: false, isCurrent: IsCurrentNavigation);
        await loadTask;
        if (!IsCurrentNavigation())
            return;

        var node = await treeTask;
        if (!IsCurrentNavigation())
            return;
        if (node == null)
        {
            await RefreshFolderTreeAsync();
            if (!IsCurrentNavigation())
                return;
            node = await ExpandAndHighlightFolderAsync(folderPath, syncSelection: false, isCurrent: IsCurrentNavigation);
        }
        if (node != null && IsCurrentNavigation())
        {
            _isProgrammaticFolderSelection = true;
            SelectedFolderNode = node;
            _isProgrammaticFolderSelection = false;
            TreeScrollToNodeRequested?.Invoke(node);
        }
        if (IsCurrentNavigation())
            _ = SaveSettingsAsync();
    }

    /// <summary>Keeps tags, hashes and vectors attached after an in-app folder move.</summary>
    public async Task UpdateMovedMediaPathsAsync(IReadOnlyDictionary<string, string> movedPaths)
    {
        foreach (var (oldPath, newPath) in movedPaths)
        {
            var metadata = await _metaRepo.GetByPathAsync(oldPath);
            if (metadata?.FolderId is long folderId)
                await _metaRepo.UpdateFilePathAsync(metadata.Id, newPath, folderId);
        }
    }
}
