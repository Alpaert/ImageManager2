using System.Diagnostics;
using ImageManager.Common.Helpers;
using ImageManager.Core.Models;
using ImageManager.Core.Services;
using ImageManager.Infrastructure.Data.Repositories;
using ImageManager.Infrastructure.Imaging;
using ImageManager.Infrastructure.Services.Inference;
using SkiaSharp;

namespace ImageManager.Infrastructure.Services;

public sealed class RegionSearchService(
    IImageEmbeddingRepository embeddings,
    ImageEmbeddingRepository pixaiRepository,
    SscdEmbeddingService sscd,
    PixaiTagService pixai,
    string modelsRoot,
    RegionCandidateSnapshotCache? snapshots = null) : IRegionSearchService
{
    private const int PixaiDimension = 1024;
    private const long MaximumRegionSourcePixels = 32_000_000;

    public IDisposable? BeginSession() => pixai.BeginSearchSession();

    public async Task<RegionSearchResponse> SearchAsync(
        string imagePath, ImageRegion? region, IReadOnlyCollection<string> candidates,
        RegionSearchMode mode, int limit, CancellationToken ct = default)
    {
        var watch = Stopwatch.StartNew();
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        ct.ThrowIfCancellationRequested();
        var model = mode switch
        {
            RegionSearchMode.FindOriginal => new RegionCandidateModel(
                SscdEmbeddingService.ModelKey, SscdEmbeddingService.ModelVersion, SscdEmbeddingService.Dimension),
            RegionSearchMode.SimilarContent => new RegionCandidateModel("pixai", "v0.9", PixaiDimension),
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
        var isOriginal = mode == RegionSearchMode.FindOriginal;
        var queryPath = Path.GetFullPath(imagePath);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in candidates)
        {
            ct.ThrowIfCancellationRequested();
            paths.Add(Path.GetFullPath(path));
        }
        var excludedPath = isOriginal ? null : queryPath;
        var candidateCount = paths.Count - (excludedPath is not null && paths.Contains(excludedPath) ? 1 : 0);
        if (candidateCount == 0)
        {
            AppLogger.Info($"RegionSearch.Ranked mode={mode} rankMs=0 totalMs={watch.ElapsedMilliseconds}");
            return new RegionSearchResponse([], 0, 0);
        }

        Task<List<ImageEmbeddingRecord>> LoadRowsAsync(CancellationToken token) => isOriginal
            ? embeddings.GetValidSearchEmbeddingsByPathsAsync(model.Key, model.Version, paths, token)
            : pixaiRepository.GetPixaiSearchEmbeddingsByPathsAsync(paths, token);

        var candidateWatch = Stopwatch.StartNew();
        var loaded = await Task.Run(async () => snapshots is not null
            ? await snapshots.GetAsync(model, paths, LoadRowsAsync, ct)
            : new RegionCandidateLoadResult(RegionCandidateSnapshotCache.FilterUsableVectors(
                await LoadRowsAsync(ct), model.Dimension, ct), false), ct);
        var loadMs = candidateWatch.ElapsedMilliseconds;
        candidateWatch.Restart();
        var usable = await Task.Run(() => FilterCurrentCandidates(loaded.Rows, excludedPath, ct), ct);
        var cacheStatus = loaded.CacheHit ? "hit" : "miss";
        AppLogger.Info($"RegionSearch.CandidateCache mode={mode} status={cacheStatus} candidates={candidateCount} " +
            $"loadMs={loadMs} fileCheckMs={candidateWatch.ElapsedMilliseconds}");
        AppLogger.Info($"RegionSearch.CandidatesReady mode={mode} usable={usable.Count} ms={watch.ElapsedMilliseconds}");
        if (usable.Count == 0 || limit == 0)
        {
            AppLogger.Info($"RegionSearch.Ranked mode={mode} rankMs=0 totalMs={watch.ElapsedMilliseconds}");
            return new RegionSearchResponse([], usable.Count, candidateCount);
        }

        if (isOriginal && !sscd.IsAvailable)
            throw new InvalidOperationException("SSCD 模型不可用，请先安装模型。");

        float[] query;
        if (isOriginal)
        {
            using var bitmap = await Task.Run(() => region is null
                ? InferenceImageLoader.DecodeForInference(imagePath) ?? throw new InvalidDataException("无法解码查询图片。")
                : DecodeRegion(imagePath, region), ct);
            AppLogger.Info($"RegionSearch.QueryDecoded mode={mode} ms={watch.ElapsedMilliseconds}");
            query = await sscd.EncodeAsync(bitmap, ct);
        }
        else
        {
            using var bitmap = await Task.Run(() => DecodePixaiQuery(imagePath, region), ct);
            AppLogger.Info($"RegionSearch.QueryDecoded mode={mode} ms={watch.ElapsedMilliseconds}");
            query = await pixai.EncodeSearchRegionAsync(bitmap, Path.Combine(modelsRoot, "pixai"), ct);
        }
        AppLogger.Info($"RegionSearch.QueryEncoded mode={mode} ms={watch.ElapsedMilliseconds}");
        if (!IsUsableVector(query, model.Dimension))
            throw new InvalidOperationException("无法提取查询图片的有效向量。");

        var rankWatch = Stopwatch.StartNew();
        var results = await Task.Run(() => usable.Select(row =>
        {
            ct.ThrowIfCancellationRequested();
            return new SimilaritySearchResult(
                row.FilePath, (float)EmbeddingMath.CosineSimilarity(query, row.Embedding));
        }).OrderByDescending(result => result.Score)
            .ThenBy(result => result.FilePath, StringComparer.OrdinalIgnoreCase)
            .Take(limit).ToArray(), ct);
        AppLogger.Info($"RegionSearch.Ranked mode={mode} rankMs={rankWatch.ElapsedMilliseconds} totalMs={watch.ElapsedMilliseconds}");
        return new RegionSearchResponse(results, usable.Count, candidateCount);
    }

    internal static List<ImageEmbeddingRecord> FilterCurrentCandidates(
        IReadOnlyList<ImageEmbeddingRecord> rows, string? excludedPath, CancellationToken ct)
    {
        var usable = new List<ImageEmbeddingRecord>(rows.Count);
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            if (excludedPath is not null && string.Equals(row.FilePath, excludedPath, StringComparison.OrdinalIgnoreCase))
                continue;
            if (MatchesFileState(row.FilePath, row.SourceFileSize, row.SourceLastWriteTicks))
                usable.Add(row);
        }
        return usable;
    }

    internal static bool IsUsableVector(float[] vector, int dimension)
    {
        if (vector.Length != dimension)
            return false;
        var nonzero = false;
        foreach (var value in vector)
        {
            if (!float.IsFinite(value))
                return false;
            nonzero |= value != 0;
        }
        return nonzero;
    }

    internal static bool MatchesFileState(string path, long length, long ticks) =>
        ImageEmbeddingRepository.MatchesSearchFileState(path, length, ticks);

    internal static SKBitmap DecodeRegion(string imagePath, ImageRegion? region)
    {
        using var stream = File.Open(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var codec = SKCodec.Create(stream) ?? throw new InvalidDataException("无法解码图片。");
        var source = codec.Info;
        if ((long)source.Width * source.Height > MaximumRegionSourcePixels)
            throw new InvalidDataException("精确选区暂支持不超过 3200 万像素的原图，请使用较小的图片或整图搜索。");
        if (region is { } bounds && (bounds.X < 0 || bounds.Y < 0 || bounds.Width <= 0 || bounds.Height <= 0 ||
            (long)bounds.X + bounds.Width > source.Width || (long)bounds.Y + bounds.Height > source.Height))
            throw new ArgumentOutOfRangeException(nameof(region), "选区超出图片范围。");

        var bitmap = new SKBitmap(source.WithColorType(SKColorType.Rgba8888));
        try
        {
            if (codec.GetPixels(bitmap.Info, bitmap.GetPixels(), new SKCodecOptions(0)) != SKCodecResult.Success)
                throw new InvalidDataException("无法解码图片的第一帧。");
            if (region is null)
                return bitmap;

            var (x, y, width, height) = region;
            var subset = new SKBitmap();
            if (!bitmap.ExtractSubset(subset, SKRectI.Create(x, y, width, height)))
            {
                subset.Dispose();
                throw new InvalidDataException("无法读取图片选区。");
            }
            bitmap.Dispose();
            return subset;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static SKBitmap DecodePixaiQuery(string path, ImageRegion? region)
    {
        if (region is null)
            return InferenceImageLoader.DecodeForInference(path)
                ?? throw new InvalidDataException("无法解码查询图片。");

        using var crop = DecodeRegion(path, region);
        var scale = Math.Min(1.0, (double)InferenceImageLoader.DefaultDecodeMaxSize / Math.Max(crop.Width, crop.Height));
        var width = Math.Max(1, (int)(crop.Width * scale));
        var height = Math.Max(1, (int)(crop.Height * scale));
        var info = new SKImageInfo(width, height, crop.ColorType, crop.AlphaType);
        return crop.Resize(info, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
            ?? throw new InvalidDataException("无法缩放查询选区。");
    }
}
