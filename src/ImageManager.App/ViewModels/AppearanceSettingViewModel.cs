using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageManager.App.Models;

namespace ImageManager.App.ViewModels;

public partial class AppearanceSettingViewModel : ViewModelBase
{
    private readonly Action<string, ImageDisplayMode> _onSave;

    [ObservableProperty] private int _themeIndex;
    [ObservableProperty] private ImageDisplayMode _displayMode;

    public AppearanceSettingViewModel(
        string currentTheme,
        ImageDisplayMode currentDisplayMode,
        Action<string, ImageDisplayMode> onSave)
    {
        _onSave = onSave;
        ThemeIndex = string.Equals(currentTheme, "Light", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        DisplayMode = currentDisplayMode == ImageDisplayMode.Continuous && !IsContinuousDisplayAvailable
            ? ImageDisplayMode.Paged
            : currentDisplayMode;
    }

    public bool IsPagedDisplayMode
    {
        get => DisplayMode == ImageDisplayMode.Paged;
        set
        {
            if (value)
                DisplayMode = ImageDisplayMode.Paged;
        }
    }

    public bool IsContinuousDisplayMode
    {
        get => DisplayMode == ImageDisplayMode.Continuous;
        set
        {
            if (value && IsContinuousDisplayAvailable)
                DisplayMode = ImageDisplayMode.Continuous;
        }
    }

    public bool IsContinuousDisplayAvailable => true;

    partial void OnDisplayModeChanged(ImageDisplayMode value)
    {
        OnPropertyChanged(nameof(IsPagedDisplayMode));
        OnPropertyChanged(nameof(IsContinuousDisplayMode));
    }

    [RelayCommand]
    private void Save()
    {
        _onSave(
            ThemeIndex == 1 ? "Light" : "Dark",
            DisplayMode);
    }
}
