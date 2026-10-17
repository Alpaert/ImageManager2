using System;
using System.Collections.Generic;

namespace ImageManager.Core.Models;

public sealed class ArtistStoreSnapshot
{
    public string VersionId { get; }
    public IReadOnlyDictionary<string, float[]> Artists { get; }
    public IReadOnlyDictionary<string, int> ImageCounts { get; }
    public int EmbeddingDim { get; }

    public ArtistStoreSnapshot(
        string versionId,
        IReadOnlyDictionary<string, float[]> artists,
        IReadOnlyDictionary<string, int> imageCounts,
        int embeddingDim = 1024)
    {
        VersionId = versionId ?? "v0";
        Artists = artists ?? new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
        ImageCounts = imageCounts ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        EmbeddingDim = embeddingDim;
    }
}

public sealed class CharacterStoreSnapshot
{
    public string VersionId { get; }
    public IReadOnlyDictionary<string, float[]> Characters { get; }
    public IReadOnlyDictionary<string, int> ImageCounts { get; }
    public int EmbeddingDim { get; }

    public CharacterStoreSnapshot(
        string versionId,
        IReadOnlyDictionary<string, float[]> characters,
        IReadOnlyDictionary<string, int> imageCounts,
        int embeddingDim = 1024)
    {
        VersionId = versionId ?? "v0";
        Characters = characters ?? new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
        ImageCounts = imageCounts ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        EmbeddingDim = embeddingDim;
    }
}
