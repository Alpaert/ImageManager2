using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    private const int PageSize = 200;
    [ObservableProperty] private int _currentPage;
    [ObservableProperty] private int _totalPages;
    [ObservableProperty] private ObservableCollection<int> _pageNumbers = new();

    /// <summary>Current active file list, including orientation filter</summary>
    public List<string> ActiveFileList
    {
        get
        {
            var baseList = IsShowingSearchResult && _searchResultFiles.Count > 0
                ? _searchResultFiles : _allFiles;
            if (OrientationFilter == OrientationFilter.All)
                return baseList;
            return _orientationFilteredFiles;
        }
    }

    private List<string> _searchResultFiles = new();
    private List<string> _orientationFilteredFiles = new();
    private int _preSearchPageIndex;

    public double PreSearchScrollOffset { get; set; }
    public event Action? ScrollRestoreRequested;

    // ==================== Thumbnail Zoom ====================
    private static readonly double[] ZoomLevels = { 160, 183, 213, 256, 284, 320, 366, 427, 512, 640 };
    [ObservableProperty] private double _thumbnailBaseWidth = 160.0;
    [ObservableProperty] private double _zoomTick = 1;

    /// <summary>Fixed height for grid mode thumbnails (NaN for waterfall = auto)</summary>
    public double GridThumbnailHeight =>
        WaterfallMode == "None"
            ? ThumbnailBaseWidth / Math.Max(0.01, AppSettings.ThumbnailAspectRatio)
            : double.NaN;

    public bool ShowAnyThumbnailText => ShowFileName || ShowTags || ShowOrientation;
    private int _currentZoomLevel = 0;                     // index into ZoomLevels
    private int _thumbnailDecodeWidth = 200;
    private CancellationTokenSource? _zoomDebounceCts;
    private readonly SemaphoreSlim _thumbnailLoadSemaphore = new(4);

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

    // ==================== Tag Suggestion Co-occurring Mode ====================
    /// <summary>When true, suggestions show co-occurring tags instead of prefix matches</summary>
    private bool _coTagMode;
    /// <summary>Cycling state per tag name: 0=none, 1=AND(green), 2=AND-each(blue), 3=NOT(red)</summary>
    private readonly Dictionary<string, int> _coTagStates = new(StringComparer.OrdinalIgnoreCase);
    private string _lastSearchText = string.Empty;

    public bool IsSuggestionCoTagMode => _coTagMode;

    public int GetCoTagState(string tagName)
    {
        _coTagStates.TryGetValue(tagName, out int state);
        return state;
    }

    /// <summary>Search box border color reflecting current search condition</summary>
    public string SearchBoxBorderColor
    {
        get
        {
            var t = TagSearchText ?? "";
            if (t.Contains(" - ", StringComparison.OrdinalIgnoreCase))
                return "#E8A0A0"; // soft red: NOT
            if (t.Contains(" e ", StringComparison.OrdinalIgnoreCase))
                return "#8CB8E8"; // soft blue: AND-each
            if (t.Contains(" a ", StringComparison.OrdinalIgnoreCase))
                return "#86D9B0"; // soft green: AND-all
            return "#4A5568"; // default dark gray
        }
    }

    // ==================== Page Cache ====================
    private readonly Dictionary<int, List<ImageViewItem>> _pageCache = new();
    private readonly object _pageCacheLock = new();
    private const int MaxCachedPages = 3;
    private readonly ConcurrentDictionary<string, string> _phashCache = new(StringComparer.OrdinalIgnoreCase);

    private List<TagCount> _allTagCounts = new();
    private Dictionary<string, List<string>> _tagCacheByPath = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, ImageMeta> _metaCache = new(StringComparer.OrdinalIgnoreCase);
    [ObservableProperty] private ImageSortOrder _currentSortOrder = ImageSortOrder.FileNameAsc;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _precomputeCts;
    private FileSystemWatcher? _folderWatcher;
    private CancellationTokenSource? _folderWatchDebounceCts;
    private int _activePageIndex;

    public MainWindowViewModel(
        ISettingsRepository settingsRepo,
        IFolderRepository folderRepo,
        IImageMetaRepository metaRepo,
        ITagRepository tagRepo,
        ISimilarImageService similarService,
        IDuplicateService duplicateService,
        ThumbnailCacheService thumbCache)
    {
        _settingsRepo = settingsRepo;
        _folderRepo = folderRepo;
        _metaRepo = metaRepo;
        _tagRepo = tagRepo;
        _similarService = similarService;
        _duplicateService = duplicateService;
        _thumbCache = thumbCache;
    }

    public async Task InitializeAsync()
    {
        AppSettings = await _settingsRepo.LoadAsync();

        _thumbnailDecodeWidth = ComputeDecodeWidth();
        _thumbCache.CacheDirectory = AppSettings.DiskCacheDirectory;
        _thumbCache.DecodeWidth = _thumbnailDecodeWidth;

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
        lock (_pageCacheLock) { _pageCache.Clear(); }
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

                TotalPages = (_allFiles.Count + PageSize - 1) / PageSize;
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
                _ = PrecomputeHashesAsync(_precomputeCts.Token, folderId.Value);
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

        TotalPages = (_allFiles.Count + PageSize - 1) / PageSize;
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
        _ = PrecomputeHashesAsync(_precomputeCts.Token, folderId);

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
        _ = PrecomputeHashesAsync(_precomputeCts.Token, fi.Id);
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
                        TotalPages = (_allFiles.Count + PageSize - 1) / PageSize;
                        PageNumbers = new ObservableCollection<int>(Enumerable.Range(1, TotalPages));
                        lock (_pageCacheLock) { _pageCache.Clear(); }
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

        int ioConcurrency = Math.Min(8, Math.Max(4, Environment.ProcessorCount / 2));
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
                    byte[] data = new byte[fi.Length];
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                               FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    {
                        int offset = 0;
                        while (offset < data.Length)
                        {
                            int n = await fs.ReadAsync(data.AsMemory(offset), ct);
                            if (n == 0) break;
                            offset += n;
                        }
                    }
                    await channel.Writer.WriteAsync(
                        (path, data, fi.Length, fi.LastWriteTimeUtc.Ticks), ct);
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
                                var (w, h) = ThumbnailGenerator.GetDimensions(item.Data);
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

        // Track which page is currently visible (for background loaders to check)
        _activePageIndex = pageIndex;

        List<ImageViewItem> pageItems;
        bool needsLoad;
        lock (_pageCacheLock)
        {
            if (!_pageCache.TryGetValue(pageIndex, out pageItems!))
            {
                pageItems = CreatePlaceholderItems(pageIndex);
                _pageCache[pageIndex] = pageItems;
            }
            needsLoad = !pageItems.TrueForAll(i => i.IsLoaded);
        }

        // Fire-and-forget loading — never cancels; semaphore(4) throttles naturally
        if (needsLoad)
            _ = LoadPageThumbnailsAsync(pageIndex);

        // Single collection swap — avoids 200 individual CollectionChanged events
        Images = new ObservableCollection<ImageViewItem>(pageItems);
        _pageItemsCopy = pageItems;
        LoadedInfoText = $"当前页: {pageIndex + 1}/{TotalPages}  每页 {PageSize} 张";

        // Defer non-critical work. Skip DB save when showing search results.
        if (!IsShowingSearchResult && !string.IsNullOrEmpty(CurrentFolder))
            _ = Task.Run(() => _folderRepo.SetLastPageIndexAsync(CurrentFolder, pageIndex));
        PreloadAdjacentPages();
        _ = Task.Run(TrimPageCache);
    }

    private List<ImageViewItem> CreatePlaceholderItems(int pageIndex)
    {
        var files = ActiveFileList;
        int start = pageIndex * PageSize;
        int count = Math.Min(PageSize, files.Count - start);
        var list = new List<ImageViewItem>();

        for (int i = 0; i < count; i++)
        {
            var file = files[start + i];
            var tags = GetTagsForFile(file);
            list.Add(new ImageViewItem
            {
                FilePath = file,
                FileName = Path.GetFileName(file),
                Tags = tags,
                IsLoading = true
            });
        }

        return list;
    }

    private async Task LoadPageThumbnailsAsync(int pageIndex)
    {
        List<ImageViewItem> pageItems;
        lock (_pageCacheLock)
        {
            if (!_pageCache.TryGetValue(pageIndex, out pageItems!)) return;
        }

        foreach (var item in pageItems)
        {
            if (item.IsLoaded) continue;

            await _thumbnailLoadSemaphore.WaitAsync();
            try
            {
                var data = await _thumbCache.GetOrCreateThumbnailAsync(item.FilePath, _thumbnailDecodeWidth);
                if (data != null)
                {
                    item.ThumbnailData = data;
                    var (w, h) = ThumbnailGenerator.GetDimensions(item.FilePath);
                    item.Width = w;
                    item.Height = h;
                    item.IsLoaded = true;
                }
            }
            catch { }
            finally { _thumbnailLoadSemaphore.Release(); }

            item.IsLoading = false;
            item.NotifyAll();
        }
    }

    private void PreloadAdjacentPages()
    {
        int currentPage = CurrentPage;
        _ = Task.Run(async () =>
        {
            await Task.Delay(300);

            int? preloadPrev = null, preloadNext = null;
            lock (_pageCacheLock)
            {
                if (currentPage - 1 >= 0 && !_pageCache.ContainsKey(currentPage - 1))
                {
                    _pageCache[currentPage - 1] = CreatePlaceholderItems(currentPage - 1);
                    preloadPrev = currentPage - 1;
                }
                if (currentPage + 1 < TotalPages && !_pageCache.ContainsKey(currentPage + 1))
                {
                    _pageCache[currentPage + 1] = CreatePlaceholderItems(currentPage + 1);
                    preloadNext = currentPage + 1;
                }
            }
            if (preloadPrev.HasValue)
                _ = LoadPageThumbnailsAsync(preloadPrev.Value);
            if (preloadNext.HasValue)
                _ = LoadPageThumbnailsAsync(preloadNext.Value);
        });
    }

    private void TrimPageCache()
    {
        lock (_pageCacheLock)
        {
            if (_pageCache.Count <= MaxCachedPages) return;

            var mustKeep = new HashSet<int> { CurrentPage };
            if (CurrentPage - 1 >= 0) mustKeep.Add(CurrentPage - 1);
            if (CurrentPage + 1 < TotalPages) mustKeep.Add(CurrentPage + 1);

            foreach (var key in _pageCache.Keys.ToList())
            {
                if (mustKeep.Contains(key)) continue;
                _pageCache.Remove(key);
            }
        }
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
        double t = Math.Clamp(value, 1.0, 10.0);

        // Interpolate ThumbnailBaseWidth between discrete levels
        int idx = (int)t - 1;
        if (idx < 0) idx = 0;
        if (idx >= ZoomLevels.Length - 1) idx = ZoomLevels.Length - 2;

        double frac = t - (idx + 1);
        if (frac < 0) frac = 0;
        if (frac > 1) frac = 1;

        ThumbnailBaseWidth = ZoomLevels[idx] + (ZoomLevels[idx + 1] - ZoomLevels[idx]) * frac;

        // Only regenerate thumbnails when crossing a discrete level threshold
        int newLevel = (int)Math.Round(t - 1);
        if (newLevel < 0) newLevel = 0;
        if (newLevel >= ZoomLevels.Length) newLevel = ZoomLevels.Length - 1;

        if (newLevel != _currentZoomLevel)
        {
            _currentZoomLevel = newLevel;
            int newDecodeWidth = ComputeDecodeWidth();
            if (newDecodeWidth != _thumbnailDecodeWidth)
            {
                _thumbnailDecodeWidth = newDecodeWidth;

                // Debounce: only rebuild when user pauses dragging (300ms of no zoom change)
                _zoomDebounceCts?.Cancel();
                _zoomDebounceCts = new CancellationTokenSource();
                var token = _zoomDebounceCts.Token;
                _ = Task.Run(async () =>
                {
                    try { await Task.Delay(300, token); }
                    catch { return; }
                    if (token.IsCancellationRequested) return;
                    var dispatcher = Avalonia.Threading.Dispatcher.UIThread;
                    await dispatcher.InvokeAsync(async () =>
                    {
                        _thumbCache.DecodeWidth = _thumbnailDecodeWidth;
                        await _thumbCache.ClearAsync();
                        lock (_pageCacheLock) { _pageCache.Clear(); }
                        await ShowPageAsync(CurrentPage);
                    });
                }, token);
            }
        }
    }

    private int ComputeDecodeWidth()
    {
        int w = (int)(ZoomLevels[_currentZoomLevel] * 2.5);
        return Math.Clamp(w, 300, 1600);
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

        // Parse " - " first: left side = include, right side = exclude
        List<string> excludeTags = new();
        string includePart;
        if (raw.Contains(" - ", StringComparison.OrdinalIgnoreCase))
        {
            var parts = raw.Split(new[] { " - " }, 2, StringSplitOptions.None);
            includePart = parts[0].Trim();
            excludeTags = parts[1].Split(new[] { " o " }, StringSplitOptions.None)
                               .Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
        }
        else
        {
            includePart = raw;
        }

        // Parse " e " = AND-each (base part + each-tags), " a " = AND-all, " o " = OR
        List<string> tags;
        bool isAnd = false;
        bool isAndEach = false;
        bool baseIsAnd = true; // base part of AND-each: AND (a) or OR (o)?
        List<string> eachTags = new();
        if (includePart.Contains(" e ", StringComparison.OrdinalIgnoreCase))
        {
            var parts = includePart.Split(new[] { " e " }, StringSplitOptions.None)
                                   .Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
            // First part is the base — recursively parse for a/o
            var basePart = parts[0];
            eachTags = parts.Skip(1).ToList();
            isAndEach = true;

            if (basePart.Contains(" a ", StringComparison.OrdinalIgnoreCase))
            {
                tags = basePart.Split(new[] { " a " }, StringSplitOptions.None)
                               .Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
                baseIsAnd = true;
            }
            else if (basePart.Contains(" o ", StringComparison.OrdinalIgnoreCase))
            {
                tags = basePart.Split(new[] { " o " }, StringSplitOptions.None)
                               .Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
                baseIsAnd = false;
            }
            else
            {
                tags = new List<string> { basePart };
            }
        }
        else if (includePart.Contains(" a ", StringComparison.OrdinalIgnoreCase))
        {
            tags = includePart.Split(new[] { " a " }, StringSplitOptions.None)
                      .Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
            isAnd = true;
        }
        else if (includePart.Contains(" o ", StringComparison.OrdinalIgnoreCase))
        {
            tags = includePart.Split(new[] { " o " }, StringSplitOptions.None)
                      .Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
        }
        else
        {
            tags = new List<string> { includePart };
        }

        if (tags.Count == 0)
        {
            CurrentTagFilter = string.Empty;
            return;
        }

        var opName = isAndEach ? "AND-each" : isAnd ? "且" : "或";
        var excludeDesc = excludeTags.Count > 0
            ? $"（排除: {string.Join(" 或 ", excludeTags)}）" : "";
        var allTags = isAndEach ? tags.Concat(eachTags).ToList() : tags;
        StatusText = $"正在搜索 Tag（{opName}）: {string.Join(" + ", allTags)}{excludeDesc}...";

        // Save current page state before replacing with search results
        if (!IsShowingSearchResult)
        {
            _preSearchPageItems = Images.ToList();
            _preSearchPageIndex = CurrentPage;
        }

        try
        {
            List<string> taggedPaths;
            if (isAndEach)
            {
                taggedPaths = await _metaRepo.GetFilePathsByTagAndEachAsync(tags, baseIsAnd, eachTags,
                    excludeTags.Count > 0 ? excludeTags : null);
            }
            else if (excludeTags.Count > 0)
            {
                taggedPaths = await _metaRepo.GetFilePathsByTagsExcludingAsync(tags, isAnd, excludeTags);
            }
            else if (tags.Count == 1)
            {
                taggedPaths = await _metaRepo.GetFilePathsByTagAsync(tags[0]);
            }
            else
            {
                taggedPaths = await _metaRepo.GetFilePathsByTagsAsync(tags, isAnd);
            }

            // Intersect with current folder files
            var fileSet = new HashSet<string>(_allFiles, StringComparer.OrdinalIgnoreCase);
            _searchResultFiles = taggedPaths.Where(p => fileSet.Contains(p)).ToList();

            if (_searchResultFiles.Count == 0)
            {
                Images = new ObservableCollection<ImageViewItem>();
                IsShowingSearchResult = true;
                TotalPages = 0;
                PageNumbers = new ObservableCollection<int>();
                StatusText = $"未找到匹配的图片";
                return;
            }

            // Setup paging for search results
            IsShowingSearchResult = true;
            TotalPages = (_searchResultFiles.Count + PageSize - 1) / PageSize;
            PageNumbers = new ObservableCollection<int>(Enumerable.Range(1, TotalPages));
            lock (_pageCacheLock) { _pageCache.Clear(); }
            await ShowPageAsync(0);
            StatusText = $"Tag（{opName}）: 找到 {_searchResultFiles.Count} 张图片";

            // Enter co-occurring tag mode: show other tags shared by search results
            _coTagMode = true;
            _lastSearchText = raw;
            _ = RefreshCoTagSuggestionsAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"搜索失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SelectTagSuggestion(TagCount tag)
    {
        // In co-occurring mode: cycle through states (AND → AND-each → NOT → remove)
        if (_coTagMode && !string.IsNullOrEmpty(_lastSearchText))
        {
            await CycleCoTagAsync(tag.Name);
            return;
        }

        // Prefix mode: replace text and search
        TagSearchText = tag.Name;
        IsTagSearchPopupOpen = false;
        await SearchByTag();
    }

    partial void OnTagSearchTextChanged(string value)
    {
        // In co-occurring mode: only react to manual edits, don't touch suggestions
        if (_coTagMode)
        {
            if (value != _lastSearchText)
            {
                _coTagMode = false;
                _coTagStates.Clear();
                OnPropertyChanged(nameof(SearchBoxBorderColor));
                UpdateTagSuggestions(value);
            }
            // If value == _lastSearchText (code-internal change), keep popup open
            return;
        }
        UpdateTagSuggestions(value);
    }

    private void UpdateTagSuggestions(string keyword)
    {
        TagSearchSuggestions.Clear();
        if (_coTagMode)
        {
            // Show co-occurring tags from previous refresh (handled async)
            return;
        }

        if (string.IsNullOrWhiteSpace(keyword) || _allTagCounts.Count == 0)
        {
            IsTagSearchPopupOpen = false;
            return;
        }

        // Prefix match mode
        var results = _allTagCounts
            .Where(t => t.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(t => t.Name.StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(t => t.Count)
            .Take(50);

        foreach (var t in results)
            TagSearchSuggestions.Add(t);

        IsTagSearchPopupOpen = TagSearchSuggestions.Count > 0;
    }

    private async Task RefreshCoTagSuggestionsAsync()
    {
        try
        {
            // Get all tag names currently in the search text (to exclude from suggestions)
            var usedTags = ParseTagNamesFromSearchText(TagSearchText);
            var coTags = await _metaRepo.GetCoOccurringTagsAsync(_searchResultFiles, usedTags);
            TagSearchSuggestions = new ObservableCollection<TagCount>(coTags.Take(50));
            IsTagSearchPopupOpen = TagSearchSuggestions.Count > 0;
        }
        catch
        {
            IsTagSearchPopupOpen = false;
        }
    }

    private static List<string> ParseTagNamesFromSearchText(string text)
    {
        return text.Split(new[] { " a ", " o ", " e ", " - " }, StringSplitOptions.None)
                   .Select(t => t.Trim())
                   .Where(t => t.Length > 0)
                   .Distinct(StringComparer.OrdinalIgnoreCase)
                   .ToList();
    }

    private async Task CycleCoTagAsync(string tagName)
    {
        // Get current state (0=none, 1=AND-green, 2=AND-each-blue, 3=NOT-red)
        _coTagStates.TryGetValue(tagName, out int state);
        state = (state + 1) % 4;
        _coTagStates[tagName] = state;

        // Rebuild search text from base + all active co-tags
        var baseTag = _lastSearchText;
        // Strip any previously appended operators
        var cleaned = new List<string>();
        foreach (var t in ParseTagNamesFromSearchText(_lastSearchText))
        {
            if (!_coTagStates.ContainsKey(t))
                cleaned.Add(t);
        }

        var andTags = new List<string>();
        var eachTags = new List<string>();
        var notTags = new List<string>();
        foreach (var kv in _coTagStates)
        {
            if (kv.Value == 0) continue;
            if (kv.Value == 1) andTags.Add(kv.Key);
            else if (kv.Value == 2) eachTags.Add(kv.Key);
            else if (kv.Value == 3) notTags.Add(kv.Key);
        }

        // Build expression: base [a andTags] [e eachTags] [- notTags]
        var sb = new System.Text.StringBuilder();
        sb.Append(string.Join(" a ", cleaned));
        foreach (var t in andTags)
            sb.Append($" a {t}");
        foreach (var t in eachTags)
            sb.Append($" e {t}");
        if (notTags.Count > 0)
            sb.Append(" - " + string.Join(" o ", notTags));

        var newText = sb.ToString();
        _lastSearchText = newText;
        TagSearchText = newText;
        OnPropertyChanged(nameof(SearchBoxBorderColor));
        // Keep popup open so user can continue adjusting conditions
    }

    /// <summary>Remove deleted files incrementally, keeping existing thumbnails intact</summary>
    public void RemoveFilesFromView(HashSet<string> deletedPaths)
    {
        // Remove from master lists
        _allFiles.RemoveAll(p => deletedPaths.Contains(p));
        if (_searchResultFiles.Count > 0)
            _searchResultFiles.RemoveAll(p => deletedPaths.Contains(p));

        // Recalculate paging
        var files = ActiveFileList;
        TotalPages = files.Count == 0 ? 0 : (files.Count + PageSize - 1) / PageSize;
        PageNumbers = new ObservableCollection<int>(Enumerable.Range(1, TotalPages));

        // Clamp current page
        if (CurrentPage >= TotalPages && TotalPages > 0)
            CurrentPage = TotalPages - 1;

        if (TotalPages == 0)
        {
            Images = new ObservableCollection<ImageViewItem>();
            _pageItemsCopy = null;
            lock (_pageCacheLock) { _pageCache.Clear(); }
            return;
        }

        // Surgical removal: remove deleted items from current Images, keep rest
        var currentImages = Images.ToList();
        currentImages.RemoveAll(i => deletedPaths.Contains(i.FilePath));

        // Fill gaps from ActiveFileList page range
        int pageStart = CurrentPage * PageSize;
        var existingPaths = new HashSet<string>(currentImages.Select(i => i.FilePath), StringComparer.OrdinalIgnoreCase);
        var newItems = new List<ImageViewItem>();
        for (int i = 0; i < PageSize && currentImages.Count + newItems.Count < PageSize; i++)
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
        _pageItemsCopy = currentImages;

        // Update page cache for current page
        lock (_pageCacheLock) { _pageCache[CurrentPage] = currentImages; }

        // Load thumbnails only for new gap-fill items
        if (newItems.Count > 0)
        {
            _ = Task.Run(async () =>
            {
                foreach (var item in newItems)
                {
                    await _thumbnailLoadSemaphore.WaitAsync();
                    try
                    {
                        var data = await _thumbCache.GetOrCreateThumbnailAsync(item.FilePath, _thumbnailDecodeWidth);
                        if (data != null)
                        {
                            item.ThumbnailData = data;
                            var (w, h) = ThumbnailGenerator.GetDimensions(item.FilePath);
                            item.Width = w; item.Height = h;
                            item.IsLoaded = true;
                        }
                    }
                    catch { }
                    finally { _thumbnailLoadSemaphore.Release(); }
                    item.IsLoading = false;
                    item.NotifyAll();
                }
            });
        }
    }

    private List<ImageViewItem>? _pageItemsCopy;
    private List<ImageViewItem>? _preSearchPageItems; // Saved before search, restored on back

    // ==================== Orientation Filter ====================

    [RelayCommand] private async Task FilterAll() { OrientationFilter = OrientationFilter.All; await RebuildFromOrientationFilterAsync(); }
    [RelayCommand] private async Task FilterLandscape() { OrientationFilter = OrientationFilter.Landscape; await RebuildFromOrientationFilterAsync(); }
    [RelayCommand] private async Task FilterPortrait() { OrientationFilter = OrientationFilter.Portrait; await RebuildFromOrientationFilterAsync(); }

    private async Task RebuildFromOrientationFilterAsync()
    {
        var source = IsShowingSearchResult && _searchResultFiles.Count > 0
            ? _searchResultFiles : _allFiles;

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
        TotalPages = files.Count == 0 ? 0 : (files.Count + PageSize - 1) / PageSize;
        PageNumbers = new ObservableCollection<int>(Enumerable.Range(1, TotalPages));
        lock (_pageCacheLock) { _pageCache.Clear(); }

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
        _searchResultFiles.Sort(comparison);
        _orientationFilteredFiles.Clear();

        var active = ActiveFileList;
        TotalPages = active.Count == 0 ? 0 : (active.Count + PageSize - 1) / PageSize;
        PageNumbers = new ObservableCollection<int>(Enumerable.Range(1, TotalPages));
        lock (_pageCacheLock) { _pageCache.Clear(); }
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
            _searchResultFiles.Clear();

            TotalPages = (_allFiles.Count + PageSize - 1) / PageSize;
            PageNumbers = new ObservableCollection<int>(Enumerable.Range(1, TotalPages));

            if (_preSearchPageItems is { Count: > 0 })
            {
                CurrentPage = _preSearchPageIndex;
                Images = new ObservableCollection<ImageViewItem>(_preSearchPageItems);
                _preSearchPageItems = null;
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
            TotalPages = (_allFiles.Count + PageSize - 1) / PageSize;
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
        {
            _preSearchPageItems = Images.ToList();
            _preSearchPageIndex = CurrentPage;
        }

        try
        {
            var results = await _similarService.FindSimilarAsync(
                filePath, _allFiles, 5, _searchCts.Token);

            _searchResultFiles = results.ToList();

            if (_searchResultFiles.Count == 0)
            {
                Images = new ObservableCollection<ImageViewItem>();
                IsShowingSearchResult = true;
                TotalPages = 0;
                PageNumbers = new ObservableCollection<int>();
                StatusText = "未找到相似图片";
                return;
            }

            IsShowingSearchResult = true;
            TotalPages = (_searchResultFiles.Count + PageSize - 1) / PageSize;
            PageNumbers = new ObservableCollection<int>(Enumerable.Range(1, TotalPages));
            lock (_pageCacheLock) { _pageCache.Clear(); }
            await ShowPageAsync(0);
            StatusText = $"找到 {_searchResultFiles.Count} 张相似图片";
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
        _searchResultFiles.Clear();

        // Recalculate paging for normal folder view
        TotalPages = (_allFiles.Count + PageSize - 1) / PageSize;
        PageNumbers = new ObservableCollection<int>(Enumerable.Range(1, TotalPages));

        if (_preSearchPageItems is { Count: > 0 })
        {
            CurrentPage = _preSearchPageIndex;
            Images = new ObservableCollection<ImageViewItem>(_preSearchPageItems);
            _preSearchPageItems = null;
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

    public async Task RefreshTagCountsAsync()
    {
        _allTagCounts = await _tagRepo.GetAllTagCountsAsync();
    }

    public List<TagCount> GetAllTagCounts() => _allTagCounts;

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
