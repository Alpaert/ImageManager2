using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ImageManager.App.ViewModels;

namespace ImageManager.App.Views.Settings;

public partial class MemorySettingWindow : Window
{
    public MemorySettingWindow()
    {
        InitializeComponent();
    }

    private async void BtnBrowseCachePath_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择缩略图磁盘缓存的存储目录",
            AllowMultiple = false
        });

        if (folders.Count > 0 && DataContext is MemorySettingViewModel vm)
        {
            vm.CachePath = folders[0].Path.LocalPath;
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
