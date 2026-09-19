using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ImageManager.Common.Helpers;

namespace ImageManager.App.Controls;

/// <summary>
/// A transient, non-activating-style image preview surface. The owner supplies
/// decoded BGRA pixels; this window owns the resulting bitmap until it is hidden.
/// </summary>
public partial class HoverPreviewPopup : Window
{
    public const int DefaultMaxPreviewWidth = 800;
    public const int DefaultMaxPreviewHeight = 600;
    private const int MinimumPreviewWidth = 240;
    private const int MinimumPreviewHeight = 180;
    private const int HorizontalChrome = 20;
    private const int VerticalChrome = 78;

    private WriteableBitmap? _bitmap;

    public HoverPreviewPopup()
    {
        InitializeComponent();
        PointerEntered += (_, _) => PointerEnteredPreview?.Invoke(this, EventArgs.Empty);
        PointerExited += (_, _) => PointerExitedPreview?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? PointerEnteredPreview;
    public event EventHandler? PointerExitedPreview;

    /// <summary>
    /// Maximum image area used by the popup. The controller can update these
    /// values from the thumbnail settings before calling <see cref="Present"/>.
    /// </summary>
    public int MaxPreviewWidth { get; set; } = DefaultMaxPreviewWidth;
    public int MaxPreviewHeight { get; set; } = DefaultMaxPreviewHeight;

    /// <summary>
    /// The visible popup bounds in physical screen pixels. This is calculated
    /// from the native window position and current screen scale so callers can
    /// use it together with other top-level window coordinates.
    /// </summary>
    public PixelRect? ActualScreenRect => CurrentScreenRect;

    /// <summary>
    /// Alias used by hover-session hit testing. Keeping this separate makes the
    /// session code independent of how this popup obtains its actual bounds.
    /// </summary>
    public PixelRect? CurrentScreenRect { get; private set; }

    /// <summary>
    /// Replaces the preview image and displays this popup near <paramref name="anchor"/>.
    /// The supplied pixels must be BGRA8888 premultiplied-alpha data.
    /// </summary>
    public void Present(
        byte[] pixels,
        int decodedWidth,
        int decodedHeight,
        string fileName,
        int originalWidth,
        int originalHeight,
        long fileSize,
        Window owner,
        PixelPoint anchor,
        PixelRect sourceRect)
    {
        if (pixels.Length == 0 || decodedWidth <= 0 || decodedHeight <= 0)
            return;

        var screen = owner.Screens.ScreenFromPoint(anchor) ?? owner.Screens.Primary;
        var workArea = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
        var screenScaling = screen?.Scaling ?? 1d;
        var layout = CalculateLayout(
            anchor, sourceRect, workArea, screenScaling, decodedWidth, decodedHeight,
            Math.Max(MinimumPreviewWidth, MaxPreviewWidth),
            Math.Max(MinimumPreviewHeight, MaxPreviewHeight));
        if (layout is null)
        {
            HidePreview();
            return;
        }

        ReleaseBitmap();
        _bitmap = new WriteableBitmap(
            new PixelSize(decodedWidth, decodedHeight),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);
        using (var frameBuffer = _bitmap.Lock())
        {
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, frameBuffer.Address, pixels.Length);
        }

        PreviewImage.Source = _bitmap;
        FileNameText.Text = fileName;
        InfoText.Text = $"{originalWidth} x {originalHeight} 像素    {FileSizeFormatter.Format(fileSize)}";

        var resolvedLayout = layout.Value;
        var scale = resolvedLayout.ImageScale;
        PreviewImage.Width = Math.Max(1, Math.Round(decodedWidth * scale));
        PreviewImage.Height = Math.Max(1, Math.Round(decodedHeight * scale));
        Width = Math.Max(180, PreviewImage.Width + HorizontalChrome);
        Height = PreviewImage.Height + VerticalChrome;
        Position = resolvedLayout.Position;

        if (!IsVisible)
            Show(owner);

        // Refresh after Show so the hover controller sees the active native
        // top-level window position.
        CurrentScreenRect = GetActualScreenRect();
    }

    public void HidePreview()
    {
        if (IsVisible)
            Hide();
        CurrentScreenRect = null;
        ReleaseBitmap();
    }

    /// <summary>
    /// Immediately removes the currently displayed preview surface before a
    /// replacement image is decoded. This prevents a previous image from
    /// remaining visible during an asynchronous hover-session transition.
    /// </summary>
    public void ResetPreviewSurface()
    {
        PreviewImage.Source = null;
        CurrentScreenRect = null;
        if (IsVisible)
            Hide();
        ReleaseBitmap();
    }

    protected override void OnClosed(EventArgs e)
    {
        CurrentScreenRect = null;
        ReleaseBitmap();
        base.OnClosed(e);
    }

    private void ReleaseBitmap()
    {
        PreviewImage.Source = null;
        _bitmap?.Dispose();
        _bitmap = null;
    }

    private PixelRect GetActualScreenRect()
    {
        var screen = Screens.ScreenFromPoint(Position) ?? Screens.Primary;
        var scaling = screen?.Scaling > 0 ? screen.Scaling : 1d;
        // This popup has no native decorations and explicitly owns Width and
        // Height, so these values describe the top-level hit-test rectangle.
        var widthDip = ClientSize.Width > 0 ? ClientSize.Width : Width;
        var heightDip = ClientSize.Height > 0 ? ClientSize.Height : Height;
        var width = Math.Max(1, (int)Math.Ceiling(widthDip * scaling));
        var height = Math.Max(1, (int)Math.Ceiling(heightDip * scaling));
        return new PixelRect(Position.X, Position.Y, width, height);
    }

    private static (double ImageScale, PixelPoint Position)? CalculateLayout(
        PixelPoint anchor,
        PixelRect sourceRect,
        PixelRect workArea,
        double screenScaling,
        int imageWidth,
        int imageHeight,
        int maxImageWidth,
        int maxImageHeight)
    {
        screenScaling = screenScaling > 0 ? screenScaling : 1d;
        var cursorGuard = Math.Max(6, (int)Math.Round(12 * screenScaling));
        var gap = Math.Max(4, (int)Math.Round(8 * screenScaling));
        var sourceGuard = Math.Max(4, (int)Math.Round(8 * screenScaling));
        var chromeWidth = (int)Math.Ceiling(HorizontalChrome * screenScaling);
        var chromeHeight = (int)Math.Ceiling(VerticalChrome * screenScaling);
        var cursorRect = new PixelRect(
            anchor.X - cursorGuard,
            anchor.Y - cursorGuard,
            cursorGuard * 2,
            cursorGuard * 2);
        var sourceAvoidanceRect = Expand(sourceRect, sourceGuard);

        // Each region is on one side of both protected areas. This means the
        // popup can never cover either the pointer or the thumbnail that owns
        // the current hover session, even when the pointer is near an edge.
        var candidates = new[]
        {
            (Name: "below", X: workArea.X,
                Y: Math.Max(cursorRect.Bottom + gap, sourceAvoidanceRect.Bottom + gap),
                Width: workArea.Width,
                Height: workArea.Bottom - Math.Max(cursorRect.Bottom + gap, sourceAvoidanceRect.Bottom + gap),
                Priority: 2),
            (Name: "above", X: workArea.X,
                Y: workArea.Y,
                Width: workArea.Width,
                Height: Math.Min(cursorRect.Y - gap, sourceAvoidanceRect.Y - gap) - workArea.Y,
                Priority: 3),
            (Name: "right",
                X: Math.Max(cursorRect.Right + gap, sourceAvoidanceRect.Right + gap),
                Y: workArea.Y,
                Width: workArea.Right - Math.Max(cursorRect.Right + gap, sourceAvoidanceRect.Right + gap),
                Height: workArea.Height,
                Priority: 0),
            (Name: "left",
                X: workArea.X,
                Y: workArea.Y,
                Width: Math.Min(cursorRect.X - gap, sourceAvoidanceRect.X - gap) - workArea.X,
                Height: workArea.Height,
                Priority: 1)
        };

        var options = new List<(double Scale, PixelRect Rect, double Distance, int Priority)>();
        foreach (var candidate in candidates)
        {
            if (candidate.Width <= chromeWidth || candidate.Height <= chromeHeight)
                continue;

            var availableImageWidth = candidate.Width - chromeWidth;
            var availableImageHeight = candidate.Height - chromeHeight;
            var maxImageWidthPixels = Math.Min(
                availableImageWidth,
                (int)Math.Floor(maxImageWidth * screenScaling));
            var maxImageHeightPixels = Math.Min(
                availableImageHeight,
                (int)Math.Floor(maxImageHeight * screenScaling));
            var scale = Math.Min(1d, Math.Min(
                maxImageWidthPixels / (imageWidth * screenScaling),
                maxImageHeightPixels / (imageHeight * screenScaling)));
            var minimumReadableScale = Math.Min(1d, Math.Max(
                MinimumPreviewWidth / (double)imageWidth,
                MinimumPreviewHeight / (double)imageHeight));
            if (scale < minimumReadableScale)
                continue;

            var width = (int)Math.Ceiling((imageWidth * scale + HorizontalChrome) * screenScaling);
            var height = (int)Math.Ceiling((imageHeight * scale + VerticalChrome) * screenScaling);
            var x = candidate.Name switch
            {
                "right" => candidate.X,
                "left" => candidate.X + candidate.Width - width,
                _ => Clamp(anchor.X - width / 2, candidate.X, candidate.X + candidate.Width - width)
            };
            var y = candidate.Name switch
            {
                "below" => candidate.Y,
                "above" => candidate.Y + candidate.Height - height,
                _ => Clamp(anchor.Y - height / 2, candidate.Y, candidate.Y + candidate.Height - height)
            };
            var rect = new PixelRect(x, y, width, height);
            if (rect.Intersects(cursorRect) || rect.Intersects(sourceAvoidanceRect))
                continue;

            if (rect.X < workArea.X || rect.Y < workArea.Y ||
                rect.Right > workArea.Right || rect.Bottom > workArea.Bottom)
                continue;

            options.Add((scale, rect, DistanceToRect(anchor, rect), candidate.Priority));
        }

        var selected = options
            .OrderByDescending(option => option.Scale)
            .ThenBy(option => option.Distance)
            .ThenBy(option => option.Priority)
            .FirstOrDefault();
        if (selected.Rect.Width <= 0 || selected.Rect.Height <= 0)
            return null;

        return (selected.Scale, new PixelPoint(selected.Rect.X, selected.Rect.Y));
    }

    private static PixelRect Expand(PixelRect rect, int amount) =>
        new(rect.X - amount, rect.Y - amount, rect.Width + amount * 2, rect.Height + amount * 2);

    private static int Clamp(int value, int minimum, int maximum) =>
        Math.Clamp(value, minimum, Math.Max(minimum, maximum));

    private static double DistanceToRect(PixelPoint point, PixelRect rect)
    {
        var horizontal = point.X < rect.X ? rect.X - point.X :
            point.X > rect.Right ? point.X - rect.Right : 0;
        var vertical = point.Y < rect.Y ? rect.Y - point.Y :
            point.Y > rect.Bottom ? point.Y - rect.Bottom : 0;
        return Math.Sqrt(horizontal * horizontal + vertical * vertical);
    }
}
