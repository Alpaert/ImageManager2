using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ImageManager.App.Controls;
using ImageManager.App.ViewModels;
using ImageManager.App.Views;

internal static class BreadcrumbNativeChecks
{
    public static void Run()
    {
        var names = new[] { "素材归档", "2026 年旅行摄影", "亚洲", "日本", "京都", "秋季采风", "相机原片", "傍晚街景", "精选照片" };
        var nodes = names.Select((name, i) => new FolderTreeNode
        {
            DisplayName = name, Path = @"E:\" + string.Join("\\", names.Take(i + 1))
        }).ToArray();
        var bar = new ResponsiveBreadcrumbBar { ItemsSource = nodes };
        var window = new Window
        {
            Width = 1100, Height = 100, ShowInTaskbar = false,
            Content = new Border { Padding = new Thickness(16), Background = Brushes.White, Child = bar },
            RequestedThemeVariant = ThemeVariant.Light
        };
        window.Styles.Add(new StyleInclude(new Uri("avares://ImageManager.App/"))
        {
            Source = new Uri("avares://ImageManager.App/Views/MainWindowStyles.axaml")
        });
        string? target = null;
        bar.NavigateRequested += (_, e) => target = e.Path;
        try
        {
            window.Show();
            Pump(window);
            Require(VisibleCrumbs(bar).Length == nodes.Length, "Wide breadcrumbs must display the full path.");
            Require(!bar.GetVisualDescendants().OfType<ScrollViewer>().Any(), "Breadcrumbs must not use a scroll surface.");
            VisibleCrumbs(bar)[0].Focus();
            window.Width = 440;
            Pump(window);
            var shown = VisibleCrumbs(bar);
            Require(shown.Length > 1 && shown.Length < nodes.Length, "Narrow breadcrumbs must retain several nearest ancestors.");
            Require(shown.Select(b => ((FolderTreeNode)b.DataContext!).Path)
                    .SequenceEqual(nodes.TakeLast(shown.Length).Select(n => n.Path)),
                "Visible breadcrumbs must be the consecutive suffix ending with the current folder.");
            foreach (var button in shown)
            {
                var position = button.TranslatePoint(default, bar)!.Value;
                Require(position.X >= -0.5 && position.X + button.Bounds.Width <= bar.Bounds.Width + 0.5,
                    "A breadcrumb escaped the available row width.");
                Require(Equals(ToolTip.GetTip(button), ((FolderTreeNode)button.DataContext!).Path), "Full path tooltip is missing.");
            }
            shown[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(target == ((FolderTreeNode)shown[0].DataContext!).Path, "A visible ancestor navigates to the wrong path.");

            var overflow = bar.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "BreadcrumbOverflow");
            Require(overflow.IsEffectivelyVisible, "Hidden ancestors need the overflow entry.");
            Require(overflow.IsKeyboardFocusWithin, "Collapsing a focused ancestor must transfer focus to overflow.");
            overflow.Flyout!.ShowAt(overflow);
            Pump(window);
            var flyout = overflow.Flyout as MenuFlyout;
            Require(flyout is { IsOpen: true }, "The overflow entry must open its ancestor menu.");
            var menuItems = flyout!.Items.OfType<MenuItem>().ToArray();
            Require(menuItems.Select(m => ((FolderTreeNode)m.DataContext!).Path)
                    .SequenceEqual(nodes.Take(nodes.Length - shown.Length).Reverse().Select(n => n.Path)),
                "Hidden ancestors must be navigable in nearest-first order.");
            menuItems[0].RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            Pump(window);
            Require(!flyout.IsOpen, "Escape must close the ancestor menu.");
            flyout.ShowAt(overflow);
            Pump(window);
            menuItems[0].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Require(target == ((FolderTreeNode)menuItems[0].DataContext!).Path, "The overflow menu lost its target path.");
            flyout.Hide();

            window.Width = 1100;
            Pump(window);
            Require(VisibleCrumbs(bar).Length == nodes.Length && !overflow.IsEffectivelyVisible,
                "Growing the row must restore all ancestors and remove the overflow entry.");
            nodes[^1].DisplayName = "这是一个名称特别长的当前文件夹_需要保留当前位置并且只对当前目录的名称使用省略号_最终精选照片";
            window.Width = 260;
            Pump(window);
            shown = VisibleCrumbs(bar);
            Require(shown.Length == 1 && ReferenceEquals(shown[0].DataContext, nodes[^1]),
                "An oversized current name must keep its position and hide all ancestors.");
            var label = shown[0].GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == nodes[^1].DisplayName);
            Require(label.TextTrimming == TextTrimming.CharacterEllipsis && label.Bounds.Width < 230,
                "The current name must ellipsize inside the available space.");
            overflow.Flyout!.ShowAt(overflow);
            Pump(window);
            bar.IsVisible = false;
            Pump(window);
            Require(!flyout.IsOpen, "Hiding the breadcrumb row must close its ancestor menu.");
            bar.IsVisible = true;
            Pump(window);
            flyout.ShowAt(overflow);
            Pump(window);
            bar.ItemsSource = [new FolderTreeNode { Path = @"E:\", DisplayName = "" }];
            Pump(window);
            Require(!flyout.IsOpen && VisibleCrumbs(bar).Length == 1 && !overflow.IsEffectivelyVisible,
                "Changing to a root must discard the old ancestor menu.");
            Require(VisibleCrumbs(bar)[0].GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == @"E:\"),
                "A drive root must have a readable label even without a directory name.");
            bar.ItemsSource = [];
            Pump(window);
            Require(VisibleCrumbs(bar).Length == 0 && !overflow.IsEffectivelyVisible, "An empty path retained stale entries.");

            nodes[^1].DisplayName = names[^1];
            bar.ItemsSource = nodes;
            window.Width = 560;
            Pump(window);
            using var capture = new RenderTargetBitmap(new PixelSize(560, 100), new Vector(96, 96));
            capture.Render((Control)window.Content!);
            var capturePath = Path.Combine(Path.GetTempPath(), "ImageManager-breadcrumb-regression.png");
            capture.Save(capturePath);
            Console.WriteLine($"breadcrumbScreenshot={capturePath}");
            Console.WriteLine("PASS native breadcrumbs: suffix, overflow navigation, resize, long name, alias, root and empty path");
        }
        finally { window.Close(); }
    }

    public static void VerifyWorkspace(MainWindow window, MainWindowViewModel vm)
    {
        var oldPath = vm.CurrentFolder;
        var oldWidth = window.Width;
        var oldOpen = vm.DetailsPane.IsOpen;
        var oldSearch = vm.IsShowingSearchResult;
        var oldSelection = vm.SelectedFilePaths.ToArray();
        var root = new FolderTreeNode { Path = @"E:\breadcrumb-fixture", DisplayName = "素材归档" };
        vm.FolderTree.Add(root);
        try
        {
            vm.CurrentFolder = Path.Combine(root.Path, "2026 年旅行摄影", "亚洲", "日本", "京都", "秋季采风", "相机原片", "傍晚街景", "精选照片");
            window.Width = 1100;
            vm.DetailsPane.IsOpen = true;
            Pump(window);
            var bar = window.FindControl<ResponsiveBreadcrumbBar>("FolderBreadcrumbBar")!;
            var heading = window.FindControl<Grid>("WorkspaceHeadingRow")!;
            Require(bar.IsVisible && Math.Abs(bar.Bounds.Width - heading.Bounds.Width) < 1 && bar.Bounds.Bottom <= heading.Bounds.Top,
                "The real workspace breadcrumb must use its own full-width row above the heading.");
            var openCount = VisibleCrumbs(bar).Length;
            vm.DetailsPane.IsOpen = false;
            Pump(window);
            Require(VisibleCrumbs(bar).Length > openCount, "Collapsing the preview must restore more ancestors.");
            vm.IsShowingSearchResult = true;
            Pump(window);
            Require(vm.WorkspaceTitle == "搜索结果" && bar.ItemsSource![^1].Path == vm.CurrentFolder,
                "Search results must preserve the full source-directory breadcrumb.");
            vm.ReplaceSelectedFiles(vm.ActiveFileList.Take(1));
            var priorSelection = vm.SelectedFilePaths.ToArray();
            Require(priorSelection.Length == 1, "The shortcut regression requires a selected gallery file.");
            VisibleCrumbs(bar)[^1].RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent, Key = Key.A, KeyModifiers = KeyModifiers.Control
            });
            Require(vm.SelectedFilePaths.SequenceEqual(priorSelection), "Breadcrumb keyboard input leaked to gallery selection.");
            Console.WriteLine("PASS workspace breadcrumbs: full header row, preview resize, search context and keyboard isolation");
        }
        finally
        {
            vm.FolderTree.Remove(root);
            vm.CurrentFolder = oldPath;
            vm.IsShowingSearchResult = oldSearch;
            vm.ReplaceSelectedFiles(oldSelection);
            vm.DetailsPane.IsOpen = oldOpen;
            window.Width = oldWidth;
            Pump(window);
        }
    }

    private static Button[] VisibleCrumbs(ResponsiveBreadcrumbBar bar) => bar.GetVisualDescendants()
        .OfType<Button>().Where(b => b.IsEffectivelyVisible && b.DataContext is FolderTreeNode).ToArray();

    private static void Pump(Window window)
    {
        for (var i = 0; i < 5; i++) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
