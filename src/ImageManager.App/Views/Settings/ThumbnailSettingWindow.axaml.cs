using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ImageManager.App.Views.Settings;

public partial class ThumbnailSettingWindow : Window
{
    public ThumbnailSettingWindow()
    {
        InitializeComponent();
    }

    private void BtnOk_Click(object? sender, RoutedEventArgs e)
    {
        Close(true);
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }
}
