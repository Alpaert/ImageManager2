using Avalonia.Controls;
using ImageManager.App.ViewModels;

namespace ImageManager.App.Views.Settings;

public partial class ArchivedFoldersWindow : Window
{
    public ArchivedFoldersWindow()
    {
        InitializeComponent();
        Opened += async (_, _) =>
        {
            if (DataContext is ArchivedFoldersViewModel viewModel)
                await viewModel.InitializeAsync();
        };
    }
}
