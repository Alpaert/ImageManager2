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
using ImageManager.Infrastructure.Hashing;
using ImageManager.Infrastructure.Imaging;

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
    [ObservableProperty] private ObservableCollection<FolderInfo> _folderList = new();
    [ObservableProperty] private FolderInfo? _selectedFolder;
    [ObservableProperty] private string _folderSearchText = string.Empty;

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

    // ==================== Status ====================
    [ObservableProperty] private string _statusText = "就绪";
    [ObservableProperty] private string _backgroundStatusText = string.Empty;
    [ObservableProperty] private string _loadedInfoText = string.Empty;
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
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _precomputeCts;
    private FileSystemWatcher? _folderWatcher;
    private CancellationTokenSource? _folderWatchDebounceCts;

    public MainWindowViewModel(
        ISettingsRepository settingsRepo,
        IFolderRepository folderRepo,
        IImageMetaRepository metaRepo,
        ITagRepository tagRepo,
        ISimilarImageService similarService,
        IDuplicateService duplicateService,
        ThumbnailCacheService thumbCache,
        PageManager pageManager,
        TagSearchController tagSearch)
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
            OnPropertyChanged(nameof(SearchBoxBorderColor));
        };
    }

    public async Task InitializeAsync()
    {
        AppSettings = await _settingsRepo.LoadAsync();

        _pageManager.InitializeDecodeWidth(0);
        _thumbCache.CacheDirectory = AppSettings.DiskCacheDirectory;

        var folders = await _folderRepo.GetAllAsync();
        FolderList = new ObservableCollection<FolderInfo>(folders);

        SyncUISettingsFromAppData();

        // Defer tag count refresh — not needed for initial display
        _ = RefreshTagCountsAsync();

        if (!string.IsNullOrEmpty(AppSettings.LastFolder) && Directory.Exists(AppSettings.LastFolder))
        {
            // Show page immediately, precompute in background
            await LoadFolderAsync(AppSettings.LastFolder);
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
        if (info != null && !FolderList.Any(f => string.Equals(f.Path, folderPath, StringComparison.OrdinalIgnoreCase)))
            FolderList.Add(info);

        await SaveSettingsAsync();
    }

    [RelayCommand]
    private async Task RemoveFolderAsync()
    {
        if (SelectedFolder == null) return;

        // Clean up disk thumbnail cache for all images in this folder
        if (SelectedFolder.Id > 0)
        {
            try
            {
                var metas = await _metaRepo.GetByFolderIdAsync(SelectedFolder.Id);
                foreach (var meta in metas)
                    _thumbCache.DeleteFromDiskCache(meta.FilePath);
            }
            catch { }
        }

        await _folderRepo.RemoveAsync(SelectedFolder.Path);
        FolderList.Remove(SelectedFolder);
        await SaveSettingsAsync();
    }

    public async Task UpdateFolderAliasAsync(string folderPath, string? alias)
    {
        await _folderRepo.UpdateAliasAsync(folderPath, alias);
        var folder = FolderList.FirstOrDefault(f => string.Equals(f.Path, folderPath, StringComparison.OrdinalIgnoreCase));
        if (folder != null)
        {
            folder.Alias = alias;
            var idx = FolderList.IndexOf(folder);
            if (idx >= 0)
            {
                FolderList.RemoveAt(idx);
                FolderList.Insert(idx, folder);
            }
        }
    }

    /// <summary>Relocate a folder whose path changed externally. Updates all paths in DB.</summary>
    public async Task RelocateFolderAsync(long folderId, string newFolderPath)
    {
        await _folderRepo.RelocateFolderAsync(folderId, newFolderPath);

        // Update the FolderList display
        var folder = FolderList.FirstOrDefault(f => f.Id == folderId);
        if (folder != null)
        {
            folder.Path = newFolderPath;
            var idx = FolderList.IndexOf(folder);
            if (idx >= 0)
            {
                FolderList.RemoveAt(idx);
                FolderList.Insert(idx, folder);
            }
        }
    }

    /// <summary>Returns true if folder needs relocation (path doesn't exist on disk)</summary>
    public bool NeedsRelocation(FolderInfo folder) => folder.Id > 0 && !Directory.Exists(folder.Path);

    public async Task SelectFolderAsync(FolderInfo? folder)
    {
        if (folder == null) return;

        if (!Directory.Exists(folder.Path))
        {
            StatusText = $"文件夹路径已变更: {folder.Path}";
            return;
        }

        SelectedFolder = folder;
        AppSettings.LastFolder = folder.Path;
        await LoadFolderAsync(folder.Path);
        await SaveSettingsAsync();
    }

    // ==================== Folder Loading ====================

    public async Task LoadFolderAsync(string folder)
    {
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
        BackgroundStatusText = "";
        CurrentPage = 0;
        TotalPages = 0;
        CurrentFolder = folder;
        StartWatchingCurrentFolder();
        await Task.Yield();

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

                foreach (var m in indexedFiles)
                {
                    _metaCache[m.FilePath] = m;
                    if (m.Tags.Count > 0)
                        _tagCacheByPath[m.FilePath] = m.Tags.Select(t => t.Name).ToList();
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

        if (!folderId.HasValue)
        {
            await _folderRepo.AddAsync(folder);
            folderInfo = await _folderRepo.GetByPathAsync(folder);
            folderId = folderInfo?.Id;
        }

        TotalPages = (_allFiles.Count + PageManager.PageSize - 1) / PageManager.PageSize;
        PageNumbers = new ObservableCollection<int>(Enumerable.Range(1, TotalPages));

        _ = Task.Run(async () =>
        {
            try
            {
                var metas = await _metaRepo.GetByFolderAsync(folder);
                foreach (var m in metas)
                {
                    _metaCache[m.FilePath] = m;
                    _tagCacheByPath[m.FilePath] = m.Tags.Select(t => t.Name).ToList();
                }
            }
            catch { }
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

            var newFiles = new List<string>();
            foreach (var file in diskFiles)
            {
                if (!dbSet.Contains(file))
                {
                    await _metaRepo.SetFolderIdAsync(file, folderId);
                    newFiles.Add(file);
                }
            }

            bool deleted = false;
            foreach (var meta in dbFiles)
            {
                if (!diskFiles.Contains(meta.FilePath))
                {
                    await _metaRepo.DeleteByPathAsync(meta.FilePath);
                    deleted = true;
                }
            }

            if (newFiles.Count > 0 || deleted)
            {
                // Refresh UI on dispatcher
                var dispatcher = Avalonia.Threading.Dispatcher.UIThread;
                await dispatcher.InvokeAsync(async () =>
                {
                    if (string.Equals(CurrentFolder, folder, StringComparison.OrdinalIgnoreCase))
                    {
                        int oldCount = _allFiles.Count;
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
                });
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
        var channel = Channel.CreateBounded<(string Path, byte[] Data, long FileSize, long LastWriteTicks)>(
            new BoundedChannelOptions(50)
            {
                SingleWriter = false, SingleReader = false,
                FullMode = BoundedChannelFullMode.Wait
            });

        int ioConcurrency = 2;
        var ioSlots = new SemaphoreSlim(ioConcurrency);

        var produceTasks = needsHashing.Select(async path =>
        {
            try
            {
                if (ct.IsCancellationRequested) return;
                await ioSlots.WaitAsync(ct);
                try
                {
                    var fi = new FileInfo(path);
                    // Decode at 256px max for hash input — avoids loading full image into memory
                    var hashInput = await Task.Run(() => ThumbnailGenerator.DecodeForHashInput(path, 256), ct);
                    if (hashInput == null) return;
                    await channel.Writer.WriteAsync(
                        (path, hashInput, fi.Length, fi.LastWriteTimeUtc.Ticks), ct);
                }
                finally { ioSlots.Release(); }
            }
            catch (OperationCanceledException) { }
            catch { }
        });

        // === Consumer: CPU-bound hash computation ===
        int cpuConcurrency = Math.Max(1, Environment.ProcessorCount - 1);
        var cpuSlots = new SemaphoreSlim(cpuConcurrency);
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
                                FileSize = item.FileSize,
                                LastWriteTicks = item.LastWriteTicks,
                                FolderId = folderId,
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
        var (baseWidth, _) = _pageManager.OnZoomTickChanged(value, CurrentPage, TotalPages,
            ActiveFileList, GetTagsForFile);
        ThumbnailBaseWidth = baseWidth;
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
            await _tagSearch.SearchByTagAsync(raw, _allFiles, IsShowingSearchResult,
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
        _tagSearch.SelectSuggestion(tag,
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

    public void OnTagSearchGotFocus()
    {
        _tagSearch.OnGotFocus(TagSearchText,
            open => IsTagSearchPopupOpen = open,
            () => TagSearchSuggestions.ToList());
    }

    /// <summary>Remove deleted files incrementally, keeping existing thumbnails intact</summary>
    public void RemoveFilesFromView(HashSet<string> deletedPaths)
    {
        // Invalidate page cache so stale entries (containing deleted files) are never served
        _pageManager.InvalidateCache();

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

        // Surgical removal: remove deleted items from current Images, keep rest
        var currentImages = Images.ToList();
        currentImages.RemoveAll(i => deletedPaths.Contains(i.FilePath));

        // Fill gaps from ActiveFileList page range
        int pageStart = CurrentPage * PageManager.PageSize;
        var existingPaths = new HashSet<string>(currentImages.Select(i => i.FilePath), StringComparer.OrdinalIgnoreCase);
        var newItems = new List<ImageViewItem>();
        for (int i = 0; i < PageManager.PageSize && currentImages.Count + newItems.Count < PageManager.PageSize; i++)
        {
            int idx = pageStart + i;
            if (idx >= files.Count) break;
            var path = files[idx];
            if (existingPaths.Contains(path)) continue;
            newItems.Add(new ImageViewItem
            {
                FilePath = path,
                FileName = Path.GetFileName(path),
                Tags = GetTagsForFile(path),
                IsLoading = true
            });
        }

        currentImages.AddRange(newItems);
        Images = new ObservableCollection<ImageViewItem>(currentImages);
        if (newItems.Count > 0)
            _pageManager.LoadThumbnailsForItems(newItems);
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
            _orientationFilteredFiles = new List<string>();
            var wantLandscape = OrientationFilter == OrientationFilter.Landscape;
            await Task.Run(() =>
            {
                foreach (var path in source)
                {
                    try
                    {
                        var (w, h) = ThumbnailGenerator.GetDimensions(path);
                        if ((wantLandscape && w >= h) || (!wantLandscape && w < h))
                            _orientationFilteredFiles.Add(path);
                    }
                    catch { }
                }
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

        var files = (_allFiles.Count > 0 ? _allFiles : null);
        if (files == null) return;

        var comparison = order switch
        {
            ImageSortOrder.FileNameAsc => (Comparison<string>)((a, b) =>
                string.Compare(Path.GetFileName(a), Path.GetFileName(b), StringComparison.OrdinalIgnoreCase)),
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

        await Task.Run(() => files.Sort(comparison));
        _tagSearch.SearchResultFiles.Sort(comparison);
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
        _searchCts = new CancellationTokenSource();

        if (!IsShowingSearchResult)
            _pageManager.SavePreSearchState(Images, CurrentPage);

        try
        {
            var results = await _similarService.FindSimilarAsync(
                filePath, _allFiles, 5, _searchCts.Token);

            _tagSearch.SearchResultFiles = results.ToList();

            if (_tagSearch.SearchResultFiles.Count == 0)
            {
                Images = new ObservableCollection<ImageViewItem>();
                IsShowingSearchResult = true;
                TotalPages = 0;
                PageNumbers = new ObservableCollection<int>();
                StatusText = "未找到相似图片";
                return;
            }

            IsShowingSearchResult = true;
            TotalPages = (_tagSearch.SearchResultFiles.Count + PageManager.PageSize - 1) / PageManager.PageSize;
            PageNumbers = new ObservableCollection<int>(Enumerable.Range(1, TotalPages));
            _pageManager.InvalidateCache();
            await ShowPageAsync(0);
            StatusText = $"找到 {_tagSearch.SearchResultFiles.Count} 张相似图片";
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
    private void BackFromSearch()
    {
        if (!IsShowingSearchResult) return;
        IsShowingSearchResult = false;
        _tagSearch.SearchResultFiles.Clear();

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
        var result = await _tagRepo.RenameTagAsync(oldName, newName);
        if (result == RenameResult.Conflict) return RenameResult.Conflict;

        // Update in-memory tag caches for all images
        foreach (var tags in _tagCacheByPath.Values)
        {
            for (int i = 0; i < tags.Count; i++)
                if (string.Equals(tags[i], oldName, StringComparison.OrdinalIgnoreCase))
                    tags[i] = newName;
        }

        // Update currently displayed images
        foreach (var img in Images)
        {
            for (int i = 0; i < img.Tags.Count; i++)
                if (string.Equals(img.Tags[i], oldName, StringComparison.OrdinalIgnoreCase))
                    img.Tags[i] = newName;
            img.NotifyAll();
        }

        await RefreshTagCountsAsync();
        return RenameResult.Success;
    }

    public async Task MergeTagsAsync(string oldName, string newName)
    {
        await _tagRepo.MergeTagsAsync(oldName, newName);

        // Update in-memory tag caches
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

        // Update currently displayed images
        foreach (var img in Images)
        {
            for (int i = img.Tags.Count - 1; i >= 0; i--)
            {
                if (string.Equals(img.Tags[i], oldName, StringComparison.OrdinalIgnoreCase))
                {
                    if (!img.Tags.Contains(newName, StringComparer.OrdinalIgnoreCase))
                        img.Tags[i] = newName;
                    else
                        img.Tags.RemoveAt(i);
                }
            }
            img.NotifyAll();
        }

        await RefreshTagCountsAsync();
    }

    public async Task RefreshTagCountsAsync()
    {
        _tagSearch.AllTagCounts = await _tagRepo.GetAllTagCountsAsync();
    }

    public List<TagCount> GetAllTagCounts() => _tagSearch.AllTagCounts;

    public async Task SetImageTagsAsync(string filePath, List<string> tags)
    {
        var meta = await _metaRepo.GetByPathAsync(filePath);
        if (meta != null)
        {
            await _metaRepo.SetTagsAsync(meta.Id, tags);
            await RefreshTagCountsAsync();
        }
        _tagCacheByPath[filePath] = tags;
    }

    public List<string> GetTagsForFile(string filePath)
    {
        return _tagCacheByPath.TryGetValue(filePath, out var tags)
            ? new List<string>(tags) : new List<string>();
    }

    public async Task<List<string>> GetTagsForFileAsync(string filePath)
    {
        var meta = await _metaRepo.GetByPathAsync(filePath);
        return meta?.Tags.Select(t => t.Name).ToList() ?? new List<string>();
    }

    // ==================== Settings ====================

    public async Task SaveSettingsAsync()
    {
        await _settingsRepo.SaveAsync(AppSettings);
    }
}
