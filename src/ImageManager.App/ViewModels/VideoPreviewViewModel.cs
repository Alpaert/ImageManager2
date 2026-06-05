using CommunityToolkit.Mvvm.ComponentModel;

namespace ImageManager.App.ViewModels;

public partial class VideoPreviewViewModel : ViewModelBase
{
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _infoText = string.Empty;

    // Playback state
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private double _position; // 0.0-1.0
    [ObservableProperty] private long _durationMs;

    // File info
    [ObservableProperty] private string? _videoFilePath;
    public long FileSizeBytes { get; set; }

    // Navigation
    public List<string> ImagePaths { get; set; } = new();
    [ObservableProperty] private int _currentIndex;
    [ObservableProperty] private bool _hasPrev;
    [ObservableProperty] private bool _hasNext;

    // Saved position
    public double SavedLeft { get; set; } = double.NaN;
    public double SavedTop { get; set; } = double.NaN;

    public string? Navigate(int delta)
    {
        if (ImagePaths.Count == 0) return null;
        int newIdx = CurrentIndex + delta;
        if (newIdx < 0 || newIdx >= ImagePaths.Count) return null;
        CurrentIndex = newIdx;
        HasPrev = CurrentIndex > 0;
        HasNext = CurrentIndex < ImagePaths.Count - 1;
        return ImagePaths[CurrentIndex];
    }

    public void UpdateInfo()
    {
        var posText = ImagePaths.Count > 0 ? $"  [{CurrentIndex + 1}/{ImagePaths.Count}]" : "";
        InfoText = $"File: {System.IO.Path.GetFileName(VideoFilePath ?? Title)}{posText}";
    }
}
