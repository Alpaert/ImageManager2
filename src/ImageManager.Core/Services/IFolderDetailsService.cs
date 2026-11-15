using ImageManager.Core.Models;

namespace ImageManager.Core.Services;

public interface IFolderDetailsService
{
    Task<FolderDetailsSnapshot> GetAsync(string path, CancellationToken ct = default);
    /// <summary>Reports provisional counts at a bounded rate, before the final result.</summary>
    Task<FolderDetailsSnapshot> GetAsync(string path, IProgress<FolderDetailsSnapshot> progress, CancellationToken ct = default)
        => GetAsync(path, ct);
    /// <summary>Invalidates cached summaries of this path and its ancestors.</summary>
    void Invalidate(string changedPath);
}
