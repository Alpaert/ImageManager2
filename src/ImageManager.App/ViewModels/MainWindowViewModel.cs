using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageManager.App.Services;
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
        var files = new List<string>();
        var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp" };
        try
        {
            var dirs = new Queue<string>();
            dirs.Enqueue(root);
            while (dirs.Count > 0)
            {
                var dir = dirs.Dequeue();
                try
                {
                    foreach (var f in Directory.EnumerateFiles(dir))
                        if (exts.Contains(Path.GetExtension(f)))
                            files.Add(f);
                    foreach (var sub in Directory.EnumerateDirectories(dir))
                        dirs.Enqueue(sub);
                }
                catch { }
            }
        }
        catch { }
        return files;
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
    private Dictionary<string, ImageMeta> _metaCache = new(StringComparer.OrdinalIgnoreCase);
    [ObservableProperty] private ImageSortOrder _currentSortOrder = ImageSortOrder.FileNameAsc;
    private int _folderViewRequestVersion;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _precomputeCts;
    private FileSystemWatcher? _folderWatcher;
    private CancellationTokenSource? _folderWatchDebounceCts;
    private CancellationTokenSource? _widthDebounceCts;

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
        await ExpandAndHighlightFolderAsync(folder.Path);

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
        TreeScrollToNodeRequested?.Invoke(folder);
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

    public async Task LoadFolderAsync(string folder)
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
        _tagCacheByPath.Clear();
        _metaCache.Clear();
        BackgroundStatusText = "";
        CurrentPage = 0;
        TotalPages = 0;
        CurrentFolder = folder;
        StartWatchingCurrentFolder();
        await Task.Yield();

        if (showAllSubfolders)
        {
            await RebuildFileListAsync(requestVersion, folder, true);
            return;
        }

        // Check if this folder already has FolderId markers in DB
        var folderInfo = await _folderRepo.GetByPathAsync(folder);
        long? folderId = folderInfo?.Id;
        var exts = new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp" };

        if (folderId.HasValue)
        {
            // Path A: Try indexed DB query first (fast, no disk IO)
            var indexedFiles = await Task.Run(() =>
                _metaRepo.GetByFolderIdAsync(folderId.Value));

            if (indexedFiles.Count > 0)
            {
                _allFiles = await Task.Run(() =>
                    indexedFiles.Select(m => m.FilePath).Where(File.Exists).ToList());

                if (_allFiles.Count == 0)
                {
                    StatusText = "该文件夹内没有图片文件";
                    return;
                }

                TotalPages = (_allFiles.Count + PageManager.PageSize - 1) / PageManager.PageSize;
                PageNumbers = new ObservableCollection<int>(Enumerable.Range(1, TotalPages));
                StatusText = $"总文件数: {_allFiles.Count}";

                int totalTaggedFromDb = 0;
                foreach (var m in indexedFiles)
                {
                    _metaCache[m.FilePath] = m;
                    if (m.Tags.Count > 0)
                    {
                        _tagCacheByPath[m.FilePath] = m.Tags.Select(t => t.Name).ToList();
                        totalTaggedFromDb++;
                    }
                }

                StatusText += $" | 索引图: {indexedFiles.Count} | DB有标签: {totalTaggedFromDb} 张";
                if (totalTaggedFromDb > 0)
                {
                    var sample = indexedFiles.FirstOrDefault(m => m.Tags.Count > 0);
                    StatusText += $" | 示例: {sample?.Tags.FirstOrDefault()?.Name}";
                }

                var lastPage = await _folderRepo.GetLastPageIndexAsync(folder);
                int startPage = lastPage.HasValue && lastPage.Value < TotalPages ? lastPage.Value : 0;
                await ShowPageAsync(startPage);

                // Sync: check for new/removed files, then compute hashes for newcomers
                await SyncFolderAsync(folder, folderId.Value, exts);
                _precomputeCts?.Cancel();
                _precomputeCts?.Dispose();
                _precomputeCts = new CancellationTokenSource();
                // Delay hash precomputation to avoid competing with initial thumbnail loading
                var captureCt = _precomputeCts.Token;
                _ = Task.Run(async () =>
                {
                    try { await Task.Delay(3000, captureCt); }
                    catch { return; }
                    await PrecomputeHashesAsync(captureCt, folderId.Value);
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

        // Load tag cache (must complete before ShowPageAsync so thumbnails show tags)
        var tagLoadTask = Task.Run(async () =>
        {
            try
            {
                var metas = await _metaRepo.GetByFolderAsync(folder);
                int dbTagged = 0;
                foreach (var m in metas)
                {
                    _metaCache[m.FilePath] = m;
                    if (m.Tags.Count > 0)
                    {
                        _tagCacheByPath[m.FilePath] = m.Tags.Select(t => t.Name).ToList();
                        dbTagged++;
                    }
                }
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    StatusText = $"总文件数: {_allFiles.Count} | DB图: {metas.Count} | DB有标签: {dbTagged} 张");
            }
            catch (Exception ex)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    StatusText = $"标签加载失败: {ex.Message}");
            }
        });

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

        await tagLoadTask;

        var lastPage2 = await _folderRepo.GetLastPageIndexAsync(folder);
        int startPage2 = lastPage2.HasValue && lastPage2.Value < TotalPages ? lastPage2.Value : 0;

        StatusText = $"总文件数: {_allFiles.Count}";
        await ShowPageAsync(startPage2);
    }

    /// <summary>Public wrapper for code-behind: sync current folder and refresh UI, then compute missing hashes</summary>
    public async Task SyncCurrentFolderAsync()
    {
        if (string.IsNullOrEmpty(CurrentFolder)) return;
        var fi = await _folderRepo.GetByPathAsync(CurrentFolder);
        if (fi == null) return;
        var exts = new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp" };
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
        var ext = Path.GetExtension(e.Name).ToLowerInvariant();
        if (ext != ".jpg" && ext != ".jpeg" && ext != ".png" && ext != ".bmp" && ext != ".gif" && ext != ".webp")
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
        var ext = Path.GetExtension(e.Name).ToLowerInvariant();
        if (ext != ".jpg" && ext != ".jpeg" && ext != ".png" && ext != ".bmp" && ext != ".gif" && ext != ".webp")
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
    private async Task<bool> SyncFolderAsync(string folder, long folderId, string[] exts)
    {
        try
        {
            var diskFiles = new HashSet<string>(
                Directory.EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(f => exts.Contains(Path.GetExtension(f).ToLower())),
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
                                _tagCacheByPath[file] = tagNames;
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

        var needsHashing = files.Where(f => !existingSet.Contains(f)).ToList();
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

    public async Task<FolderTreeNode?> ExpandAndHighlightFolderAsync(string targetPath, bool syncSelection = false)
    {
        ClearSearchHighlight();
        var parts = new List<string>();
        var current = targetPath;
        while (!string.IsNullOrEmpty(current) && current.Length > 3)
        {
            parts.Insert(0, current);
            current = Path.GetDirectoryName(current) ?? "";
        }

        // Find deepest part that already exists in the tree
        int startIdx = parts.Count - 1;
        for (; startIdx >= 0; startIdx--)
        {
            if (FindNodeByPath(parts[startIdx]) != null) break;
        }
        if (startIdx < 0) return null; // no part exists in tree

        // Expand from found ancestor down to target
        FolderTreeNode? node = null;
        for (int i = startIdx; i < parts.Count; i++)
        {
            node = FindNodeByPath(parts[i]);
            if (node == null) break;
            if (i < parts.Count - 1)
            {
                node.IsExpanded = true;
                if (node.LoadTask != null) await node.LoadTask;
            }
        }

        if (node != null)
        {
            node.IsSearchHighlight = true;
            if (syncSelection)
            {
                _isProgrammaticFolderSelection = true;
                SelectedFolderNode = node;
                _isProgrammaticFolderSelection = false;
            }
            Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                () => TreeScrollToNodeRequested?.Invoke(node),
                Avalonia.Threading.DispatcherPriority.Background);
        }
        return node;
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
                    .Where(f => new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp" }
                        .Contains(Path.GetExtension(f).ToLower()))
                    .ToList());

        if (!IsFolderViewRequestCurrent(requestVersion, folderPath, showAllSubfolders))
            return;

        // Preload tags for subfolder files BEFORE SortImagesAsync (which triggers ShowPageAsync)
        int subTagCount = 0;
        if (showAllSubfolders)
        {
            try
            {
                var metas = await Task.Run(() => _metaRepo.GetByFolderAsync(folderPath));
                if (!IsFolderViewRequestCurrent(requestVersion, folderPath, showAllSubfolders))
                    return;
                foreach (var m in metas)
                {
                    _metaCache[m.FilePath] = m;
                    if (m.Tags.Count > 0 && !_tagCacheByPath.ContainsKey(m.FilePath))
                    {
                        _tagCacheByPath[m.FilePath] = m.Tags.Select(t => t.Name).ToList();
                        subTagCount++;
                    }
                }
            }
            catch { }
        }

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

        StatusText = subTagCount > 0
            ? $"总文件数: {_allFiles.Count} (含子文件夹) | 标签: {subTagCount}"
            : $"总文件数: {_allFiles.Count}";

        if (showAllSubfolders)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(3000);
                if (!IsFolderViewRequestCurrent(requestVersion, folderPath, showAllSubfolders))
                    return;
                await PrecomputeHashesAsync(CancellationToken.None, (await _folderRepo.GetByPathAsync(folderPath))?.Id);
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
            _ = NavigateToResultAsync();
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
        var files = _tagSearch.SearchResultFiles;
        if (files.Count == 0) return;
        var targetPath = files[_currentResultIndex];
        var targetDir = Path.GetDirectoryName(targetPath) ?? "";
        _ = ExpandAndHighlightFolderAsync(targetDir);

        var indexInList = ActiveFileList.FindIndex(f =>
            string.Equals(f, targetPath, StringComparison.OrdinalIgnoreCase));
        if (indexInList < 0)
        {
            // Target in subfolder — switch to that folder's view
            if (!string.IsNullOrEmpty(targetDir) && Directory.Exists(targetDir))
            {
                await LoadFolderAsync(targetDir);
                indexInList = ActiveFileList.FindIndex(f =>
                    string.Equals(f, targetPath, StringComparison.OrdinalIgnoreCase));
            }
            if (indexInList < 0) return;
        }

        int targetPage = indexInList / PageManager.PageSize;
        if (targetPage != CurrentPage)
            await ShowPageAsync(targetPage);

        var item = Images.FirstOrDefault(i =>
            string.Equals(i.FilePath, targetPath, StringComparison.OrdinalIgnoreCase));
        if (item != null)
        {
            foreach (var img in Images) img.IsSelected = false;
            item.IsSelected = true;
            Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                () => ScrollToSelectedRequested?.Invoke(),
                Avalonia.Threading.DispatcherPriority.Background);
        }

        OnPropertyChanged(nameof(SearchResultInfo));
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

            foreach (var tags in _tagCacheByPath.Values)
            {
                for (int i = 0; i < tags.Count; i++)
                    if (string.Equals(tags[i], oldName, StringComparison.OrdinalIgnoreCase))
                        tags[i] = newName;
            }

            _tagSearch.AllTagCounts = await _tagRepo.GetAllTagCountsAsync();
            SyncArtistName(oldName, newName);
            return RenameResult.Success;
        });
    }

    public async Task MergeTagsAsync(string oldName, string newName)
    {
        await Task.Run(async () =>
        {
            await _tagRepo.MergeTagsAsync(oldName, newName);

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

            _tagSearch.AllTagCounts = await _tagRepo.GetAllTagCountsAsync();
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

    public async Task RefreshTagCountsAsync()
    {
        _tagSearch.AllTagCounts = await Task.Run(() => _tagRepo.GetAllTagCountsAsync());
    }

    public List<TagCount> GetAllTagCounts() => _tagSearch.AllTagCounts;

    public async Task DeleteTagFromAllImagesAsync(string tagName)
    {
        await _tagRepo.DeleteTagAsync(tagName);

        // Clear from in-memory caches
        foreach (var kv in _tagCacheByPath)
            kv.Value.RemoveAll(t => string.Equals(t, tagName, StringComparison.OrdinalIgnoreCase));

        // Update displayed images
        foreach (var img in Images)
        {
            img.Tags.RemoveAll(t => string.Equals(t, tagName, StringComparison.OrdinalIgnoreCase));
            img.NotifyAll();
        }

        await RefreshTagCountsAsync();
    }

    public async Task RefreshImageTagsAsync(string filePath)
    {
        var meta = await _metaRepo.GetByPathAsync(filePath);
        if (meta == null) return;

        var tags = meta.Tags.Select(t => t.Name).ToList();
        _tagCacheByPath[filePath] = tags;

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

        _tagCacheByPath[filePath] = tags;
    }

    public async Task AddTagToImageAsync(string filePath, string tag)
    {
        var tags = _tagCacheByPath.GetValueOrDefault(filePath) ?? new List<string>();
        if (!tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
        {
            tags.Add(tag);
            await SetImageTagsAsync(filePath, tags);
        }
    }

    public async Task RemoveTagFromImageAsync(string filePath, string tag)
    {
        var tags = _tagCacheByPath.GetValueOrDefault(filePath);
        if (tags != null && tags.RemoveAll(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            await SetImageTagsAsync(filePath, tags);
        }
    }

    public async Task AddTagToImagesBatchAsync(List<string> filePaths, string tag)
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

        var pathToId = await _metaRepo.GetIdsByPathsAsync(filePaths);
        if (pathToId.Count > 0)
            await _metaRepo.AddTagToImagesAsync(pathToId.Values.ToList(), tag);

        await RefreshTagCountsAsync();
    }

    public async Task RemoveTagFromImagesBatchAsync(List<string> filePaths, string tag)
    {
        foreach (var path in filePaths)
        {
            if (_tagCacheByPath.TryGetValue(path, out var tags))
                tags.RemoveAll(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
        }

        var pathToId = await _metaRepo.GetIdsByPathsAsync(filePaths);
        if (pathToId.Count > 0)
            await _metaRepo.RemoveTagFromImagesAsync(pathToId.Values.ToList(), tag);

        await RefreshTagCountsAsync();
    }

    public async Task ClearTagsFromImagesBatchAsync(List<string> filePaths)
    {
        foreach (var path in filePaths)
            _tagCacheByPath[path] = new List<string>();

        var pathToId = await _metaRepo.GetIdsByPathsAsync(filePaths);
        if (pathToId.Count > 0)
        {
            await _metaRepo.ClearTagsFromImagesAsync(pathToId.Values.ToList());
            foreach (var path in filePaths)
                await _metaRepo.SetAutoTagStatusByPathAsync(path, 0);
        }

        await RefreshTagCountsAsync();
    }

    public List<string> GetTagsForFile(string filePath)
    {
        if (_tagCacheByPath.TryGetValue(filePath, out var tags))
            return new List<string>(tags);
        // Cache miss — preload will fill before page renders
        return new List<string>();
    }

    /// <summary>Batch-preload tag cache for a page's worth of files (for subfolder files not yet cached)</summary>
    public async Task PreloadTagsForFilesAsync(List<string> paths)
    {
        var missing = paths.Where(p => !_tagCacheByPath.ContainsKey(p)).ToList();
        if (missing.Count == 0) return;
        await Task.Run(async () =>
        {
            foreach (var path in missing)
            {
                var meta = await _metaRepo.GetByPathAsync(path);
                _tagCacheByPath[path] = meta?.Tags.Select(t => t.Name).ToList() ?? new List<string>();
            }
        });
    }

    public void ClearTagCacheForPath(string filePath)
    {
        _tagCacheByPath[filePath] = new List<string>();
    }

    public void InvalidatePageCache() => _pageManager.InvalidateCache();

    public async Task<List<string>> GetTagsForFileAsync(string filePath)
    {
        var meta = await _metaRepo.GetByPathAsync(filePath);
        return meta?.Tags.Select(t => t.Name).ToList() ?? new List<string>();
    }

    // ==================== Settings ====================

    public async Task SaveSettingsAsync()
    {
        await Task.Run(() => _settingsRepo.SaveAsync(AppSettings));
    }
}
