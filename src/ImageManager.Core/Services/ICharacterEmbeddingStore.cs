using System.Collections.Generic;
using ImageManager.Core.Models;

namespace ImageManager.Core.Services;

public interface ICharacterEmbeddingStore
{
    int Count { get; }
    IReadOnlyDictionary<string, float[]> Characters { get; }
    CharacterStoreSnapshot CurrentSnapshot { get; }
    void SwapSnapshot(CharacterStoreSnapshot newSnapshot);
    int GetImageCount(string characterName);
    void Clear();
    void Remove(string characterName);
    void Add(string characterName, float[] embedding, int imageCount = 1);
    IReadOnlyList<(string CharacterName, double Similarity)> SearchTop(
        float[] queryEmbedding,
        double minSimilarity = 0.35,
        int maxResults = 1);
    void Save(string path);
    void Load(string path);
    void LoadVersioned(string storeDir);
}
