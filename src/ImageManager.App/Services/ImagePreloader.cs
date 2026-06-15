using System.Collections.Concurrent;
using System.Diagnostics;
using ImageManager.Common.Helpers;
using ImageManager.Infrastructure.Caching;
using ImageManager.Infrastructure.Imaging;

namespace ImageManager.App.Services;

/// <summary>
/// Bidirectional sliding window preloader for the preview window.
///
/// On position change:
///   1. Cancel all stale preload tasks
///   2. Compute new window: forward=4, backward=2 (asymmetric)
///   3. Submit CRITICAL priority for current image, HIGH for immediate neighbors, NORMAL for distant
///
/// Uses SemaphoreSlim to limit concurrent decodes (CPU-bound work).
/// Pre-decoded images are stored in PreviewImageCache.
/// </summary>
public sealed class ImagePreloader : IDisposable
{
    private readonly PreviewImageCache _cache;
    private readonly SemaphoreSlim _decodeSemaphore;
    private CancellationTokenSource _globalCts = new();
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _activeTasks = new();

    private List<string> _filePaths = new();
    private int _currentIndex = -1;

    // Window sizes (asymmetric: forward bias)
    private const int ForwardWindow = 4;
    private const int BackwardWindow = 2;
    private const int MaxConcurrentDecodes = 4;

    // Decode width cap for preview (4K-equivalent)
    private const int MaxDecodeWidth = 3840;

    public PreviewImageCache Cache => _cache;

    public ImagePreloader()
    {
        _cache = new PreviewImageCache();
        _decodeSemaphore = new SemaphoreSlim(MaxConcurrentDecodes, MaxConcurrentDecodes);
    }

    /// <summary>
    /// Initialize/update the file list. Called when preview window opens.
    /// </summary>
    public void SetFileList(List<string> filePaths, int startIndex)
    {
        _filePaths = filePaths;
        _currentIndex = startIndex;
        _cache.SetCurrentIndex(startIndex);
    }

    /// <summary>
    /// Called when the user navigates to a new image.
    /// Returns raw BGRA pixel data for direct WriteableBitmap fill.
    /// If cache miss, decodes via DecodeRawPixels (single decode, no JPEG round-trip).
    /// Also triggers background preloading of the sliding window.
    /// </summary>
    public async Task<(byte[]? Data, int Width, int Height)> NavigateToAsync(int newIndex, CancellationToken externalCt = default)
    {
        if (newIndex < 0 || newIndex >= _filePaths.Count)
            return (null, 0, 0);

        int oldIndex = _currentIndex;
        _currentIndex = newIndex;
        _cache.SetCurrentIndex(newIndex);

        // Cancel all pending preloads from the previous position
        CancelAllPending();

        var filePath = _filePaths[newIndex];

        // 1. Check cache first
        var cached = _cache.TryGet(filePath, out int w, out int h);
        if (cached != null)
        {
            // Cache hit — trigger window update and return immediately
            _ = Task.Run(() => UpdateSlidingWindow(newIndex));
            return (cached, w, h);
        }

        // 2. Cache miss — decode with CRITICAL priority
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt, _globalCts.Token);
        var result = await DecodeImageAsync(filePath, newIndex, cts.Token);

        // 3. Trigger sliding window update in background
        if (result.Data != null)
        {
            _ = Task.Run(() => UpdateSlidingWindow(newIndex));
        }

        return result;
    }

    /// <summary>
    /// Get the cached image data for the current index (non-blocking).
    /// Returns null if not in cache.
    /// </summary>
    public (byte[]? Data, int Width, int Height) GetCached(int index)
    {
        if (index < 0 || index >= _filePaths.Count)
            return (null, 0, 0);
        var data = _cache.TryGet(_filePaths[index], out int w, out int h);
        return (data, w, h);
    }

    /// <summary>
    /// Signal that the navigation settled (e.g., user stopped rapidly pressing keys).
    /// Useful for triggering lower-priority preloads after rapid scrolling.
    /// </summary>
    public void OnNavigationSettled(int index)
    {
        _ = Task.Run(() => UpdateSlidingWindow(index));
    }

    /// <summary>
    /// Cancel all in-flight decode tasks.
    /// </summary>
    public void CancelAllPending()
    {
        // Cancel the global CTS and create a new one
        var oldCts = Interlocked.Exchange(ref _globalCts, new CancellationTokenSource());
        try { oldCts.Cancel(); } catch { }
        oldCts.Dispose();

        // Also cancel individually tracked tasks
        foreach (var kv in _activeTasks)
        {
            try { kv.Value.Cancel(); } catch { }
            kv.Value.Dispose();
        }
        _activeTasks.Clear();
    }

    private async Task UpdateSlidingWindow(int centerIndex)
    {
        // Compute the full window of indices to ensure are decoded
        var desired = new HashSet<int>();

        // Forward window: center+1 .. center+ForwardWindow
        for (int i = 1; i <= ForwardWindow; i++)
        {
            int idx = centerIndex + i;
            if (idx >= 0 && idx < _filePaths.Count)
                desired.Add(idx);
        }

        // Backward window: center-BackwardWindow .. center-1
        for (int i = 1; i <= BackwardWindow; i++)
        {
            int idx = centerIndex - i;
            if (idx >= 0 && idx < _filePaths.Count)
                desired.Add(idx);
        }

        // Sort by priority: closer to center = higher priority
        var sorted = desired
            .Where(idx => _cache.TryGet(_filePaths[idx], out _, out _) == null) // skip already cached
            .OrderBy(idx => Math.Abs(idx - centerIndex))
            .ToList();

        if (sorted.Count == 0) return;

        // Use a fresh linked CTS for this window update
        using var windowCts = CancellationTokenSource.CreateLinkedTokenSource(_globalCts.Token);

        // Decode in parallel with semaphore limiting
        var tasks = sorted.Select(async idx =>
        {
            try
            {
                if (windowCts.Token.IsCancellationRequested) return;
                var path = _filePaths[idx];
                await DecodeImageAsync(path, idx, windowCts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AppLogger.Warn($"[Preloader] Failed to preload {_filePaths[idx]}: {ex.Message}");
            }
        });

        await Task.WhenAll(tasks);
    }

    private async Task<(byte[]? Data, int Width, int Height)> DecodeImageAsync(
        string filePath, int fileIndex, CancellationToken ct)
    {
        if (!File.Exists(filePath))
            return (null, 0, 0);

        bool acquired = false;
        try
        {
            await _decodeSemaphore.WaitAsync(ct);
            acquired = true;

            // Double-check cache (another task may have decoded it while we waited)
            var cached = _cache.TryGet(filePath, out int cw, out int ch);
            if (cached != null)
                return (cached, cw, ch);

            if (ct.IsCancellationRequested)
                return (null, 0, 0);

            // Decode raw BGRA pixels directly (single decode, no JPEG re-encode → re-decode waste)
            var (data, pixW, pixH) = await Task.Run(() =>
            {
                if (ct.IsCancellationRequested)
                    return (null!, 0, 0);

                return ThumbnailGenerator.DecodeRawPixels(filePath, MaxDecodeWidth);
            });

            if (data != null)
            {
                _cache.Store(filePath, data, pixW, pixH, fileIndex);
            }

            return (data, pixW, pixH);
        }
        catch (OperationCanceledException)
        {
            return (null, 0, 0);
        }
        finally
        {
            if (acquired)
                _decodeSemaphore.Release();
        }
    }

    public void Dispose()
    {
        CancelAllPending();
        _globalCts.Dispose();
        _decodeSemaphore.Dispose();
        _cache.Clear();
    }
}
