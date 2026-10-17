using System.Collections.Generic;
using ImageManager.Core.Models;

namespace ImageManager.Core.Services;

public interface IArtistEmbeddingStore
{
    int Count { get; }
    IReadOnlyDictionary<string, float[]> Artists { get; }
    ArtistStoreSnapshot CurrentSnapshot { get; }
    void SwapSnapshot(ArtistStoreSnapshot newSnapshot);
    int GetImageCount(string artistName);
    void Clear();
    void Remove(string artistName);
    void Add(string artistName, float[] embedding, int imageCount = 1);
    (string artistName, double similarity)? Search(float[] queryEmbedding, double minSimilarity = 0.6);
    void Save(string path);
    void Load(string path);
    void LoadVersioned(string storeDir);
}
