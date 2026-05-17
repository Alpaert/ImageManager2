using Avalonia.Controls;
using Avalonia.Interactivity;
using ImageManager.App.ViewModels;

namespace ImageManager.App.Views.Settings;

public partial class SizeSettingWindow : Window
{
    public SizeSettingWindow()
    {
        InitializeComponent();
    }

    private void BtnSave_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SizeSettingViewModel vm)
        {
            vm.WindowWidth = Width;
            vm.WindowHeight = Height;
        }
        Close(true);
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }
}
