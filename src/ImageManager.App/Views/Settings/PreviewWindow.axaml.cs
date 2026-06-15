using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ImageManager.App.Services;
using ImageManager.App.ViewModels;
using ImageManager.Common.Helpers;
using ImageManager.Infrastructure.Imaging;
using Avalonia.Threading;
using SkiaSharp;

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
    private readonly ImagePreloader _preloader;

    // Throttle: during rapid key-repeat, skip intermediate loads
    private const int SettleDelayMs = 120;
    private long _lastLoadStartTicks;

    // === 3-slot WriteableBitmap pool (cyclic reuse, fixed memory) ===
    private sealed class BitmapSlot
    {
        public WriteableBitmap? Bitmap;
        public int ImageIndex = -1;
        public int PixelWidth;
        public int PixelHeight;
    }
    private readonly BitmapSlot[] _pool = { new(), new(), new() };
    private int _activeSlotIdx = -1;
    private static readonly Vector Dpi = new(96, 96);

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
        ClearPool();
        base.OnClosed(e);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        Vm.SavedLeft = Position.X;
        Vm.SavedTop = Position.Y;
        StopGif();
        CancelAllLoads();
        ClearPool();
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

    // ==================== Bitmap Pool (3-slot cyclic reuse) ====================

    /// <summary>Find pool slot by image index, or return LRU slot (not active, lowest index).</summary>
    private int FindSlot(int imageIndex)
    {
        for (int i = 0; i < 3; i++)
            if (_pool[i].Bitmap != null && _pool[i].ImageIndex == imageIndex)
                return i;

        // Pick any idle slot (Bitmap == null), or the one farthest from active
        for (int i = 0; i < 3; i++)
            if (_pool[i].Bitmap == null)
                return i;

        // All occupied: replace the slot that's NOT the active one and has oldest index
        int best = 0;
        int bestDist = int.MinValue;
        for (int i = 0; i < 3; i++)
        {
            if (i == _activeSlotIdx) continue;
            int dist = Math.Abs(_pool[i].ImageIndex - imageIndex);
            if (dist > bestDist) { bestDist = dist; best = i; }
        }
        return best;
    }

    /// <summary>
    /// Prepare a WriteableBitmap in a pool slot with the given raw BGRA pixels.
    /// Reuses existing WriteableBitmap if dimensions match, otherwise creates new.
    /// Returns the prepared WriteableBitmap.
    /// </summary>
    private WriteableBitmap PrepareSlot(int slot, int imageIndex, byte[] pixels, int w, int h)
    {
        ref var s = ref _pool[slot];

        // If dimensions differ, dispose old and create new
        if (s.Bitmap != null && (s.PixelWidth != w || s.PixelHeight != h))
        {
            s.Bitmap.Dispose();
            s.Bitmap = null;
        }

        if (s.Bitmap == null)
        {
            s.Bitmap = new WriteableBitmap(new PixelSize(w, h), Dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
            s.PixelWidth = w;
            s.PixelHeight = h;
        }

        // Copy raw BGRA pixels directly into WriteableBitmap's framebuffer
        using (var fb = s.Bitmap.Lock())
        {
            if (fb.RowBytes == w * 4)
                Marshal.Copy(pixels, 0, fb.Address, pixels.Length);
            else
                for (int y = 0; y < h; y++)
                    Marshal.Copy(pixels, y * w * 4, IntPtr.Add(fb.Address, y * fb.RowBytes), Math.Min(fb.RowBytes, w * 4));
        }

        s.ImageIndex = imageIndex;
        return s.Bitmap;
    }

    /// <summary>
    /// Prepare a WriteableBitmap from an SKBitmap (decoded from JPEG by SkiaSharp).
    /// Handles RGBA→BGRA swap if needed. Row-by-row copy with stride safety.
    /// </summary>
    private WriteableBitmap PrepareSlot(int slot, int imageIndex, SKBitmap skBitmap)
    {
        ref var s = ref _pool[slot];
        int w = skBitmap.Width, h = skBitmap.Height;
        bool swapRB = skBitmap.ColorType == SKColorType.Rgba8888;

        if (s.Bitmap != null && (s.PixelWidth != w || s.PixelHeight != h))
        {
            s.Bitmap.Dispose();
            s.Bitmap = null;
        }

        if (s.Bitmap == null)
        {
            s.Bitmap = new WriteableBitmap(new PixelSize(w, h), Dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
            s.PixelWidth = w;
            s.PixelHeight = h;
        }

        using (var fb = s.Bitmap.Lock())
        {
            IntPtr src = skBitmap.GetPixels();
            int srcRowBytes = skBitmap.RowBytes;
            int destRowBytes = fb.RowBytes;
            int copyBytes = Math.Min(destRowBytes, w * 4);
            byte[] rowBuf = new byte[w * 4];

            for (int y = 0; y < h; y++)
            {
                IntPtr srcRow = IntPtr.Add(src, y * srcRowBytes);
                IntPtr destRow = IntPtr.Add(fb.Address, y * destRowBytes);

                Marshal.Copy(srcRow, rowBuf, 0, copyBytes);
                if (swapRB)
                {
                    for (int x = 0; x < w; x++)
                    {
                        byte tmp = rowBuf[x * 4];
                        rowBuf[x * 4] = rowBuf[x * 4 + 2];
                        rowBuf[x * 4 + 2] = tmp;
                    }
                }
                Marshal.Copy(rowBuf, 0, destRow, copyBytes);
            }
        }

        s.ImageIndex = imageIndex;
        return s.Bitmap;
    }

    /// <summary>Dispose all pooled WriteableBitmaps.</summary>
    private void ClearPool()
    {
        for (int i = 0; i < 3; i++)
        {
            _pool[i].Bitmap?.Dispose();
            _pool[i].Bitmap = null;
            _pool[i].ImageIndex = -1;
        }
        _activeSlotIdx = -1;
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

        Vm.IsLoading = true;
        Vm.LoadingText = "解码中...";
        _imageReady = false;

        try
        {
            // Get JPEG bytes from preloader (cache-hit: ~0ms, miss: Generate on thread pool)
            var index = Vm.CurrentIndex;
            var (jpegData, pixW, pixH) = await _preloader.NavigateToAsync(index, token);

            if (version != _loadVersion || token.IsCancellationRequested) return;

            var fi = new FileInfo(filePath);

            if (jpegData != null)
            {
                // Decode JPEG → SKBitmap → pool WriteableBitmap (on thread pool to avoid UI block)
                var wb = await Task.Run(() =>
                {
                    using var skBitmap = SKBitmap.Decode(jpegData);
                    if (skBitmap == null) return null;

                    int slot = FindSlot(index);
                    return PrepareSlot(slot, index, skBitmap);
                }, token);

                if (wb == null || version != _loadVersion || token.IsCancellationRequested) return;

                // Pointer swap
                ImgFull.Source = wb;
                _activeSlotIdx = FindSlot(index);

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
        catch (OperationCanceledException) { }
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
            ImgFull.Source = null;
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

        ImgFull.Source = null;
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
        ImgFull.Source = null;
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
