using System;
using System.Collections.Generic;
using System.IO;
using ImageManager.Common.Helpers;
using ImageManager.Core.Models;
using ImageManager.Core.Services;

namespace ImageManager.Infrastructure.Services;

/// <summary>
/// 画师嵌入向量存储。每个画师存储一个 1024维嵌入（多张参考图取均值）。
/// 搜索时用余弦相似度找最近的画师。
/// 持久化到二进制文件，支持版本化快照发布与原子替换。
/// </summary>
public class ArtistEmbeddingStore : IArtistEmbeddingStore
{
    private volatile ArtistStoreSnapshot _snapshot;
    private readonly object _writeLock = new();
    private readonly int _embeddingDim;

    public ArtistStoreSnapshot CurrentSnapshot => _snapshot;
    public int Count => _snapshot.Artists.Count;
    public IReadOnlyDictionary<string, float[]> Artists => _snapshot.Artists;

    public ArtistEmbeddingStore(int embeddingDim = 1024)
    {
        _embeddingDim = embeddingDim;
        _snapshot = new ArtistStoreSnapshot(
            "empty",
            new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            embeddingDim);
    }

    /// <summary>原子切换内存快照</summary>
    public void SwapSnapshot(ArtistStoreSnapshot newSnapshot)
    {
        _snapshot = newSnapshot ?? throw new ArgumentNullException(nameof(newSnapshot));
        AppLogger.Info($"画师嵌入库快照已原子替换: version={newSnapshot.VersionId} count={newSnapshot.Artists.Count}");
    }

    /// <summary>获取画师的参考图数量，-1 表示未知（旧格式）</summary>
    public int GetImageCount(string artistName)
        => _snapshot.ImageCounts.TryGetValue(artistName, out var c) ? c : -1;

    public void Clear()
    {
        lock (_writeLock)
        {
            _snapshot = new ArtistStoreSnapshot(
                Guid.NewGuid().ToString("N"),
                new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                _embeddingDim);
        }
    }

    /// <summary>移除画师</summary>
    public void Remove(string artistName)
    {
        lock (_writeLock)
        {
            var snap = _snapshot;
            var newArtists = new Dictionary<string, float[]>(snap.Artists, StringComparer.OrdinalIgnoreCase);
            var newCounts = new Dictionary<string, int>(snap.ImageCounts, StringComparer.OrdinalIgnoreCase);
            newArtists.Remove(artistName);
            newCounts.Remove(artistName);
            _snapshot = new ArtistStoreSnapshot(
                Guid.NewGuid().ToString("N"),
                newArtists,
                newCounts,
                _embeddingDim);
        }
    }

    /// <summary>添加或更新画师嵌入（多张图调用此方法后自动取均值）</summary>
    public void Add(string artistName, float[] embedding, int imageCount = 1)
    {
        if (embedding.Length != _embeddingDim)
        {
            AppLogger.Warn($"嵌入维度不匹配: expected={_embeddingDim} got={embedding.Length}");
            return;
        }

        lock (_writeLock)
        {
            var snap = _snapshot;
            var newArtists = new Dictionary<string, float[]>(snap.Artists, StringComparer.OrdinalIgnoreCase);
            var newCounts = new Dictionary<string, int>(snap.ImageCounts, StringComparer.OrdinalIgnoreCase);

            if (newArtists.TryGetValue(artistName, out var existing))
            {
                var updated = new float[_embeddingDim];
                Array.Copy(existing, updated, _embeddingDim);
                float factor = (float)imageCount / (imageCount + 1);
                for (int i = 0; i < _embeddingDim; i++)
                    updated[i] = updated[i] * factor + embedding[i] / (imageCount + 1);
                newArtists[artistName] = updated;
            }
            else
            {
                var copy = new float[_embeddingDim];
                Array.Copy(embedding, copy, _embeddingDim);
                newArtists[artistName] = copy;
            }
            newCounts[artistName] = imageCount;

            _snapshot = new ArtistStoreSnapshot(
                Guid.NewGuid().ToString("N"),
                newArtists,
                newCounts,
                _embeddingDim);
        }
    }

    /// <summary>余弦相似度搜索，返回最匹配的画师及相似度。查询全程持有独立快照引用，不受并发替换影响。</summary>
    public (string artistName, double similarity)? Search(float[] queryEmbedding, double minSimilarity = 0.6)
    {
        var snap = _snapshot;
        var artists = snap.Artists;
        if (artists.Count == 0) return null;

        var normalized = new float[queryEmbedding.Length];
        Array.Copy(queryEmbedding, normalized, queryEmbedding.Length);
        Normalize(normalized);

        string? bestArtist = null;
        double bestSim = -1;

        foreach (var (artistName, emb) in artists)
        {
            double sim = CosineSimilarity(normalized, emb);
            if (sim > bestSim)
            {
                bestSim = sim;
                bestArtist = artistName;
            }
        }

        if (bestSim < minSimilarity || bestArtist == null)
            return null;

        return (bestArtist, bestSim);
    }

    /// <summary>保存当前快照到二进制文件（含 imageCount，格式 v2）</summary>
    public void Save(string path)
    {
        var snap = _snapshot;
        SaveSnapshotToFile(snap, path);
    }

    public static void SaveSnapshotToFile(ArtistStoreSnapshot snapshot, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var bw = new BinaryWriter(fs);

        bw.Write(2);                    // format version
        bw.Write(snapshot.Artists.Count);
        bw.Write(snapshot.EmbeddingDim);

        foreach (var (name, emb) in snapshot.Artists)
        {
            bw.Write(name);
            bw.Write(snapshot.ImageCounts.GetValueOrDefault(name, 1));
            for (int i = 0; i < snapshot.EmbeddingDim; i++)
                bw.Write(emb[i]);
        }

        AppLogger.Info($"画师嵌入库快照已保存: {path} version={snapshot.VersionId} artists={snapshot.Artists.Count}");
    }

    /// <summary>从二进制文件加载快照并原子替换当前内存对象（兼容 v1 旧格式）</summary>
    public void Load(string path)
    {
        if (!File.Exists(path))
        {
            AppLogger.Warn($"画师嵌入库文件不存在: {path}，将使用空库");
            return;
        }

        try
        {
            var snap = LoadSnapshotFromFile(path);
            SwapSnapshot(snap);
            AppLogger.Info($"画师嵌入库已加载: {path} artists={snap.Artists.Count} dim={snap.EmbeddingDim}");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"加载画师嵌入库失败: {path} error={ex.Message}");
            throw;
        }
    }

    public static ArtistStoreSnapshot LoadSnapshotFromFile(string path, string? versionId = null)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"画师库文件未找到: {path}", path);

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var br = new BinaryReader(fs);

        int first = br.ReadInt32();
        int count, dim, version;
        if (first == 2)
        {
            version = 2;
            count = br.ReadInt32();
            dim = br.ReadInt32();
        }
        else
        {
            version = 1;
            count = first;
            dim = br.ReadInt32();
        }

        var artists = new Dictionary<string, float[]>(count, StringComparer.OrdinalIgnoreCase);
        var counts = new Dictionary<string, int>(count, StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < count; i++)
        {
            var name = br.ReadString();
            int imgCount = -1;
            if (version >= 2)
                imgCount = br.ReadInt32();

            var emb = new float[dim];
            for (int j = 0; j < dim; j++)
                emb[j] = br.ReadSingle();
            artists[name] = emb;
            counts[name] = imgCount;
        }

        return new ArtistStoreSnapshot(versionId ?? Path.GetFileName(path), artists, counts, dim);
    }

    /// <summary>
    /// 支持从版本化目录加载：先检查 current.version 指针文件，再加载对应版本；若指针不存在或损坏则优雅回退。
    /// </summary>
    public void LoadVersioned(string storeDir)
    {
        if (!Directory.Exists(storeDir))
        {
            AppLogger.Warn($"画师版本库目录不存在: {storeDir}");
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
                    var versionBin = Path.Combine(storeDir, activeVersion, "artist_embeddings.bin");
                    if (File.Exists(versionBin))
                    {
                        var snap = LoadSnapshotFromFile(versionBin, activeVersion);
                        SwapSnapshot(snap);
                        AppLogger.Info($"画师嵌入库版本指针加载成功: version={activeVersion} artists={snap.Artists.Count}");
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"读取画师当前版本指针失败: {ex.Message}，尝试回退历史版本或旧格式");
            }
        }

        // 回退检查：检查最新的 v_* 目录
        try
        {
            var versionDirs = Directory.GetDirectories(storeDir, "v_*")
                .OrderByDescending(d => Directory.GetCreationTimeUtc(d))
                .ToList();

            foreach (var vDir in versionDirs)
            {
                var bin = Path.Combine(vDir, "artist_embeddings.bin");
                if (File.Exists(bin))
                {
                    try
                    {
                        var vName = Path.GetFileName(vDir);
                        var snap = LoadSnapshotFromFile(bin, vName);
                        SwapSnapshot(snap);
                        AppLogger.Info($"回退并成功加载有效历史版本: {vName}");
                        return;
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"回退扫描历史版本异常: {ex.Message}");
        }

        // 最终回退：检查 storeDir/artist_embeddings.bin 或父级
        var rootBin = Path.Combine(storeDir, "artist_embeddings.bin");
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
