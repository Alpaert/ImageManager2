using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ImageManager.App.Views.Settings;

public partial class AppearanceSettingWindow : Window
{
    public AppearanceSettingWindow()
    {
        InitializeComponent();
    }

    private void BtnOk_Click(object? sender, RoutedEventArgs e) => Close(true);
    private void BtnCancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
