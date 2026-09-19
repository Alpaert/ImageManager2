using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ImageManager.App.ViewModels;

public partial class AppearanceSettingViewModel : ViewModelBase
{
    private readonly Action<string, string> _onSave;

    [ObservableProperty] private int _themeIndex;
    [ObservableProperty] private int _searchToolbarLayoutIndex;

    public IReadOnlyList<string> SearchToolbarLayoutOptions { get; } =
    [
        "A - 分层搜索工具栏",
        "C - 命令栏 + 更多菜单"
    ];

    public AppearanceSettingViewModel(string currentTheme, string currentLayout, Action<string, string> onSave)
    {
        _onSave = onSave;
        ThemeIndex = string.Equals(currentTheme, "Light", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        SearchToolbarLayoutIndex = string.Equals(currentLayout, "C", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    }

    [RelayCommand]
    private void Save()
    {
        _onSave(
            ThemeIndex == 1 ? "Light" : "Dark",
            SearchToolbarLayoutIndex == 1 ? "C" : "A");
    }
}
