using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageManager.Core.Models;

namespace ImageManager.App.ViewModels;

public partial class WallpaperSettingViewModel : ViewModelBase
{
    private readonly AppSettings _data;
    private readonly Action _onApply;

    [ObservableProperty] private string _wallpaperPath = string.Empty;
    [ObservableProperty] private int _stretchIndex;
    [ObservableProperty] private int _alignmentIndex;
    [ObservableProperty] private double _opacity = 0.25;
    [ObservableProperty] private string _opacityText = "0.25";
    [ObservableProperty] private byte[]? _previewData;

    public string[] StretchModes { get; } = { "Uniform", "UniformToFill", "Fill", "None" };
    public string[] Alignments { get; } = { "Center", "TopLeft", "TopRight", "BottomLeft", "BottomRight" };

    public WallpaperSettingViewModel(AppSettings data, Action onApply)
    {
        _data = data;
        _onApply = onApply;

        WallpaperPath = data.WallpaperPath;
        Opacity = data.WallpaperOpacity > 0 ? data.WallpaperOpacity : 0.25;
        OpacityText = Opacity.ToString("0.00");

        StretchIndex = (data.WallpaperStretch ?? "Uniform") switch
        {
            "UniformToFill" => 1, "Fill" => 2, "None" => 3, _ => 0
        };

        AlignmentIndex = (data.WallpaperAlignment ?? "Center") switch
        {
            "TopLeft" => 1, "TopRight" => 2, "BottomLeft" => 3, "BottomRight" => 4, _ => 0
        };

        if (!string.IsNullOrWhiteSpace(WallpaperPath) && File.Exists(WallpaperPath))
            LoadPreview(WallpaperPath);
    }

    partial void OnOpacityChanged(double value)
    {
        OpacityText = value.ToString("0.00");
    }

    public void LoadPreview(string path)
    {
        try
        {
            WallpaperPath = path;
            PreviewData = File.ReadAllBytes(path);
        }
        catch { PreviewData = null; }
    }

    [RelayCommand]
    private void ClearWallpaper()
    {
        WallpaperPath = string.Empty;
        PreviewData = null;
    }

    [RelayCommand]
    private void Save()
    {
        _data.WallpaperPath = WallpaperPath;
        _data.WallpaperStretch = StretchModes[Math.Clamp(StretchIndex, 0, 4)];
        _data.WallpaperAlignment = Alignments[Math.Clamp(AlignmentIndex, 0, 5)];
        _data.WallpaperOpacity = Opacity;
        _onApply();
    }
}
