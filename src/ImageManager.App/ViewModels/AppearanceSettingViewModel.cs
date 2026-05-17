using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ImageManager.App.ViewModels;

public partial class AppearanceSettingViewModel : ViewModelBase
{
    private readonly Action<string> _onSave;

    [ObservableProperty] private int _themeIndex;

    public AppearanceSettingViewModel(string currentTheme, Action<string> onSave)
    {
        _onSave = onSave;
        ThemeIndex = string.Equals(currentTheme, "Light", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    }

    [RelayCommand]
    private void Save()
    {
        _onSave(ThemeIndex == 1 ? "Light" : "Dark");
    }
}
