using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ImageManager.App.Services;

namespace ImageManager.App.Views.Settings;

public partial class StartupCacheWindow : Window
{
    private readonly bool _isRepairRequired;

    public bool IsAccepted { get; private set; }
    public string SelectedPath { get; private set; }

    public StartupCacheWindow()
        : this(StartupCacheConfig.DefaultCacheDirectory, isRepairRequired: false)
    {
    }

    public StartupCacheWindow(string initialPath, bool isRepairRequired, string? repairReason = null)
    {
        InitializeComponent();

        _isRepairRequired = isRepairRequired;
        SelectedPath = string.IsNullOrWhiteSpace(initialPath)
            ? StartupCacheConfig.DefaultCacheDirectory
            : initialPath;
        PathBox.Text = SelectedPath;

        if (isRepairRequired)
        {
            Title = "修复缓存位置";
            TitleText.Text = "缓存位置不可用";
            DescriptionText.Text =
                "上次配置的缓存目录不存在或不可写。请选择一个可用目录后再进入软件，避免数据库和缓存分散。";
            ExitButton.IsVisible = true;
            if (!string.IsNullOrWhiteSpace(repairReason))
                ShowError(repairReason);
        }
    }

    private async void BtnBrowse_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = _isRepairRequired ? "选择可用的缓存目录" : "选择缓存目录",
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            PathBox.Text = folders[0].Path.LocalPath;
            ErrorText.IsVisible = false;
            TryAcceptSelectedPath();
        }
    }

    private void BtnDefault_Click(object? sender, RoutedEventArgs e)
    {
        PathBox.Text = StartupCacheConfig.DefaultCacheDirectory;
        ErrorText.IsVisible = false;
    }

    private void BtnContinue_Click(object? sender, RoutedEventArgs e)
    {
        TryAcceptSelectedPath();
    }

    private void TryAcceptSelectedPath()
    {
        var path = PathBox.Text ?? string.Empty;
        if (!StartupCacheConfig.TryValidateWritableDirectory(path, out var error))
        {
            ShowError(error);
            return;
        }

        SelectedPath = Path.GetFullPath(path);
        IsAccepted = true;
        Close(true);
    }

    private void BtnExit_Click(object? sender, RoutedEventArgs e) => Close(false);

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }
}
