using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ImageManager.App.ViewModels;
using ImageManager.Core.Services;

namespace ImageManager.App.Views.Settings;

public partial class TagEditWindow : Window
{
    private TagEditViewModel Vm => (TagEditViewModel)DataContext!;

    public TagEditWindow()
    {
        InitializeComponent();
        KeyUp += OnWindowKeyUp;
        // Intercept all close paths (X button / Alt+F4 / Enter / buttons) to clear
        // large collections before the window tears down 4000+ visual children
        Closing += (_, _) =>
        {
            Vm.Dispose();
            Vm.AutoTagSuggestions.Clear();
            Vm.FilteredCurrentTags.Clear();
            Vm.FavoriteTagSuggestions.Clear();
        };
    }

    private bool _suppressNextEnterKeyUp;

    private void RootGrid_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not TextBox and not Button)
            RootGrid.Focus();
    }

    private void CloseWindow(bool result)
    {
        Close(result);
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

        CloseWindow(true);
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

    private async void AutoTag_Rename_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem || menuItem.Tag is not string oldName) return;

        var prompt = new RenameTagWindow();
        if (!await prompt.ShowDialog<bool>(this)) return;

        var newName = prompt.NewName;
        if (string.IsNullOrWhiteSpace(newName) || string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
            return;

        // Defer rename to avoid nested dispatcher frame deadlock after ShowDialog
        Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var result = await Vm.HandleRenameAsync(oldName, newName);
            if (result == RenameResult.Conflict)
            {
                var confirm = new RenameTagWindow();
                confirm.SetTitle("Tag 已存在 — 合并");
                confirm.SetPrompt($"Tag \"{newName}\" 已存在，合并 \"{oldName}\" 到 \"{newName}\"?(确定请再输入一次)");
                if (await confirm.ShowDialog<bool>(this))
                    await Vm.HandleMergeAsync(oldName, newName);
            }
        });
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
        CloseWindow(true);
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e)
    {
        CloseWindow(false);
    }

    private void BtnClearAllCurrentTags_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (DataContext is ViewModels.TagEditViewModel vm)
        {
            vm.ClearAllCurrentTagsCommand.Execute(null);
        }
    }
}
