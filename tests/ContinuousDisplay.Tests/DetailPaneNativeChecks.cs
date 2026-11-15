using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ImageManager.App.Controls;
using ImageManager.App.Helpers;
using ImageManager.App.Services;
using ImageManager.App.ViewModels;
using SkiaSharp;

internal static class DetailPaneNativeChecks
{
    public static void Run()
    {
        using var bitmap = new SKBitmap(300, 160);
        using (var canvas = new SKCanvas(bitmap)) canvas.Clear(SKColors.CadetBlue);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var bytes = encoded.ToArray();
        using var model = new DetailPaneViewModel(new Loader(), new AvaloniaDispatcher());
        var source = new ImageViewItem { FilePath = "native-preview.png", ThumbnailData = bytes };
        var pane = new DetailPreviewPane { DataContext = model };
        var window = new Window
        {
            Width = 312, Height = 850, Content = pane, ShowInTaskbar = false,
            RequestedThemeVariant = ThemeVariant.Dark
        };
        window.Styles.Add(new StyleInclude(new Uri("avares://ImageManager.App/"))
        {
            Source = new Uri("avares://ImageManager.App/Views/MainWindowStyles.axaml")
        });
        try
        {
            model.Show(source.FilePath, source, 256, 3);
            window.Show();
            Wait(() => !model.IsLoading && model.Tags.Count == 36, window);
            var image = pane.GetVisualDescendants().OfType<AsyncThumbnailImage>().Single();
            Wait(() => image.HasThumbnail, window);
            Require(image.Stretch == Stretch.Uniform, "Detail preview must preserve the full image aspect ratio.");
            Require(!ReferenceEquals(model.ThumbnailItem, source) &&
                    ReferenceEquals(model.ThumbnailItem!.ThumbnailData, bytes),
                "Detail preview must own an independent item sharing the source bytes.");
            Require(model.SelectionHint.Contains("3"), "The multi-selection hint is missing.");

            var scroller = pane.FindControl<ScrollViewer>("TagScroller")!;
            var chips = pane.FindControl<WrapPanel>("TagChips")!;
            var toggle = pane.FindControl<Button>("ToggleTags")!;
            CheckFolded();
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(window);
            Require(chips.Children.All(c => c.IsVisible) && scroller.Bounds.Height <= 150.5,
                "Expanded tags must stay within the bounded scroll area.");
            Require(scroller.Extent.Height > scroller.Viewport.Height &&
                    scroller.VerticalScrollBarVisibility == ScrollBarVisibility.Auto,
                "Long tag lists need independent scrolling.");
            window.Width = 248;
            Pump(window);
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(window);
            CheckFolded();
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            source.ThumbnailData = null;
            Pump(window);
            Require(model.ThumbnailItem?.ThumbnailData is not null, "Unloading a gallery card cleared the preview owner.");
            model.Show("next-preview.png", new ImageViewItem { FilePath = "next-preview.png", ThumbnailData = bytes }, 256, 1);
            Wait(() => !model.IsLoading && model.Tags.Count == 36, window);
            CheckFolded();

            window.Width = 312;
            Pump(window);
            using (var capture = new RenderTargetBitmap(new PixelSize(312, 850), new Vector(96, 96)))
            {
                capture.Render(pane);
                var path = Path.Combine(Path.GetTempPath(), "ImageManager-detail-preview-regression.png");
                capture.Save(path);
                Console.WriteLine($"detailScreenshot={path}");
            }
            var owner = model.ThumbnailItem!;
            model.ShowFolder("preview-folder", bytes);
            Wait(() => !model.IsLoading && model.Folder != null, window);
            Require(model.IsFolder && !model.CanEditTags && model.Folder!.FileCount == 12,
                "Folder details must show physical counts without file tag actions.");
            Require(pane.FindControl<Button>("DetailOpen")!.Content?.ToString() == "打开文件夹",
                "Folder preview must preserve the open-folder action.");
            using (var folderCapture = new RenderTargetBitmap(new PixelSize(312, 850), new Vector(96, 96)))
            {
                folderCapture.Render(pane);
                var folderPath = Path.Combine(Path.GetTempPath(), "ImageManager-folder-preview-regression.png");
                folderCapture.Save(folderPath);
                Console.WriteLine($"folderDetailScreenshot={folderPath}");
            }
            model.IsOpen = false;
            Pump(window);
            Require(owner.ThumbnailData is null && image.Source is null,
                "Closing a still-attached pane must release its thumbnail ownership.");
            Console.WriteLine("PASS native detail tags fold/expand/resize, independent ownership, selection hint and close release");

            void CheckFolded()
            {
                Pump(window);
                var shown = chips.Children.Where(c => c.IsVisible).ToArray();
                Require(shown.Length > 0 && shown.Length < chips.Children.Count && toggle.IsVisible,
                    "Long tags must be folded by default.");
                var rows = shown.Select(c => Math.Round(c.Bounds.Y, 1)).Distinct().Count();
                Require(rows <= 2 && scroller.Bounds.Height <= 54.5,
                    $"Collapsed tags exceed two full rows: {rows}, height {scroller.Bounds.Height}.");
                Require(shown.All(c => c.Bounds.Right <= chips.Bounds.Width + 0.5),
                    "A long tag escaped the available width.");
            }
        }
        finally { window.Close(); }
    }

    private static void Pump(Window window)
    {
        for (var i = 0; i < 5; i++) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    }

    private static void Wait(Func<bool> predicate, Window window)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (!predicate())
        {
            Pump(window);
            if (timer.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Native detail preview did not settle.");
            Thread.Sleep(5);
        }
        Pump(window);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Loader : IDetailPaneDataLoader
    {
        public Task<DetailPaneData> LoadAsync(DetailPaneLoadRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(request.IsFolder
                ? new DetailPaneData([], Folder: new ImageManager.Core.Models.FolderDetailsSnapshot(
                    request.Path, 4, 12, 9, 2, 3145728, ImageManager.Core.Models.FolderStatisticsState.Complete, DateTimeOffset.UtcNow))
                : new DetailPaneData(
                Enumerable.Range(0, 36).Select(i => i == 0
                    ? "这是一条需要省略显示而不能撑破预览栏的很长的标签"
                    : $"标签 {i:00}").ToArray(), 2400, 1280, 1258300, new DateTime(2026, 11, 12, 12, 30, 0)));
    }
}
