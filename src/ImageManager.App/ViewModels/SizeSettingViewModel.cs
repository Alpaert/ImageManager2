using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ImageManager.App.ViewModels;

public partial class SizeSettingViewModel : ViewModelBase
{
    [ObservableProperty] private double _windowWidth;
    [ObservableProperty] private double _windowHeight;
    [ObservableProperty] private string _title = "设置窗口大小";

    public Action<double, double>? OnSave { get; set; }

    public SizeSettingViewModel(double width, double height, string title, Action<double, double>? onSave)
    {
        _windowWidth = width > 0 ? width : 800;
        _windowHeight = height > 0 ? height : 600;
        _title = title;
        OnSave = onSave;
    }

    [RelayCommand]
    private void Save()
    {
        OnSave?.Invoke(WindowWidth, WindowHeight);
    }
}
