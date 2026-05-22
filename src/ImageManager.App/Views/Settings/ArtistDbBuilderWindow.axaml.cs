using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ImageManager.App.Views.Settings;

public partial class ArtistDbBuilderWindow : Window
{
    public ArtistDbBuilderWindow()
    {
        InitializeComponent();
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e) => Close();
}
