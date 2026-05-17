using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ImageManager.App.ViewModels;
using ImageManager.Infrastructure.Imaging;

namespace ImageManager.App.Views.Settings;

public partial class PreviewWindow : Window
{
    private PreviewViewModel Vm => (PreviewViewModel)DataContext!;

    private bool _imageReady;
    private bool _isDragging;
    private Point _dragStart;
    private double _dragStartOffX, _dragStartOffY;
    private bool _isNavigating;

    public PreviewWindow()
    {
        InitializeComponent();
        // Tunnel = PreviewMouseWheel in WPF — fires before ScrollViewer's scroll handler
        Scroller.AddHandler(ScrollViewer.PointerWheelChangedEvent, OnPreviewPointerWheel, RoutingStrategies.Tunnel);
        // Keyboard navigation
        KeyDown += OnPreviewKeyDown;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Save position BEFORE native window destruction (Closed fires too late)
        Vm.SavedLeft = Position.X;
        Vm.SavedTop = Position.Y;
        base.OnClosing(e);
    }

    /// <summary>Create with image list for navigation. Replace later with PNG icons via IconLeft/IconRight.</summary>
    public static PreviewWindow Create(List<string> imagePaths, int startIndex)
    {
        var win = new PreviewWindow();
        var vm = new PreviewViewModel
        {
            ImagePaths = imagePaths,
            CurrentIndex = startIndex,
            HasPrev = startIndex > 0,
            HasNext = startIndex < imagePaths.Count - 1
        };
        win.DataContext = vm;
        win.LoadImage(imagePaths[startIndex]);
        return win;
    }

    /// <summary>Compatibility overload for single-file preview (no navigation)</summary>
    public static PreviewWindow Create(string filePath)
    {
        return Create(new List<string> { filePath }, 0);
    }

    private void LoadImage(string filePath)
    {
        if (!File.Exists(filePath)) return;

        try
        {
            var (pixW, pixH) = ThumbnailGenerator.GetDimensions(filePath);
            var fi = new FileInfo(filePath);

            // Decode at preview resolution capped to avoid OOM on huge images.
            // 3840px covers 4K displays at 1x zoom with headroom for moderate zoom.
            int decodeWidth = Math.Min(pixW, 3840);
            var data = ThumbnailGenerator.Generate(filePath, decodeWidth);

            Vm.ImageData = data;
            Vm.PixelWidth = pixW;
            Vm.PixelHeight = pixH;
            Vm.FileSizeBytes = fi.Length;
            Vm.ImageWidthDip = pixW;
            Vm.ImageHeightDip = pixH;
            Vm.Title = Path.GetFileName(filePath);
            _imageReady = true;
            Vm.UpdateInfo();
        }
        catch { }
    }

    private void NavigateTo(string filePath)
    {
        _isNavigating = true;
        Vm.UserZoomed = false;
        LoadImage(filePath);
        if (_imageReady) FitToViewport();
        _isNavigating = false;
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape || e.Key == Key.Back)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Left)
        {
            var path = Vm.Navigate(-1);
            if (path != null) NavigateTo(path);
            e.Handled = true;
        }
        else if (e.Key == Key.Right)
        {
            var path = Vm.Navigate(1);
            if (path != null) NavigateTo(path);
            e.Handled = true;
        }
    }

    private void ArrowLeft_Click(object? sender, PointerPressedEventArgs e)
    {
        var path = Vm.Navigate(-1);
        if (path != null) NavigateTo(path);
        e.Handled = true;
    }

    private void ArrowRight_Click(object? sender, PointerPressedEventArgs e)
    {
        var path = Vm.Navigate(1);
        if (path != null) NavigateTo(path);
        e.Handled = true;
    }

    private void Scroller_Loaded(object? sender, RoutedEventArgs e)
    {
        if (_imageReady && !Vm.UserZoomed)
            FitToViewport();
    }

    // ==================== Fit To Viewport ====================

    private void Scroller_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (!_imageReady) return;
        if (!Vm.UserZoomed)
            FitToViewport();
        else
            ApplyZoomLayout();
    }

    private void FitToViewport()
    {
        if (!_imageReady) return;

        double vw = Scroller.Viewport.Width;
        double vh = Scroller.Viewport.Height;
        if (vw <= 0 || vh <= 0) return;

        double sx = vw / Vm.ImageWidthDip;
        double sy = vh / Vm.ImageHeightDip;
        double fit = Math.Min(sx, sy);
        if (fit <= 0) fit = 1.0;
        if (fit > PreviewViewModel.MaxZoom) fit = PreviewViewModel.MaxZoom;

        Vm.FitZoom = fit;
        Vm.ZoomFactor = fit;
        Vm.UserZoomed = false;

        // Apply zoom to image layout size
        ImgFull.Width = Vm.ImageWidthDip * fit;
        ImgFull.Height = Vm.ImageHeightDip * fit;

        ApplyZoomLayout();
        Scroller.UpdateLayout();
        CenterScroll();
        Vm.UpdateInfo();
    }

    /// <summary>
    /// ContentGrid should be at least viewport-sized so the image can center properly.
    /// </summary>
    private void ApplyZoomLayout()
    {
        if (!_imageReady) return;

        double vw = Scroller.Viewport.Width;
        double vh = Scroller.Viewport.Height;
        if (vw <= 0 || vh <= 0) return;

        double sw = Vm.ImageWidthDip * Vm.ZoomFactor;
        double sh = Vm.ImageHeightDip * Vm.ZoomFactor;

        ContentGrid.Width = Math.Max(vw, sw);
        ContentGrid.Height = Math.Max(vh, sh);
    }

    private void CenterScroll()
    {
        double maxX = Math.Max(0, Scroller.Extent.Width - Scroller.Viewport.Width);
        double maxY = Math.Max(0, Scroller.Extent.Height - Scroller.Viewport.Height);
        Scroller.Offset = new Vector(maxX / 2.0, maxY / 2.0);
    }

    // ==================== Mouse Wheel Zoom ====================

    private void OnPreviewPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!_imageReady) return;

        double oldZoom = Vm.ZoomFactor;
        double newZoom = e.Delta.Y > 0
            ? oldZoom * PreviewViewModel.ZoomStep
            : oldZoom / PreviewViewModel.ZoomStep;

        double minAllowed = Math.Min(Vm.FitZoom * 0.05, Vm.FitZoom);
        if (newZoom < minAllowed) newZoom = minAllowed;
        if (newZoom > PreviewViewModel.MaxZoom) newZoom = PreviewViewModel.MaxZoom;

        if (Math.Abs(newZoom - Vm.FitZoom) / Vm.FitZoom < 0.05)
        {
            newZoom = Vm.FitZoom;
            Vm.UserZoomed = false;
        }
        else
        {
            Vm.UserZoomed = true;
        }

        double vw = Scroller.Viewport.Width;
        double vh = Scroller.Viewport.Height;
        if (vw <= 0 || vh <= 0) return;

        // Mouse position in viewport
        Point mouseInViewport = e.GetPosition(Scroller);

        // Calculate ratio of mouse position within total extent (as original WPF)
        double oldExtentW = Math.Max(Vm.ImageWidthDip * oldZoom, vw);
        double oldExtentH = Math.Max(Vm.ImageHeightDip * oldZoom, vh);

        double relX = (Scroller.Offset.X + mouseInViewport.X) / oldExtentW;
        double relY = (Scroller.Offset.Y + mouseInViewport.Y) / oldExtentH;

        // Apply new zoom
        Vm.ZoomFactor = newZoom;
        ImgFull.Width = Vm.ImageWidthDip * newZoom;
        ImgFull.Height = Vm.ImageHeightDip * newZoom;

        ApplyZoomLayout();
        Scroller.UpdateLayout();

        // Calculate new offset to keep same ratio under mouse
        double newExtentW = Math.Max(Vm.ImageWidthDip * newZoom, vw);
        double newExtentH = Math.Max(Vm.ImageHeightDip * newZoom, vh);

        double targetX = relX * newExtentW - mouseInViewport.X;
        double targetY = relY * newExtentH - mouseInViewport.Y;

        double maxOffX = Math.Max(0, Scroller.Extent.Width - vw);
        double maxOffY = Math.Max(0, Scroller.Extent.Height - vh);

        targetX = Math.Clamp(targetX, 0, maxOffX);
        targetY = Math.Clamp(targetY, 0, maxOffY);

        Scroller.Offset = new Vector(targetX, targetY);
        Vm.UpdateInfo();
        e.Handled = true;
    }

    // ==================== Mouse Drag Pan ====================

    private void Scroller_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_imageReady) return;
        var point = e.GetCurrentPoint(Scroller);
        if (!point.Properties.IsLeftButtonPressed) return;

        _isDragging = true;
        _dragStart = e.GetPosition(Scroller);
        _dragStartOffX = Scroller.Offset.X;
        _dragStartOffY = Scroller.Offset.Y;
        e.Pointer.Capture(Scroller);
        e.Handled = true;
    }

    private void Scroller_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _isDragging = false;
        e.Pointer.Capture(null);
    }

    private void Scroller_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDragging) return;

        Point cur = e.GetPosition(Scroller);
        Vector delta = cur - _dragStart;

        double vw = Scroller.Viewport.Width;
        double vh = Scroller.Viewport.Height;
        if (vw <= 0 || vh <= 0) return;

        double nx = _dragStartOffX - delta.X;
        double ny = _dragStartOffY - delta.Y;

        double maxOffX = Math.Max(0, Scroller.Extent.Width - vw);
        double maxOffY = Math.Max(0, Scroller.Extent.Height - vh);

        nx = Math.Clamp(nx, 0, maxOffX);
        ny = Math.Clamp(ny, 0, maxOffY);

        Scroller.Offset = new Vector(nx, ny);
        e.Handled = true;
    }
}
