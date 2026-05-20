using Avalonia.Controls;
using Avalonia.Input;
using ImageManager.App.ViewModels;

namespace ImageManager.App.Views.Settings;

public partial class TagImageViewerWindow : Window
{
    private TagImageViewerViewModel Vm => (TagImageViewerViewModel)DataContext!;

    public TagImageViewerWindow()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
    }

    private void ArrowLeft_Click(object? sender, PointerPressedEventArgs e)
    {
        Vm.PrevCommand.Execute(null);
        e.Handled = true;
    }

    private void ArrowRight_Click(object? sender, PointerPressedEventArgs e)
    {
        Vm.NextCommand.Execute(null);
        e.Handled = true;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
        else if (e.Key == Key.Left) Vm.PrevCommand.Execute(null);
        else if (e.Key == Key.Right) Vm.NextCommand.Execute(null);
        e.Handled = true;
    }
}
