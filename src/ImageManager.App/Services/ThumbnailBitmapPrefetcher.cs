using System.ComponentModel;
using ImageManager.App.Controls;
using ImageManager.App.ViewModels;

namespace ImageManager.App.Services;

/// <summary>
/// Tracks a small data-only lookahead on the UI thread. Once a candidate gets
/// encoded bytes, its bitmap can be warmed without creating an item control.
/// </summary>
internal sealed class ThumbnailBitmapPrefetcher : IDisposable
{
    internal const int CandidateLimit = 24;

    private readonly Dictionary<ImageViewItem, Candidate> _candidates = new(ReferenceEqualityComparer.Instance);
    private readonly Func<byte[], CancellationToken, ValueTask> _prewarm;
    private CancellationToken _parentToken;
    private bool _disposed;

    public ThumbnailBitmapPrefetcher() : this(AsyncThumbnailImage.PrewarmAsync) { }

    internal ThumbnailBitmapPrefetcher(Func<byte[], CancellationToken, ValueTask> prewarm)
    {
        _prewarm = prewarm;
    }

    public void UpdateCandidates(IReadOnlyList<ImageViewItem> items, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_parentToken != token)
        {
            Clear();
            _parentToken = token;
        }
        if (token.IsCancellationRequested)
        {
            Clear();
            return;
        }

        var wanted = new HashSet<ImageViewItem>(ReferenceEqualityComparer.Instance);
        var ordered = new List<ImageViewItem>(Math.Min(items.Count, CandidateLimit));
        foreach (var item in items)
        {
            if (wanted.Add(item))
                ordered.Add(item);
            if (ordered.Count == CandidateLimit)
                break;
        }
        foreach (var item in _candidates.Keys.Where(item => !wanted.Contains(item)).ToArray())
        {
            _candidates[item].Dispose();
            _candidates.Remove(item);
        }
        foreach (var item in ordered)
        {
            if (!_candidates.ContainsKey(item))
                _candidates.Add(item, new Candidate(item, token, _prewarm));
        }
    }

    public void Clear()
    {
        foreach (var candidate in _candidates.Values)
            candidate.Dispose();
        _candidates.Clear();
        _parentToken = default;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Clear();
    }

    private sealed class Candidate : IDisposable
    {
        private readonly ImageViewItem _item;
        private readonly CancellationToken _parentToken;
        private readonly Func<byte[], CancellationToken, ValueTask> _prewarm;
        private byte[]? _bytes;
        private WarmRequest? _request;

        public Candidate(
            ImageViewItem item, CancellationToken token, Func<byte[], CancellationToken, ValueTask> prewarm)
        {
            _item = item;
            _parentToken = token;
            _prewarm = prewarm;
            item.PropertyChanged += OnPropertyChanged;
            RequestCurrentBytes();
        }

        public void Dispose()
        {
            _item.PropertyChanged -= OnPropertyChanged;
            _request?.Cancel();
            _request = null;
            _bytes = null;
        }

        private void OnPropertyChanged(object? sender, PropertyChangedEventArgs change)
        {
            if (change.PropertyName is null or "" or nameof(ImageViewItem.ThumbnailData))
                RequestCurrentBytes();
        }

        private void RequestCurrentBytes()
        {
            var bytes = _item.ThumbnailData;
            if (ReferenceEquals(_bytes, bytes))
                return;
            _bytes = bytes;
            _request?.Cancel();
            _request = null;
            if (bytes is not { Length: > 0 } || _parentToken.IsCancellationRequested)
                return;

            var request = new WarmRequest(_parentToken);
            _request = request;
            _ = RunAsync(bytes, request);
        }

        private async Task RunAsync(byte[] bytes, WarmRequest request)
        {
            using (request)
            {
                var token = request.Token;
                try
                {
                    await _prewarm(bytes, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Thumbnail prefetch failed: {ex.Message}");
                }
            }
        }
    }

    private sealed class WarmRequest(CancellationToken parent) : IDisposable
    {
        private readonly object _gate = new();
        private CancellationTokenSource? _source = CancellationTokenSource.CreateLinkedTokenSource(parent);

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
