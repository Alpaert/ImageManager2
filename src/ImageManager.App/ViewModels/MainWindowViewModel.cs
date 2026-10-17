using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using ImageManager.App.Models;
using ImageManager.App.Services;
using ImageManager.Common.Constants;
using ImageManager.Core.Models;
using ImageManager.Core.Services;
using ImageManager.Common.Helpers;

namespace ImageManager.App.ViewModels;

public enum ImageSortOrder
{
    FileNameAsc, FileNameDesc,
    ModifiedAsc, ModifiedDesc,
    FileSizeAsc, FileSizeDesc,
    ResolutionAsc, ResolutionDesc
}

public enum ImageSortScope
{
    AllFiles,
    FolderGrouped
}

internal readonly record struct FileEnumerationResult(List<string> Files, bool Canceled);

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly ISettingsRepository _settingsRepo;
    private readonly IFolderRepository _folderRepo;
    private readonly IImageMetaRepository _metaRepo;
    private readonly ITagRepository _tagRepo;
    private readonly ICharacterTagSuppressionRepository _characterTagSuppressionRepo;
    private readonly ISimilarImageService _similarService;
    private readonly IDuplicateService _duplicateService;
    private readonly IThumbnailCacheService _thumbCache;

    // Tag counts cache
    private List<TagCount>? _cachedTagCounts;
    private DateTime _tagCountsCacheTime = DateTime.MinValue;
    private readonly TimeSpan _tagCountsCacheDuration = TimeSpan.FromMinutes(5);
    private readonly object _tagCountsCacheLock = new();

    // ==================== Settings ====================
    [ObservableProperty] private AppSettings _appSettings = new();

    // Observable mirror properties for UI bindings (AppSettings is POCO, no INPC)
    [ObservableProperty] private string _waterfallMode = "None";
    [ObservableProperty] private string _searchToolbarLayout = "A";
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

    public PageManager PageManager => _pageManager;
    public IFolderRepository FolderRepository => _folderRepo;
    public Task ClearThumbnailCacheAsync() => _thumbCache.ClearAsync();

    public void SyncUISettingsFromAppData()
    {
        AppSettings.SearchToolbarLayout = NormalizeSearchToolbarLayout(AppSettings.SearchToolbarLayout);
        AppSettings.ImageDisplayMode = NormalizeImageDisplayMode(AppSettings.ImageDisplayMode);
        CurrentSortScope = Enum.TryParse<ImageSortScope>(AppSettings.ImageSortScope, true, out var sortScope) &&
                           Enum.IsDefined(typeof(ImageSortScope), sortScope)
            ? sortScope
            : ImageSortScope.FolderGrouped;
        CurrentSortOrder = Enum.TryParse<ImageSortOrder>(AppSettings.ImageSortOrder, true, out var sortOrder) &&
                           Enum.IsDefined(typeof(ImageSortOrder), sortOrder)
            ? sortOrder
            : ImageSortOrder.FileNameAsc;
        AppSettings.ImageSortScope = CurrentSortScope.ToString();
        AppSettings.ImageSortOrder = CurrentSortOrder.ToString();
        SearchToolbarLayout = AppSettings.SearchToolbarLayout;
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
        DisplayMode = Enum.Parse<ImageDisplayMode>(AppSettings.ImageDisplayMode);
        OnPropertyChanged(nameof(IsPagedDisplay));
    }

    public bool IsSearchToolbarLayoutA => string.Equals(SearchToolbarLayout, "A", StringComparison.OrdinalIgnoreCase);
    public bool IsSearchToolbarLayoutC => string.Equals(SearchToolbarLayout, "C", StringComparison.OrdinalIgnoreCase);
    public bool IsTagSearchPopupOpenA => IsSearchToolbarLayoutA && IsTagSearchPopupOpen;
    public bool IsTagSearchPopupOpenC => IsSearchToolbarLayoutC && IsTagSearchPopupOpen;

    public void SetSearchToolbarLayout(string? layout)
    {
        var normalized = NormalizeSearchToolbarLayout(layout);
        if (string.Equals(SearchToolbarLayout, normalized, StringComparison.OrdinalIgnoreCase))
        {
            AppSettings.SearchToolbarLayout = normalized;
            return;
        }

        DismissTagSearchSuggestions();
        SearchToolbarLayout = normalized;
        AppSettings.SearchToolbarLayout = normalized;
    }

    private static string NormalizeSearchToolbarLayout(string? layout) =>
        string.Equals(layout, "C", StringComparison.OrdinalIgnoreCase) ? "C" : "A";

    private string NormalizeImageDisplayMode(string? mode) =>
        IsContinuousDisplayAvailable &&
        string.Equals(mode, nameof(ImageDisplayMode.Continuous), StringComparison.OrdinalIgnoreCase)
            ? nameof(ImageDisplayMode.Continuous)
            : nameof(ImageDisplayMode.Paged);

    partial void OnSearchToolbarLayoutChanged(string value)
    {
        OnPropertyChanged(nameof(IsSearchToolbarLayoutA));
        OnPropertyChanged(nameof(IsSearchToolbarLayoutC));
        OnPropertyChanged(nameof(IsTagSearchPopupOpenA));
        OnPropertyChanged(nameof(IsTagSearchPopupOpenC));
    }

    partial void OnIsTagSearchPopupOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(IsTagSearchPopupOpenA));
        OnPropertyChanged(nameof(IsTagSearchPopupOpenC));
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
    [ObservableProperty] private int _selectedSimilarityMode;
    [ObservableProperty] private string _naturalLanguageQuery = string.Empty;
    private static readonly SimilaritySearchMode[] SimilaritySearchModeValues =
        [SimilaritySearchMode.Perceptual, SimilaritySearchMode.Semantic, SimilaritySearchMode.Atmosphere, SimilaritySearchMode.Color];
    public IReadOnlyList<string> SimilaritySearchModes { get; } =
        ["感知相似", "语义相似", "氛围相似", "颜色相似"];
    private int _currentResultIndex;
    private readonly Dictionary<string, float> _similarityScores = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SimilarityMatchKind> _similarityMatchKinds = new(StringComparer.OrdinalIgnoreCase);
    public string SearchResultInfo => _tagSearch.SearchResultFiles.Count > 0
        ? FormatSearchResultInfo()
        : "";
    public bool HasSearchResults => _tagSearch.SearchResultFiles.Count > 0;
    public event Action? ScrollToSelectedRequested;
    public event Action? ScrollSearchResultsToTopRequested;
    public event Action<FolderTreeNode>? TreeScrollToNodeRequested;

    private async Task<List<string>> GetSearchScopeFilesAsync()
    {
        if (SearchScope == 0 || string.IsNullOrEmpty(CurrentFolder))
            return _allFiles;
        var result = await EnumerateMediaFilesAsync(CurrentFolder, recursive: true, CurrentFolderWorkToken);
        return result.Canceled ? _allFiles : result.Files;
    }

    private static Task<FileEnumerationResult> EnumerateMediaFilesAsync(
        string root,
        bool recursive,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var files = new List<string>();
            try
            {
                var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                foreach (var file in Directory.EnumerateFiles(root, "*.*", searchOption))
                {
                    if (ct.IsCancellationRequested)
                        return new FileEnumerationResult(files, true);
                    if (FileTypeConstants.IsMediaFile(file))
                        files.Add(file);
                }
            }
            catch
            {
                return new FileEnumerationResult(new List<string>(), false);
            }

            return new FileEnumerationResult(files, ct.IsCancellationRequested);
        }, CancellationToken.None);
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
    private int _fileListMutationVersion;
    private FileSortSnapshot? _fileSortSnapshot;
    private readonly FileSortSnapshotBuilder _fileSortSnapshotBuilder = new();
    [ObservableProperty] private int _currentPage;
    [ObservableProperty] private int _totalPages;
    [ObservableProperty] private ObservableCollection<int> _pageNumbers = new();
    [ObservableProperty] private ImageDisplayMode _displayMode = ImageDisplayMode.Paged;

    /// <summary>
    /// Continuous mode remains disabled until it can create only a bounded
    /// viewport window of controls and thumbnail requests.
    /// </summary>
    public bool IsContinuousDisplayAvailable => true;
    public bool IsPagedDisplay => DisplayMode == ImageDisplayMode.Paged;

    /// <summary>Published display snapshot, shared by paging and selection.</summary>
    public List<string> ActiveFileList => _displayFilteredFiles;

    // Continuous display keeps a complete lightweight geometry snapshot, but it
    // must never turn that snapshot into a full set of view items or thumbnails.
    // The UI integration will request this explicitly once its virtualizing panel
    // is ready.
    private readonly ContinuousDisplayGeometryBuilder _continuousDisplayGeometryBuilder = new();
    private CancellationTokenSource? _continuousDisplayGeometryCts;
    private CancellationTokenSource? _continuousDimensionRepairCts;
    private readonly ConcurrentDictionary<string, (int Width, int Height)> _continuousResolvedDimensions =
        new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _continuousViewportThumbnailCts;
    private ContinuousImageItemSource? _continuousViewportSource;
    private ImageDisplayRange _continuousVisibleRange;
    private ImageDisplayRange _continuousPrefetchRange;
    private int _continuousDisplayGeometryVersion;
    [ObservableProperty] private ContinuousDisplayGeometrySnapshot? _continuousDisplayGeometrySnapshot;
    [ObservableProperty] private ImageDisplayGeometryIndex? _continuousDisplayGeometryIndex;
    [ObservableProperty] private ContinuousImageItemSource? _continuousImageItemSource;
    [ObservableProperty] private bool _isContinuousGeometryBuilding;

    /// <summary>
    /// Builds a frozen, complete geometry snapshot for a future virtualized
    /// continuous display. It intentionally does not create thumbnail work or
    /// materialize all <see cref="ImageViewItem"/> instances.
    /// </summary>
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
    [ObservableProperty] private ObservableCollection<TagCount> _tagSearchSuggestions = new();
    [ObservableProperty] private bool _isTagSearchPopupOpen;
    [ObservableProperty] private string _coTagFilterText = string.Empty;
    private enum TagSuggestionMode { None, FocusCoTags, TypedSuggestions, SearchResultCoTags }
    private readonly List<TagCount> _coTagSuggestionBase = new();
    private List<string> _coTagSuggestionFiles = new();
    private bool _coTagSuggestionBaseLoaded;
    private CancellationTokenSource? _tagSuggestionCts;
    private int _tagSuggestionVersion;
    private CancellationTokenSource? _coTagFilterCts;
    private readonly object _coTagFilterCtsSync = new();
    private int _coTagFilterVersion;
    private int _searchResultSuggestionVersion = -1;
    private TagSuggestionMode _tagSuggestionMode;
    private bool _isTagSearchInputFocused;
    private bool _suppressCoTagFilterChange;
    private bool _suppressTagSearchTextChange;

    // ==================== Status ====================
    [ObservableProperty] private string _statusText = "就绪";
    [ObservableProperty] private string _backgroundStatusText = string.Empty;
    [ObservableProperty] private string _loadedInfoText = string.Empty;

    [ObservableProperty] private bool _isAutoTagRunning;

    [RelayCommand]
    private async Task StopAutoTag()
    {
        StatusText = "正在停止自动打标...";
        if (_autoTagOrchestrator != null)
        {
            await _autoTagOrchestrator.CancelAsync();
        }
    }
    [ObservableProperty] private bool _isShowingSearchResult;

    public bool IsSuggestionCoTagMode =>
        _tagSuggestionMode is TagSuggestionMode.FocusCoTags or TagSuggestionMode.SearchResultCoTags;

    public int GetCoTagState(string tagName) => _tagSearch.GetCoTagState(tagName);

    public string SearchBoxBorderColor => _tagSearch.SearchBoxBorderColor(TagSearchText);

    // ==================== Page Manager ====================
    private readonly PageManager _pageManager;
    private readonly ITagSearchEngine _tagSearch;
    private readonly CommunityToolkit.Mvvm.Messaging.IMessenger _messenger;
    private readonly IHashService? _hashService;
    private readonly ImageManager.Core.Services.IDispatcher _dispatcher;

    // ==================== Cache ====================
    private readonly ConcurrentDictionary<string, string> _phashCache = new(StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, List<string>> _tagCacheByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _tagCacheLock = new();
    private Dictionary<string, ImageMeta> _metaCache = new(StringComparer.OrdinalIgnoreCase);
    [ObservableProperty] private ImageSortOrder _currentSortOrder = ImageSortOrder.FileNameAsc;
    [ObservableProperty] private ImageSortScope _currentSortScope = ImageSortScope.FolderGrouped;
    private int _folderViewRequestVersion;
    private int _sortRequestVersion;
    private CancellationTokenSource _sortCts = new();
    private CancellationTokenSource? _searchCts;
    private int _searchRequestVersion;
    private readonly SemaphoreSlim _hashGate = new(1, 1);
    private readonly SemaphoreSlim _maintenanceGate = new(1, 1);
    private CancellationTokenSource? _hashCts;
    private CancellationTokenSource _folderWorkCts = new();
    private DispatcherTimer? _idleTimer;
    private DateTime _deferMaintenanceUntilUtc = DateTime.MinValue;
    private FileSystemWatcher? _folderWatcher;
    private CancellationTokenSource? _folderWatchDebounceCts;
    private int _resultNavigationVersion;
    private HashSet<string>? _pendingSelectionRestorePaths;
    private readonly HashSet<string> _selectedFilePaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Canonical selection state for both paged and continuous display. UI items
    /// are projections of these paths and may be recycled at any time.
    /// </summary>
    public IReadOnlyCollection<string> SelectedFilePaths => _selectedFilePaths;

    public bool IsFileSelected(string? filePath) =>
        !string.IsNullOrWhiteSpace(filePath) && _selectedFilePaths.Contains(filePath);

    public void ReplaceSelectedFiles(IEnumerable<string> filePaths)
    {
        _selectedFilePaths.Clear();
        foreach (var filePath in filePaths.Where(path => !string.IsNullOrWhiteSpace(path)))
            _selectedFilePaths.Add(filePath);
        ApplySelectionToRealizedItems();
    }

    public void SetFileSelected(string filePath, bool selected)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return;
        if (selected)
            _selectedFilePaths.Add(filePath);
        else
            _selectedFilePaths.Remove(filePath);
        ApplySelectionToRealizedItems();
    }

    public IReadOnlyList<ImageViewItem> GetSelectedRealizedItems() =>
        Images.Where(item => IsFileSelected(item.FilePath)).ToList();

    public void ApplySelectionToRealizedItems()
    {
        foreach (var item in Images)
            item.IsSelected = IsFileSelected(item.FilePath);
        if (ContinuousImageItemSource is not null)
        {
            foreach (var item in ContinuousImageItemSource.GetAllCachedItems())
                item.IsSelected = IsFileSelected(item.FilePath);
        }
    }

    // 用于精确等待 Images 集合更新完成的信号
    private TaskCompletionSource<bool>? _imagesUpdatedTcs;

    private readonly IArtistEmbeddingStore _artistStore;
    private readonly IChineseTagLibrary _chineseLib;
    private readonly ICharacterEmbeddingStore _characterStore;
    private bool _characterStoreLoadAttempted;

    private readonly ICharacterMatchingService? _characterMatchingService;
    private readonly IMediaMoveService? _mediaMoveService;
    private readonly IMediaThumbnailPrewarmService? _mediaThumbnailPrewarmService;
    private readonly IVideoOriginalFrameCacheService? _videoOriginalFrameCacheService;
    private readonly IAutoTagOrchestrator? _autoTagOrchestrator;
    private readonly ILibraryBuilderService? _libraryBuilderService;
    private readonly IAiRecommendService? _aiRecommendService;
    private readonly ITagMappingRepository? _tagMappingRepo;
    private readonly IVectorIndexService? _vectorIndexService;
    private readonly IChineseClipService? _chineseClipService;
    private readonly IVideoService? _videoService;
    private readonly object _backgroundTasksLock = new();
    private CancellationTokenSource? _characterMatchCts;
    private long _characterMatchOpId;
    private CancellationTokenSource? _mediaMoveCts;
    private CancellationTokenSource? _mediaCopyCts;
    private CancellationTokenSource? _mediaDeleteCts;
    private CancellationTokenSource? _duplicateDetectCts;

    public MainWindowViewModel(
        ISettingsRepository settingsRepo,
        IFolderRepository folderRepo,
        IImageMetaRepository metaRepo,
        ITagRepository tagRepo,
        ICharacterTagSuppressionRepository characterTagSuppressionRepo,
        ISimilarImageService similarService,
        IDuplicateService duplicateService,
        IThumbnailCacheService thumbCache,
        PageManager pageManager,
        ITagSearchEngine tagSearch,
        IArtistEmbeddingStore artistStore,
        IChineseTagLibrary chineseLib,
        ICharacterEmbeddingStore characterStore,
        CommunityToolkit.Mvvm.Messaging.IMessenger messenger,
        ImageManager.Core.Services.IDispatcher dispatcher,
        ICharacterMatchingService? characterMatchingService = null,
        IMediaMoveService? mediaMoveService = null,
        IMediaThumbnailPrewarmService? mediaThumbnailPrewarmService = null,
        IVideoOriginalFrameCacheService? videoOriginalFrameCacheService = null,
        IAutoTagOrchestrator? autoTagOrchestrator = null,
        ILibraryBuilderService? libraryBuilderService = null,
        IAiRecommendService? aiRecommendService = null,
        ITagMappingRepository? tagMappingRepo = null,
        IVectorIndexService? vectorIndexService = null,
        IChineseClipService? chineseClipService = null,
        IVideoService? videoService = null,
        IHashService? hashService = null)
    {
        _settingsRepo = settingsRepo;
        _folderRepo = folderRepo;
        _metaRepo = metaRepo;
        _tagRepo = tagRepo;
        _characterTagSuppressionRepo = characterTagSuppressionRepo;
        _similarService = similarService;
        _duplicateService = duplicateService;
        _thumbCache = thumbCache;
        _pageManager = pageManager;
        _tagSearch = tagSearch;
        _artistStore = artistStore;
        _chineseLib = chineseLib;
        _characterStore = characterStore;
        _messenger = messenger;
        _dispatcher = dispatcher;
        _characterMatchingService = characterMatchingService;
        _mediaMoveService = mediaMoveService;
        _mediaThumbnailPrewarmService = mediaThumbnailPrewarmService;
        _videoOriginalFrameCacheService = videoOriginalFrameCacheService;
        _autoTagOrchestrator = autoTagOrchestrator;
        _libraryBuilderService = libraryBuilderService;
        _aiRecommendService = aiRecommendService;
        _tagMappingRepo = tagMappingRepo;
        _vectorIndexService = vectorIndexService;
        _chineseClipService = chineseClipService;
        _videoService = videoService;
        _hashService = hashService;

        _pageManager.PageChanged += args =>
        {
            Images = new ObservableCollection<ImageViewItem>(args.Items);
            RestorePendingSelection(Images);
            ApplySelectionToRealizedItems();
            _isNavigating = true;
            CurrentPage = args.PageIndex;
            _isNavigating = false;
            LoadedInfoText = args.LoadedInfoText;

            // 发送 Images 更新完成信号
            _imagesUpdatedTcs?.TrySetResult(true);
        };
        _pageManager.ThumbnailRefreshRequested += RefreshContinuousViewportThumbnailsAfterZoom;

        // Tag search completed
        messenger.Register<MainWindowViewModel, ImageManager.Core.Messages.TagSearchCompletedMessage>(this, (r, msg) =>
        {
            _dispatcher.Post(() =>
            {
                InvalidateTagSuggestionRequest();
                _searchResultSuggestionVersion = _tagSuggestionVersion;
                SetCoTagFilterTextWithoutRefresh(string.Empty);
                _tagSuggestionMode = _tagSearch.IsSuggestionCoTagMode && msg.ResultFiles.Count > 0
                    ? TagSuggestionMode.SearchResultCoTags
                    : TagSuggestionMode.None;
                _coTagSuggestionBase.Clear();
                _coTagSuggestionBaseLoaded = false;
                _coTagSuggestionFiles = msg.ResultFiles.ToList();
                OnPropertyChanged(nameof(IsSuggestionCoTagMode));

                _ = ApplyTagSearchDisplayAsync();

                // The result list is already available. Co-tag aggregation is the
                // expensive secondary query and must never run on the UI thread.
                if (_tagSuggestionMode == TagSuggestionMode.SearchResultCoTags)
                    _ = _tagSearch.RefreshCurrentCoTagSuggestionsAsync();
            });
        });

        // Suggestions changed
        messenger.Register<MainWindowViewModel, ImageManager.Core.Messages.TagSearchSuggestionsChangedMessage>(this, (r, msg) =>
        {
            _dispatcher.Post(() =>
            {
                if (!_tagSearch.IsSuggestionCoTagMode ||
                    _tagSuggestionMode != TagSuggestionMode.SearchResultCoTags ||
                    _tagSuggestionVersion != _searchResultSuggestionVersion)
                    return;

                _tagSuggestionMode = TagSuggestionMode.SearchResultCoTags;
                _coTagSuggestionBase.Clear();
                _coTagSuggestionBase.AddRange(msg.Suggestions);
                _coTagSuggestionBaseLoaded = true;
                _coTagSuggestionFiles = _tagSearch.SearchResultFiles.ToList();
                if (_isTagSearchInputFocused)
                {
                    if (string.IsNullOrWhiteSpace(CoTagFilterText))
                        PublishTagSuggestions(msg.Suggestions);
                    else
                        FilterCoTagSuggestions(CoTagFilterText);
                }
                OnPropertyChanged(nameof(IsSuggestionCoTagMode));
            });
        });

        // Co-tag cycled
        messenger.Register<MainWindowViewModel, ImageManager.Core.Messages.CoTagCycledMessage>(this, (r, msg) =>
        {
            OnPropertyChanged(nameof(SearchBoxBorderColor));
        });

        // Co-tag mode exited
        messenger.Register<MainWindowViewModel, ImageManager.Core.Messages.CoTagModeExitedMessage>(this, (r, msg) =>
        {
            SetCoTagFilterTextWithoutRefresh(string.Empty);
            if (_tagSuggestionMode != TagSuggestionMode.TypedSuggestions)
                _tagSuggestionMode = TagSuggestionMode.None;
            _coTagSuggestionBase.Clear();
            _coTagSuggestionBaseLoaded = false;
            _coTagSuggestionFiles.Clear();
            OnPropertyChanged(nameof(SearchBoxBorderColor));
            OnPropertyChanged(nameof(IsSuggestionCoTagMode));
        });
    }

    public async Task InitializeAsync()
    {
        AppSettings = await _settingsRepo.LoadAsync();

        _pageManager.InitializeDecodeWidth(0);
        AppSettings.ImagesPerPage = ImagePaging.Clamp(AppSettings.ImagesPerPage);
        _pageManager.PageSize = AppSettings.ImagesPerPage;

        // The startup selection owns all cache services and the database.
        // A setting from the destination's existing database must not switch
        // thumbnails back to a different drive after startup.
        var diskCacheNeedsSync = !string.Equals(AppSettings.DiskCacheDirectory,
            App.CacheDirectoryPath, StringComparison.OrdinalIgnoreCase);

        if (diskCacheNeedsSync)
        {
            AppSettings.DiskCacheDirectory = App.CacheDirectoryPath;
            try
            {
                await _settingsRepo.SaveAsync(AppSettings);
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"修复缓存目录设置失败: {ex.Message}");
            }
        }
        _thumbCache.CacheDirectory = AppSettings.DiskCacheDirectory;

        var folders = await _folderRepo.GetAllAsync();
        var nodes = folders.Where(f => !f.IsArchived).Select(f => new FolderTreeNode
        {
            Path = f.Path, DisplayName = f.DisplayName, Alias = f.Alias, DbId = f.Id
        }).ToList();
        await Task.WhenAll(nodes.Select(n => n.EnsureExpanderVisibleAsync()));
        FolderTree = new ObservableCollection<FolderTreeNode>(nodes);

        SyncUISettingsFromAppData();

        // Clean orphan thumbnails from externally-deleted files
        RunBackgroundSafe("initial-orphan-cleanup", CancellationToken.None, async token =>
        {
            var orphanFolders = await _metaRepo.UnlinkOrphanFolderIdsAsync();
            if (orphanFolders > 0)
                AppLogger.Info($"ImageMeta orphan FolderId unlinked count={orphanFolders}");

            var unlinked = await _metaRepo.GetAllUnlinkedAsync();
            foreach (var m in unlinked)
                _thumbCache.InvalidateThumbnail(m.FilePath);
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

    public void ShutdownBackgroundWork()
    {
        CancelActiveBackgroundTasks();
        CancelDisplayFilter();
        InvalidateContinuousDisplayGeometry();
        StopWatchingCurrentFolder();
        _idleTimer?.Stop();
        _pageManager.CancelCurrentLoads();
        _folderWorkCts.Cancel();
        _sortCts.Cancel();
        _hashCts?.Cancel();
        _searchCts?.Cancel();
        InvalidateTagSuggestionRequest();
        _tagSearch.Dispose();
    }

    private bool IsFolderViewRequestCurrent(int requestVersion, string folderPath, bool showAllSubfolders)
    {
        return Volatile.Read(ref _folderViewRequestVersion) == requestVersion
            && string.Equals(CurrentFolder, folderPath, StringComparison.OrdinalIgnoreCase)
            && ShowAllSubfolders == showAllSubfolders;
    }

    private async Task SortFilesAsync(
        List<string> files,
        ImageSortOrder order,
        bool showAllSubfolders,
        ImageSortScope sortScope) =>
        await SortFilesAsync(files, order, showAllSubfolders, sortScope, CancellationToken.None)
            .ConfigureAwait(false);

    private async Task SortFilesAsync(
        List<string> files,
        ImageSortOrder order,
        bool showAllSubfolders,
        ImageSortScope sortScope,
        CancellationToken cancellationToken)
    {
        var snapshot = await BuildFileSortSnapshotAsync(
            files,
            showAllSubfolders,
            Volatile.Read(ref _fileListMutationVersion),
            useFolderQuery: false,
            cancellationToken).ConfigureAwait(false);
        await SortFilesAsync(files, order, showAllSubfolders, sortScope, snapshot, cancellationToken).ConfigureAwait(false);
    }

    private async Task SortFilesAsync(
        List<string> files,
        ImageSortOrder order,
        bool showAllSubfolders,
        ImageSortScope sortScope,
        FileSortSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        await Task.Run(() =>
        {
            var sorted = snapshot.SortPaths(
                files,
                order,
                showAllSubfolders && sortScope == ImageSortScope.FolderGrouped,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            files.Clear();
            files.AddRange(sorted);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<FileSortSnapshot> SortFilesUsingCurrentSettingsAsync(
        List<string> files,
        bool showAllSubfolders,
        CancellationToken cancellationToken = default)
    {
        var snapshotStopwatch = Stopwatch.StartNew();
        while (true)
        {
            var order = CurrentSortOrder;
            var scope = CurrentSortScope;
            var snapshot = await BuildFileSortSnapshotAsync(
                files,
                showAllSubfolders,
                Volatile.Read(ref _fileListMutationVersion) + 1,
                useFolderQuery: true,
                cancellationToken).ConfigureAwait(false);
            PerfLogger.Log($"[SortPerf] metadata_snapshot_ms={snapshotStopwatch.ElapsedMilliseconds} count={files.Count}");
            var sortStopwatch = Stopwatch.StartNew();
            await SortFilesAsync(files, order, showAllSubfolders, scope, snapshot, cancellationToken).ConfigureAwait(false);
            PerfLogger.Log($"[SortPerf] sort_ms={sortStopwatch.ElapsedMilliseconds} count={files.Count}");
            if (order == CurrentSortOrder && scope == CurrentSortScope)
                return snapshot;
        }
    }

    // ==================== Settings ====================

    public async Task SaveSettingsAsync()
    {
        await Task.Run(() => _settingsRepo.SaveAsync(AppSettings));
    }

    public AiRecommendViewModel CreateAiRecommendViewModel()
    {
        return new AiRecommendViewModel(
            _aiRecommendService ?? throw new InvalidOperationException("IAiRecommendService is not registered."),
            _tagMappingRepo ?? throw new InvalidOperationException("ITagMappingRepository is not registered."));
    }

    public VectorIndexViewModel CreateVectorIndexViewModel()
    {
        return new VectorIndexViewModel(
            _vectorIndexService ?? throw new InvalidOperationException("IVectorIndexService is not registered."),
            _chineseClipService ?? throw new InvalidOperationException("IChineseClipService is not registered."));
    }

    public ArtistDbBuilderViewModel CreateArtistDbBuilderViewModel(LibraryBuildTarget target)
    {
        return new ArtistDbBuilderViewModel(_libraryBuilderService)
        {
            Target = target
        };
    }
}
