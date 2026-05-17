using CommunityToolkit.Mvvm.ComponentModel;

namespace ImageManager.App.ViewModels;

public partial class ImageViewItem : ObservableObject
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;

    // Thumbnail as raw bytes; converted to Avalonia Bitmap via converter or code
    [ObservableProperty] private byte[]? _thumbnailData;

    [ObservableProperty] private int _width = 1;
    [ObservableProperty] private int _height = 1;

    public string? FileHash { get; set; }
    public string? PerceptualHash { get; set; }
    public List<string> Tags { get; set; } = new();

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isLoaded;

    public bool IsNotLoaded => !IsLoaded;
    public bool IsLandscape => Width >= Height;
    public string OrientationText => IsLandscape ? "横图" : "竖图";

    // Deterministic placeholder color from filename — avoids empty white space during loading
    public string PlaceholderColor
    {
        get
        {
            unchecked
            {
                int hash = FileName.GetHashCode();
                // Soft pastel colors using high bits for hue, fixed saturation/lightness
                byte r = (byte)(180 + ((hash >> 16) & 0x3F));
                byte g = (byte)(180 + ((hash >> 8) & 0x3F));
                byte b = (byte)(180 + (hash & 0x3F));
                return $"#{r:X2}{g:X2}{b:X2}";
            }
        }
    }

    public string TagSummary =>
        Tags is { Count: > 0 } ? string.Join(", ", Tags) : "(无 Tag)";

    public string StatusText => IsLoading ? "加载中..." : TagSummary;

    public void NotifyAll()
    {
        OnPropertyChanged(nameof(IsSelected));
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(IsLoaded));
        OnPropertyChanged(nameof(IsNotLoaded));
        OnPropertyChanged(nameof(Width));
        OnPropertyChanged(nameof(Height));
        OnPropertyChanged(nameof(IsLandscape));
        OnPropertyChanged(nameof(OrientationText));
        OnPropertyChanged(nameof(TagSummary));
        OnPropertyChanged(nameof(StatusText));
    }
}
