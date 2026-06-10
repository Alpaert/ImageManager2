using System.Collections.Concurrent;
using ImageManager.Core.Services;
using ImageManager.Infrastructure.Hashing;

namespace ImageManager.Infrastructure.Services;

public class SimilarImageService : ISimilarImageService
{
    private readonly IImageMetaRepository _metaRepo;

    private const double HistogramThreshold = 0.35;
    private const int AThreshold = 8;
    private const int DThreshold = 8;
    private const int PThreshold = 10;

    public SimilarImageService(IImageMetaRepository metaRepo)
    {
        _metaRepo = metaRepo;
    }

    public async Task<List<string>> FindSimilarAsync(
        string baseFilePath,
        IEnumerable<string> candidates,
        int threshold = 5,
        CancellationToken ct = default)
    {
        var files = candidates.ToList();
        if (files.Count == 0) return new List<string>();

        // === Step 1: Preload hashes for candidate files from DB into memory ===
        var hashCache = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await PreloadHashCacheAsync(hashCache, files);

        // === Step 2: Get / compute hash for the base image ===
        var baseHash = await GetOrComputeBaseHashAsync(baseFilePath, hashCache);
        if (string.IsNullOrEmpty(baseHash))
            return new List<string>();

        var baseHist = ExtractHistogram(baseHash);
        var result = new List<string>();

        // === Step 3: Parallel scan over candidates ===
        await Task.Run(() =>
        {
            Parallel.ForEach(files, new ParallelOptions
            {
                CancellationToken = ct,
                MaxDegreeOfParallelism = Environment.ProcessorCount
            }, path =>
            {
                if (ct.IsCancellationRequested) return;

                var candHash = GetCachedHash(path, hashCache);
                if (string.IsNullOrEmpty(candHash)) return;

                // Stage 1: Color histogram pre-filter
                if (!string.IsNullOrEmpty(baseHist))
                {
                    var candHist = ExtractHistogram(candHash);
                    if (!string.IsNullOrEmpty(candHist))
                    {
                        double sim = HashService.CompareHistograms(baseHist, candHist);
                        if (sim < HistogramThreshold) return;
                    }
                }

                // Stage 2: Multi-hash voting
                if (HashService.AreSimilarByMultiHash(baseHash, candHash,
                    AThreshold, DThreshold, PThreshold))
                {
                    lock (result) result.Add(path);
                }
            });
        }, ct);

        return result;
    }

    // ==================== Helpers ====================

    /// <summary>Load perceptual hashes for candidate files from DB into a concurrent dictionary.</summary>
    private async Task PreloadHashCacheAsync(ConcurrentDictionary<string, string> cache, List<string> candidates)
    {
        try
        {
            if (candidates.Count == 0) return;

            var hashes = await _metaRepo.GetPerceptualHashesByPathsAsync(candidates);
            foreach (var kv in hashes)
            {
                if (!string.IsNullOrEmpty(kv.Value) && kv.Value.Split('|').Length >= 4)
                {
                    cache[kv.Key] = kv.Value;
                }
            }
        }
        catch { }
    }

    /// <summary>Always compute the base image hash fresh from disk. Temporary — discarded after search.</summary>
    private static async Task<string> GetOrComputeBaseHashAsync(
        string filePath, ConcurrentDictionary<string, string> cache)
    {
        // Compute fresh from disk every time (one file, fast, ensures accuracy if file was modified)
        var combined = await Task.Run(() =>
            HashService.ComputeCombinedPerceptualHashFromFile(filePath));

        if (!string.IsNullOrEmpty(combined))
            cache[filePath] = combined;

        return combined ?? string.Empty;
    }

    /// <summary>Read from cache only. Candidates must be preloaded.</summary>
    private static string GetCachedHash(string filePath, ConcurrentDictionary<string, string> cache)
    {
        return cache.TryGetValue(filePath, out var hash) ? hash : string.Empty;
    }

    /// <summary>Extract the 4th pipe-separated field (color histogram).</summary>
    private static string? ExtractHistogram(string combinedHash)
    {
        var parts = combinedHash.Split('|');
        return parts.Length >= 4 ? parts[3] : null;
    }
}
