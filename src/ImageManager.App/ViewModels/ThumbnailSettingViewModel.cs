using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageManager.Core.Models;

namespace ImageManager.App.ViewModels;

public partial class ThumbnailSettingViewModel : ViewModelBase
{
    private readonly AppSettings _data;
    private readonly Action _onChanged;

    [ObservableProperty] private bool _showFileName = true;
    [ObservableProperty] private bool _showTags = true;
    [ObservableProperty] private bool _showOrientation = true;
    [ObservableProperty] private string _aspectRatioText = "1:1";
    [ObservableProperty] private bool _enableCornerRadius;
    [ObservableProperty] private string _cornerRadiusText = "0";
    [ObservableProperty] private bool _keepPadding = true;
    [ObservableProperty] private int _waterfallModeIndex;
    [ObservableProperty] private string _borderColor = "#FF808080";
    [ObservableProperty] private string _backgroundColor = "#CCFFFFFF";
    [ObservableProperty] private string _opacityText = "1";

    public string[] WaterfallModes { get; } = { "None", "Vertical", "Horizontal" };

    public ThumbnailSettingViewModel(AppSettings data, Action onChanged)
    {
        _data = data;
        _onChanged = onChanged;

        ShowFileName = data.ShowThumbnailFileName;
        ShowTags = data.ShowThumbnailTags;
        ShowOrientation = data.ShowThumbnailOrientation;

        double ratio = data.ThumbnailAspectRatio > 0 ? data.ThumbnailAspectRatio : 1.0;
        AspectRatioText = ratio.ToString("0.##") + ":1";

        KeepPadding = data.ThumbnailNoTextKeepPadding;
        EnableCornerRadius = data.ThumbnailCornerRadius > 0;
        CornerRadiusText = data.ThumbnailCornerRadius > 0
            ? data.ThumbnailCornerRadius.ToString("0.##")
            : "0";

        WaterfallModeIndex = data.WaterfallMode switch
        {
            "Vertical" => 1,
            "Horizontal" => 2,
            _ => 0
        };

        BorderColor = data.ThumbnailBorderColor;
        BackgroundColor = data.ThumbnailBackgroundColor;
        OpacityText = data.ThumbnailOpacity.ToString("0.##");
    }

    [RelayCommand]
    private void Save()
    {
        _data.ShowThumbnailFileName = ShowFileName;
        _data.ShowThumbnailTags = ShowTags;
        _data.ShowThumbnailOrientation = ShowOrientation;

        _data.ThumbnailAspectRatio = ParseRatio(AspectRatioText, _data.ThumbnailAspectRatio);
        _data.ThumbnailNoTextKeepPadding = KeepPadding;
        _data.ThumbnailCornerRadius = EnableCornerRadius
            ? Math.Max(0, ParseDouble(CornerRadiusText))
            : 0;
        _data.WaterfallMode = WaterfallModes[Math.Clamp(WaterfallModeIndex, 0, 2)];

        _data.ThumbnailBorderColor = BorderColor;
        _data.ThumbnailBackgroundColor = BackgroundColor;

        if (double.TryParse(OpacityText, out double op))
        {
            _data.ThumbnailOpacity = Math.Clamp(op, 0, 1);
        }

        _onChanged();
    }

    private static double ParseRatio(string text, double fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback > 0 ? fallback : 1.0;

        var parts = text.Trim().Split(':');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], out double w) &&
            double.TryParse(parts[1], out double h) && h > 0)
            return w / h;

        if (parts.Length == 1 && double.TryParse(parts[0], out double d) && d > 0)
            return d;

        return fallback > 0 ? fallback : 1.0;
    }

    private static double ParseDouble(string text)
    {
        return double.TryParse(text, out double v) ? v : 0;
    }
}
