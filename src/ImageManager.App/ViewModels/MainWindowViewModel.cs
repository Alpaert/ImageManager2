using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageManager.App.Services;
using ImageManager.Common.Constants;
using ImageManager.Core.Models;
using ImageManager.Core.Services;
using ImageManager.Infrastructure.Caching;
using ImageManager.Common.Helpers;
using ImageManager.Infrastructure.Hashing;
using ImageManager.Infrastructure.Imaging;
using Microsoft.Extensions.DependencyInjection;

namespace ImageManager.App.ViewModels;

public enum OrientationFilter { All, Landscape, Portrait }

public enum ImageSortOrder
{
    FileNameAsc, FileNameDesc,
    ModifiedAsc, ModifiedDesc,
    FileSizeAsc, FileSizeDesc,
    ResolutionAsc, ResolutionDesc
}

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly ISettingsRepository _settingsRepo;
    private readonly IFolderRepository _folderRepo;
    private readonly IImageMetaRepository _metaRepo;
    private readonly ITagRepository _tagRepo;
    private readonly ISimilarImageService _similarService;
    private readonly IDuplicateService _duplicateService;
    private readonly ThumbnailCacheService _thumbCache;

    // Tag counts cache
    private List<TagCount>? _cachedTagCounts;
    private DateTime _tagCountsCacheTime = DateTime.MinValue;
    private readonly TimeSpan _tagCountsCacheDuration = TimeSpan.FromMinutes(5);
    private readonly object _tagCountsCacheLock = new();

    // ==================== Settings ====================
    [ObservableProperty] private AppSettings _appSettings = new();

    // Observable mirror properties for UI bindings (AppSettings is POCO, no INPC)
    [ObservableProperty] private string _waterfallMode = "None";
    [ObservableProperty] private bool _showFileName = true;
    [ObservableProperty] private bool _showTags = true;
    [ObservableProperty] private bool _showOrientation = true;
    [ObservableProperty] private string _thumbnailBorderColor = "#FF808080";
    [ObservableProperty] private string _thumbnailBackgroundColor = "#CCFFFFFF";
    [ObservableProperty] private double _thumbnailOpacity = 1.0;
    [ObservableProperty] private bool _keepPadding = true;
    [ObservableProperty] private double _cornerRadiusDip;

    public Thickness ThumbnailPadding => KeepPadding ? new Thickness(3) : new Thickness(0);
    public CornerRadius ThumbnailCornerRadius => new CornerRadius(CornerRadiusDip);
    public double ThumbnailBorderThickness => KeepPadding ? 1 : 0;
    public Stretch ThumbnailImageStretch => KeepPadding ? Stretch.Uniform : Stretch.UniformToFill;
    public CornerRadius ThumbnailInnerCornerRadius =>
        new CornerRadius(Math.Max(0, CornerRadiusDip - (KeepPadding ? 3 : 0)));
    public CornerRadius ThumbnailSelectionCornerRadius =>
        new CornerRadius(CornerRadiusDip -3);

    public void SyncUISettingsFromAppData()
    {
        WaterfallMode = AppSettings.WaterfallMode;
        // Restore zoom for the current mode
        ZoomTick = AppSettings.WaterfallMode switch
        {
            "Vertical" => AppSettings.VerticalZoomLevel,
            "Horizontal" => AppSettings.HorizontalZoomLevel,
            _ => AppSettings.GridZoomLevel
        };
        ShowFileName = AppSettings.ShowThumbnailFileName;
        ShowTags = AppSettings.ShowThumbnailTags;
        ShowOrientation = AppSettings.ShowThumbnailOrientation;
        ThumbnailBorderColor = AppSettings.ThumbnailBorderColor;
        ThumbnailBackgroundColor = AppSettings.ThumbnailBackgroundColor;
        ThumbnailOpacity = AppSettings.ThumbnailOpacity;
        KeepPadding = AppSettings.ThumbnailNoTextKeepPadding;
        CornerRadiusDip = AppSettings.ThumbnailCornerRadius;
    }

    /// <summary>Save current zoom to AppSettings for the given mode</summary>
    private void SaveZoomForMode(string? mode)
    {
        switch (mode)
        {
            case "Vertical": AppSettings.VerticalZoomLevel = ZoomTick; break;
            case "Horizontal": AppSettings.HorizontalZoomLevel = ZoomTick; break;
            default: AppSettings.GridZoomLevel = ZoomTick; break;
        }
    }

    /// <summary>Switch waterfall mode, saving old zoom and restoring new mode's zoom</summary>
    public void SwitchWaterfallMode(string newMode)
    {
        SaveZoomForMode(WaterfallMode);
        WaterfallMode = newMode;
        ZoomTick = newMode switch
        {
            "Vertical" => AppSettings.VerticalZoomLevel,
            "Horizontal" => AppSettings.HorizontalZoomLevel,
            _ => AppSettings.GridZoomLevel
        };
    }

    // ==================== Folder Panel ====================
    [ObservableProperty] private ObservableCollection<FolderTreeNode> _folderTree = new();
    [ObservableProperty] private FolderTreeNode? _selectedFolderNode;
    [ObservableProperty] private string _folderSearchText = string.Empty;
    [ObservableProperty] private ObservableCollection<FolderTreeNode> _folderSearchSuggestions = new();
    [ObservableProperty] private bool _isFolderSearchPopupOpen;
    [ObservableProperty] private int _searchScope; // 0=current folder, 1=recursive
    [ObservableProperty] private bool _showAllSubfolders;
    private int _currentResultIndex;
    public string SearchResultInfo => IsShowingSearchResult && _tagSearch.SearchResultFiles.Count > 0
        ? $"找到 {_tagSearch.SearchResultFiles.Count} 张相似图片  第 {_currentResultIndex + 1}/{_tagSearch.SearchResultFiles.Count}"
        : "";
    public bool HasSearchResults => _tagSearch.SearchResultFiles.Count > 0;
    public event Action? ScrollToSelectedRequested;
    public event Action<FolderTreeNode>? TreeScrollToNodeRequested;

    private List<string> GetSearchScopeFiles()
    {
        if (SearchScope == 0 || string.IsNullOrEmpty(CurrentFolder))
            return _allFiles;
        return GetImageFilesRecursive(CurrentFolder);
    }

    private static List<string> GetImageFilesRecursive(string root)
    {
        var exts = FileTypeConstants.AllMediaExtensions;
        try
        {
            return Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                .Where(f => exts.Contains(Path.GetExtension(f)))
                .ToList();
        }
        catch { return new List<string>(); }
    }

    private FolderTreeNode? FindNodeByPath(string path)
    {
        foreach (var root in FolderTree)
        {
            var found = FindNodeRecursive(root, path);
            if (found != null) return found;
        }
        return null;
    }

    private static FolderTreeNode? FindNodeRecursive(FolderTreeNode node, string path)
    {
        if (string.Equals(node.Path, path, StringComparison.OrdinalIgnoreCase))
            return node;
        foreach (var child in node.Children)
        {
            var found = FindNodeRecursive(child, path);
            if (found != null) return found;
        }
        return null;
    }

    // ==================== Image Display ====================
    [ObservableProperty] private ObservableCollection<ImageViewItem> _images = new();
    [ObservableProperty] private string _currentFolder = string.Empty;

    // ==================== Paging ====================
    private List<string> _allFiles = new();
    [ObservableProperty] private int _currentPage;
    [ObservableProperty] private int _totalPages;
    [ObservableProperty] private ObservableCollection<int> _pageNumbers = new();

    /// <summary>Current active file list, including orientation filter</summary>
    public List<string> ActiveFileList
    {
        get
        {
            var searchFiles = _tagSearch.SearchResultFiles;
            var baseList = IsShowingSearchResult && searchFiles.Count > 0
                ? searchFiles : _allFiles;
            if (OrientationFilter == OrientationFilter.All)
                return baseList;
            return _orientationFilteredFiles;
        }
    }

    private List<string> _orientationFilteredFiles = new();

    public double PreSearchScrollOffset { get; set; }
    public event Action? ScrollRestoreRequested;

    // ==================== Thumbnail Zoom ====================
    [ObservableProperty] private double _thumbnailBaseWidth = 160.0;
    [ObservableProperty] private double _zoomTick = 1;

    /// <summary>Fixed height for grid mode thumbnails (NaN for waterfall = auto)</summary>
    public double GridThumbnailHeight =>
        WaterfallMode == "None"
            ? ThumbnailBaseWidth / Math.Max(0.01, AppSettings.ThumbnailAspectRatio)
            : double.NaN;

    public bool ShowAnyThumbnailText => ShowFileName || ShowTags || ShowOrientation;

    // ==================== Filters ====================
    [ObservableProperty] private string _tagSearchText = string.Empty;
    [ObservableProperty] private string _currentTagFilter = string.Empty;
    [ObservableProperty] private OrientationFilter _orientationFilter = OrientationFilter.All;
    [ObservableProperty] private ObservableCollection<TagCount> _tagSearchSuggestions = new();
    [ObservableProperty] private bool _isTagSearchPopupOpen;
    [ObservableProperty] private string _coTagFilterText = string.Empty;

    // ==================== Status ====================
    [ObservableProperty] private string _statusText = "就绪";
    [ObservableProperty] private string _backgroundStatusText = string.Empty;
    [ObservableProperty] private string _loadedInfoText = string.Empty;

    [ObservableProperty] private bool _isAutoTagRunning;

    [RelayCommand]
    private async Task StopAutoTag()
    {
        IsAutoTagRunning = false;
        var controller = App.Services.GetRequiredService<ImageManager.App.Services.AutoTagController>();
        await controller.CancelAsync();
    }
    [ObservableProperty] private bool _isShowingSearchResult;

    public bool IsSuggestionCoTagMode => _tagSearch.IsSuggestionCoTagMode;

    public int GetCoTagState(string tagName) => _tagSearch.GetCoTagState(tagName);

    public string SearchBoxBorderColor => _tagSearch.SearchBoxBorderColor(TagSearchText);

    // ==================== Page Manager ====================
    private readonly PageManager _pageManager;
    private readonly TagSearchController _tagSearch;

    // ==================== Cache ====================
    private readonly ConcurrentDictionary<string, string> _phashCache = new(StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, List<string>> _tagCacheByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _tagCacheLock = new();
    private Dictionary<string, ImageMeta> _metaCache = new(StringComparer.OrdinalIgnoreCase);
    [ObservableProperty] private ImageSortOrder _currentSortOrder = ImageSortOrder.FileNameAsc;
    private int _folderViewRequestVersion;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _precomputeCts;
    private FileSystemWatcher? _folderWatcher;
    private CancellationTokenSource? _folderWatchDebounceCts;
    private CancellationTokenSource? _widthDebounceCts;
    private int _resultNavigationVersion;

    // 用于精确等待 Images 集合更新完成的信号
    private TaskCompletionSource<bool>? _imagesUpdatedTcs;

    private readonly ImageManager.Infrastructure.Services.ArtistEmbeddingStore _artistStore;
    private readonly ImageManager.Infrastructure.Services.ChineseTagLibrary _chineseLib;

    public MainWindowViewModel(
        ISettingsRepository settingsRepo,
        IFolderRepository folderRepo,
        IImageMetaRepository metaRepo,
        ITagRepository tagRepo,
        ISimilarImageService similarService,
        IDuplicateService duplicateService,
        ThumbnailCacheService thumbCache,
        PageManager pageManager,
        TagSearchController tagSearch,
        ImageManager.Infrastructure.Services.ArtistEmbeddingStore artistStore,
        ImageManager.Infrastructure.Services.ChineseTagLibrary chineseLib)
    {
        _settingsRepo = settingsRepo;
        _folderRepo = folderRepo;
        _metaRepo = metaRepo;
        _tagRepo = tagRepo;
        _similarService = similarService;
        _duplicateService = duplicateService;
        _thumbCache = thumbCache;
        _pageManager = pageManager;
        _tagSearch = tagSearch;
        _artistStore = artistStore;
        _chineseLib = chineseLib;

        _pageManager.PageChanged += args =>
        {
            Images = new ObservableCollection<ImageViewItem>(args.Items);
            _isNavigating = true;
            CurrentPage = args.PageIndex;
            _isNavigating = false;
            LoadedInfoText = args.LoadedInfoText;

            // 发送 Images 更新完成信号
            _imagesUpdatedTcs?.TrySetResult(true);
        };

        _tagSearch.SearchCompleted += result =>
        {
            CoTagFilterText = string.Empty;
            OnPropertyChanged(nameof(IsSuggestionCoTagMode));

            if (!result.HasResults)
            {
                CurrentTagFilter = string.Empty;
                return;
            }

            IsShowingSearchResult = true;
            if (result.TotalPages == 0)
            {
                Images = new ObservableCollection<ImageViewItem>();
                TotalPages = 0;
                PageNumbers = new ObservableCollection<int>();
            }
            else
            {
                TotalPages = result.TotalPages;
                PageNumbers = new ObservableCollection<int>(Enumerable.Range(1, result.TotalPages));
                _pageManager.InvalidateCache();
                _ = ShowPageAsync(0);
            }
            StatusText = result.StatusText;
        };

        _tagSearch.SuggestionsChanged += (suggestions, isOpen) =>
        {
            TagSearchSuggestions = new ObservableCollection<TagCount>(suggestions);
            IsTagSearchPopupOpen = isOpen;
        };

        _tagSearch.CoTagCycled += _ =>
        {
            OnPropertyChanged(nameof(SearchBoxBorderColor));
        };

        _tagSearch.CoTagModeExited += () =>
        {
            CoTagFilterText = string.Empty;
            OnPropertyChanged(nameof(SearchBoxBorderColor));
            OnPropertyChanged(nameof(IsSuggestionCoTagMode));
        };
    }

    public async Task InitializeAsync()
    {
        AppSettings = await _settingsRepo.LoadAsync();

        _pageManager.InitializeDecodeWidth(0);

        // Sync: if DB was recovered fresh, settings default may not match actual cache dir
        if (string.IsNullOrWhiteSpace(AppSettings.DiskCacheDirectory)
            || AppSettings.DiskCacheDirectory == @"C:\ImageManagerCache")
        {
            AppSettings.DiskCacheDirectory = App.CacheDirectoryPath;
        }
        _thumbCache.CacheDirectory = AppSettings.DiskCacheDirectory;

        var folders = await _folderRepo.GetAllAsync();
        var nodes = folders.Select(f => new FolderTreeNode
        {
            Path = f.Path, DisplayName = f.DisplayName, DbId = f.Id
        }).ToList();
        foreach (var n in nodes) n.EnsureExpanderVisible();
        FolderTree = new ObservableCollection<FolderTreeNode>(nodes);

        SyncUISettingsFromAppData();

        // Clean orphan thumbnails from externally-deleted files
        _ = Task.Run(async () =>
        {
            try
            {
                var unlinked = await _metaRepo.GetAllUnlinkedAsync();
                foreach (var m in unlinked)
                    _thumbCache.DeleteFromDiskCache(m.FilePath);
            }
            catch { }
        });

        // Defer tag count refresh — not needed for initial display
        _ = RefreshTagCountsAsync();

        if (!string.IsNullOrEmpty(AppSettings.LastFolder) && Directory.Exists(AppSettings.LastFolder))
        {
            await LoadFolderAsync(AppSettings.LastFolder);
            SelectedFolderNode = FolderTree.FirstOrDefault(f =>
                string.Equals(f.Path, AppSettings.LastFolder, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            StatusText = "未选择文件夹";
        }
    }

    // ==================== Folder Commands ====================

    [RelayCommand]
    private async Task AddFolderAsync(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath)) return;

        await _folderRepo.AddAsync(folderPath);
        var info = await _folderRepo.GetByPathAsync(folderPath);
        if (info != null && FolderTree.All(f => !string.Equals(f.Path, folderPath, StringComparison.OrdinalIgnoreCase)))
        {
            var node = new FolderTreeNode
            {
                Path = info.Path, DisplayName = info.DisplayName, DbId = info.Id
            };
            node.EnsureExpanderVisible();
            FolderTree.Add(node);
        }

        await SaveSettingsAsync();
    }

    [RelayCommand]
    private async Task RemoveFolderAsync()
    {
        if (SelectedFolderNode == null) return;

        if (SelectedFolderNode.DbId > 0 || !string.IsNullOrEmpty(SelectedFolderNode.Path))
        {
            try
            {
                var metas = await _metaRepo.GetByFolderAsync(SelectedFolderNode.Path);
                foreach (var meta in metas)
                    _thumbCache.DeleteFromDiskCache(meta.FilePath);
            }
            catch { }
        }

        await _folderRepo.RemoveAsync(SelectedFolderNode.Path);
        FolderTree.Remove(SelectedFolderNode);
        await SaveSettingsAsync();
    }

    [RelayCommand]
    private async Task SelectFolderSuggestion(FolderTreeNode folder)
    {
        FolderSearchText = string.Empty;
        IsFolderSearchPopupOpen = false;

        // 传递 syncSelection: true，让 ExpandAndHighlightFolderAsync 内部处理滚动
        await ExpandAndHighlightFolderAsync(folder.Path, syncSelection: true);

        if (!Directory.Exists(folder.Path))
        {
            StatusText = $"文件夹路径已变更: {folder.Path}";
            return;
        }

        _isProgrammaticFolderSelection = true;
        SelectedFolderNode = folder;
        AppSettings.LastFolder = folder.Path;
        await LoadFolderAsync(folder.Path);
        await SaveSettingsAsync();
        _isProgrammaticFolderSelection = false;

        // 删除显式滚动调用，避免双重滚动
        // TreeScrollToNodeRequested?.Invoke(folder);
    }

    internal bool _isProgrammaticFolderSelection;

    public async Task UpdateFolderAliasAsync(string folderPath, string? alias)
    {
        await _folderRepo.UpdateAliasAsync(folderPath, alias);
        var node = FindNodeByPath(folderPath);
        if (node != null)
            node.DisplayName = alias ?? System.IO.Path.GetFileName(folderPath.TrimEnd('\\', '/'));
    }

    /// <summary>Relocate a folder whose path changed externally. Updates all paths in DB.</summary>
    public async Task RelocateFolderAsync(long folderId, string newFolderPath)
    {
        await _folderRepo.RelocateFolderAsync(folderId, newFolderPath);
        var node = FindNodeByPath(newFolderPath) ?? FindNodeByDbId(FolderTree, folderId);
        if (node != null)
            node.Path = newFolderPath;
    }

    private static FolderTreeNode? FindNodeByDbId(IEnumerable<FolderTreeNode> nodes, long id)
    {
        foreach (var n in nodes)
        {
            if (n.DbId == id) return n;
            var found = FindNodeByDbId(n.Children, id);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>Returns true if folder needs relocation (path doesn't exist on disk)</summary>
    public bool NeedsRelocation(FolderTreeNode folder) => folder.DbId > 0 && !Directory.Exists(folder.Path);

    public async Task SelectFolderAsync(FolderTreeNode? folder)
    {
        if (folder == null) return;

        if (!Directory.Exists(folder.Path))
        {
            StatusText = $"文件夹路径已变更: {folder.Path}";
            return;
        }

        SelectedFolderNode = folder;
        AppSettings.LastFolder = folder.Path;
        await LoadFolderAsync(folder.Path);
        await SaveSettingsAsync();
    }

    // ==================== Folder Loading ====================

    private int GetStartPageForLoadedFolder(string? preferredFilePath, int? lastPage)
    {
        if (!string.IsNullOrEmpty(preferredFilePath))
        {
            var preferredIndex = _allFiles.FindIndex(f =>
                string.Equals(f, preferredFilePath, StringComparison.OrdinalIgnoreCase));
            if (preferredIndex >= 0)
                return preferredIndex / PageManager.PageSize;
        }

        return lastPage.HasValue && lastPage.Value < TotalPages ? lastPage.Value : 0;
    }

    public async Task LoadFolderAsync(string folder, string? preferredFilePath = null, Func<bool>? isCurrent = null)
    {
        var requestVersion = BeginFolderViewRequest();
        var showAllSubfolders = ShowAllSubfolders;

        if (!Directory.Exists(folder))
        {
            // Try to relocate if this was a previously imported folder
            var existingFolder = await _folderRepo.GetByPathAsync(folder);
            if (existingFolder != null)
            {
                StatusText = "文件夹路径已变更，请在侧边栏重新点击该文件夹以重定位";
            }
            else
            {
                StatusText = "文件夹不存在";
            }
            return;
        }

        IsShowingSearchResult = false;
        Images.Clear();
        _allFiles.Clear();
        _pageManager.InvalidateCache();
        _phashCache.Clear();
        lock (_tagCacheLock)
        {
            _tagCacheByPath.Clear();
        }
        _metaCache.Clear();
        BackgroundStatusText = "";
        CurrentPage = 0;
        TotalPages = 0;
        CurrentFolder = folder;
        StartWatchingCurrentFolder();
        await Task.Yield();
        if (isCurrent?.Invoke() == false)
            return;

        if (showAllSubfolders)
        {
            await RebuildFileListAsync(requestVersion, folder, true);
            return;
        }

        // Check if this folder already has FolderId markers in DB
        var folderInfo = await _folderRepo.GetByPathAsync(folder);
        if (isCurrent?.Invoke() == false)
            return;
        long? folderId = folderInfo?.Id;
        var exts = FileTypeConstants.AllMediaExtensions.ToArray();

        if (folderId.HasValue)
        {
            // Path A: Try indexed DB query first (fast, no disk IO)
            var indexedFiles = await Task.Run(() =>
                _metaRepo.GetByFolderIdAsync(folderId.Value));
            if (isCurrent?.Invoke() == false)
                return;

            if (indexedFiles.Count > 0)
            {
                _allFiles = await Task.Run(() =>
                    indexedFiles.Select(m => m.FilePath).Where(File.Exists).ToList());
                if (isCurrent?.Invoke() == false)
                    return;

                if (_allFiles.Count == 0)
                {
                    StatusText = "该文件夹内没有图片文件";
                    return;
                }

                TotalPages = (_allFiles.Count + PageManager.PageSize - 1) / PageManager.PageSize;
                PageNumbers = new ObservableCollection<int>(Enumerable.Range(1, TotalPages));
                StatusText = $"总文件数: {_allFiles.Count}";

                int? lastPage = string.IsNullOrEmpty(preferredFilePath)
                    ? await _folderRepo.GetLastPageIndexAsync(folder)
                    : null;
                if (isCurrent?.Invoke() == false)
                    return;
                int startPage = GetStartPageForLoadedFolder(preferredFilePath, lastPage);

                // 立即触发页面显示，不等待标签加载
                await ShowPageAsync(startPage);
                if (isCurrent?.Invoke() == false)
                    return;

                // 标签异步加载（后台线程），不阻塞主线程
                _ = Task.Run(() =>
                {
                    try
                    {
                        int totalTaggedFromDb = 0;
                        lock (_tagCacheLock)
                        {
                            foreach (var m in indexedFiles)
                            {
                                _metaCache[m.FilePath] = m;
                                if (m.Tags.Count > 0)
                                {
                                    _tagCacheByPath[m.FilePath] = m.Tags.Select(t => t.Name).ToList();
                                    totalTaggedFromDb++;
                                }
                            }
                        }

                        // 标签加载完成后，通知 UI 刷新
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            StatusText += $" | 索引图: {indexedFiles.Count} | DB有标签: {totalTaggedFromDb} 张";
                            if (totalTaggedFromDb > 0)
                            {
                                var sample = indexedFiles.FirstOrDefault(m => m.Tags.Count > 0);
                                StatusText += $" | 示例: {sample?.Tags.FirstOrDefault()?.Name}";
                            }

                            // 刷新当前页面的标签显示
                            foreach (var item in Images)
                            {
                                if (_tagCacheByPath.TryGetValue(item.FilePath, out var tags))
                                {
                                    item.Tags = tags;
                                    item.NotifyAll();
                                }
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error($"标签异步加载失败: {ex.Message}");
                    }
                });

                // 延迟后台同步：检查磁盘新文件并更新数据库
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(2000); // 延迟2秒，避免与页面加载竞争
                        if (string.Equals(CurrentFolder, folder, StringComparison.OrdinalIgnoreCase))
                        {
                            await SyncFolderAsync(folder, folderId.Value, exts, isCurrent);
                        }
                    }
                    catch { }
                });

                return;
            }
        }

        // Path B: First time — full disk enumeration + mark with FolderId
        try
        {
            _allFiles = await Task.Run(() =>
                Directory.EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(f => exts.Contains(Path.GetExtension(f).ToLower()))
                    .ToList()
            );
            if (isCurrent?.Invoke() == false)
                return;
        }
        catch (Exception ex)
        {
            StatusText = $"读取文件夹失败: {ex.Message}";
            return;
        }

        if (_allFiles.Count == 0)
        {
            StatusText = "该文件夹内没有图片文件";
            return;
        }

        if (!folderId.HasValue && FolderTree.Any(f =>
                string.Equals(f.Path, folder, StringComparison.OrdinalIgnoreCase)))
        {
            await _folderRepo.AddAsync(folder);
            folderInfo = await _folderRepo.GetByPathAsync(folder);
            folderId = folderInfo?.Id;
        }

        TotalPages = (_allFiles.Count + PageManager.PageSize - 1) / PageManager.PageSize;
        PageNumbers = new ObservableCollection<int>(Enumerable.Range(1, TotalPages));

        var fileSet = new HashSet<string>(_allFiles, StringComparer.OrdinalIgnoreCase);
        _ = CleanMetaForFolderAsync(folder, fileSet);

        _precomputeCts?.Cancel();
        _precomputeCts?.Dispose();
        _precomputeCts = new CancellationTokenSource();
        var captureCt2 = _precomputeCts.Token;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(3000, captureCt2); }
            catch { return; }
            await PrecomputeHashesAsync(captureCt2, folderId);
        });

        if (isCurrent?.Invoke() == false)
            return;

        int? lastPage2 = string.IsNullOrEmpty(preferredFilePath)
            ? await _folderRepo.GetLastPageIndexAsync(folder)
            : null;
        if (isCurrent?.Invoke() == false)
            return;
        int startPage2 = GetStartPageForLoadedFolder(preferredFilePath, lastPage2);

        // 立即显示第一页（图片优先）
        await ShowPageAsync(startPage2);

        // 异步加载标签（不阻塞显示）
        _ = Task.Run(async () =>
        {
            try
            {
                var metas = await _metaRepo.GetByFolderAsync(folder);
                int dbTagged = 0;
                lock (_tagCacheLock)
                {
                    foreach (var m in metas)
                    {
                        _metaCache[m.FilePath] = m;
                        if (m.Tags.Count > 0)
                        {
                            _tagCacheByPath[m.FilePath] = m.Tags.Select(t => t.Name).ToList();
                            dbTagged++;
                        }
                    }
                }

                // 刷新当前页面以显示标签
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _pageManager.InvalidateCache();
                    _ = ShowPageAsync(CurrentPage);
                    StatusText = $"总文件数: {_allFiles.Count} | DB图: {metas.Count} | DB有标签: {dbTagged} 张";
                });
            }
            catch (Exception ex)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    StatusText = $"标签加载失败: {ex.Message}");
            }
        });

        StatusText = $"总文件数: {_allFiles.Count}";
        await ShowPageAsync(startPage2);
    }

    /// <summary>Public wrapper for code-behind: sync current folder and refresh UI, then compute missing hashes</summary>
    public async Task SyncCurrentFolderAsync()
    {
        if (string.IsNullOrEmpty(CurrentFolder)) return;
        var fi = await _folderRepo.GetByPathAsync(CurrentFolder);
        if (fi == null) return;
        var exts = FileTypeConstants.AllMediaExtensions.ToArray();
        await SyncFolderAsync(CurrentFolder, fi.Id, exts);

        _precomputeCts?.Cancel();
        _precomputeCts?.Dispose();
        _precomputeCts = new CancellationTokenSource();
        var captureCt3 = _precomputeCts.Token;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(3000, captureCt3); }
            catch { return; }
            await PrecomputeHashesAsync(captureCt3, fi.Id);
        });
    }

    private void StartWatchingCurrentFolder()
    {
        StopWatchingCurrentFolder();
        if (string.IsNullOrEmpty(CurrentFolder) || !Directory.Exists(CurrentFolder)) return;

        _folderWatcher = new FileSystemWatcher(CurrentFolder)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName,
            EnableRaisingEvents = false
        };
        _folderWatcher.Created += OnFolderFileCreated;
        _folderWatcher.Deleted += OnFolderFileDeleted;
        _folderWatcher.EnableRaisingEvents = true;
    }

    private void StopWatchingCurrentFolder()
    {
        _folderWatchDebounceCts?.Cancel();
        _folderWatchDebounceCts?.Dispose();
        _folderWatchDebounceCts = null;
        if (_folderWatcher != null)
        {
            _folderWatcher.EnableRaisingEvents = false;
            _folderWatcher.Created -= OnFolderFileCreated;
            _folderWatcher.Deleted -= OnFolderFileDeleted;
            _folderWatcher.Dispose();
            _folderWatcher = null;
        }
    }

    public void SuppressDeletedEvent()
    {
        if (_folderWatcher != null)
            _folderWatcher.Deleted -= OnFolderFileDeleted;
    }

    public void RestoreDeletedEvent()
    {
        if (_folderWatcher != null)
            _folderWatcher.Deleted += OnFolderFileDeleted;
    }

    private void OnFolderFileCreated(object sender, FileSystemEventArgs e)
    {
        if (!FileTypeConstants.IsMediaFile(e.Name))
            return;

        _folderWatchDebounceCts?.Cancel();
        _folderWatchDebounceCts?.Dispose();
        _folderWatchDebounceCts = new CancellationTokenSource();
        var ct = _folderWatchDebounceCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(2000, ct);
                await SyncCurrentFolderAsync();
            }
            catch (OperationCanceledException) { }
        });
    }

    private void OnFolderFileDeleted(object sender, FileSystemEventArgs e)
    {
        if (!FileTypeConstants.IsMediaFile(e.Name))
            return;

        _folderWatchDebounceCts?.Cancel();
        _folderWatchDebounceCts?.Dispose();
        _folderWatchDebounceCts = new CancellationTokenSource();
        var ct = _folderWatchDebounceCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(2000, ct);
                await SyncCurrentFolderAsync();
            }
            catch (OperationCanceledException) { }
        });
    }

    /// <summary>
    /// Sync folder: detect new/deleted files, compute hashes for new files, refresh UI if changed.
    /// Returns true if any files were added or removed.
    /// </summary>
    private async Task<bool> SyncFolderAsync(string folder, long folderId, string[] exts, Func<bool>? isCurrent = null)
    {
        try
        {
            var diskFiles = new HashSet<string>(
                Directory.EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(f => FileTypeConstants.IsMediaFile(f)),
                StringComparer.OrdinalIgnoreCase);

            var dbFiles = await _metaRepo.GetByFolderIdAsync(folderId);
            var dbSet = new HashSet<string>(dbFiles.Select(m => m.FilePath), StringComparer.OrdinalIgnoreCase);

            // MD5 + DB on background thread to avoid blocking UI
            (List<string> newFiles, bool deleted) = await Task.Run(async () =>
            {
                var newFiles = new List<string>();
                foreach (var file in diskFiles)
                {
                    if (!dbSet.Contains(file))
                    {
                        string? md5 = null;
                        try
                        {
                            using var fs = File.OpenRead(file);
                            md5 = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(fs)).ToLowerInvariant();
                        }
                        catch { }
                        if (!string.IsNullOrEmpty(md5))
                        {
                            var match = await _metaRepo.GetByFileHashAsync(md5);
                            if (match != null)
                            {
                                var tagNames = match.Tags.Select(t => t.Name).ToList();
                                if (!File.Exists(match.FilePath))
                                {
                                    await _metaRepo.UpdateFilePathAsync(match.Id, file, folderId);
                                }
                                else
                                {
                                    var fi = new FileInfo(file);
                                    var newMeta = new ImageMeta
                                    {
                                        FilePath = file, FileHash = md5, FolderId = folderId,
                                        FileSize = fi.Length, LastWriteTicks = fi.LastWriteTimeUtc.Ticks
                                    };
                                    var newId = await _metaRepo.UpsertAsync(newMeta);
                                    if (tagNames.Count > 0)
                                        await _metaRepo.SetTagsAsync(newId, tagNames);
                                }
                                lock (_tagCacheLock)
                                {
                                    _tagCacheByPath[file] = tagNames;
                                }
                                newFiles.Add(file);
                                continue;
                            }
                        }
                        await _metaRepo.SetFolderIdAsync(file, folderId);
                        newFiles.Add(file);
                    }
                }

                bool deleted = false;
                foreach (var meta in dbFiles)
                {
                    if (!diskFiles.Contains(meta.FilePath))
                    {
                        await _metaRepo.SetFolderIdAsync(meta.FilePath, 0L);
                        _thumbCache.DeleteFromDiskCache(meta.FilePath);
                        deleted = true;
                    }
                }
                return (newFiles, deleted);
            });

            if (newFiles.Count > 0 || deleted)
            {
                if (string.Equals(CurrentFolder, folder, StringComparison.OrdinalIgnoreCase))
                {
                    if (isCurrent?.Invoke() == false)
                        return false;

                    _allFiles = diskFiles.ToList();
                    TotalPages = (_allFiles.Count + PageManager.PageSize - 1) / PageManager.PageSize;
                    PageNumbers = new ObservableCollection<int>(Enumerable.Range(1, TotalPages));
                    _pageManager.InvalidateCache();
                    _phashCache.Clear();
                    int targetPage = CurrentPage;
                    if (targetPage >= TotalPages) targetPage = Math.Max(0, TotalPages - 1);
                    await ShowPageAsync(targetPage);
                    StatusText = $"总文件数: {_allFiles.Count}";
                }
                return true;
            }
        }
        catch { }
        return false;
    }

    private async Task PrecomputeHashesAsync(CancellationToken ct, long? folderId = null)
    {
        var files = _allFiles.ToArray();
        if (files.Length == 0) return;

        // Force re-hash if algorithm was upgraded (or old data has corrupt FolderId)
        HashSet<string> existingSet;
        const string CurrentHashVersion = "3";
        if (AppSettings.HashVersion != CurrentHashVersion)
        {
            AppSettings.HashVersion = CurrentHashVersion;
            await _settingsRepo.SaveAsync(AppSettings);
            existingSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            // Lightweight pre-check: only query FilePath + PerceptualHash (no tag joins)
            try
            {
                var hashDict = await _metaRepo.GetPerceptualHashesByPathsAsync(files.ToList());
                existingSet = new HashSet<string>(
                    hashDict.Where(kv => kv.Value.Split('|').Length >= 4)
                            .Select(kv => kv.Key),
                    StringComparer.OrdinalIgnoreCase);
                foreach (var kv in hashDict)
                    _phashCache[kv.Key] = kv.Value;
            }
            catch
            {
                existingSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        var needsHashing = files
            .Where(f => !existingSet.Contains(f))
            .Where(f => FileTypeConstants.IsImageFile(f))  // Skip video files
            .ToList();
        if (needsHashing.Count == 0) return;

        // === Producer: I/O-bound file reading ===
        var channel = Channel.CreateBounded<(string Path, byte[] Data, long FileSize, long LastWriteTicks, string FileHash)>(
            new BoundedChannelOptions(50)
            {
                SingleWriter = false, SingleReader = false,
                FullMode = BoundedChannelFullMode.Wait
            });

        int ioConcurrency = 2;
        using var ioSlots = new SemaphoreSlim(ioConcurrency);

        var produceTasks = needsHashing.Select(async path =>
        {
            try
            {
                if (ct.IsCancellationRequested) return;
                await ioSlots.WaitAsync(ct);
                try
                {
                    var fi = new FileInfo(path);
                    string fileHash;
                    using (var fs = File.OpenRead(path))
                        fileHash = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(fs)).ToLowerInvariant();
                    var hashInput = await Task.Run(() => ThumbnailGenerator.DecodeForHashInput(path, 256), ct);
                    if (hashInput == null) return;
                    await channel.Writer.WriteAsync(
                        (path, hashInput, fi.Length, fi.LastWriteTimeUtc.Ticks, fileHash), ct);
                }
                finally { ioSlots.Release(); }
            }
            catch (OperationCanceledException) { }
            catch { }
        });

        // === Consumer: CPU-bound hash computation ===
        int cpuConcurrency = Math.Max(1, Environment.ProcessorCount - 1);
        using var cpuSlots = new SemaphoreSlim(cpuConcurrency);
        var upsertBatch = new ConcurrentQueue<ImageMeta>();
        int processed = 0;
        int totalNeed = needsHashing.Count;

        Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            BackgroundStatusText = $"正在计算图片指纹... 0/{totalNeed}");

        var consumeTasks = Enumerable.Range(0, cpuConcurrency).Select(async _ =>
        {
            try
            {
                while (await channel.Reader.WaitToReadAsync(ct))
                {
                    while (channel.Reader.TryRead(out var item))
                    {
                        await cpuSlots.WaitAsync(ct);
                        try
                        {
                            var meta = new ImageMeta
                            {
                                FilePath = item.Path,
                                FileHash = item.FileHash,
                                FileSize = item.FileSize,
                                LastWriteTicks = item.LastWriteTicks,
                                FolderId = folderId, // may be null — BulkUpsert will preserve existing non-null
                                PerceptualHash = HashService.ComputeCombinedPerceptualHashFromBytes(item.Data)
                            };
                            try
                            {
                                var (w, h) = ThumbnailGenerator.GetDimensions(item.Path);
                                meta.Width = w; meta.Height = h;
                            }
                            catch { }

                            _phashCache[item.Path] = meta.PerceptualHash;
                            upsertBatch.Enqueue(meta);

                            if (upsertBatch.Count >= 100)
                            {
                                var batch = DrainBatch();
                                if (batch.Count > 0)
                                    await _metaRepo.BulkUpsertAsync(batch);
                                Interlocked.Add(ref processed, batch.Count);
                                int snap = Interlocked.CompareExchange(ref processed, 0, 0);
                                Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                                    BackgroundStatusText = $"正在计算图片指纹... {snap}/{totalNeed}");
                            }
                        }
                        finally { cpuSlots.Release(); }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch { }
        });

        // Start consumers first to avoid channel-full deadlock
        var consumerTaskList = consumeTasks.ToList();
        var producerTaskList = produceTasks.ToList();

        await Task.WhenAll(producerTaskList);
        channel.Writer.Complete();
        await Task.WhenAll(consumerTaskList);

        var final = DrainBatch();
        if (final.Count > 0)
            await _metaRepo.BulkUpsertAsync(final);
        Interlocked.Add(ref processed, final.Count);

        Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            BackgroundStatusText = "");

        return;

        List<ImageMeta> DrainBatch()
        {
            var list = new List<ImageMeta>();
            while (upsertBatch.TryDequeue(out var m))
                list.Add(m);
            return list;
        }
    }

    private async Task CleanMetaForFolderAsync(string folderPath, HashSet<string> existingFiles)
    {
        try
        {
            var metas = await _metaRepo.GetByFolderAsync(folderPath);
            foreach (var meta in metas)
            {
                if (!existingFiles.Contains(meta.FilePath))
                    _ = _metaRepo.DeleteByPathAsync(meta.FilePath);
            }
        }
        catch { }
    }

    // ==================== Paging ====================

    [RelayCommand]
    private async Task PrevPageAsync() { if (CurrentPage > 0) await ShowPageAsync(CurrentPage - 1); }

    [RelayCommand]
    private async Task NextPageAsync() { if (CurrentPage < TotalPages - 1) await ShowPageAsync(CurrentPage + 1); }

    public async Task ShowPageAsync(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= TotalPages) return;
        _isNavigating = true;
        CurrentPage = pageIndex;
        _isNavigating = false;
        // Preload tag cache for the current page's files (subfolder files especially)
        var pageFiles = ActiveFileList.Skip(pageIndex * PageManager.PageSize).Take(PageManager.PageSize).ToList();
        if (pageFiles.Count > 0) _ = PreloadTagsForFilesAsync(pageFiles);
        await _pageManager.ShowPageAsync(pageIndex, TotalPages,
            ActiveFileList, GetTagsForFile, IsShowingSearchResult, CurrentFolder);
    }

    // ==================== Thumbnail Zoom ====================

    private bool _isNavigating;

    // Respond to combobox page selection
    partial void OnCurrentPageChanged(int value)
    {
        if (!_isNavigating && value >= 0 && value < TotalPages)
            _ = ShowPageAsync(value);
    }

    partial void OnWaterfallModeChanged(string value) => OnPropertyChanged(nameof(GridThumbnailHeight));
    partial void OnThumbnailBaseWidthChanged(double value) => OnPropertyChanged(nameof(GridThumbnailHeight));
    partial void OnShowFileNameChanged(bool value) => OnPropertyChanged(nameof(ShowAnyThumbnailText));
    partial void OnShowTagsChanged(bool value) => OnPropertyChanged(nameof(ShowAnyThumbnailText));
    partial void OnShowOrientationChanged(bool value) => OnPropertyChanged(nameof(ShowAnyThumbnailText));
partial void OnKeepPaddingChanged(bool value)
{
    OnPropertyChanged(nameof(ThumbnailPadding));
    OnPropertyChanged(nameof(ThumbnailBorderThickness));
    OnPropertyChanged(nameof(ThumbnailInnerCornerRadius));
    OnPropertyChanged(nameof(ThumbnailImageStretch));
}
partial void OnCornerRadiusDipChanged(double value)
{
    OnPropertyChanged(nameof(ThumbnailCornerRadius));
    OnPropertyChanged(nameof(ThumbnailInnerCornerRadius));
    OnPropertyChanged(nameof(ThumbnailSelectionCornerRadius));
}

    partial void OnZoomTickChanged(double value)
    {
        SaveZoomForMode(WaterfallMode);
        _pageManager.UpdateUiState(new PageUiState(
            (double)0, WaterfallMode, AppSettings.ThumbnailAspectRatio));
        var (baseWidth, _) = _pageManager.OnZoomTickChanged(value, CurrentPage, TotalPages,
            ActiveFileList, GetTagsForFile);
        if ((int)baseWidth != (int)ThumbnailBaseWidth)
        {
            _widthDebounceCts?.Cancel();
            _widthDebounceCts?.Dispose();
            _widthDebounceCts = new CancellationTokenSource();
            var capturedWidth = baseWidth;
            var token = _widthDebounceCts.Token;
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(50, token); }
                catch { return; }
                if (token.IsCancellationRequested) return;
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!token.IsCancellationRequested)
                        ThumbnailBaseWidth = capturedWidth;
                });
            }, token);
        }
    }

    partial void OnShowAllSubfoldersChanged(bool value)
    {
        if (value) SearchScope = 1; // auto-switch to recursive
        if (string.IsNullOrEmpty(CurrentFolder)) return;
        var requestVersion = BeginFolderViewRequest();
        var folder = CurrentFolder;
        _ = RebuildFileListAsync(requestVersion, folder, value);
    }

    partial void OnFolderSearchTextChanged(string value)
    {
        FolderSearchSuggestions.Clear();
        var keyword = value?.Trim();
        if (string.IsNullOrEmpty(keyword))
        {
            IsFolderSearchPopupOpen = false;
            return;
        }
        foreach (var root in FolderTree)
            CollectLoadedNodes(root, FolderSearchSuggestions, keyword);
        IsFolderSearchPopupOpen = FolderSearchSuggestions.Count > 0;
    }

    private static void CollectLoadedNodes(FolderTreeNode node,
        ObservableCollection<FolderTreeNode> results, string keyword)
    {
        if (node.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            results.Add(node);
        foreach (var child in node.Children)
        {
            if (child.DisplayName != "...") // skip placeholder
                CollectLoadedNodes(child, results, keyword);
        }
    }

    public void ClearSearchHighlight()
    {
        foreach (var root in FolderTree)
            ClearHighlightRecursive(root);
    }

    private static void ClearHighlightRecursive(FolderTreeNode node)
    {
        node.IsSearchHighlight = false;
        foreach (var child in node.Children)
            ClearHighlightRecursive(child);
    }

    private bool IsPathHighlighted(string normalizedPath)
    {
        foreach (var root in FolderTree)
        {
            if (IsPathHighlightedRecursive(root, normalizedPath))
                return true;
        }

        return false;
    }

    private static bool IsPathHighlightedRecursive(FolderTreeNode node, string normalizedPath)
    {
        if (node.IsSearchHighlight &&
            string.Equals(NormalizePath(node.Path), normalizedPath, StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var child in node.Children)
        {
            if (IsPathHighlightedRecursive(child, normalizedPath))
                return true;
        }

        return false;
    }

    public async Task<FolderTreeNode?> ExpandAndHighlightFolderAsync(
        string targetPath,
        bool syncSelection = false,
        bool forceScroll = false,
        Func<bool>? isCurrent = null)
    {
        targetPath = NormalizePath(targetPath);
        if (isCurrent?.Invoke() == false)
            return null;

        ClearSearchHighlight();

        // 标准化输入路径
        targetPath = NormalizePath(targetPath);

        // 1. 构建路径层级列表
        var parts = new List<string>();
        var current = targetPath;
        while (!string.IsNullOrEmpty(current) && current.Length > 3)
        {
            parts.Insert(0, current);
            current = Path.GetDirectoryName(current) ?? "";
        }

        if (parts.Count == 0)
        {
            StatusText = "路径解析失败";
            return null;
        }

        // 2. 找到路径的根节点（FolderTree 中的顶层节点）
        FolderTreeNode? rootNode = null;
        int startIdx = -1;

        // 从开头向后查找，找到第一个存在于 FolderTree 中的节点
        for (int i = 0; i < parts.Count; i++)
        {
            rootNode = FolderTree.FirstOrDefault(n =>
                string.Equals(
                    NormalizePath(n.Path),
                    NormalizePath(parts[i]),
                    StringComparison.OrdinalIgnoreCase));
            if (rootNode != null)
            {
                startIdx = i;
                break;
            }
        }

        if (rootNode == null || startIdx < 0)
        {
            // 调试信息：显示尝试匹配的路径和可用的根节点
            var availableRoots = string.Join(", ", FolderTree.Select(n => $"[{NormalizePath(n.Path)}]"));
            var attemptedPaths = string.Join(", ", parts.Select(p => $"[{NormalizePath(p)}]"));
            StatusText = $"无法找到根节点。尝试: {attemptedPaths}；可用根节点: {availableRoots}";
            return null;
        }

        // 3. 从根节点开始，逐层向下展开
        var currentNode = rootNode;

        for (int i = startIdx + 1; i < parts.Count; i++)
        {
            var targetPathAtLevel = parts[i];

            // 3.1 确保当前节点已展开并加载子节点
            if (!currentNode.IsExpanded)
            {
                currentNode.IsExpanded = true;
                if (currentNode.LoadTask != null)
                    await currentNode.LoadTask;
                if (isCurrent?.Invoke() == false)
                    return null;
            }
            else
            {
                // 节点已展开，需要确保子节点真的加载完成
                if (!currentNode.ChildrenLoaded)
                {
                    // 子节点未加载，但LoadTask可能为null（节点初始就是展开状态）
                    if (currentNode.LoadTask == null)
                    {
                        // LoadTask为null说明OnIsExpandedChanged未触发，强制重新触发
                        currentNode.IsExpanded = false;
                        await Task.Delay(1); // 确保UI处理属性变化
                        currentNode.IsExpanded = true;
                    }

                    // 现在LoadTask应该存在了，等待它完成
                    if (currentNode.LoadTask != null)
                        await currentNode.LoadTask;
                }
                else if (currentNode.LoadTask != null && !currentNode.LoadTask.IsCompleted)
                {
                    // 已加载但可能有正在进行的任务
                    await currentNode.LoadTask;
                }

                if (isCurrent?.Invoke() == false)
                    return null;
            }

            // 3.2 在当前节点的直接子节点中查找下一级路径
            var nextNode = currentNode.Children.FirstOrDefault(child =>
                string.Equals(
                    NormalizePath(child.Path),
                    NormalizePath(targetPathAtLevel),
                    StringComparison.OrdinalIgnoreCase));

            if (nextNode == null)
            {
                // 找不到子节点，可能是路径不存在或未加载
                StatusText = $"无法展开到 {Path.GetFileName(targetPathAtLevel)}";
                return null;
            }

            currentNode = nextNode;
        }

        // 4. 到达目标节点，高亮并滚动
        if (currentNode != null)
        {
            currentNode.IsSearchHighlight = true;

            if (syncSelection)
            {
                _isProgrammaticFolderSelection = true;
                SelectedFolderNode = currentNode;
                _isProgrammaticFolderSelection = false;
            }

            // 滚动条件：
            // 1. forceScroll=true: 强制滚动（以图搜图切换场景）
            // 2. syncSelection=true: 需要选中节点，滚动确保可见
            if (forceScroll || syncSelection)
            {
                // 多次重试滚动，递增延迟以应对大型树渲染
                for (int retry = 0; retry < 3; retry++)
                {
                    if (isCurrent?.Invoke() == false)
                        return currentNode;

                    await Task.Delay(50 * (retry + 1));  // 50ms, 100ms, 150ms

                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                        () => TreeScrollToNodeRequested?.Invoke(currentNode),
                        Avalonia.Threading.DispatcherPriority.Loaded);
                }
            }
        }

        return currentNode;
    }

    public Task RebuildFileListAsync()
    {
        var requestVersion = BeginFolderViewRequest();
        return RebuildFileListAsync(requestVersion, CurrentFolder, ShowAllSubfolders);
    }

    private async Task RebuildFileListAsync(int requestVersion, string folderPath, bool showAllSubfolders)
    {
        if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
            return;

        var rebuiltFiles = showAllSubfolders
            ? GetImageFilesRecursive(folderPath)
            : await Task.Run(() =>
                Directory.EnumerateFiles(folderPath, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(f => FileTypeConstants.IsMediaFile(f))
                    .ToList());

        if (!IsFolderViewRequestCurrent(requestVersion, folderPath, showAllSubfolders))
            return;

        await SortFilesAsync(rebuiltFiles, CurrentSortOrder, showAllSubfolders);
        if (!IsFolderViewRequestCurrent(requestVersion, folderPath, showAllSubfolders))
            return;

        _allFiles = rebuiltFiles;
        _tagSearch.SearchResultFiles.Sort(CreateSortComparison(CurrentSortOrder));
        _orientationFilteredFiles.Clear();

        var active = ActiveFileList;
        TotalPages = active.Count == 0 ? 0 : (active.Count + PageManager.PageSize - 1) / PageManager.PageSize;
        PageNumbers = new ObservableCollection<int>(Enumerable.Range(1, TotalPages));
        _pageManager.InvalidateCache();

        if (active.Count == 0)
        {
            Images = new ObservableCollection<ImageViewItem>();
        }
        else
        {
            await ShowPageAsync(0);
        }

        StatusText = $"总文件数: {_allFiles.Count}";

        // Load tags in background — page already visible, tags fill in asynchronously
        if (showAllSubfolders)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var tagMap = await _metaRepo.GetTagMapByFolderAsync(folderPath);
                    if (!IsFolderViewRequestCurrent(requestVersion, folderPath, showAllSubfolders))
                        return;
                    int loadedCount = 0;
                    lock (_tagCacheLock)
                    {
                        foreach (var kv in tagMap)
                        {
                            if (kv.Value.Count > 0 && !_tagCacheByPath.ContainsKey(kv.Key))
                            {
                                _tagCacheByPath[kv.Key] = kv.Value;
                                loadedCount++;
                            }
                        }
                    }
                    if (loadedCount > 0)
                    {
                        // Refresh current page to show newly loaded tags
                        _ = Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            _pageManager.InvalidateCache();
                            _ = ShowPageAsync(CurrentPage);
                            StatusText = $"总文件数: {_allFiles.Count} (含子文件夹) | 标签: {loadedCount}";
                        });
                    }

                    await Task.Delay(3000);
                    if (!IsFolderViewRequestCurrent(requestVersion, folderPath, showAllSubfolders))
                        return;
                    await PrecomputeHashesAsync(CancellationToken.None, (await _folderRepo.GetByPathAsync(folderPath))?.Id);
                }
                catch { }
            });
        }
    }

    private int BeginFolderViewRequest()
    {
        return Interlocked.Increment(ref _folderViewRequestVersion);
    }

    private bool IsFolderViewRequestCurrent(int requestVersion, string folderPath, bool showAllSubfolders)
    {
        return Volatile.Read(ref _folderViewRequestVersion) == requestVersion
            && string.Equals(CurrentFolder, folderPath, StringComparison.OrdinalIgnoreCase)
            && ShowAllSubfolders == showAllSubfolders;
    }

    private Comparison<string> CreateSortComparison(ImageSortOrder order)
    {
        return order switch
        {
            ImageSortOrder.FileNameAsc => (a, b) =>
                string.Compare(Path.GetFileName(a), Path.GetFileName(b), StringComparison.OrdinalIgnoreCase),
            ImageSortOrder.FileNameDesc => (a, b) =>
                string.Compare(Path.GetFileName(b), Path.GetFileName(a), StringComparison.OrdinalIgnoreCase),
            ImageSortOrder.ModifiedAsc => (a, b) =>
                GetMetaTicks(a).CompareTo(GetMetaTicks(b)),
            ImageSortOrder.ModifiedDesc => (a, b) =>
                GetMetaTicks(b).CompareTo(GetMetaTicks(a)),
            ImageSortOrder.FileSizeAsc => (a, b) =>
                GetMetaFileSize(a).CompareTo(GetMetaFileSize(b)),
            ImageSortOrder.FileSizeDesc => (a, b) =>
                GetMetaFileSize(b).CompareTo(GetMetaFileSize(a)),
            ImageSortOrder.ResolutionAsc => (a, b) =>
                GetMetaResolution(a).CompareTo(GetMetaResolution(b)),
            ImageSortOrder.ResolutionDesc => (a, b) =>
                GetMetaResolution(b).CompareTo(GetMetaResolution(a)),
            _ => (a, b) => 0
        };
    }

    private async Task SortFilesAsync(List<string> files, ImageSortOrder order, bool showAllSubfolders)
    {
        var comparison = CreateSortComparison(order);
        await Task.Run(() =>
        {
            if (showAllSubfolders)
            {
                files.Sort((a, b) =>
                {
                    int dirCmp = string.Compare(
                        Path.GetDirectoryName(a), Path.GetDirectoryName(b),
                        StringComparison.OrdinalIgnoreCase);
                    return dirCmp != 0 ? dirCmp : comparison(a, b);
                });
            }
            else
            {
                files.Sort(comparison);
            }
        });
    }

    // ==================== Tag Search ====================

    [RelayCommand]
    private async Task SearchByTag()
    {
        var raw = TagSearchText.Trim();
        if (string.IsNullOrEmpty(raw))
        {
            CurrentTagFilter = string.Empty;
            return;
        }

        CurrentTagFilter = raw;

        if (!IsShowingSearchResult)
            _pageManager.SavePreSearchState(Images, CurrentPage);

        try
        {
            await _tagSearch.SearchByTagAsync(raw, GetSearchScopeFiles(), IsShowingSearchResult,
                list => { foreach (var t in list) TagSearchSuggestions.Add(t); });
        }
        catch (Exception ex)
        {
            StatusText = $"搜索失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SelectTagSuggestion(TagCount tag)
    {
        _tagSearch.SelectSuggestion(tag, TagSearchText,
            name =>
            {
                _tagSearchText = name;
                OnPropertyChanged(nameof(TagSearchText));
            },
            open => IsTagSearchPopupOpen = open,
            () => SearchByTagCommand.ExecuteAsync(null));
    }

    partial void OnTagSearchTextChanged(string value)
    {
        _tagSearch.OnTextChanged(value, TagSearchText,
            () => OnPropertyChanged(nameof(SearchBoxBorderColor)),
            keyword => UpdateTagSuggestions(keyword));
    }

    private void UpdateTagSuggestions(string keyword)
    {
        TagSearchSuggestions.Clear();
        _tagSearch.UpdateSuggestions(keyword,
            t => TagSearchSuggestions.Add(t),
            open => IsTagSearchPopupOpen = open);
    }

    partial void OnCoTagFilterTextChanged(string value)
    {
        if (_tagSearch.IsSuggestionCoTagMode)
            _ = _tagSearch.SearchCoTagsAsync(value);
    }

    public void OnTagSearchGotFocus()
    {
        _tagSearch.OnGotFocus(TagSearchText,
            open => IsTagSearchPopupOpen = open,
            () => TagSearchSuggestions.ToList());
    }

    /// <summary>Remove deleted files incrementally, keeping existing thumbnails intact</summary>
    public async Task RemoveFilesFromViewAsync(HashSet<string> deletedPaths)
    {
        // Guard against OnCurrentPageChanged firing ShowPageAsync during paging updates
        _isNavigating = true;

        // Remove from master lists
        _allFiles.RemoveAll(p => deletedPaths.Contains(p));
        if (_tagSearch.SearchResultFiles.Count > 0)
            _tagSearch.SearchResultFiles.RemoveAll(p => deletedPaths.Contains(p));

        // Recalculate paging
        var files = ActiveFileList;
        TotalPages = files.Count == 0 ? 0 : (files.Count + PageManager.PageSize - 1) / PageManager.PageSize;
        PageNumbers = new ObservableCollection<int>(Enumerable.Range(1, TotalPages));

        // Clamp current page
        if (CurrentPage >= TotalPages && TotalPages > 0)
            CurrentPage = TotalPages - 1;

        if (TotalPages == 0)
        {
            Images = new ObservableCollection<ImageViewItem>();
            _pageManager.InvalidateCache();
            return;
        }

        // Find deleted positions in Images (sorted ascending)
        var removedIndices = new List<int>();
        for (int i = 0; i < Images.Count; i++)
        {
            if (deletedPaths.Contains(Images[i].FilePath))
                removedIndices.Add(i);
        }

        // Build gap-fill file paths from ActiveFileList that aren't already in Images
        int pageStart = CurrentPage * PageManager.PageSize;
        var existingPaths = new HashSet<string>(Images.Select(i => i.FilePath), StringComparer.OrdinalIgnoreCase);
        var gapPaths = new List<string>();
        for (int i = 0; i < PageManager.PageSize && gapPaths.Count < removedIndices.Count; i++)
        {
            int idx = pageStart + i;
            if (idx >= files.Count) break;
            var path = files[idx];
            if (existingPaths.Contains(path)) continue;
            gapPaths.Add(path);
            existingPaths.Add(path);
        }

        // Reuse existing ImageViewItem objects — mutate in-place to avoid CollectionChanged
        int reuseCount = Math.Min(removedIndices.Count, gapPaths.Count);
        var reloadItems = new List<ImageViewItem>();
        for (int i = 0; i < reuseCount; i++)
        {
            var item = Images[removedIndices[i]];
            item.FilePath = gapPaths[i];
            item.FileName = System.IO.Path.GetFileName(gapPaths[i]);
            item.Tags = GetTagsForFile(gapPaths[i]);
            item.IsLoading = true;
            item.IsLoaded = false;
            item.ThumbnailData = null;
            item.Width = 1;
            item.Height = 1;
            reloadItems.Add(item);
        }

        // If more deleted than gap-fill, remove extras
        for (int i = removedIndices.Count - 1; i >= reuseCount; i--)
            Images.RemoveAt(removedIndices[i]);

        // If more gap-fill than deleted, add new items at the end
        for (int i = reuseCount; i < gapPaths.Count; i++)
        {
            var path = gapPaths[i];
            var item = new ImageViewItem
            {
                FilePath = path,
                FileName = System.IO.Path.GetFileName(path),
                Tags = GetTagsForFile(path),
                IsLoading = true
            };
            Images.Add(item);
            reloadItems.Add(item);
        }

        // Load thumbnails for gap-fill items
        if (reloadItems.Count > 0)
            await _pageManager.LoadThumbnailsForItemsAsync(reloadItems);

        // Clear all cached pages except current (stale after deletion), then store current
        _pageManager.InvalidateCache();
        _pageManager.SetPageCache(CurrentPage, Images.ToList());

        _isNavigating = false;
    }


    // ==================== Orientation Filter ====================

    [RelayCommand] private async Task FilterAll() { OrientationFilter = OrientationFilter.All; await RebuildFromOrientationFilterAsync(); }
    [RelayCommand] private async Task FilterLandscape() { OrientationFilter = OrientationFilter.Landscape; await RebuildFromOrientationFilterAsync(); }
    [RelayCommand] private async Task FilterPortrait() { OrientationFilter = OrientationFilter.Portrait; await RebuildFromOrientationFilterAsync(); }

    private async Task RebuildFromOrientationFilterAsync()
    {
        var source = IsShowingSearchResult && _tagSearch.SearchResultFiles.Count > 0
            ? _tagSearch.SearchResultFiles : _allFiles;

        if (OrientationFilter == OrientationFilter.All)
        {
            _orientationFilteredFiles.Clear();
        }
        else
        {
            var wantLandscape = OrientationFilter == OrientationFilter.Landscape;
            // Batch-load dimensions from DB (covers all hashed files in one query)
            var dimensions = await _metaRepo.GetDimensionsByPathsAsync(source);

            _orientationFilteredFiles = await Task.Run(() =>
            {
                var filtered = new List<string>();
                foreach (var path in source)
                {
                    if (dimensions.TryGetValue(path, out var dim) && dim.Width > 0)
                    {
                        if ((wantLandscape && dim.Width >= dim.Height) ||
                            (!wantLandscape && dim.Width < dim.Height))
                            filtered.Add(path);
                    }
                    else
                    {
                        // Fallback: read file header for unhashed files
                        try
                        {
                            var (w, h) = ThumbnailGenerator.GetDimensions(path);
                            if ((wantLandscape && w >= h) || (!wantLandscape && w < h))
                                filtered.Add(path);
                        }
                        catch { }
                    }
                }
                return filtered;
            });
        }

        var files = ActiveFileList;
        TotalPages = files.Count == 0 ? 0 : (files.Count + PageManager.PageSize - 1) / PageManager.PageSize;
        PageNumbers = new ObservableCollection<int>(Enumerable.Range(1, TotalPages));
        _pageManager.InvalidateCache();

        if (files.Count == 0)
        {
            Images = new ObservableCollection<ImageViewItem>();
            StatusText = "没有符合方向筛选的图片";
        }
        else
        {
            await ShowPageAsync(0);
            StatusText = $"{(OrientationFilter == OrientationFilter.Landscape ? "横图" : "竖图")}: {files.Count} 张";
        }
    }

    // ==================== Sort ====================

    public async Task SortImagesAsync(ImageSortOrder order)
    {
        CurrentSortOrder = order;

        if (_allFiles.Count == 0) return;

        await SortFilesAsync(_allFiles, order, ShowAllSubfolders);
        _tagSearch.SearchResultFiles.Sort(CreateSortComparison(order));
        _orientationFilteredFiles.Clear();

        var active = ActiveFileList;
        TotalPages = active.Count == 0 ? 0 : (active.Count + PageManager.PageSize - 1) / PageManager.PageSize;
        PageNumbers = new ObservableCollection<int>(Enumerable.Range(1, TotalPages));
        _pageManager.InvalidateCache();
        await ShowPageAsync(0);

        var labels = new Dictionary<ImageSortOrder, string>
        {
            [ImageSortOrder.FileNameAsc] = "文件名 ↑",
            [ImageSortOrder.FileNameDesc] = "文件名 ↓",
            [ImageSortOrder.ModifiedAsc] = "修改时间 ↑",
            [ImageSortOrder.ModifiedDesc] = "修改时间 ↓",
            [ImageSortOrder.FileSizeAsc] = "文件大小 ↑",
            [ImageSortOrder.FileSizeDesc] = "文件大小 ↓",
            [ImageSortOrder.ResolutionAsc] = "分辨率 ↑",
            [ImageSortOrder.ResolutionDesc] = "分辨率 ↓"
        };
        StatusText = $"排序: {labels.GetValueOrDefault(order, order.ToString())}";
    }

    private long GetMetaTicks(string path)
    {
        if (_metaCache.TryGetValue(path, out var m) && m.LastWriteTicks > 0)
            return m.LastWriteTicks;
        try { return new FileInfo(path).LastWriteTimeUtc.Ticks; }
        catch { return 0; }
    }

    private long GetMetaFileSize(string path)
    {
        if (_metaCache.TryGetValue(path, out var m) && m.FileSize > 0)
            return m.FileSize;
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    private long GetMetaResolution(string path)
    {
        if (_metaCache.TryGetValue(path, out var m) && m.Width > 0)
            return (long)m.Width * m.Height;
        try
        {
            var (w, h) = ImageManager.Infrastructure.Imaging.ThumbnailGenerator.GetDimensions(path);
            return (long)w * h;
        }
        catch { return 0; }
    }

    [RelayCommand]
    private async Task ClearFilter()
    {
        TagSearchText = string.Empty;
        CurrentTagFilter = string.Empty;
        OrientationFilter = OrientationFilter.All;
        _orientationFilteredFiles.Clear();

        if (IsShowingSearchResult)
        {
            // Restore normal page view
            IsShowingSearchResult = false;
            _tagSearch.SearchResultFiles.Clear();

            TotalPages = (_allFiles.Count + PageManager.PageSize - 1) / PageManager.PageSize;
            PageNumbers = new ObservableCollection<int>(Enumerable.Range(1, TotalPages));

            if (_pageManager.TryRestorePreSearchState(out _, out var pageIndex))
            {
                await ShowPageAsync(pageIndex);
                StatusText = $"总文件数: {_allFiles.Count}";
                ScrollRestoreRequested?.Invoke();
            }
            else if (!string.IsNullOrEmpty(CurrentFolder))
            {
                await LoadFolderAsync(CurrentFolder);
            }
        }
        else
        {
            TotalPages = (_allFiles.Count + PageManager.PageSize - 1) / PageManager.PageSize;
            PageNumbers = new ObservableCollection<int>(Enumerable.Range(1, TotalPages));
            await ShowPageAsync(0);
        }
    }

    // ==================== Similar Image Search ====================

    [RelayCommand]
    private async Task SearchSimilarAsync(string filePath)
    {
        StatusText = "正在搜索相似图片...";
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();

        if (!IsShowingSearchResult)
            _pageManager.SavePreSearchState(Images, CurrentPage);

        try
        {
            var results = await _similarService.FindSimilarAsync(
                filePath, GetSearchScopeFiles(), 5, _searchCts.Token);

            _tagSearch.SearchResultFiles = results.ToList();

            if (_tagSearch.SearchResultFiles.Count == 0)
            {
                StatusText = "未找到相似图片";
                OnPropertyChanged(nameof(HasSearchResults));
                return;
            }

            StatusText = $"找到 {_tagSearch.SearchResultFiles.Count} 张相似图片";
            OnPropertyChanged(nameof(HasSearchResults));
            _currentResultIndex = 0;
            await NavigateToResultAsync();
        }
        catch (OperationCanceledException)
        {
            StatusText = "搜索已取消";
        }
    }

    [RelayCommand]
    private void StopSearch()
    {
    }

    [RelayCommand]
    private async Task PrevResult()
    {
        var total = _tagSearch.SearchResultFiles.Count;
        if (total == 0) return;
        _currentResultIndex = (_currentResultIndex - 1 + total) % total;
        await NavigateToResultAsync();
    }

    [RelayCommand]
    private async Task NextResult()
    {
        var total = _tagSearch.SearchResultFiles.Count;
        if (total == 0) return;
        _currentResultIndex = (_currentResultIndex + 1) % total;
        await NavigateToResultAsync();
    }

    private async Task NavigateToResultAsync()
    {
        // === 阶段0：版本控制 ===
        var navigationVersion = Interlocked.Increment(ref _resultNavigationVersion);
        bool IsCurrentNavigation() => navigationVersion == Volatile.Read(ref _resultNavigationVersion);

        var files = _tagSearch.SearchResultFiles;
        if (files.Count == 0) return;

        var targetPath = files[_currentResultIndex];
        var targetDir = Path.GetDirectoryName(targetPath) ?? "";

        // === 阶段1：展开并高亮目标文件夹 ===
        bool needSwitchFolder = !ShowAllSubfolders &&
                                !string.Equals(CurrentFolder, targetDir, StringComparison.OrdinalIgnoreCase);
        bool shouldScroll = ShowAllSubfolders || needSwitchFolder;

        var targetNode = await ExpandAndHighlightFolderAsync(
            targetDir,
            syncSelection: false,
            forceScroll: shouldScroll,
            IsCurrentNavigation);

        if (!IsCurrentNavigation()) return;
        if (targetNode == null)
        {
            StatusText = "无法展开到目标文件夹";
            return;
        }

        // === 阶段2：切换文件夹（轻量级 — 不调用 LoadFolderAsync，避免销毁缓存和搜索状态）===
        if (needSwitchFolder)
        {
            if (!Directory.Exists(targetDir))
            {
                StatusText = "目标文件夹不存在";
                return;
            }

            // 轻量级文件枚举：仅加载目标文件夹的文件列表，不清除缓存和搜索状态
            var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp" };
            var folderFiles = await Task.Run(() =>
            {
                try
                {
                    return Directory.EnumerateFiles(targetDir, "*.*", SearchOption.TopDirectoryOnly)
                        .Where(f => exts.Contains(Path.GetExtension(f)))
                        .ToList();
                }
                catch { return new List<string>(); }
            });

            if (!IsCurrentNavigation()) return;

            if (folderFiles.Count == 0)
            {
                StatusText = "该文件夹内没有图片文件";
                return;
            }

            // 更新文件列表并跳到目标文件所在页
            _allFiles = folderFiles;
            CurrentFolder = targetDir;
            _pageManager.InvalidateCache();
            TotalPages = (folderFiles.Count + PageManager.PageSize - 1) / PageManager.PageSize;
            PageNumbers = new ObservableCollection<int>(Enumerable.Range(1, TotalPages));

            int fileIdx = folderFiles.FindIndex(f =>
                string.Equals(f, targetPath, StringComparison.OrdinalIgnoreCase));
            int page = fileIdx >= 0 ? fileIdx / PageManager.PageSize : 0;
            await ShowPageAsync(page);
            if (!IsCurrentNavigation()) return;

            // 更新文件夹树选中状态（抑制 SelectionChanged 重入）
            _isProgrammaticFolderSelection = true;
            SelectedFolderNode = targetNode;
            AppSettings.LastFolder = targetDir;
            _isProgrammaticFolderSelection = false;
        }

        // === 阶段3：定位并选中目标图片 ===
        var indexInList = ActiveFileList.FindIndex(f =>
            string.Equals(f, targetPath, StringComparison.OrdinalIgnoreCase));

        if (indexInList < 0)
        {
            StatusText = $"目标图片不在当前列表中";
            return;
        }

        // 切换到目标页
        int targetPage = indexInList / PageManager.PageSize;
        if (targetPage != CurrentPage)
        {
            await ShowPageAsync(targetPage);
            if (!IsCurrentNavigation()) return;
        }

        // 选中并滚动到图片
        await SelectAndScrollToImageAsync(targetPath, IsCurrentNavigation);
        if (!IsCurrentNavigation()) return;

        OnPropertyChanged(nameof(SearchResultInfo));
    }

    /// <summary>选中指定图片并滚动到可见区域</summary>
    private void SelectAndScrollToImage(string targetPath)
    {
        var item = Images.FirstOrDefault(i =>
            string.Equals(i.FilePath, targetPath, StringComparison.OrdinalIgnoreCase));

        if (item != null)
        {
            foreach (var img in Images)
                img.IsSelected = false;

            item.IsSelected = true;

            Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                () => ScrollToSelectedRequested?.Invoke(),
                Avalonia.Threading.DispatcherPriority.Background);
        }
    }

    /// <summary>
    /// 等待树节点的UI容器生成完成
    /// </summary>
    private async Task<bool> WaitForTreeContainerAsync(FolderTreeNode node, Func<bool>? isCurrent = null)
    {
        const int maxAttempts = 20;  // 增加到20次，1秒总等待
        const int delayMs = 50;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (isCurrent?.Invoke() == false) return false;

            var ready = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                // 改进检测：节点已展开且子节点已加载
                return TreeScrollToNodeRequested?.GetInvocationList().Length > 0
                       && node.IsExpanded
                       && node.ChildrenLoaded;
            }, Avalonia.Threading.DispatcherPriority.Loaded);

            if (ready)
            {
                await Task.Delay(100);  // 更长的稳定时间
                return true;
            }

            await Task.Delay(delayMs);
        }

        return false;  // 超时返回false
    }

    /// <summary>
    /// 等待 Images 集合更新完成。
    /// 如果 _imagesUpdatedTcs 已在外部提前创建（如在 LoadFolderAsync 之前），则复用已存在的 TCS，
    /// 避免 PageChanged 事件在 LoadFolderAsync 内触发时 TCS 尚未创建的竞态问题。
    /// </summary>
    private async Task WaitForImagesUpdatedAsync(string targetPath, Func<bool>? isCurrent = null)
    {
        // 复用已存在的 TCS（由 NavigateToResultAsync 在 LoadFolderAsync 之前创建），否则创建新的
        if (_imagesUpdatedTcs == null)
            _imagesUpdatedTcs = new TaskCompletionSource<bool>();

        using var cts = new CancellationTokenSource(5000); // 5秒超时
        using var registration = cts.Token.Register(() =>
            _imagesUpdatedTcs.TrySetCanceled());

        try
        {
            // 等待 PageChanged 事件触发（可能已经在 LoadFolderAsync 内触发过了）
            await _imagesUpdatedTcs.Task;

            // 额外延迟确保UI绑定完成
            await Task.Delay(50);

            // 再次检查导航版本
            if (isCurrent?.Invoke() == false) return;
        }
        catch (OperationCanceledException)
        {
            // 超时或取消
            StatusText = "图片加载超时";
        }
        finally
        {
            _imagesUpdatedTcs = null;
        }
    }

    /// <summary>
    /// 异步选中并滚动到指定图片
    /// </summary>
    private async Task SelectAndScrollToImageAsync(string targetPath, Func<bool>? isCurrent = null)
    {
        var item = Images.FirstOrDefault(i =>
            string.Equals(i.FilePath, targetPath, StringComparison.OrdinalIgnoreCase));

        if (item == null) return;

        foreach (var img in Images)
            img.IsSelected = false;

        item.IsSelected = true;

        await Task.Delay(20);
        if (isCurrent?.Invoke() == false) return;

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
            () => ScrollToSelectedRequested?.Invoke(),
            Avalonia.Threading.DispatcherPriority.Loaded);

        await Task.Delay(100);
    }

    [RelayCommand]
    private void BackFromSearch()
    {
        if (!IsShowingSearchResult) return;
        IsShowingSearchResult = false;
        _tagSearch.SearchResultFiles.Clear();
        OnPropertyChanged(nameof(HasSearchResults));

        // Recalculate paging for normal folder view
        TotalPages = (_allFiles.Count + PageManager.PageSize - 1) / PageManager.PageSize;
        PageNumbers = new ObservableCollection<int>(Enumerable.Range(1, TotalPages));

        if (_pageManager.TryRestorePreSearchState(out _, out var pageIndex))
        {
            _ = ShowPageAsync(pageIndex);
            StatusText = $"总文件数: {_allFiles.Count}";
            ScrollRestoreRequested?.Invoke();
        }
        else if (!string.IsNullOrEmpty(CurrentFolder))
        {
            _ = LoadFolderAsync(CurrentFolder);
        }
    }

    // ==================== Duplicate Detection ====================

    [RelayCommand]
    private async Task DetectDuplicatesAsync(string targetDir)
    {
        StatusText = "全局查重中...";
        var (exact, fuzzy) = await _duplicateService.DetectAndMoveDuplicatesAsync(
            _allFiles, targetDir);

        StatusText = $"查重完成: 精确 {exact} 张, 模糊 {fuzzy} 张";
        if (!string.IsNullOrEmpty(CurrentFolder))
            await LoadFolderAsync(CurrentFolder);
    }

    // ==================== Tag Management ====================

    public async Task<RenameResult> RenameTagAsync(string oldName, string newName)
    {
        return await Task.Run(async () =>
        {
            var result = await _tagRepo.RenameTagAsync(oldName, newName);
            if (result == RenameResult.Conflict) return RenameResult.Conflict;

            lock (_tagCacheLock)
            {
                foreach (var tags in _tagCacheByPath.Values)
                {
                    for (int i = 0; i < tags.Count; i++)
                        if (string.Equals(tags[i], oldName, StringComparison.OrdinalIgnoreCase))
                            tags[i] = newName;
                }
            }

            InvalidateTagCountsCache();
            await RefreshTagCountsAsync(forceRefresh: true);
            SyncArtistName(oldName, newName);
            return RenameResult.Success;
        });
    }

    public async Task MergeTagsAsync(string oldName, string newName)
    {
        await Task.Run(async () =>
        {
            await _tagRepo.MergeTagsAsync(oldName, newName);

            lock (_tagCacheLock)
            {
                foreach (var tags in _tagCacheByPath.Values)
                {
                    for (int i = tags.Count - 1; i >= 0; i--)
                    {
                        if (string.Equals(tags[i], oldName, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!tags.Contains(newName, StringComparer.OrdinalIgnoreCase))
                                tags[i] = newName;
                            else
                                tags.RemoveAt(i);
                        }
                    }
                }
            }

            InvalidateTagCountsCache();
            await RefreshTagCountsAsync(forceRefresh: true);
            SyncArtistName(oldName, newName);
        });
    }

    private void SyncArtistName(string oldName, string newName)
    {
        var modelsDir = System.IO.Path.Combine(_thumbCache.CacheDirectory, "models");
        var embPath = System.IO.Path.Combine(modelsDir, "artist_embeddings.bin");
        var namesPath = System.IO.Path.Combine(modelsDir, "artist_names.txt");

        // 更新嵌入库中的画师名
        var emb = _artistStore.Artists.GetValueOrDefault(oldName);
        if (emb != null)
        {
            _artistStore.Add(newName, emb, _artistStore.GetImageCount(oldName));
            // 如果新旧不同名，移除旧条目
            if (!string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
            {
                _artistStore.Remove(oldName);
            }
            _artistStore.Save(embPath);
        }

        // 更新中文库映射
        _chineseLib.Register(newName, newName);
        if (!string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
        {
            _chineseLib.RemoveArtistName(oldName);
        }
        _chineseLib.SaveArtistNames(namesPath);
    }

    public async Task RefreshTagCountsAsync(bool forceRefresh = false)
    {
        lock (_tagCountsCacheLock)
        {
            if (!forceRefresh && _cachedTagCounts != null &&
                DateTime.Now - _tagCountsCacheTime < _tagCountsCacheDuration)
            {
                _tagSearch.AllTagCounts = _cachedTagCounts;
                return;
            }
        }

        var counts = await Task.Run(() => _tagRepo.GetAllTagCountsAsync());

        lock (_tagCountsCacheLock)
        {
            _cachedTagCounts = counts;
            _tagCountsCacheTime = DateTime.Now;
        }

        _tagSearch.AllTagCounts = counts;
    }

    public List<TagCount> GetAllTagCounts()
    {
        lock (_tagCountsCacheLock)
        {
            if (_cachedTagCounts != null &&
                DateTime.Now - _tagCountsCacheTime < _tagCountsCacheDuration)
            {
                return _cachedTagCounts;
            }
        }
        return _tagSearch.AllTagCounts;
    }

    private void InvalidateTagCountsCache()
    {
        lock (_tagCountsCacheLock)
        {
            _cachedTagCounts = null;
            _tagCountsCacheTime = DateTime.MinValue;
        }
    }

    public async Task DeleteTagFromAllImagesAsync(string tagName)
    {
        await _tagRepo.DeleteTagAsync(tagName);

        // Clear from in-memory caches
        lock (_tagCacheLock)
        {
            foreach (var kv in _tagCacheByPath)
                kv.Value.RemoveAll(t => string.Equals(t, tagName, StringComparison.OrdinalIgnoreCase));
        }

        // Update displayed images
        foreach (var img in Images)
        {
            img.Tags.RemoveAll(t => string.Equals(t, tagName, StringComparison.OrdinalIgnoreCase));
            img.NotifyAll();
        }

        InvalidateTagCountsCache();
        await RefreshTagCountsAsync(forceRefresh: true);
    }

    public async Task RefreshImageTagsAsync(string filePath)
    {
        var meta = await _metaRepo.GetByPathAsync(filePath);
        if (meta == null) return;

        var tags = meta.Tags.Select(t => t.Name).ToList();
        lock (_tagCacheLock)
        {
            _tagCacheByPath[filePath] = tags;
        }

        // Update the displayed ImageViewItem if present
        var imgItem = Images.FirstOrDefault(i =>
            string.Equals(i.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (imgItem != null)
        {
            imgItem.Tags = tags;
            imgItem.NotifyAll();
        }
    }

    public async Task SetImageTagsAsync(string filePath, List<string> tags)
    {
        await Task.Run(async () =>
        {
            var meta = await _metaRepo.GetByPathAsync(filePath);
            if (meta != null)
            {
                await _metaRepo.SetTagsAsync(meta.Id, tags);
                await _metaRepo.SetAutoTagStatusByPathAsync(filePath, 0);
            }
        });

        lock (_tagCacheLock)
        {
            _tagCacheByPath[filePath] = tags;
        }
    }

    public async Task AddTagToImageAsync(string filePath, string tag)
    {
        List<string> tags;
        lock (_tagCacheLock)
        {
            tags = _tagCacheByPath.GetValueOrDefault(filePath) ?? new List<string>();
        }
        if (!tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
        {
            tags.Add(tag);
            await SetImageTagsAsync(filePath, tags);
        }
    }

    public async Task RemoveTagFromImageAsync(string filePath, string tag)
    {
        List<string>? tags;
        lock (_tagCacheLock)
        {
            tags = _tagCacheByPath.GetValueOrDefault(filePath);
        }
        if (tags != null && tags.RemoveAll(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            await SetImageTagsAsync(filePath, tags);
        }
    }

    public async Task AddTagToImagesBatchAsync(List<string> filePaths, string tag)
    {
        lock (_tagCacheLock)
        {
            foreach (var path in filePaths)
            {
                var tags = _tagCacheByPath.GetValueOrDefault(path) ?? new List<string>();
                if (!tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                {
                    tags.Add(tag);
                    _tagCacheByPath[path] = tags;
                }
            }
        }

        var pathToId = await _metaRepo.GetIdsByPathsAsync(filePaths);
        if (pathToId.Count > 0)
            await _metaRepo.AddTagToImagesAsync(pathToId.Values.ToList(), tag);

        InvalidateTagCountsCache();
        await RefreshTagCountsAsync(forceRefresh: true);
    }

    public async Task RemoveTagFromImagesBatchAsync(List<string> filePaths, string tag)
    {
        lock (_tagCacheLock)
        {
            foreach (var path in filePaths)
            {
                if (_tagCacheByPath.TryGetValue(path, out var tags))
                    tags.RemoveAll(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
            }
        }

        var pathToId = await _metaRepo.GetIdsByPathsAsync(filePaths);
        if (pathToId.Count > 0)
            await _metaRepo.RemoveTagFromImagesAsync(pathToId.Values.ToList(), tag);

        InvalidateTagCountsCache();
        await RefreshTagCountsAsync(forceRefresh: true);
    }

    public async Task ClearTagsFromImagesBatchAsync(List<string> filePaths)
    {
        lock (_tagCacheLock)
        {
            foreach (var path in filePaths)
                _tagCacheByPath[path] = new List<string>();
        }

        var pathToId = await _metaRepo.GetIdsByPathsAsync(filePaths);
        if (pathToId.Count > 0)
        {
            await _metaRepo.ClearTagsFromImagesAsync(pathToId.Values.ToList());
            foreach (var path in filePaths)
                await _metaRepo.SetAutoTagStatusByPathAsync(path, 0);
        }

        InvalidateTagCountsCache();
        await RefreshTagCountsAsync(forceRefresh: true);
    }

    public List<string> GetTagsForFile(string filePath)
    {
        lock (_tagCacheLock)
        {
            if (_tagCacheByPath.TryGetValue(filePath, out var tags))
                return new List<string>(tags);
            // Cache miss — preload will fill before page renders
            return new List<string>();
        }
    }

    /// <summary>Batch-preload tag cache for a page's worth of files (for subfolder files not yet cached)</summary>
    public async Task PreloadTagsForFilesAsync(List<string> paths)
    {
        List<string> missing;
        lock (_tagCacheLock)
        {
            missing = paths.Where(p => !_tagCacheByPath.ContainsKey(p)).ToList();
        }
        if (missing.Count == 0) return;
        await Task.Run(async () =>
        {
            var tagMap = await _metaRepo.GetTagMapByPathsAsync(missing);
            lock (_tagCacheLock)
            {
                foreach (var path in missing)
                {
                    _tagCacheByPath[path] = tagMap.TryGetValue(path, out var tags)
                        ? tags : new List<string>();
                }
            }
        });
    }

    public void ClearTagCacheForPath(string filePath)
    {
        lock (_tagCacheLock)
        {
            _tagCacheByPath[filePath] = new List<string>();
        }
    }

    public void InvalidatePageCache() => _pageManager.InvalidateCache();

    public async Task<List<string>> GetTagsForFileAsync(string filePath)
    {
        var meta = await _metaRepo.GetByPathAsync(filePath);
        return meta?.Tags.Select(t => t.Name).ToList() ?? new List<string>();
    }

    /// <summary>确保文件列表的 Tag 已加载，返回 FilePath → Tags 字典</summary>
    public async Task<Dictionary<string, List<string>>> EnsureTagsLoadedAsync(List<string> filePaths)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var missingPaths = new List<string>();

        lock (_tagCacheLock)
        {
            foreach (var path in filePaths)
            {
                if (_tagCacheByPath.TryGetValue(path, out var tags))
                    result[path] = tags;
                else
                    missingPaths.Add(path);
            }
        }

        if (missingPaths.Count > 0)
        {
            try
            {
                var tagMap = await _metaRepo.GetTagMapByPathsAsync(missingPaths);
                lock (_tagCacheLock)
                {
                    foreach (var kv in tagMap)
                    {
                        _tagCacheByPath[kv.Key] = kv.Value;
                        result[kv.Key] = kv.Value;
                    }
                    // 对于数据库中也没有的文件，标记为空列表
                    foreach (var path in missingPaths)
                    {
                        if (!result.ContainsKey(path))
                            result[path] = new List<string>();
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error($"EnsureTagsLoadedAsync failed: {ex.Message}");
            }
        }

        return result;
    }

    // ==================== Settings ====================

    public async Task SaveSettingsAsync()
    {
        await Task.Run(() => _settingsRepo.SaveAsync(AppSettings));
    }
}
