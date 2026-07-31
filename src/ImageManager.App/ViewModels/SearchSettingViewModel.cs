using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageManager.Core.Models;

namespace ImageManager.App.ViewModels;

public partial class SearchSettingViewModel : ViewModelBase
{
    private readonly Func<PerceptualSearchResultMode, int, Task> _onSave;

    [ObservableProperty] private int _resultModeIndex;
    [ObservableProperty] private decimal _resultLimit;

    public SearchSettingViewModel(
        PerceptualSearchResultMode currentMode,
        int currentResultLimit,
        Func<PerceptualSearchResultMode, int, Task> onSave)
    {
        _onSave = onSave;
        ResultModeIndex = currentMode == PerceptualSearchResultMode.Jump ? 1 : 0;
        ResultLimit = Math.Clamp(currentResultLimit, 1, 500);
    }

    [RelayCommand]
    private Task SaveAsync()
    {
        var mode = ResultModeIndex == 1
            ? PerceptualSearchResultMode.Jump
            : PerceptualSearchResultMode.Ranked;
        return _onSave(mode, Math.Clamp((int)ResultLimit, 1, 500));
    }
}
