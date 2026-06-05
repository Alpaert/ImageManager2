using ImageManager.Common.Helpers;

namespace ImageManager.Infrastructure.Services;

/// <summary>
/// 画师嵌入向量存储。每个画师存储一个 1024维嵌入（多张参考图取均值）。
/// 搜索时用余弦相似度找最近的画师。
/// 持久化到二进制文件，支持增量添加（新画师无需重训模型）。
/// </summary>
public class ArtistEmbeddingStore
{
    private readonly Dictionary<string, float[]> _artists = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _imageCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _embeddingDim;

    public int Count => _artists.Count;
    public IReadOnlyDictionary<string, float[]> Artists => _artists;

    public ArtistEmbeddingStore(int embeddingDim = 1024)
    {
        _embeddingDim = embeddingDim;
    }

    /// <summary>获取画师的参考图数量，-1 表示未知（旧格式）</summary>
    public int GetImageCount(string artistName)
        => _imageCounts.TryGetValue(artistName, out var c) ? c : -1;

    public void Clear()
    {
        _artists.Clear();
        _imageCounts.Clear();
    }

    /// <summary>移除画师</summary>
    public void Remove(string artistName)
    {
        _artists.Remove(artistName);
        _imageCounts.Remove(artistName);
    }

    /// <summary>添加或更新画师嵌入（多张图调用此方法后自动取均值）</summary>
    public void Add(string artistName, float[] embedding, int imageCount = 1)
    {
        if (embedding.Length != _embeddingDim)
        {
            AppLogger.Warn($"嵌入维度不匹配: expected={_embeddingDim} got={embedding.Length}");
            return;
        }

        if (_artists.TryGetValue(artistName, out var existing))
        {
            // 增量平均：new_avg = (old * n + new) / (n + 1)
            float factor = (float)imageCount / (imageCount + 1);
            for (int i = 0; i < _embeddingDim; i++)
                existing[i] = existing[i] * factor + embedding[i] / (imageCount + 1);
        }
        else
        {
            var copy = new float[_embeddingDim];
            Array.Copy(embedding, copy, _embeddingDim);
            _artists[artistName] = copy;
        }
        _imageCounts[artistName] = imageCount;
    }

    /// <summary>余弦相似度搜索，返回最匹配的画师及相似度</summary>
    public (string artistName, double similarity)? Search(float[] queryEmbedding, double minSimilarity = 0.6)
    {
        if (_artists.Count == 0) return null;

        // Copy to avoid mutating caller's array
        var normalized = new float[queryEmbedding.Length];
        Array.Copy(queryEmbedding, normalized, queryEmbedding.Length);
        Normalize(normalized);

        string? bestArtist = null;
        double bestSim = -1;

        foreach (var (artistName, emb) in _artists)
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

        AppLogger.Tag("Artist", $"搜索命中 artist={bestArtist} sim={bestSim:F4} dbCount={_artists.Count}");
        return (bestArtist, bestSim);
    }

    /// <summary>保存到二进制文件（含 imageCount，格式 v2）</summary>
    public void Save(string path)
    {
        using var fs = new FileStream(path, FileMode.Create);
        using var bw = new BinaryWriter(fs);

        bw.Write(2);                    // format version
        bw.Write(_artists.Count);
        bw.Write(_embeddingDim);

        foreach (var (name, emb) in _artists)
        {
            bw.Write(name);
            bw.Write(_imageCounts.GetValueOrDefault(name, 1));
            for (int i = 0; i < _embeddingDim; i++)
                bw.Write(emb[i]);
        }

        AppLogger.Info($"画师嵌入库已保存 v2: {path} artists={_artists.Count}");
    }

    /// <summary>从二进制文件加载（兼容 v1 旧格式）</summary>
    public void Load(string path)
    {
        if (!File.Exists(path))
        {
            AppLogger.Warn($"画师嵌入库文件不存在: {path}，将使用空库");
            return;
        }

        using var fs = new FileStream(path, FileMode.Open);
        using var br = new BinaryReader(fs);

        // Detect format: first int32 is count in v1, version in v2
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

        for (int i = 0; i < count; i++)
        {
            var name = br.ReadString();
            int imgCount = -1;
            if (version >= 2)
                imgCount = br.ReadInt32();

            var emb = new float[dim];
            for (int j = 0; j < dim; j++)
                emb[j] = br.ReadSingle();
            _artists[name] = emb;
            _imageCounts[name] = imgCount;
        }

        AppLogger.Info($"画师嵌入库已加载 v{version}: {path} artists={_artists.Count} dim={dim}");
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
