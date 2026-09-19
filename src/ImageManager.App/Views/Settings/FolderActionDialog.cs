using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace ImageManager.App.Views.Settings;

public static class FolderActionDialog
{
    public static Task<bool?> ShowScopeAsync(
        Window owner,
        string title,
        string folderName,
        string folderPath,
        string description,
        string actionText,
        bool defaultRecursive = false)
    {
        var window = CreateWindow(title);
        var recursive = new RadioButton { Content = "包含当前文件夹及所有子文件夹", GroupName = "scope", IsChecked = defaultRecursive };
        var current = new RadioButton { Content = "仅当前文件夹", GroupName = "scope", IsChecked = !defaultRecursive };
        var action = new Button { Content = actionText, MinWidth = 96 };
        var cancel = new Button { Content = "取消", MinWidth = 76 };

        action.Click += (_, _) => window.Close(recursive.IsChecked == true);
        cancel.Click += (_, _) => window.Close(null);
        HookEscape(window, () => window.Close(null));

        window.Content = BuildContent(folderName, folderPath, description,
            new StackPanel { Spacing = 8, Children = { current, recursive } }, action, cancel);
        return window.ShowDialog<bool?>(owner);
    }

    public static Task<bool?> ShowHashModeAsync(Window owner, string folderName, string folderPath)
    {
        var window = CreateWindow("计算图片指纹");
        var incremental = new RadioButton { Content = "补齐缺失和失败项", GroupName = "hash", IsChecked = true };
        var failedOnly = new RadioButton { Content = "仅重试失败项", GroupName = "hash" };
        var action = new Button { Content = "开始计算", MinWidth = 96 };
        var cancel = new Button { Content = "取消", MinWidth = 76 };

        action.Click += (_, _) => window.Close(failedOnly.IsChecked == true);
        cancel.Click += (_, _) => window.Close(null);
        HookEscape(window, () => window.Close(null));

        window.Content = BuildContent(folderName, folderPath,
            "将固定处理当前文件夹及所有子文件夹。请选择计算模式：",
            new StackPanel { Spacing = 8, Children = { incremental, failedOnly } }, action, cancel);
        return window.ShowDialog<bool?>(owner);
    }

    public static Task<bool> ShowConfirmAsync(
        Window owner,
        string title,
        string folderName,
        string folderPath,
        string description,
        string actionText)
    {
        var window = CreateWindow(title);
        var action = new Button { Content = actionText, MinWidth = 96 };
        var cancel = new Button { Content = "取消", MinWidth = 76 };
        action.Click += (_, _) => window.Close(true);
        cancel.Click += (_, _) => window.Close(false);
        HookEscape(window, () => window.Close(false));

        window.Content = BuildContent(folderName, folderPath, description, null, action, cancel);
        return window.ShowDialog<bool>(owner);
    }

    public static Task<string?> ShowAliasAsync(
        Window owner,
        string folderName,
        string folderPath,
        string? currentAlias)
    {
        var window = CreateWindow("设置文件夹显示名称");
        var input = new TextBox { Text = currentAlias ?? string.Empty, PlaceholderText = "输入显示名称", MinWidth = 360 };
        var restore = new Button { Content = "恢复原文件夹名称", MinWidth = 132 };
        var save = new Button { Content = "保存", MinWidth = 76 };
        var cancel = new Button { Content = "取消", MinWidth = 76 };

        restore.Click += (_, _) => { input.Text = string.Empty; input.Focus(); };
        save.Click += (_, _) => window.Close(input.Text?.Trim() ?? string.Empty);
        cancel.Click += (_, _) => window.Close(null);
        HookEscape(window, () => window.Close(null));

        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(new TextBlock { Text = "显示名称（不改磁盘上的文件夹名称）：", TextWrapping = TextWrapping.Wrap });
        body.Children.Add(input);
        window.Content = BuildContent(folderName, folderPath, body, save, cancel, restore);
        return window.ShowDialog<string?>(owner);
    }

    private static Window CreateWindow(string title) => new()
    {
        Title = title,
        Width = 520,
        SizeToContent = SizeToContent.Height,
        MinHeight = 220,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        ShowInTaskbar = false,
        CanResize = false,
        Padding = new Thickness(22)
    };

    private static Control BuildContent(string name, string path, string description, Control? options, Button primary, Button cancel)
    {
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new TextBlock { Text = name, FontSize = 18, FontWeight = Avalonia.Media.FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = path, Opacity = 0.78, TextWrapping = TextWrapping.Wrap, MaxWidth = 470 });
        body.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, MaxWidth = 470 });
        if (options is not null) body.Children.Add(options);
        body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancel, primary }
        });
        return body;
    }

    private static Control BuildContent(string name, string path, Control customBody, Button primary, Button cancel, Button? extra)
    {
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new TextBlock { Text = name, FontSize = 18, FontWeight = Avalonia.Media.FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = path, Opacity = 0.78, TextWrapping = TextWrapping.Wrap, MaxWidth = 470 });
        body.Children.Add(customBody);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        if (extra is not null) buttons.Children.Add(extra);
        buttons.Children.Add(cancel);
        buttons.Children.Add(primary);
        body.Children.Add(buttons);
        return body;
    }

    private static void HookEscape(Window window, Action close)
    {
        window.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                close();
            }
        };
    }
}
