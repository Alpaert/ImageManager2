using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ImageManager.App.ViewModels;

namespace ImageManager.App.Views.Settings;

public partial class TagEditWindow : Window
{
    private TagEditViewModel Vm => (TagEditViewModel)DataContext!;

    public TagEditWindow()
    {
        InitializeComponent();
        // KeyUp bubbling: unlike KeyDown (which Button converts to Click), KeyUp reliably bubbles to Window
        KeyUp += OnWindowKeyUp;
    }

    private bool _suppressNextEnterKeyUp;

    private void RootGrid_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not TextBox and not Button)
            RootGrid.Focus();
    }

    private void OnWindowKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        if (_suppressNextEnterKeyUp)
        {
            _suppressNextEnterKeyUp = false;
            return;
        }

        // Don't intercept Enter in tag input TextBox — TxtTagInput_KeyDown handles adding the tag
        if (e.Source is TextBox) return;

        Close(true);
        e.Handled = true;
    }

    private void TxtTagInput_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (string.IsNullOrWhiteSpace(Vm.TagInputText))
            {
                RootGrid.Focus();
                _suppressNextEnterKeyUp = true;
            }
            else
                Vm.AddTagCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void TxtFavoriteInput_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (string.IsNullOrWhiteSpace(Vm.NewFavoriteText))
            {
                RootGrid.Focus();
                _suppressNextEnterKeyUp = true;
            }
            else
                Vm.AddFavoriteCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void AutoTag_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string name)
            Vm.AddSuggestedTagCommand.Execute(name);
    }

    private void DeleteCurrentTag_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string name)
            Vm.DeleteCurrentTagCommand.Execute(name);
    }

    private void FavoriteTag_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string name)
            Vm.AddFavoriteTagCommand.Execute(name);
    }

    private void DeleteFavorite_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string name)
            Vm.DeleteFavoriteCommand.Execute(name);
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
