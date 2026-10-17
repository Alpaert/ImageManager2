using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ImageManager.Common.Helpers;
using ImageManager.Core.Models;
using ImageManager.Core.Services;

namespace ImageManager.Infrastructure.Services;

/// <summary>
/// Stores one mean PixAI embedding per custom character and searches by cosine similarity.
/// Supports versioned snapshot swapping and copy-on-write updates.
/// </summary>
public class CharacterEmbeddingStore : ICharacterEmbeddingStore
{
    private volatile CharacterStoreSnapshot _snapshot;
    private readonly object _writeLock = new();
    private readonly int _embeddingDim;

    public CharacterStoreSnapshot CurrentSnapshot => _snapshot;
    public int Count => _snapshot.Characters.Count;
    public IReadOnlyDictionary<string, float[]> Characters => _snapshot.Characters;

    public CharacterEmbeddingStore(int embeddingDim = 1024)
    {
        _embeddingDim = embeddingDim;
        _snapshot = new CharacterStoreSnapshot(
            "empty",
            new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            embeddingDim);
    }

    /// <summary>原子切换内存快照</summary>
    public void SwapSnapshot(CharacterStoreSnapshot newSnapshot)
    {
        _snapshot = newSnapshot ?? throw new ArgumentNullException(nameof(newSnapshot));
        AppLogger.Info($"角色嵌入库快照已原子替换: version={newSnapshot.VersionId} count={newSnapshot.Characters.Count}");
    }

    public int GetImageCount(string characterName)
        => _snapshot.ImageCounts.TryGetValue(characterName, out var c) ? c : -1;

    public void Clear()
    {
        lock (_writeLock)
        {
            _snapshot = new CharacterStoreSnapshot(
                Guid.NewGuid().ToString("N"),
                new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                _embeddingDim);
        }
    }

    public void Remove(string characterName)
    {
        lock (_writeLock)
        {
            var snap = _snapshot;
            var newChars = new Dictionary<string, float[]>(snap.Characters, StringComparer.OrdinalIgnoreCase);
            var newCounts = new Dictionary<string, int>(snap.ImageCounts, StringComparer.OrdinalIgnoreCase);
            newChars.Remove(characterName);
            newCounts.Remove(characterName);
            _snapshot = new CharacterStoreSnapshot(
                Guid.NewGuid().ToString("N"),
                newChars,
                newCounts,
                _embeddingDim);
        }
    }

    public void Add(string characterName, float[] embedding, int imageCount = 1)
    {
        if (embedding.Length != _embeddingDim)
        {
            AppLogger.Warn($"Character embedding dimension mismatch: expected={_embeddingDim} got={embedding.Length}");
            return;
        }

        var normalized = new float[_embeddingDim];
        Array.Copy(embedding, normalized, _embeddingDim);
        Normalize(normalized);

        lock (_writeLock)
        {
            var snap = _snapshot;
            var newChars = new Dictionary<string, float[]>(snap.Characters, StringComparer.OrdinalIgnoreCase);
            var newCounts = new Dictionary<string, int>(snap.ImageCounts, StringComparer.OrdinalIgnoreCase);

            newChars[characterName] = normalized;
            newCounts[characterName] = imageCount;

            _snapshot = new CharacterStoreSnapshot(
                Guid.NewGuid().ToString("N"),
                newChars,
                newCounts,
                _embeddingDim);
        }
    }

    /// <summary>余弦相似度搜索。查询全程持有独立快照引用，不受并发替换影响。</summary>
    public IReadOnlyList<(string CharacterName, double Similarity)> SearchTop(
        float[] queryEmbedding,
        double minSimilarity = 0.35,
        int maxResults = 1)
    {
        var snap = _snapshot;
        var chars = snap.Characters;
        if (chars.Count == 0 || maxResults <= 0)
            return Array.Empty<(string CharacterName, double Similarity)>();

        var normalized = new float[queryEmbedding.Length];
        Array.Copy(queryEmbedding, normalized, queryEmbedding.Length);
        Normalize(normalized);

        return chars
            .Select(kv => (CharacterName: kv.Key, Similarity: CosineSimilarity(normalized, kv.Value)))
            .Where(m => m.Similarity >= minSimilarity)
            .OrderByDescending(m => m.Similarity)
            .Take(maxResults)
            .ToList();
    }

    public void Save(string path)
    {
        var snap = _snapshot;
        SaveSnapshotToFile(snap, path);
    }

    public static void SaveSnapshotToFile(CharacterStoreSnapshot snapshot, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var bw = new BinaryWriter(fs);

        bw.Write(1);
        bw.Write(snapshot.Characters.Count);
        bw.Write(snapshot.EmbeddingDim);

        foreach (var (name, emb) in snapshot.Characters)
        {
            bw.Write(name);
            bw.Write(snapshot.ImageCounts.GetValueOrDefault(name, 1));
            for (int i = 0; i < snapshot.EmbeddingDim; i++)
                bw.Write(emb[i]);
        }

        AppLogger.Info($"Character embedding store snapshot saved: {path} version={snapshot.VersionId} characters={snapshot.Characters.Count}");
    }

    public void Load(string path)
    {
        if (!File.Exists(path))
        {
            AppLogger.Info($"Character embedding store not found: {path}; custom character recognition disabled until built");
            return;
        }

        try
        {
            var snap = LoadSnapshotFromFile(path);
            SwapSnapshot(snap);
            AppLogger.Info($"Character embedding store loaded: {path} characters={snap.Characters.Count} dim={snap.EmbeddingDim}");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Failed to load character embedding store: {path} error={ex.Message}");
            throw;
        }
    }

    public static CharacterStoreSnapshot LoadSnapshotFromFile(string path, string? versionId = null)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Character store file not found: {path}", path);

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var br = new BinaryReader(fs);

        var version = br.ReadInt32();
        var count = br.ReadInt32();
        var dim = br.ReadInt32();

        var chars = new Dictionary<string, float[]>(count, StringComparer.OrdinalIgnoreCase);
        var counts = new Dictionary<string, int>(count, StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < count; i++)
        {
            var name = br.ReadString();
            var imgCount = version >= 1 ? br.ReadInt32() : -1;

            var emb = new float[dim];
            for (int j = 0; j < dim; j++)
                emb[j] = br.ReadSingle();
            Normalize(emb);
            chars[name] = emb;
            counts[name] = imgCount;
        }

        return new CharacterStoreSnapshot(versionId ?? Path.GetFileName(path), chars, counts, dim);
    }

    public void LoadVersioned(string storeDir)
    {
        if (!Directory.Exists(storeDir))
        {
            AppLogger.Info($"Character version store directory not found: {storeDir}");
            return;
        }

        var pointerFile = Path.Combine(storeDir, "current.version");
        if (File.Exists(pointerFile))
        {
            try
            {
                var activeVersion = File.ReadAllText(pointerFile).Trim();
                if (!string.IsNullOrEmpty(activeVersion))
                {
                    var versionBin = Path.Combine(storeDir, activeVersion, "character_embeddings.bin");
                    if (File.Exists(versionBin))
                    {
                        var snap = LoadSnapshotFromFile(versionBin, activeVersion);
                        SwapSnapshot(snap);
                        AppLogger.Info($"Character embedding store version pointer loaded: version={activeVersion} count={snap.Characters.Count}");
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Failed to read character store current.version: {ex.Message}");
            }
        }

        // Fallback to latest v_* directory
        try
        {
            var versionDirs = Directory.GetDirectories(storeDir, "v_*")
                .OrderByDescending(d => Directory.GetCreationTimeUtc(d))
                .ToList();

            foreach (var vDir in versionDirs)
            {
                var bin = Path.Combine(vDir, "character_embeddings.bin");
                if (File.Exists(bin))
                {
                    try
                    {
                        var vName = Path.GetFileName(vDir);
                        var snap = LoadSnapshotFromFile(bin, vName);
                        SwapSnapshot(snap);
                        AppLogger.Info($"Fallback successfully loaded valid character version: {vName}");
                        return;
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Error during fallback scan for character store versions: {ex.Message}");
        }

        // Final fallback: check storeDir/character_embeddings.bin
        var rootBin = Path.Combine(storeDir, "character_embeddings.bin");
        if (File.Exists(rootBin))
        {
            Load(rootBin);
        }
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        double dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            normA += (double)a[i] * a[i];
            normB += (double)b[i] * b[i];
        }
        if (normA < 1e-10 || normB < 1e-10) return 0;
        return dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }

    private static void Normalize(float[] v)
    {
        double norm = 0;
        for (int i = 0; i < v.Length; i++)
            norm += (double)v[i] * v[i];
        if (norm < 1e-10) return;
        float inv = (float)(1.0 / Math.Sqrt(norm));
        for (int i = 0; i < v.Length; i++)
            v[i] *= inv;
    }
}
