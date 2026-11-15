using ImageManager.Common.Constants;
using ImageManager.Core.Models;
using ImageManager.Core.Services;

namespace ImageManager.Infrastructure.Services;

/// <summary>Bounded, streaming summaries of physical folders; never follows reparse points.</summary>
public sealed class FolderDetailsService : IFolderDetailsService, IDisposable
{
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, (FolderDetailsSnapshot Value, long Order)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private long _order, _revision;
    private bool _disposed;

    public Task<FolderDetailsSnapshot> GetAsync(string path, CancellationToken ct = default)
        => GetCoreAsync(path, null, ct);

    public Task<FolderDetailsSnapshot> GetAsync(string path, IProgress<FolderDetailsSnapshot> progress, CancellationToken ct = default)
        => GetCoreAsync(path, progress, ct);

    private async Task<FolderDetailsSnapshot> GetCoreAsync(string path, IProgress<FolderDetailsSnapshot>? progress, CancellationToken ct)
    {
        var fullPath = Path.GetFullPath(path);
        CancellationToken token;
        CancellationTokenSource linked;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
            token = linked.Token;
        }
        using (linked)
        {
            token.ThrowIfCancellationRequested();
            if (TryRead(fullPath, out var cached)) return cached!;
            await _scanGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (TryRead(fullPath, out cached)) return cached!;
                long revision;
                lock (_gate) revision = _revision;
                var result = await Task.Run(() => Scan(fullPath, progress, token), token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    if (!_disposed && revision == _revision)
                    {
                        _cache[fullPath] = (result, ++_order);
                        while (_cache.Count > 32)
                            _cache.Remove(_cache.MinBy(entry => entry.Value.Order).Key);
                    }
                }
                return result;
            }
            finally { _scanGate.Release(); }
        }
    }

    private bool TryRead(string path, out FolderDetailsSnapshot? snapshot)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(path, out var entry) && DateTimeOffset.UtcNow - entry.Value.ScannedAt < TimeSpan.FromSeconds(30))
            {
                snapshot = entry.Value;
                _cache[path] = (entry.Value, ++_order);
                return true;
            }
            _cache.Remove(path);
        }
        snapshot = null;
        return false;
    }

    public void Invalidate(string changedPath)
    {
        if (string.IsNullOrWhiteSpace(changedPath)) return;
        string path;
        try { path = Path.GetFullPath(changedPath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return; }
        lock (_gate)
        {
            ++_revision;
            foreach (var key in _cache.Keys.Where(key => Contains(key, path) || Contains(path, key)).ToArray())
                _cache.Remove(key);
        }
    }

    private static bool Contains(string parent, string path) =>
        string.Equals(parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static FolderDetailsSnapshot Scan(string path, IProgress<FolderDetailsSnapshot>? progress, CancellationToken ct)
    {
        long direct = 0, files = 0, media = 0, folders = 0, bytes = 0, skipped = 0;
        try
        {
            var attributes = File.GetAttributes(path);
            if (!attributes.HasFlag(FileAttributes.Directory))
                return Unavailable("目标不是文件夹");
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
                return Unavailable("目录链接不进行递归统计");
        }
        catch (Exception ex) when (IsFileError(ex)) { return Unavailable("文件夹不存在或无法访问"); }

        var pending = new Stack<string>();
        pending.Push(path);
        var progressClock = System.Diagnostics.Stopwatch.StartNew();
        ReportProgress();
        while (pending.TryPop(out var directory))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    ct.ThrowIfCancellationRequested();
                    if (progressClock.ElapsedMilliseconds >= 250) ReportProgress();
                    try
                    {
                        var attributes = File.GetAttributes(entry);
                        if (attributes.HasFlag(FileAttributes.ReparsePoint)) { skipped++; continue; }
                        if (attributes.HasFlag(FileAttributes.Directory))
                        {
                            folders++;
                            pending.Push(entry);
                            continue;
                        }
                        var length = new FileInfo(entry).Length;
                        bytes = checked(bytes + length);
                        files++;
                        if (StringComparer.OrdinalIgnoreCase.Equals(directory, path)) direct++;
                        if (FileTypeConstants.IsMediaFile(entry)) media++;
                    }
                    catch (Exception ex) when (IsFileError(ex) || ex is OverflowException) { skipped++; }
                }
            }
            catch (Exception ex) when (IsFileError(ex)) { skipped++; }
        }
        ct.ThrowIfCancellationRequested();
        return new(path, direct, files, media, folders, bytes,
            skipped == 0 ? FolderStatisticsState.Complete : FolderStatisticsState.Partial,
            DateTimeOffset.UtcNow, skipped, skipped == 0 ? null : "部分统计：已跳过链接或无法读取的项目");

        FolderDetailsSnapshot Unavailable(string error) => new(path, 0, 0, 0, 0, 0,
            FolderStatisticsState.Unavailable, DateTimeOffset.UtcNow, Error: error);

        void ReportProgress()
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new(path, direct, files, media, folders, bytes,
                FolderStatisticsState.Scanning, DateTimeOffset.UtcNow, skipped));
            progressClock.Restart();
        }
    }

    private static bool IsFileError(Exception ex) => ex is IOException or UnauthorizedAccessException or System.Security.SecurityException;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _cache.Clear();
            _shutdown.Cancel();
        }
        // Waiters/running native directory calls own the remaining gate/CTS lifetime.
    }
}
