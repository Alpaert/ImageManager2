using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ImageManager.App.Services;
using ImageManager.App.ViewModels;

namespace ImageManager.App.Controls;

/// <summary>
/// An Image for the gallery whose encoded thumbnail is decoded off the UI thread.
/// </summary>
public sealed class AsyncThumbnailImage : Image
{
    private static readonly DecodedThumbnailCache<Bitmap> Cache = new(
        Decode, bitmap => (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4,
        workerCount: 2, queueCapacity: 128, idleBudget: 96L * 1024 * 1024);

    public static readonly StyledProperty<byte[]?> ThumbnailDataProperty =
        AvaloniaProperty.Register<AsyncThumbnailImage, byte[]?>(nameof(ThumbnailData));

    public static readonly DirectProperty<AsyncThumbnailImage, bool> HasThumbnailProperty =
        AvaloniaProperty.RegisterDirect<AsyncThumbnailImage, bool>(nameof(HasThumbnail), image => image.HasThumbnail);

    private DecodedThumbnailCache<Bitmap>.Lease? _displayed;
    private byte[]? _displayedData;
    private PendingThumbnail? _pending;
    private byte[]? _pendingData;
    private object? _item;
    private bool _attached;
    private bool _hasThumbnail;

    protected override Type StyleKeyOverride => typeof(Image);

    public byte[]? ThumbnailData
    {
        get => GetValue(ThumbnailDataProperty);
        set => SetValue(ThumbnailDataProperty, value);
    }

    public bool HasThumbnail => _hasThumbnail;

    public static void TrimIdleCache() => Cache.TrimIdle(0);

    internal static async ValueTask PrewarmAsync(byte[] data, CancellationToken token)
    {
        using var lease = await Cache.AcquireAsync(data, token, prefetch: true).ConfigureAwait(false);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ThumbnailDataProperty)
            UpdateThumbnail();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        UpdateThumbnail();
        base.OnDataContextChanged(e);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        UpdateThumbnail();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        CancelPending();
        ShowThumbnail(null, null);
        _item = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void UpdateThumbnail()
    {
        if (!_attached)
            return;

        var context = DataContext;
        if (!ReferenceEquals(_item, context))
        {
            _item = context;
            CancelPending();
            ShowThumbnail(null, null);
        }

        var data = ThumbnailData;
        if (context is not ImageViewItem item || data is not { Length: > 0 })
        {
            CancelPending();
            ShowThumbnail(null, null);
            return;
        }

        // Recycling can update the context while the binding still exposes
        // the previous item's value. Only use bytes owned by the current item.
        if (!ReferenceEquals(data, item.ThumbnailData))
            return;

        if (ReferenceEquals(data, _pendingData))
            return;
        CancelPending();
        if (ReferenceEquals(data, _displayedData))
            return;

        // A realized/recycled card can already have a ready bitmap for the
        // same bytes. Publish it before the next render on the UI thread.
        if (Cache.TryAcquireReady(data) is { } ready)
        {
            ShowThumbnail(ready, data);
            return;
        }

        var pending = new PendingThumbnail();
        _pending = pending;
        _pendingData = data;
        _ = LoadAsync(data, pending);
    }

    private async Task LoadAsync(byte[] data, PendingThumbnail pending)
    {
        DecodedThumbnailCache<Bitmap>.Lease? lease = null;
        var token = pending.Token;
        try
        {
            try
            {
                lease = await Cache.AcquireAsync(data, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // The waiting/queued cache reservation has already been released.
            }
            catch (Exception ex)
            {
                // Still run the UI handoff so the next input can retry.
                System.Diagnostics.Debug.WriteLine($"Thumbnail request failed: {ex.Message}");
            }

            if (token.IsCancellationRequested)
            {
                lease?.Dispose();
                lease = null;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!ReferenceEquals(pending, _pending))
                    return;

                _pending = null;
                _pendingData = null;
                if (lease is null || !_attached || DataContext is not ImageViewItem item
                    || !ReferenceEquals(_item, item) || !ReferenceEquals(data, item.ThumbnailData)
                    || !ReferenceEquals(data, ThumbnailData))
                    return;

                var ready = lease;
                lease = null;
                ShowThumbnail(ready, data);
            }, DispatcherPriority.Background);
        }
        catch (OperationCanceledException)
        {
            // The dispatcher can abort publication while the window is closing.
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Thumbnail publication failed: {ex.Message}");
        }
        finally
        {
            pending.Dispose();
            lease?.Dispose();
        }
    }

    private void CancelPending()
    {
        var pending = _pending;
        _pending = null;
        _pendingData = null;
        pending?.Cancel();
    }

    private void ShowThumbnail(DecodedThumbnailCache<Bitmap>.Lease? next, byte[]? data)
    {
        var previous = _displayed;
        var previousData = _displayedData;
        _displayed = next;
        _displayedData = data;
        try
        {
            Source = next?.Value;
        }
        finally
        {
            // The property setter can call observers, including reentrant updates.
            // Release the old lease only if the control no longer owns that bitmap.
            if (ReferenceEquals(_displayed, next) && !ReferenceEquals(Source, next?.Value))
            {
                if (ReferenceEquals(Source, previous?.Value))
                {
                    _displayed = previous;
                    _displayedData = previousData;
                }
                else
                {
                    _displayed = null;
                    _displayedData = null;
                }
                next?.Dispose();
            }
            if (!ReferenceEquals(_displayed, previous))
                previous?.Dispose();
            SetAndRaise(HasThumbnailProperty, ref _hasThumbnail, _displayed is not null);
        }
    }

    private static Bitmap Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return new Bitmap(stream);
    }

    // Dispatcher shutdown can end a request before the control sees its detachment.
    // Serialize cancellation and disposal so either order is safe.
    private sealed class PendingThumbnail : IDisposable
    {
        private readonly object _gate = new();
        private CancellationTokenSource? _source = new();

        public CancellationToken Token => _source!.Token;

        public void Cancel()
        {
            lock (_gate)
                _source?.Cancel();
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _source?.Dispose();
                _source = null;
            }
        }
    }
}
