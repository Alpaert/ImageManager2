using System.Collections.ObjectModel;

namespace ImageManager.App.Services;

/// <summary>
/// An immutable input and geometry snapshot for a continuous display result set.
/// The paths and dimensions are copied before any asynchronous metadata work starts,
/// so later result-list changes cannot alter an in-flight or completed layout.
/// </summary>
public sealed class ContinuousDisplayGeometrySnapshot
{
    private readonly ReadOnlyCollection<string> _paths;
    private readonly ReadOnlyCollection<ImageDisplayItemSize> _sizes;

    internal ContinuousDisplayGeometrySnapshot(
        string[] paths,
        ImageDisplayItemSize[] sizes,
        ImageDisplayGeometryIndex geometryIndex,
        int dimensionHitCount)
    {
        _paths = Array.AsReadOnly(paths);
        _sizes = Array.AsReadOnly(sizes);
        GeometryIndex = geometryIndex;
        DimensionHitCount = dimensionHitCount;
    }

    /// <summary>Frozen source paths in display-result order.</summary>
    public IReadOnlyList<string> Paths => _paths;

    /// <summary>
    /// Frozen source dimensions in the same order as <see cref="Paths"/>. Missing
    /// or invalid metadata remains 0x0 so <see cref="ImageDisplayGeometryIndex"/>
    /// can apply its normal fallback aspect ratio.
    /// </summary>
    public IReadOnlyList<ImageDisplayItemSize> Sizes => _sizes;

    /// <summary>The complete logical geometry calculated from <see cref="Sizes"/>.</summary>
    public ImageDisplayGeometryIndex GeometryIndex { get; }

    /// <summary>Number of source entries with usable width and height metadata.</summary>
    public int DimensionHitCount { get; }
}

/// <summary>
/// Builds a continuous display geometry snapshot without depending on UI controls,
/// thumbnails, or a particular metadata repository. Callers provide the batched
/// dimension loader so the service can be tested with an in-memory implementation.
/// </summary>
public sealed class ContinuousDisplayGeometryBuilder
{
    /// <summary>Largest dimension lookup request issued to the supplied loader.</summary>
    public const int BatchSize = 900;

    public async Task<ContinuousDisplayGeometrySnapshot> BuildAsync(
        IReadOnlyList<string> sourcePaths,
        ImageDisplayGeometryOptions options,
        Func<List<string>, CancellationToken, Task<Dictionary<string, (int Width, int Height)>>> loadDimensionsAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        ArgumentNullException.ThrowIfNull(loadDimensionsAsync);
        cancellationToken.ThrowIfCancellationRequested();

        // Snapshot before the first await: search, filter, and folder changes can
        // replace the active list while metadata loading is in progress.
        var paths = new string[sourcePaths.Count];
        for (var index = 0; index < paths.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            paths[index] = sourcePaths[index];
        }

        var sizes = new ImageDisplayItemSize[paths.Length];
        var dimensionHitCount = 0;
        for (var batchStart = 0; batchStart < paths.Length; batchStart += BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batchEnd = Math.Min(batchStart + BatchSize, paths.Length);
            var batchPaths = new List<string>(batchEnd - batchStart);
            for (var index = batchStart; index < batchEnd; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                batchPaths.Add(paths[index]);
            }

            var dimensions = await loadDimensionsAsync(batchPaths, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The dimension loader returned no result.");
            cancellationToken.ThrowIfCancellationRequested();

            // Read from the frozen snapshot, not from batchPaths: the loader owns
            // its input for the duration of its call and must not affect filling.
            for (var index = batchStart; index < batchEnd; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!dimensions.TryGetValue(paths[index], out var dimension)
                    || dimension.Width <= 0
                    || dimension.Height <= 0)
                {
                    continue;
                }

                sizes[index] = new ImageDisplayItemSize(dimension.Width, dimension.Height);
                dimensionHitCount++;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var geometryIndex = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = ImageDisplayGeometryIndex.Build(sizes, options);
            cancellationToken.ThrowIfCancellationRequested();
            return index;
        }, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        return new ContinuousDisplayGeometrySnapshot(paths, sizes, geometryIndex, dimensionHitCount);
    }
}
