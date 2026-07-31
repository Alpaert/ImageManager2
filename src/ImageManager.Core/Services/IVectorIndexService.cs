using ImageManager.Core.Models;

namespace ImageManager.Core.Services;

public interface IVectorIndexService
{
    bool IsRunning { get; }

    Task<IReadOnlyList<VectorIndexStatus>> GetStatusesAsync(VectorIndexScope scope);

    Task<VectorIndexStatus> GetStatusAsync(VectorIndexKind kind, VectorIndexScope scope);

    Task BuildAsync(
        VectorIndexKind kind,
        bool rebuild,
        VectorIndexScope scope,
        IProgress<VectorIndexProgress>? progress = null,
        CancellationToken ct = default);

    void Pause();
    void Resume();
    void Cancel();
}
