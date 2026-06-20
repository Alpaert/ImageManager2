using ImageManager.Common.Helpers;

namespace ImageManager.Infrastructure.Services;

/// <summary>
/// Stores one mean PixAI embedding per custom character and searches by cosine similarity.
/// </summary>
public class CharacterEmbeddingStore
{
    private readonly Dictionary<string, float[]> _characters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _imageCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _embeddingDim;

    public int Count => _characters.Count;
    public IReadOnlyDictionary<string, float[]> Characters => _characters;

    public CharacterEmbeddingStore(int embeddingDim = 1024)
    {
        _embeddingDim = embeddingDim;
    }

    public int GetImageCount(string characterName)
        => _imageCounts.TryGetValue(characterName, out var c) ? c : -1;

    public void Clear()
    {
        _characters.Clear();
        _imageCounts.Clear();
    }

    public void Remove(string characterName)
    {
        _characters.Remove(characterName);
        _imageCounts.Remove(characterName);
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

        _characters[characterName] = normalized;
        _imageCounts[characterName] = imageCount;
    }

    public IReadOnlyList<(string CharacterName, double Similarity)> SearchTop(
        float[] queryEmbedding,
        double minSimilarity = 0.35,
        int maxResults = 1)
    {
        if (_characters.Count == 0 || maxResults <= 0)
            return Array.Empty<(string CharacterName, double Similarity)>();

        var normalized = new float[queryEmbedding.Length];
        Array.Copy(queryEmbedding, normalized, queryEmbedding.Length);
        Normalize(normalized);

        return _characters
            .Select(kv => (CharacterName: kv.Key, Similarity: CosineSimilarity(normalized, kv.Value)))
            .Where(m => m.Similarity >= minSimilarity)
            .OrderByDescending(m => m.Similarity)
            .Take(maxResults)
            .ToList();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var fs = new FileStream(path, FileMode.Create);
        using var bw = new BinaryWriter(fs);

        bw.Write(1);
        bw.Write(_characters.Count);
        bw.Write(_embeddingDim);

        foreach (var (name, emb) in _characters)
        {
            bw.Write(name);
            bw.Write(_imageCounts.GetValueOrDefault(name, 1));
            for (int i = 0; i < _embeddingDim; i++)
                bw.Write(emb[i]);
        }

        AppLogger.Info($"Character embedding store saved: {path} characters={_characters.Count}");
    }

    public void Load(string path)
    {
        _characters.Clear();
        _imageCounts.Clear();

        if (!File.Exists(path))
        {
            AppLogger.Info($"Character embedding store not found: {path}; custom character recognition disabled until built");
            return;
        }

        using var fs = new FileStream(path, FileMode.Open);
        using var br = new BinaryReader(fs);

        var version = br.ReadInt32();
        var count = br.ReadInt32();
        var dim = br.ReadInt32();

        if (dim != _embeddingDim)
            throw new InvalidDataException($"Character embedding dimension mismatch: file={dim} expected={_embeddingDim}");

        for (int i = 0; i < count; i++)
        {
            var name = br.ReadString();
            var imgCount = version >= 1 ? br.ReadInt32() : -1;

            var emb = new float[dim];
            for (int j = 0; j < dim; j++)
                emb[j] = br.ReadSingle();
            Normalize(emb);
            _characters[name] = emb;
            _imageCounts[name] = imgCount;
        }

        AppLogger.Info($"Character embedding store loaded: {path} characters={_characters.Count} dim={dim}");
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
