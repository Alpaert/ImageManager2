using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ImageManager.App.Services;
using ImageManager.App.ViewModels;
using ImageManager.Common.Helpers;
using ImageManager.Infrastructure.Imaging;
using Avalonia.Threading;

namespace ImageManager.App.Views.Settings;

public partial class PreviewWindow : Window
{
    private PreviewViewModel Vm => (PreviewViewModel)DataContext!;

    private bool _imageReady;
    private bool _isDragging;
    private Point _dragStart;
    private double _dragStartOffX, _dragStartOffY;
    private int _loadVersion;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _settleCts;
    private int _settleVersion;
    private readonly ImagePreloader _preloader;

    // Throttle: during rapid key-repeat, skip intermediate loads
    private const int SettleDelayMs = 120;
    private long _lastLoadStartTicks;

    public PreviewWindow()
    {
        InitializeComponent();
        Scroller.AddHandler(ScrollViewer.PointerWheelChangedEvent, OnPreviewPointerWheel, RoutingStrategies.Tunnel);
        KeyDown += OnPreviewKeyDown;

        _preloader = App.Services.GetService(typeof(ImagePreloader)) as ImagePreloader
            ?? new ImagePreloader();
    }

    protected override void OnClosed(EventArgs e)
    {
        // Save position BEFORE native window destruction (Closed fires too late)
        Vm.SavedLeft = Position.X;
        Vm.SavedTop = Position.Y;
        StopGif();
        CancelAllLoads();
        Vm.ReleaseImage();
        base.OnClosed(e);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        Vm.SavedLeft = Position.X;
        Vm.SavedTop = Position.Y;
        StopGif();
        CancelAllLoads();
        Vm.ReleaseImage();
        base.OnClosing(e);
    }

    /// <summary>Create with image list for navigation.</summary>
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

        // Initialize preloader with the full file list
        win._preloader.SetFileList(imagePaths, startIndex);

        if (imagePaths.Count == 0) return win;
        if (startIndex >= imagePaths.Count) startIndex = imagePaths.Count - 1;

        // Load the initial image (async, fire-and-forget since window is about to show)
        _ = win.LoadImageAsync(imagePaths[startIndex]);
        return win;
    }

    /// <summary>Compatibility overload for single-file preview (no navigation)</summary>
    public static PreviewWindow Create(string filePath)
    {
        return Create(new List<string> { filePath }, 0);
    }

    // ==================== Image Loading (Async) ====================

    private async Task LoadImageAsync(string filePath)
    {
        if (!File.Exists(filePath)) return;
        StopGif();

        if (Path.GetExtension(filePath).ToLowerInvariant() == ".gif")
        {
            await LoadGifAsync(filePath);
            return;
        }

        Vm.IsGif = false;
        Vm.GifCurrentFrame = null;
        ImgFull.IsVisible = true;

        int version = Interlocked.Increment(ref _loadVersion);
        var cts = RenewLoadCts();
        var token = cts.Token;

        _lastLoadStartTicks = Stopwatch.GetTimestamp();

        // Show loading state immediately
        Vm.IsLoading = true;
        Vm.LoadingText = "解码中...";
        _imageReady = false;
        Vm.ImageData = null;

        try
        {
            // Use preloader (which checks cache first, then decodes)
            var index = Vm.CurrentIndex;
            var (data, pixW, pixH) = await _preloader.NavigateToAsync(index, token);

            if (version != _loadVersion || token.IsCancellationRequested) return;

            var fi = new FileInfo(filePath);

            if (data != null)
            {
                Vm.ImageData = data;
                Vm.PixelWidth = pixW;
                Vm.PixelHeight = pixH;
                Vm.FileSizeBytes = fi.Length;
                Vm.ImageWidthDip = pixW;
                Vm.ImageHeightDip = pixH;
                Vm.Title = Path.GetFileName(filePath);
                Vm.IsLoading = false;
                _imageReady = true;
                Vm.UpdateInfo();
                FitToViewport();
            }
            else
            {
                Vm.Title = Path.GetFileName(filePath);
                Vm.InfoText = "预览失败：图片过大或格式不支持";
                Vm.PixelWidth = pixW;
                Vm.PixelHeight = pixH;
                Vm.FileSizeBytes = fi.Length;
                Vm.IsLoading = false;
            }
        }
        catch (OperationCanceledException)
        {
            // Load was cancelled — another navigation took over, nothing to do
        }
        catch (Exception ex)
        {
            if (version != _loadVersion || token.IsCancellationRequested) return;
            AppLogger.Warn($"Preview load failed for {filePath}: {ex.Message}");
            Vm.Title = Path.GetFileName(filePath);
            Vm.InfoText = "预览失败：图片过大或格式不支持";
            Vm.IsLoading = false;
        }
    }

    private async Task LoadGifAsync(string filePath)
    {
        int version = Interlocked.Increment(ref _loadVersion);
        var cts = RenewLoadCts();
        var token = cts.Token;

        Vm.IsLoading = true;
        Vm.LoadingText = "加载GIF...";

        try
        {
            var (pixW, pixH) = await Task.Run(() => ThumbnailGenerator.GetDimensions(filePath), token);
            var fi = new FileInfo(filePath);

            Vm.IsGif = true;
            Vm.PixelWidth = pixW;
            Vm.PixelHeight = pixH;
            Vm.FileSizeBytes = fi.Length;
            Vm.ImageWidthDip = pixW;
            Vm.ImageHeightDip = pixH;
            Vm.Title = Path.GetFileName(filePath);
            Vm.ImageData = null;
            Vm.GifFrames = null;
            ImgFull.IsVisible = false;
            _imageReady = false;

            // First frame: decode on background thread too
            var firstFrame = await Task.Run(() => ThumbnailGenerator.DecodeFirstGifFrame(filePath, 800), token);
            if (token.IsCancellationRequested || version != _loadVersion) return;

            if (firstFrame != null)
            {
                Vm.GifCurrentFrame = firstFrame.JpegData;
                _imageReady = true;
                Vm.IsLoading = false;
                Vm.UpdateInfo();
                FitToViewport();
            }
            else
            {
                Vm.GifCurrentFrame = null;
                Vm.InfoText = $"分辨率：{pixW} x {pixH}    文件大小：{FileSizeFormatter.Format(fi.Length)}    加载中...";
            }

            // Remaining frames: decode on background
            var path = filePath;
            var frames = await Task.Run(() => ThumbnailGenerator.DecodeGifFrames(path, 800), token);
            if (token.IsCancellationRequested || version != _loadVersion) return;

            if (frames != null && frames.Count > 1)
            {
                Vm.GifFrames = frames;
                if (!_imageReady)
                {
                    Vm.GifCurrentFrame = frames[0].JpegData;
                    _imageReady = true;
                    Vm.IsLoading = false;
                    Vm.UpdateInfo();
                    FitToViewport();
                }
                Vm.GifFrameIndex = 0;
                StartGifTimer(1);
            }
            else if (!_imageReady)
            {
                Vm.IsGif = false;
                Vm.GifCurrentFrame = null;
                ImgFull.IsVisible = true;
                Vm.IsLoading = false;
                // Fall through: treat as static image
                if (File.Exists(filePath))
                {
                    Vm.IsGif = false;
                    await LoadImageAsync(filePath);
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (version != _loadVersion) return;
            AppLogger.Warn($"GIF load failed for {filePath}: {ex.Message}");
            Vm.Title = Path.GetFileName(filePath);
            Vm.InfoText = "预览失败：GIF加载出错";
            Vm.IsLoading = false;
        }
    }

    // ==================== Navigation (Async with Throttle) ====================

    private void NavigateTo(int newIndex)
    {
        if (newIndex < 0 || newIndex >= Vm.ImagePaths.Count) return;

        Vm.UserZoomed = false;
        StopGif();
        CancelSettleTimer();
        CancelLoad();

        Vm.ImageData = null;
        Vm.IsLoading = true;
        Vm.LoadingText = "加载中...";
        _imageReady = false;

        var path = Vm.ImagePaths[newIndex];
        Vm.Title = Path.GetFileName(path);
        Vm.UpdateInfo();

        _ = LoadImageAsync(path);
    }

    /// <summary>
    /// Start a settle timer: load the image at capturedIndex after SettleDelayMs
    /// of inactivity. Used during rapid key-repeat to skip intermediate images.
    /// </summary>
    private void StartSettleTimer(int capturedIndex)
    {
        CancelSettleTimer();
        var settleCts = new CancellationTokenSource();
        _settleCts = settleCts;
        var token = settleCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SettleDelayMs, token);
                if (token.IsCancellationRequested) return;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested) return;
                    _ = LoadImageAsync(Vm.ImagePaths[capturedIndex]);
                });
            }
            catch (OperationCanceledException) { }
        }, token);
    }

    // ==================== Keyboard & Click Handlers ====================

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape || e.Key == Key.Back)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Left)
        {
            HandleNavigate(-1);
            e.Handled = true;
        }
        else if (e.Key == Key.Right)
        {
            HandleNavigate(1);
            e.Handled = true;
        }
    }

    private void ArrowLeft_Click(object? sender, PointerPressedEventArgs e)
    {
        HandleNavigate(-1);
        e.Handled = true;
    }

    private void ArrowRight_Click(object? sender, PointerPressedEventArgs e)
    {
        HandleNavigate(1);
        e.Handled = true;
    }

    /// <summary>
    /// Smart navigation: on first press, load immediately (responsiveness).
    /// On rapid repeat (within SettleDelayMs), throttle to skip intermediate images.
    /// </summary>
    private void HandleNavigate(int delta)
    {
        var newIndex = Vm.NavigateIndex(delta);
        if (newIndex < 0) return;

        CancelLoad();
        Vm.UserZoomed = false;
        StopGif();
        Vm.ImageData = null;
        Vm.IsLoading = true;
        Vm.LoadingText = "加载中...";
        _imageReady = false;
        Vm.Title = Path.GetFileName(Vm.ImagePaths[newIndex]);
        Vm.UpdateInfo();

        // Time-based detection: if a load was started recently, we're in rapid repeat
        long now = Stopwatch.GetTimestamp();
        double msSinceLastLoad = (now - _lastLoadStartTicks) * 1000.0 / Stopwatch.Frequency;

        if (msSinceLastLoad < SettleDelayMs && _lastLoadStartTicks > 0)
        {
            // Rapid repeat: skip loading, just update the settle timer
            StartSettleTimer(newIndex);
        }
        else
        {
            // First press or long pause: load immediately
            _lastLoadStartTicks = now;
            _ = LoadImageAsync(Vm.ImagePaths[newIndex]);
        }
    }

    // ==================== Cancellation Helpers ====================

    private CancellationTokenSource RenewLoadCts()
    {
        CancelLoad();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        return cts;
    }

    private void CancelLoad()
    {
        var cts = Interlocked.Exchange(ref _loadCts, null!);
        if (cts != null)
        {
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }
    }

    private void CancelSettleTimer()
    {
        var cts = Interlocked.Exchange(ref _settleCts, null!);
        if (cts != null)
        {
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }
    }

    private void CancelAllLoads()
    {
        CancelLoad();
        CancelSettleTimer();
        _preloader.CancelAllPending();
    }

    // ==================== Fit To Viewport ====================

    private void Scroller_Loaded(object? sender, RoutedEventArgs e)
    {
        if (_imageReady && !Vm.UserZoomed)
            FitToViewport();
    }

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

        ImgFull.Width = Vm.ImageWidthDip * fit;
        ImgFull.Height = Vm.ImageHeightDip * fit;
        ImgGif.Width = Vm.ImageWidthDip * fit;
        ImgGif.Height = Vm.ImageHeightDip * fit;

        ApplyZoomLayout();
        Scroller.UpdateLayout();
        CenterScroll();
        Vm.UpdateInfo();
    }

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

        Point mouseInViewport = e.GetPosition(Scroller);

        double oldExtentW = Math.Max(Vm.ImageWidthDip * oldZoom, vw);
        double oldExtentH = Math.Max(Vm.ImageHeightDip * oldZoom, vh);

        double relX = (Scroller.Offset.X + mouseInViewport.X) / oldExtentW;
        double relY = (Scroller.Offset.Y + mouseInViewport.Y) / oldExtentH;

        Vm.ZoomFactor = newZoom;
        ImgFull.Width = Vm.ImageWidthDip * newZoom;
        ImgFull.Height = Vm.ImageHeightDip * newZoom;
        ImgGif.Width = Vm.ImageWidthDip * newZoom;
        ImgGif.Height = Vm.ImageHeightDip * newZoom;

        ApplyZoomLayout();
        Scroller.UpdateLayout();

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

    // ==================== GIF Animation ====================

    private void StartGifTimer(int fromIndex)
    {
        Vm.GifTimer?.Stop();
        Vm.GifTimer = null;
        var frames = Vm.GifFrames;
        if (frames == null || frames.Count <= 1) return;

        Vm.GifFrameIndex = fromIndex % frames.Count;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(30, frames[Vm.GifFrameIndex].DurationMs)) };
        timer.Tick += GifTimer_Tick;
        timer.Start();
        Vm.GifTimer = timer;
    }

    private void GifTimer_Tick(object? sender, EventArgs e)
    {
        var frames = Vm.GifFrames;
        if (frames == null || frames.Count <= 1)
        {
            StopGif();
            return;
        }

        Vm.GifCurrentFrame = frames[Vm.GifFrameIndex].JpegData;
        Vm.GifFrameIndex = (Vm.GifFrameIndex + 1) % frames.Count;

        var timer = (DispatcherTimer)sender!;
        timer.Interval = TimeSpan.FromMilliseconds(Math.Max(30, frames[Vm.GifFrameIndex].DurationMs));
    }

    private void StopGif()
    {
        Vm.GifTimer?.Stop();
        Vm.GifTimer = null;
        Vm.GifFrames = null;
        Vm.IsGif = false;
        Vm.GifCurrentFrame = null;
        Interlocked.Increment(ref _loadVersion);
    }
}
