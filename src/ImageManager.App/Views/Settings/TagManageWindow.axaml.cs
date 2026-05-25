using Avalonia.Controls;
using Avalonia.Interactivity;
using ImageManager.App.ViewModels;
using ImageManager.Core.Models;
using ImageManager.Core.Services;

namespace ImageManager.App.Views.Settings;

public partial class TagManageWindow : Window
{
    private TagManageViewModel Vm => (TagManageViewModel)DataContext!;

    public TagManageWindow()
    {
        InitializeComponent();
        Closing += (_, _) => Vm.Dispose();
    }

    private async void Delete_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not TagCount tag) return;
        await Vm.DeleteTagCommand.ExecuteAsync(tag);
    }

    private async void Rename_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not TagCount tag) return;

        var prompt = new RenameTagWindow();
        if (!await prompt.ShowDialog<bool>(this)) return;

        var newName = prompt.NewName;
        if (string.IsNullOrWhiteSpace(newName) || string.Equals(tag.Name, newName, StringComparison.OrdinalIgnoreCase))
            return;

        // Defer rename to avoid nested dispatcher frame deadlock after ShowDialog
        var oldName = tag.Name;
        Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var result = await Vm.HandleRenameAsync(oldName, newName);
            if (result == RenameResult.Conflict)
            {
                var confirm = new RenameTagWindow();
                confirm.SetTitle("Tag 已存在 — 合并");
                confirm.SetPrompt($"Tag \"{newName}\" 已存在，合并 \"{oldName}\" 到 \"{newName}\"？（确定请再输入一次）");
                if (await confirm.ShowDialog<bool>(this))
                    await Vm.HandleMergeAsync(oldName, newName);
            }
            if (result != RenameResult.Cancelled)
                Vm.UpdateTagName(oldName, newName);
            else
                Vm.RefreshList();
        });
    }
}
