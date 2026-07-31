using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ImageManager.App.ViewModels;

namespace ImageManager.App.Views.Settings;

public partial class VectorIndexWindow : Window
{
    public VectorIndexWindow()
    {
        InitializeComponent();
        Opened += async (_, _) =>
        {
            if (DataContext is VectorIndexViewModel viewModel)
            {
                viewModel.FolderPicker = async () =>
                {
                    var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                    {
                        Title = "选择向量索引文件夹",
                        AllowMultiple = false
                    });
                    return folders.Count > 0 ? folders[0].Path.LocalPath : null;
                };
                await viewModel.InitializeAsync();
            }
        };
    }
}
