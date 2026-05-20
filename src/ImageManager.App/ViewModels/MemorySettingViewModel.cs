using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ImageManager.App.ViewModels;

public partial class MemorySettingViewModel : ViewModelBase
{
    [ObservableProperty] private int _maxCacheMB = 512;
    [ObservableProperty] private string _cachePath = @"C:\ImageManagerCache";
    [ObservableProperty] private string _currentUsageHint = string.Empty;
    [ObservableProperty] private string _diskUsageHint = string.Empty;
    [ObservableProperty] private string _deepSeekApiKey = string.Empty;
    [ObservableProperty] private double _onnxConfidenceThreshold = 0.35;

    public Action<bool>? OnSave { get; set; }
    public Func<long>? GetCurrentCacheBytes { get; set; }
    public Func<string, long>? GetDiskUsage { get; set; }

    private string _originalCachePath = string.Empty;

    public MemorySettingViewModel(int maxCacheMB, string cachePath,
        string deepSeekApiKey, double onnxConfidenceThreshold,
        Func<long>? getCurrentCacheBytes,
        Func<string, long>? getDiskUsage,
        Action<bool>? onSave)
    {
        _maxCacheMB = maxCacheMB > 0 ? maxCacheMB : 512;
        _cachePath = cachePath;
        _deepSeekApiKey = deepSeekApiKey ?? string.Empty;
        _onnxConfidenceThreshold = onnxConfidenceThreshold > 0 ? onnxConfidenceThreshold : 0.35;
        _originalCachePath = cachePath;
        GetCurrentCacheBytes = getCurrentCacheBytes;
        GetDiskUsage = getDiskUsage;
        OnSave = onSave;

        UpdateHints();
    }

    private void UpdateHints()
    {
        if (GetCurrentCacheBytes != null)
        {
            double curMb = GetCurrentCacheBytes() / 1024.0 / 1024.0;
            CurrentUsageHint = $"当前缩略图缓存估算占用约 {curMb:0.0} MB。（上限：{MaxCacheMB} MB）";
        }
        if (GetDiskUsage != null)
        {
            double diskMb = GetDiskUsage(CachePath) / 1024.0 / 1024.0;
            DiskUsageHint = diskMb >= 1024
                ? $"磁盘缓存占用约 {diskMb / 1024:0.0} GB"
                : $"磁盘缓存占用约 {diskMb:0.0} MB";
        }
    }

    [RelayCommand]
    private void Save()
    {
        if (MaxCacheMB < 64) MaxCacheMB = 64;
        if (MaxCacheMB > 4096) MaxCacheMB = 4096;

        bool pathChanged = !string.Equals(
            (CachePath ?? "").TrimEnd('\\', '/'),
            (_originalCachePath ?? "").TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);

        OnSave?.Invoke(pathChanged);
    }
}
