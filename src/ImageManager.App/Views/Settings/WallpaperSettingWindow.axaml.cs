using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ImageManager.App.ViewModels;

namespace ImageManager.App.Views.Settings;

public partial class WallpaperSettingWindow : Window
{
    private WallpaperSettingViewModel Vm => (WallpaperSettingViewModel)DataContext!;

    public WallpaperSettingWindow()
    {
        InitializeComponent();
    }

    private async void BtnBrowse_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择壁纸图片",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("图片文件")
                {
                    Patterns = new[] { "*.jpg", "*.jpeg", "*.png", "*.bmp", "*.gif" }
                }
            }
        });

        if (files.Count > 0)
        {
            Vm.LoadPreview(files[0].Path.LocalPath);
        }
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
