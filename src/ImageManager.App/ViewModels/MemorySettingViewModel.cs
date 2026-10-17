using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ImageManager.App.ViewModels;

public partial class MemorySettingViewModel : ViewModelBase
{
    [ObservableProperty] private string _cachePath =
        Services.StartupCacheConfig.DefaultCacheDirectory;
    [ObservableProperty] private bool _isSaving;
    [ObservableProperty] private string _diskUsageHint = string.Empty;
    [ObservableProperty] private string _deepSeekApiKey = string.Empty;

    // --- 打标模式 ---
    [ObservableProperty] private int _tagMode = 1;  // 0=单模型(PixAI), 1=双模型(WD+PixAI)
    [ObservableProperty] private string _tagModeHint = string.Empty;

    // --- 双模型流水线设置 ---
    [ObservableProperty] private int _ensembleMaxTags = 75;
    [ObservableProperty] private double _pixaiMinConfidence = 0.30;
    [ObservableProperty] private double _artistMatchThreshold = 0.35;
    [ObservableProperty] private bool _enableCharacterRecognition = true;
    [ObservableProperty] private double _characterMatchThreshold = 0.35;
    [ObservableProperty] private int _characterMaxMatches = 1;

    // --- 单模型设置 ---
    [ObservableProperty] private double _singleModelMinConfidence = 0.15;

    public Func<bool, Task<bool>>? OnSave { get; set; }
    public Func<string, long>? GetDiskUsage { get; set; }

    private string _originalCachePath = string.Empty;
    private int _diskUsageRefreshVersion;

    public MemorySettingViewModel(string cachePath,
        string deepSeekApiKey,
        int tagMode, int ensembleMaxTags,
        double pixaiMinConfidence, double artistMatchThreshold,
        bool enableCharacterRecognition, double characterMatchThreshold, int characterMaxMatches,
        double singleModelMinConfidence,
        Func<string, long>? getDiskUsage,
        Func<bool, Task<bool>>? onSave)
    {
        _cachePath = cachePath;
        _deepSeekApiKey = deepSeekApiKey ?? string.Empty;
        _tagMode = tagMode;
        _ensembleMaxTags = ensembleMaxTags > 0 ? ensembleMaxTags : 75;
        _pixaiMinConfidence = pixaiMinConfidence > 0 ? pixaiMinConfidence : 0.30;
        _artistMatchThreshold = artistMatchThreshold > 0 ? artistMatchThreshold : 0.35;
        _enableCharacterRecognition = enableCharacterRecognition;
        _characterMatchThreshold = characterMatchThreshold > 0 ? characterMatchThreshold : 0.35;
        _characterMaxMatches = Math.Clamp(characterMaxMatches > 0 ? characterMaxMatches : 1, 1, 5);
        _singleModelMinConfidence = singleModelMinConfidence > 0 ? singleModelMinConfidence : 0.15;
        _originalCachePath = cachePath;
        GetDiskUsage = getDiskUsage;
        OnSave = onSave;
    }

    public bool IsSingleModelMode => TagMode == 0;
    public bool IsEnsembleMode => TagMode == 1;

    partial void OnTagModeChanged(int value)
    {
        OnPropertyChanged(nameof(IsSingleModelMode));
        OnPropertyChanged(nameof(IsEnsembleMode));
        TagModeHint = value == 0
            ? "单模型：仅 PixAI 打标（特征+角色），无 Rating 分级，无画师识别"
            : "双模型流水线：WD 负责 Rating 分级 + PixAI 负责打标（特征+角色）+ 嵌入匹配画师";
    }

    public async Task RefreshDiskUsageHintAsync()
    {
        var getDiskUsage = GetDiskUsage;
        if (getDiskUsage == null)
            return;

        var path = CachePath;
        var refreshVersion = Interlocked.Increment(ref _diskUsageRefreshVersion);
        DiskUsageHint = "正在统计缓存占用…";
        try
        {
            var bytes = await Task.Run(() => getDiskUsage(path));
            if (refreshVersion != Volatile.Read(ref _diskUsageRefreshVersion) ||
                !string.Equals(path, CachePath, StringComparison.OrdinalIgnoreCase))
                return;

            double diskMb = bytes / 1024.0 / 1024.0;
            DiskUsageHint = diskMb >= 1024
                ? $"磁盘缓存占用约 {diskMb / 1024:0.0} GB"
                : $"磁盘缓存占用约 {diskMb:0.0} MB";
        }
        catch (Exception ex)
        {
            if (refreshVersion == Volatile.Read(ref _diskUsageRefreshVersion))
                DiskUsageHint = $"无法读取缓存占用：{ex.Message}";
        }
    }

    public async Task<bool> SaveSettingsAsync()
    {
        if (IsSaving) return false;
        IsSaving = true;
        var selectedPath = CachePath;
        try
        {
            bool pathChanged = !string.Equals(
                (selectedPath ?? "").TrimEnd('\\', '/'),
                (_originalCachePath ?? "").TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);

            var saved = OnSave == null || await OnSave(pathChanged);
            if (saved) _originalCachePath = selectedPath ?? string.Empty;
            return saved;
        }
        finally
        {
            IsSaving = false;
        }
    }

    [RelayCommand]
    private Task<bool> SaveAsync() => SaveSettingsAsync();
}
