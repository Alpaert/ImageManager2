using CommunityToolkit.Mvvm.ComponentModel;

namespace ImageManager.App.Models;

/// <summary>One direct child folder projected into the main browsing surface.</summary>
public partial class FolderViewItem : ObservableObject
{
    public string FolderPath { get; init; } = string.Empty;
    public string Path => FolderPath;
    public string DisplayName { get; init; } = string.Empty;
    public string MediaCountText => MediaCount == 1 ? "1 个媒体文件" : $"{MediaCount} 个媒体文件";

    [ObservableProperty]
    private string? _coverMediaPath;

    [ObservableProperty]
    private int _mediaCount;

    [ObservableProperty]
    private byte[]? _thumbnailData;

    [ObservableProperty]
    private bool _isLoadingThumbnail;

    [ObservableProperty]
    private bool _isSelected;

    public bool HasThumbnail => ThumbnailData is { Length: > 0 };

    partial void OnMediaCountChanged(int value) => OnPropertyChanged(nameof(MediaCountText));
    partial void OnThumbnailDataChanged(byte[]? value) => OnPropertyChanged(nameof(HasThumbnail));
}
