using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ImageManager.App.Controls;
using ImageManager.App.ViewModels;
using SkiaSharp;

internal static class ThumbnailDecodeChecks
{
    public static void Run()
    {
        var initial = Encode(31, 47);
        var superseded = Encode(52, 79);
        var newest = Encode(29, 43);
        var replacement = Encode(47, 53);
        var item = new ImageViewItem { ThumbnailData = initial };
        var image = new AsyncThumbnailImage { Width = 160, Height = 120, Stretch = Stretch.UniformToFill };
        using var binding = image.Bind(AsyncThumbnailImage.ThumbnailDataProperty,
            new Binding(nameof(ImageViewItem.ThumbnailData)));
        var holder = new Border { DataContext = item, Child = image };
        var window = new Window { Width = 320, Height = 240, ShowInTaskbar = false, Content = holder };
        try
        {
            window.Show();
            WaitUntil(() => image.Source is Bitmap first && first.PixelSize.Width == 31);
            var original = image.Source;
            var bounds = image.Bounds;
            Require(image.HasThumbnail, "A published bitmap must set decoded readiness.");

            var observedWidths = new List<int>();
            var clearCount = 0;
            image.PropertyChanged += (_, e) =>
            {
                if (e.Property != Image.SourceProperty)
                    return;
                Require(Dispatcher.UIThread.CheckAccess(), "Thumbnail Source published off the UI thread.");
                if (image.Source is Bitmap bitmap)
                    observedWidths.Add(bitmap.PixelSize.Width);
                else if (image.Source is null)
                    clearCount++;
            };

            item.ThumbnailData = superseded;
            Require(ReferenceEquals(original, image.Source) && image.HasThumbnail,
                "A quality refresh cleared the old image before its replacement decoded.");
            item.ThumbnailData = newest;
            WaitUntil(() => image.Source is Bitmap last && last.PixelSize.Width == 29);
            Require(!observedWidths.Contains(52), "A superseded request overwrote the newer thumbnail.");
            Require(image.Bounds == bounds, "A decoded size change moved the explicitly sized image.");

            item.ThumbnailData = [1, 2, 3]; // Malformed input must not clear the last good image.
            Dispatcher.UIThread.RunJobs();
            Require(image.HasThumbnail && image.Source is Bitmap current && current.PixelSize.Width == 29,
                "An undecodable replacement discarded the current thumbnail.");

            holder.DataContext = new ImageViewItem { ThumbnailData = replacement };
            Require(image.Source is null && !image.HasThumbnail, "A recycled container displayed the previous item's thumbnail.");
            WaitUntil(() => image.Source is Bitmap next && next.PixelSize.Width == 47);

            ((ImageViewItem)holder.DataContext!).ThumbnailData = Encode(61, 77);
            var clearsBeforeHotSwap = clearCount;
            var publicationsBeforeHotSwap = observedWidths.Count;
            holder.DataContext = new ImageViewItem { ThumbnailData = initial };
            Require(clearCount > clearsBeforeHotSwap, "A reused container must clear ownership of the previous item.");
            Require(image.HasThumbnail && ReferenceEquals(original, image.Source),
                "A ready cached bitmap must publish on rebind without pumping dispatcher jobs.");

            var recycledItem = (ImageViewItem)holder.DataContext!;
            recycledItem.ThumbnailData = null;
            Require(image.Source is null && !image.HasThumbnail, "Null bytes did not clear the visual.");
            recycledItem.ThumbnailData = Encode(63, 78);
            var publicationsBeforeDetach = observedWidths.Count;
            holder.Child = null;
            Dispatcher.UIThread.RunJobs();
            Require(image.Source is null && !image.HasThumbnail, "A detached control retained or published a bitmap.");
            Require(!observedWidths.Skip(publicationsBeforeHotSwap).Contains(61) &&
                !observedWidths.Skip(publicationsBeforeDetach).Contains(63),
                "A cancelled decode published after rebind or detachment.");

            recycledItem.ThumbnailData = initial;
            holder.Child = image;
            Require(image.HasThumbnail && ReferenceEquals(original, image.Source),
                "A ready cached bitmap must publish on reattachment without pumping dispatcher jobs.");
            var publicationsBeforeWarmDetach = observedWidths.Count;
            holder.Child = null;
            Dispatcher.UIThread.RunJobs();
            Require(observedWidths.Count == publicationsBeforeWarmDetach && image.Source is null,
                "Losing the inherited DataContext republished a cached bitmap without an owner.");
        }
        finally
        {
            window.Close();
        }
        Console.WriteLine("PASS async gallery bitmap replacement, recycle, detach and reattach");
    }

    private static byte[] Encode(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
            canvas.Clear(SKColors.Red);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static void WaitUntil(Func<bool> predicate)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (!predicate())
        {
            Dispatcher.UIThread.RunJobs();
            if (timer.Elapsed > TimeSpan.FromSeconds(5))
                throw new TimeoutException("Timed out waiting for the gallery bitmap.");
            Thread.Sleep(5);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
