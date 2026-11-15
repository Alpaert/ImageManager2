using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ImageManager.Common.Constants;
using ImageManager.Common.Helpers;
using ImageManager.Core.Models;
using SkiaSharp;

namespace ImageManager.App.Controls;

public enum RegionSearchInputBehavior
{
    AwaitSelection,
    SearchWholeImage
}

public sealed class RegionSearchQueryEventArgs(string path, ImageRegion? region, RegionSearchMode mode) : EventArgs
{
    public string Path { get; } = path;
    public ImageRegion? Region { get; } = region;
    public RegionSearchMode Mode { get; } = mode;
}

public partial class RegionSearchPane : UserControl, IDisposable
{
    private const int PreviewMaxSide = 1200;
    private const long MaximumRegionPixels = 32_000_000;
    private const long MaximumPreviewDecodePixels = 64_000_000;
    private readonly SemaphoreSlim _decodeGate = new(1);
    private readonly List<string> _temporaryFiles = [];
    private readonly Dictionary<StandardCursorType, Cursor> _cursors = [];
    private CancellationTokenSource? _inputLoad;
    private Bitmap? _bitmap;
    private PixelSize _sourceSize;
    private Gesture? _gesture;
    private Rect? _draft;
    private Vector _pan;
    private double _zoom = 1;
    private bool _panTool;
    private bool _spaceHeld;
    private bool _choosing;
    private bool _disposed;
    private int _inputRevision;
    private RegionSearchMode _mode = RegionSearchMode.SimilarContent;

    public event EventHandler<RegionSearchQueryEventArgs>? QueryChanged;
    public event EventHandler? InputChanging;
    public event EventHandler? InputStateChanged;
    public event EventHandler? PickerOpening;

    public string? InputPath { get; private set; }
    public ImageRegion? SelectedRegion { get; private set; }
    public RegionSearchInputBehavior InputBehavior { get; set; } = RegionSearchInputBehavior.AwaitSelection;
    public bool IsAwaitingSelection { get; private set; }
    public RegionSearchMode Mode
    {
        get => _mode;
        set
        {
            if (_disposed || _mode == value)
                return;

            CancelGesture();
            _mode = value;
            RefreshControls();
            InputStateChanged?.Invoke(this, EventArgs.Empty);
            PublishQuery();
        }
    }

    private enum GestureKind { Draw, Move, Resize, Pan }

    private sealed record Gesture(IPointer Pointer, MouseButton Button, GestureKind Kind,
        Point Start, Vector Pan, Rect? Selection, RegionResizeEdge Edge)
    {
        public bool InterruptedSearch { get; set; }
    }

    private Rect ImageBounds => RegionSelectionGeometry.ImageBounds(Stage.Bounds.Size, _sourceSize, _zoom, _pan);
    private Rect? CommittedSelection => SelectedRegion is { } region
        ? RegionSelectionGeometry.FromPixels(region, _sourceSize)
        : null;
    private bool CanSelectRegion => InputPath is not null &&
        (long)_sourceSize.Width * _sourceSize.Height <= MaximumRegionPixels;
    private bool IsLoading => _inputLoad is not null;

    public RegionSearchPane()
    {
        InitializeComponent();
        Stage.SizeChanged += (_, _) =>
        {
            CancelGesture(restartSearch: true);
            _pan = RegionSelectionGeometry.ClampPan(Stage.Bounds.Size, ImageBounds, _pan);
            RefreshCanvas();
        };
        RefreshControls();
        UpdateFeedback();
    }

    public async Task ChooseImageAsync()
    {
        if (_disposed || _choosing || TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } provider)
            return;

        _choosing = true;
        CancelInteraction();
        var revision = ++_inputRevision;
        try
        {
            // Release pointer gestures and owner transient UI before entering the
            // native modal loop. Canceling the picker keeps the committed input.
            PickerOpening?.Invoke(this, EventArgs.Empty);
            await Dispatcher.Yield(DispatcherPriority.Background);
            if (_disposed || revision != _inputRevision) return;
            var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择输入图片",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("图片")
                {
                    Patterns = FileTypeConstants.ImageExtensions.Select(extension => "*" + extension).ToArray()
                }]
            });

            AppLogger.Info($"[RegionSearch.PickerReturned] selectedCount={files.Count}");
            // Let native ownership/focus restoration finish before publishing
            // replacement results and recycling their visual controls.
            await Dispatcher.Yield(DispatcherPriority.Background);
            if (!_disposed && revision == _inputRevision && files.FirstOrDefault()?.TryGetLocalPath() is { } path)
                await LoadImageAsync(path);
        }
        catch (Exception ex)
        {
            if (!_disposed && revision == _inputRevision)
                UpdateFeedback($"无法选择图片：{ex.Message}");
        }
        finally
        {
            _choosing = false;
        }
    }

    public Task PasteImageAsync()
    {
        if (_disposed || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return Task.CompletedTask;

        return ReplaceInputAsync(async token =>
        {
            var files = await clipboard.TryGetFilesAsync();
            token.ThrowIfCancellationRequested();
            if (FirstImagePath(files) is { } filePath)
                return filePath;

            using var clipboardBitmap = await clipboard.TryGetBitmapAsync();
            token.ThrowIfCancellationRequested();
            if (clipboardBitmap is null)
                throw new InvalidDataException("剪贴板中没有图片文件或位图。");

            return await SaveTemporaryImageAsync(clipboardBitmap, token);
        });
    }

    public Task LoadImageAsync(string path)
    {
        if (_disposed)
            return Task.CompletedTask;

        return ReplaceInputAsync(_ => Task.FromResult(path));
    }

    private async Task ReplaceInputAsync(Func<CancellationToken, Task<string>> readPath)
    {
        var inputBehavior = InputBehavior;
        ++_inputRevision;
        CancelGesture();
        _inputLoad?.Cancel();
        using var load = new CancellationTokenSource();
        _inputLoad = load;
        RefreshControls();
        UpdateFeedback("正在读取输入图片…");
        InputChanging?.Invoke(this, EventArgs.Empty);

        string? error = null;
        var loaded = false;
        try
        {
            var path = await readPath(load.Token);
            load.Token.ThrowIfCancellationRequested();
            if (!FileTypeConstants.IsImageFile(path))
                throw new InvalidDataException("请选择支持的图片格式。");

            await _decodeGate.WaitAsync(load.Token);
            (Bitmap Bitmap, PixelSize SourceSize) preview;
            try
            {
                preview = await Task.Run(() => DecodePreview(path, load.Token), load.Token);
            }
            finally
            {
                _decodeGate.Release();
            }

            if (!ReferenceEquals(_inputLoad, load) || load.IsCancellationRequested)
            {
                preview.Bitmap.Dispose();
                return;
            }

            var previous = _bitmap;
            _bitmap = preview.Bitmap;
            _sourceSize = preview.SourceSize;
            InputPath = path;
            SelectedRegion = null;
            IsAwaitingSelection = inputBehavior == RegionSearchInputBehavior.AwaitSelection;
            _panTool = false;
            ResetView();
            previous?.Dispose();
            loaded = true;
        }
        catch (OperationCanceledException) when (load.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            error = $"无法读取图片：{ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_inputLoad, load))
            {
                _inputLoad = null;
                RefreshControls();
                UpdateFeedback(error);
            }
        }

        if (!loaded)
            return;

        if (IsAwaitingSelection)
            Stage.Focus();
        InputStateChanged?.Invoke(this, EventArgs.Empty);
        PublishQuery();
    }

    private async Task<string> SaveTemporaryImageAsync(Bitmap bitmap, CancellationToken token)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ImageManager-region-{Guid.NewGuid():N}.png");
        try
        {
            await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                bitmap.Save(stream);
            }, token);

            token.ThrowIfCancellationRequested();
            _temporaryFiles.Add(path);
            return path;
        }
        catch
        {
            await Task.Run(() => TryDeleteTemporaryFile(path));
            throw;
        }
    }

    private static (Bitmap Bitmap, PixelSize SourceSize) DecodePreview(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var stream = File.OpenRead(path);
        using var codec = SKCodec.Create(stream) ?? throw new InvalidDataException("图片格式不受支持。");
        var source = codec.Info;
        if (source.Width <= 0 || source.Height <= 0)
            throw new InvalidDataException("图片没有有效尺寸。");

        var scale = Math.Min(1f, (float)PreviewMaxSide / Math.Max(source.Width, source.Height));
        var sample = codec.GetScaledDimensions(scale);
        if (sample.Width <= 0 || sample.Height <= 0 || (long)sample.Width * sample.Height > MaximumPreviewDecodePixels)
            throw new InvalidDataException("图片过大，无法生成安全的预览。");

        var sampleInfo = new SKImageInfo(sample.Width, sample.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var decoded = new SKBitmap(sampleInfo);
        token.ThrowIfCancellationRequested();
        if (codec.GetPixels(sampleInfo, decoded.GetPixels()) != SKCodecResult.Success)
            throw new InvalidDataException("无法解码图片的第一帧。");

        token.ThrowIfCancellationRequested();
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        using var preview = decoded.Resize(new SKSizeI(width, height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None))
            ?? throw new InvalidDataException("无法生成图片预览。");
        token.ThrowIfCancellationRequested();
        var bitmap = new Bitmap(PixelFormat.Rgba8888, AlphaFormat.Premul, preview.GetPixels(),
            new PixelSize(width, height), new Vector(96, 96), preview.RowBytes);
        return (bitmap, new PixelSize(source.Width, source.Height));
    }

    public void CancelInteraction()
    {
        if (_disposed)
            return;

        ++_inputRevision;
        var load = _inputLoad;
        _inputLoad = null;
        load?.Cancel();
        _spaceHeld = false;
        CancelGesture();
        StageBorder.Classes.Remove("region-drop");
        RefreshControls();
        if (load is not null)
            UpdateFeedback();
    }

    public void ClearInput()
    {
        if (_disposed)
            return;

        CancelInteraction();
        InputPath = null;
        SelectedRegion = null;
        IsAwaitingSelection = false;
        _sourceSize = default;
        _panTool = false;
        var previous = _bitmap;
        _bitmap = null;
        ResetView();
        previous?.Dispose();
        RefreshControls();
        UpdateFeedback();
        InputStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        ClearInput();
        _disposed = true;
        Stage.Cursor = null;
        foreach (var cursor in _cursors.Values)
            cursor.Dispose();

        var temporaryFiles = _temporaryFiles.ToArray();
        _temporaryFiles.Clear();
        _ = Task.Run(() =>
        {
            foreach (var path in temporaryFiles)
                TryDeleteTemporaryFile(path);
        });
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void PublishQuery(bool explicitSubmit = false)
    {
        if (_disposed || IsLoading || _gesture is not null || InputPath is not { } path)
            return;

        if (IsAwaitingSelection)
        {
            if (!explicitSubmit)
                return;

            IsAwaitingSelection = false;
            RefreshControls();
        }

        if (explicitSubmit && SelectedRegion is null)
            InputStateChanged?.Invoke(this, EventArgs.Empty);
        QueryChanged?.Invoke(this, new RegionSearchQueryEventArgs(path, SelectedRegion, Mode));
    }

    private void RefreshControls()
    {
        var hasImage = InputPath is not null;
        var editable = hasImage && !IsLoading;
        ChangeButton.IsVisible = hasImage || IsLoading;
        EmptyInput.IsVisible = !hasImage && !IsLoading;
        OriginalButton.Classes.Set("workspace-preview-active", Mode == RegionSearchMode.FindOriginal);
        SimilarButton.Classes.Set("workspace-preview-active", Mode == RegionSearchMode.SimilarContent);
        CropButton.Classes.Set("workspace-preview-active", !_panTool);
        PanButton.Classes.Set("workspace-preview-active", _panTool);
        CropButton.IsEnabled = editable && CanSelectRegion;
        PanButton.IsEnabled = FitButton.IsEnabled = editable;
        ClearButton.IsEnabled = editable && SelectedRegion is not null && _gesture is null;
        SearchButton.IsEnabled = editable && _gesture is null;
        SearchButton.Content = SelectedRegion is null ? "搜索整图" : "重新搜索";
        ZoomLabel.IsVisible = hasImage;
        SelectionLabel.Text = SelectedRegion is { } region
            ? $"选区 {region.Width} × {region.Height} 像素"
            : hasImage ? $"{(IsAwaitingSelection ? "待框选" : "整张图片")} {_sourceSize.Width} × {_sourceSize.Height}" : "";
        RefreshCanvas();
    }

    private void UpdateFeedback(string? error = null)
    {
        Feedback.Text = error ?? (InputPath is null
            ? ""
            : CanSelectRegion ? "拖动框选，松开后自动搜索"
                : "图片超过 3,200 万像素，可使用整张图片搜索。");
        Feedback.IsVisible = !string.IsNullOrEmpty(Feedback.Text);
    }

    private void RefreshCanvas()
    {
        Stage.SetScene(_bitmap, ImageBounds, _gesture?.Kind == GestureKind.Draw ? _draft : _draft ?? CommittedSelection);
        ZoomLabel.Text = $"{Math.Round(_zoom * 100)}%";
    }

    private void ResetView()
    {
        _pan = default;
        _zoom = 1;
        RefreshCanvas();
    }

    private void Stage_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var properties = e.GetCurrentPoint(Stage).Properties;
        if (!properties.IsLeftButtonPressed && !properties.IsMiddleButtonPressed)
            return;

        Stage.Focus();
        var bounds = ImageBounds;
        if (_gesture is not null || IsLoading || bounds.Width <= 0 || bounds.Height <= 0)
            return;

        var point = e.GetPosition(Stage);
        var middle = properties.IsMiddleButtonPressed;
        var pan = middle || _spaceHeld || _panTool;
        var selection = CommittedSelection;
        var viewportSelection = selection is { } selected ? RegionSelectionGeometry.ToViewport(selected, bounds) : (Rect?)null;
        var edge = viewportSelection is { } viewport ? RegionSelectionGeometry.FindHandle(viewport, point) : RegionResizeEdge.None;
        if (!pan && (!CanSelectRegion || (edge == RegionResizeEdge.None && !bounds.Contains(point))))
            return;

        var kind = pan ? GestureKind.Pan
            : edge != RegionResizeEdge.None ? GestureKind.Resize
            : viewportSelection?.Contains(point) == true ? GestureKind.Move
            : GestureKind.Draw;
        _gesture = new Gesture(e.Pointer, middle ? MouseButton.Middle : MouseButton.Left, kind,
            pan ? point : RegionSelectionGeometry.ToImage(point, bounds), _pan, selection, edge);
        _draft = selection;
        e.Pointer.Capture(Stage);
        e.Handled = true;
        RefreshControls();
        UpdateCursor(point);
    }

    private void Stage_PointerMoved(object? sender, PointerEventArgs e)
    {
        var point = e.GetPosition(Stage);
        if (_gesture is { } gesture && gesture.Pointer == e.Pointer)
        {
            UpdateGesture(gesture, point);
            e.Handled = true;
        }
        UpdateCursor(point);
    }

    private void UpdateGesture(Gesture gesture, Point point)
    {
        var bounds = ImageBounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        if (gesture.Kind == GestureKind.Pan)
        {
            _pan = RegionSelectionGeometry.ClampPan(Stage.Bounds.Size, bounds, gesture.Pan + (point - gesture.Start));
        }
        else
        {
            var current = RegionSelectionGeometry.ToImage(point, bounds);
            var delta = current - gesture.Start;
            _draft = gesture.Kind switch
            {
                GestureKind.Draw => RegionSelectionGeometry.Draw(gesture.Start, current),
                GestureKind.Move => RegionSelectionGeometry.Move(gesture.Selection!.Value, delta),
                _ => RegionSelectionGeometry.Resize(gesture.Selection!.Value, delta, gesture.Edge, bounds.Size)
            };
            if (!IsAwaitingSelection && !gesture.InterruptedSearch && GetDraftRegion(gesture.Kind, bounds) != SelectedRegion)
            {
                gesture.InterruptedSearch = true;
                InputChanging?.Invoke(this, EventArgs.Empty);
            }
        }

        RefreshCanvas();
    }

    private void Stage_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_gesture is not { } gesture || gesture.Pointer != e.Pointer || e.InitialPressMouseButton != gesture.Button)
            return;

        var point = e.GetPosition(Stage);
        UpdateGesture(gesture, point);
        var previous = SelectedRegion;
        SelectedRegion = GetDraftRegion(gesture.Kind, ImageBounds);
        if (SelectedRegion is not null)
            IsAwaitingSelection = false;

        EndGesture();
        RefreshControls();
        UpdateCursor(point);
        e.Handled = true;
        if (previous != SelectedRegion)
        {
            UpdateFeedback();
            InputStateChanged?.Invoke(this, EventArgs.Empty);
        }
        if (gesture.InterruptedSearch || previous != SelectedRegion)
            PublishQuery();
    }

    private ImageRegion? GetDraftRegion(GestureKind kind, Rect bounds)
    {
        if (kind == GestureKind.Pan || _draft is not { Width: > 0, Height: > 0 } draft ||
            (kind == GestureKind.Draw && (draft.Width * bounds.Width < 6 || draft.Height * bounds.Height < 6)))
            return SelectedRegion;

        return kind == GestureKind.Move && SelectedRegion is { } original
            ? new ImageRegion(Math.Clamp((int)Math.Round(draft.X * _sourceSize.Width), 0, _sourceSize.Width - original.Width),
                Math.Clamp((int)Math.Round(draft.Y * _sourceSize.Height), 0, _sourceSize.Height - original.Height),
                original.Width, original.Height)
            : RegionSelectionGeometry.ToPixels(draft, _sourceSize);
    }

    private void Stage_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_gesture?.Pointer == e.Pointer)
            CancelGesture(restartSearch: true);
    }

    private void CancelGesture(bool restartSearch = false)
    {
        if (_gesture is not { } gesture)
            return;

        _pan = gesture.Pan;
        EndGesture();
        RefreshControls();
        if (restartSearch && gesture.InterruptedSearch)
            PublishQuery();
    }

    private void EndGesture()
    {
        var pointer = _gesture?.Pointer;
        _gesture = null;
        _draft = null;
        if (pointer?.Captured == Stage)
            pointer.Capture(null);
    }

    private void Stage_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var bounds = ImageBounds;
        if (IsLoading || _gesture is not null || bounds.Width <= 0 || bounds.Height <= 0 || e.Delta.Y == 0)
            return;

        var point = e.GetPosition(Stage);
        var anchor = RegionSelectionGeometry.ToImage(point, bounds);
        _zoom = Math.Clamp(_zoom * Math.Pow(1.18, e.Delta.Y), 1, 8);
        if (_zoom == 1)
        {
            ResetView();
        }
        else
        {
            var after = ImageBounds;
            var projected = new Point(after.X + anchor.X * after.Width, after.Y + anchor.Y * after.Height);
            _pan = RegionSelectionGeometry.ClampPan(Stage.Bounds.Size, after, _pan + (point - projected));
            RefreshCanvas();
        }

        UpdateCursor(point);
        e.Handled = true;
    }

    private async void Stage_KeyDown(object? sender, KeyEventArgs e)
    {
        if (!Stage.IsFocused)
            return;

        if (e.Key == Key.V && e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = true;
            await PasteImageAsync();
        }
        else if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            PublishQuery(explicitSubmit: true);
        }
        else if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.None)
        {
            _spaceHeld = true;
            UpdateCursor();
            e.Handled = true;
        }
    }

    private void Stage_KeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && _spaceHeld)
        {
            _spaceHeld = false;
            UpdateCursor();
            e.Handled = true;
        }
    }

    private void Stage_LostFocus(object? sender, RoutedEventArgs e)
    {
        _spaceHeld = false;
        UpdateCursor();
    }

    private void UpdateCursor(Point? point = null)
    {
        if (_disposed)
            return;

        var type = StandardCursorType.Arrow;
        if (_gesture?.Kind == GestureKind.Pan || (_bitmap is not null && (_panTool || _spaceHeld)))
        {
            type = StandardCursorType.Hand;
        }
        else if (CanSelectRegion && !IsLoading)
        {
            var bounds = ImageBounds;
            var selection = _gesture?.Selection ?? CommittedSelection;
            var viewport = selection is { } region ? RegionSelectionGeometry.ToViewport(region, bounds) : (Rect?)null;
            var edge = _gesture?.Edge ?? (viewport is { } rect && point is { } position
                ? RegionSelectionGeometry.FindHandle(rect, position) : RegionResizeEdge.None);
            type = edge switch
            {
                RegionResizeEdge.North | RegionResizeEdge.West => StandardCursorType.TopLeftCorner,
                RegionResizeEdge.North | RegionResizeEdge.East => StandardCursorType.TopRightCorner,
                RegionResizeEdge.South | RegionResizeEdge.West => StandardCursorType.BottomLeftCorner,
                RegionResizeEdge.South | RegionResizeEdge.East => StandardCursorType.BottomRightCorner,
                RegionResizeEdge.North or RegionResizeEdge.South => StandardCursorType.SizeNorthSouth,
                RegionResizeEdge.West or RegionResizeEdge.East => StandardCursorType.SizeWestEast,
                _ when _gesture?.Kind == GestureKind.Move || point is { } cursor && viewport?.Contains(cursor) == true
                    => StandardCursorType.SizeAll,
                _ => StandardCursorType.Cross
            };
        }

        if (!_cursors.TryGetValue(type, out var value))
            _cursors[type] = value = new Cursor(type);
        Stage.Cursor = value;
    }

    private static string? FirstImagePath(IEnumerable<IStorageItem>? files) =>
        files?.OfType<IStorageFile>().Select(file => file.TryGetLocalPath())
            .FirstOrDefault(path => path is not null && FileTypeConstants.IsImageFile(path));

    private void Stage_DragOver(object? sender, DragEventArgs e)
    {
        var canDrop = e.DataTransfer.Formats.Contains(DataFormat.File) || e.DataTransfer.Formats.Contains(DataFormat.Bitmap);
        e.DragEffects = canDrop ? DragDropEffects.Copy : DragDropEffects.None;
        StageBorder.Classes.Set("region-drop", canDrop);
        e.Handled = true;
    }

    private void Stage_DragLeave(object? sender, DragEventArgs e) => StageBorder.Classes.Remove("region-drop");

    private async void Stage_Drop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        StageBorder.Classes.Remove("region-drop");
        if (_disposed)
            return;

        try
        {
            if (FirstImagePath(e.DataTransfer.TryGetFiles()) is { } path)
            {
                await LoadImageAsync(path);
            }
            else if (e.DataTransfer.TryGetBitmap() is { } bitmap)
            {
                using (bitmap)
                    await ReplaceInputAsync(token => SaveTemporaryImageAsync(bitmap, token));
            }
            else
            {
                UpdateFeedback("请拖入支持的图片文件。");
            }
        }
        catch (Exception ex)
        {
            if (!_disposed)
                UpdateFeedback($"无法读取拖入的图片：{ex.Message}");
        }
    }

    private async void Choose_Click(object? sender, RoutedEventArgs e) => await ChooseImageAsync();
    private async void Paste_Click(object? sender, RoutedEventArgs e) => await PasteImageAsync();
    private void Original_Click(object? sender, RoutedEventArgs e) => Mode = RegionSearchMode.FindOriginal;
    private void Similar_Click(object? sender, RoutedEventArgs e) => Mode = RegionSearchMode.SimilarContent;

    private void Crop_Click(object? sender, RoutedEventArgs e)
    {
        _panTool = false;
        RefreshControls();
        Stage.Focus();
        UpdateCursor();
    }

    private void Pan_Click(object? sender, RoutedEventArgs e)
    {
        _panTool = true;
        RefreshControls();
        Stage.Focus();
        UpdateCursor();
    }

    private void Fit_Click(object? sender, RoutedEventArgs e) => ResetView();

    private void Clear_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedRegion is null)
            return;

        CancelGesture();
        SelectedRegion = null;
        IsAwaitingSelection = true;
        RefreshControls();
        UpdateFeedback();
        Stage.Focus();
        InputChanging?.Invoke(this, EventArgs.Empty);
        InputStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Search_Click(object? sender, RoutedEventArgs e) => PublishQuery(explicitSubmit: true);
}

public sealed class RegionSearchCanvas : Control
{
    private static readonly IBrush Shade = new SolidColorBrush(Color.FromArgb(120, 17, 16, 25));
    private static readonly IPen Shadow = new Pen(new SolidColorBrush(Color.FromArgb(170, 24, 23, 34)), 3);
    private static readonly IPen Outline = new Pen(Brushes.White, 1);
    private static readonly IPen Guide = new Pen(new SolidColorBrush(Color.FromArgb(102, 255, 255, 255)), 1);
    private Bitmap? _bitmap;
    private Rect _image;
    private Rect? _selection;

    internal void SetScene(Bitmap? bitmap, Rect image, Rect? selection)
    {
        if (ReferenceEquals(_bitmap, bitmap) && _image == image && _selection == selection)
            return;

        _bitmap = bitmap;
        _image = image;
        _selection = selection;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (_bitmap is null || _image.Width <= 0 || _image.Height <= 0)
            return;

        context.DrawImage(_bitmap, _image);
        if (_selection is not { Width: > 0, Height: > 0 } selection)
            return;

        var box = RegionSelectionGeometry.ToViewport(selection, _image);
        context.FillRectangle(Shade, new Rect(_image.Left, _image.Top, _image.Width, Math.Max(0, box.Top - _image.Top)));
        context.FillRectangle(Shade, new Rect(_image.Left, box.Bottom, _image.Width, Math.Max(0, _image.Bottom - box.Bottom)));
        context.FillRectangle(Shade, new Rect(_image.Left, box.Top, Math.Max(0, box.Left - _image.Left), box.Height));
        context.FillRectangle(Shade, new Rect(box.Right, box.Top, Math.Max(0, _image.Right - box.Right), box.Height));
        context.DrawRectangle(null, Shadow, box);
        context.DrawRectangle(null, Outline, box);
        for (var index = 1; index <= 2; index++)
        {
            var x = box.Left + box.Width * index / 3;
            var y = box.Top + box.Height * index / 3;
            context.DrawLine(Guide, new Point(x, box.Top), new Point(x, box.Bottom));
            context.DrawLine(Guide, new Point(box.Left, y), new Point(box.Right, y));
        }

        foreach (var edge in RegionSelectionGeometry.Handles)
        {
            var point = RegionSelectionGeometry.HandlePoint(box, edge);
            context.DrawRectangle(Brushes.White, Shadow, new Rect(point.X - 4, point.Y - 4, 8, 8), 2, 2);
        }
    }
}
