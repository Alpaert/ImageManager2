using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ImageManager.App.Views.Settings;

public partial class RenameTagWindow : Window
{
    public string NewName => TxtNewName.Text?.Trim() ?? "";

    public RenameTagWindow()
    {
        InitializeComponent();
        TxtPrompt.Text = "请输入新的 Tag 名称:";
    }

    public void SetPrompt(string text) => TxtPrompt.Text = text;
    public void SetTitle(string text) => Title = text;

    private void BtnOk_Click(object? sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(NewName))
            Close(true);
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
