using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageManager.Common.Helpers;

namespace ImageManager.App.ViewModels;

public partial class PreviewViewModel : ViewModelBase
{
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _infoText = string.Empty;

    // Image properties passed by code-behind after loading
    [ObservableProperty] private byte[]? _imageData;
    public double ImageWidthDip { get; set; }
    public double ImageHeightDip { get; set; }
    public int PixelWidth { get; set; }
    public int PixelHeight { get; set; }
    public long FileSizeBytes { get; set; }

    [ObservableProperty] private double _zoomFactor = 1.0;
    [ObservableProperty] private double _fitZoom = 1.0;
    [ObservableProperty] private bool _userZoomed;

    // Navigation
    public List<string> ImagePaths { get; set; } = new();
    [ObservableProperty] private int _currentIndex;
    [ObservableProperty] private bool _hasPrev;
    [ObservableProperty] private bool _hasNext;

    // Saved position: set by MainWindow before Show, updated by PreviewWindow on close
    public double SavedLeft { get; set; } = double.NaN;
    public double SavedTop { get; set; } = double.NaN;

    /// <summary>Called by PreviewWindow to navigate. Returns new file path or null.</summary>
    public string? Navigate(int delta)
    {
        if (ImagePaths.Count == 0) return null;
        int newIdx = CurrentIndex + delta;
        if (newIdx < 0 || newIdx >= ImagePaths.Count) return null;
        ImageData = null; // release previous image before loading new
        CurrentIndex = newIdx;
        HasPrev = CurrentIndex > 0;
        HasNext = CurrentIndex < ImagePaths.Count - 1;
        return ImagePaths[CurrentIndex];
    }

    public void ReleaseImage() => ImageData = null;

    public double MinZoom => Math.Min(FitZoom * 0.05, FitZoom);
    public const double MaxZoom = 10.0;
    public const double ZoomStep = 1.15;

    public void UpdateInfo()
    {
        var zoomPct = ZoomFactor * 100.0;
        var posText = ImagePaths.Count > 0 ? $"  [{CurrentIndex + 1}/{ImagePaths.Count}]" : "";
        InfoText = $"分辨率：{PixelWidth} x {PixelHeight}    文件大小：{FileSizeFormatter.Format(FileSizeBytes)}    缩放：{zoomPct:F0}%{posText}";
    }
}
