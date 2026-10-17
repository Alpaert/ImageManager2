using System;
using System.Collections.Generic;
using System.IO;
using ImageManager.Core.Models;
using ImageManager.Infrastructure.Services;
using Xunit;

namespace Infrastructure.IntegrationTests;

public sealed class EmbeddingStoreAtomicTests : IDisposable
{
    private readonly string _testRoot;

    public EmbeddingStoreAtomicTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "ImageManager2-StoreAtomicTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testRoot))
                Directory.Delete(_testRoot, true);
        }
        catch { }
    }

    [Fact]
    public void SwapSnapshot_ReplacesStateAtomicallyWithoutCorruptingReads()
    {
        var store = new ArtistEmbeddingStore(embeddingDim: 4);

        var snap1Artists = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["ArtistA"] = new float[] { 1f, 0f, 0f, 0f },
            ["ArtistB"] = new float[] { 0f, 1f, 0f, 0f }
        };
        var snap1Counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["ArtistA"] = 1,
            ["ArtistB"] = 2
        };
        var snap1 = new ArtistStoreSnapshot("v1", snap1Artists, snap1Counts, 4);

        store.SwapSnapshot(snap1);

        Assert.Equal(2, store.Count);
        Assert.Equal("v1", store.CurrentSnapshot.VersionId);
        Assert.True(store.Artists.ContainsKey("ArtistA"));
        Assert.Equal(1, store.GetImageCount("ArtistA"));

        // Search returns closest match
        var match = store.Search(new float[] { 0.9f, 0.1f, 0f, 0f }, minSimilarity: 0.5);
        Assert.NotNull(match);
        Assert.Equal("ArtistA", match!.Value.artistName);

        // Swap to snap2
        var snap2Artists = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["ArtistC"] = new float[] { 0f, 0f, 1f, 0f }
        };
        var snap2Counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["ArtistC"] = 5
        };
        var snap2 = new ArtistStoreSnapshot("v2", snap2Artists, snap2Counts, 4);

        store.SwapSnapshot(snap2);

        Assert.Equal(1, store.Count);
        Assert.Equal("v2", store.CurrentSnapshot.VersionId);
        Assert.False(store.Artists.ContainsKey("ArtistA"));
        Assert.True(store.Artists.ContainsKey("ArtistC"));
        Assert.Equal(5, store.GetImageCount("ArtistC"));
    }

    [Fact]
    public void SaveAndLoadSnapshot_PreservesAllVectorDataAndCounts()
    {
        var artists = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["ArtX"] = new float[] { 0.5f, 0.5f, 0.5f, 0.5f }
        };
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["ArtX"] = 42
        };
        var snap = new ArtistStoreSnapshot("v_test", artists, counts, 4);

        var filePath = Path.Combine(_testRoot, "artist_test.bin");
        ArtistEmbeddingStore.SaveSnapshotToFile(snap, filePath);

        var loaded = ArtistEmbeddingStore.LoadSnapshotFromFile(filePath, "v_loaded");
        Assert.Equal("v_loaded", loaded.VersionId);
        Assert.Equal(4, loaded.EmbeddingDim);
        Assert.Single(loaded.Artists);
        Assert.Equal(42, loaded.ImageCounts["ArtX"]);
        Assert.Equal(0.5f, loaded.Artists["ArtX"][0]);
    }

    [Fact]
    public void LoadVersioned_WithPointer_LoadsCorrectVersion()
    {
        var storeDir = Path.Combine(_testRoot, "artist_store");
        var v1Dir = Path.Combine(storeDir, "v_100");
        var v2Dir = Path.Combine(storeDir, "v_200");
        Directory.CreateDirectory(v1Dir);
        Directory.CreateDirectory(v2Dir);

        var store = new ArtistEmbeddingStore(embeddingDim: 4);

        // Write v1
        var snap1 = new ArtistStoreSnapshot("v_100",
            new Dictionary<string, float[]> { ["V1Artist"] = new float[] { 1, 0, 0, 0 } },
            new Dictionary<string, int> { ["V1Artist"] = 10 }, 4);
        ArtistEmbeddingStore.SaveSnapshotToFile(snap1, Path.Combine(v1Dir, "artist_embeddings.bin"));

        // Write v2
        var snap2 = new ArtistStoreSnapshot("v_200",
            new Dictionary<string, float[]> { ["V2Artist"] = new float[] { 0, 1, 0, 0 } },
            new Dictionary<string, int> { ["V2Artist"] = 20 }, 4);
        ArtistEmbeddingStore.SaveSnapshotToFile(snap2, Path.Combine(v2Dir, "artist_embeddings.bin"));

        // Point to v2
        File.WriteAllText(Path.Combine(storeDir, "current.version"), "v_200");

        store.LoadVersioned(storeDir);

        Assert.Equal("v_200", store.CurrentSnapshot.VersionId);
        Assert.True(store.Artists.ContainsKey("V2Artist"));
        Assert.False(store.Artists.ContainsKey("V1Artist"));
    }

    [Fact]
    public void LoadVersioned_WithCorruptedPointer_FallsBackToLatestValidVersion()
    {
        var storeDir = Path.Combine(_testRoot, "artist_fallback_store");
        var v1Dir = Path.Combine(storeDir, "v_001");
        Directory.CreateDirectory(v1Dir);

        var store = new ArtistEmbeddingStore(embeddingDim: 4);
        var snap1 = new ArtistStoreSnapshot("v_001",
            new Dictionary<string, float[]> { ["FallbackArtist"] = new float[] { 1, 0, 0, 0 } },
            new Dictionary<string, int> { ["FallbackArtist"] = 1 }, 4);
        ArtistEmbeddingStore.SaveSnapshotToFile(snap1, Path.Combine(v1Dir, "artist_embeddings.bin"));

        // Write corrupt current.version pointer
        File.WriteAllText(Path.Combine(storeDir, "current.version"), "v_non_existent_or_corrupt");

        store.LoadVersioned(storeDir);

        // Fallback should load v_001
        Assert.Equal("v_001", store.CurrentSnapshot.VersionId);
        Assert.True(store.Artists.ContainsKey("FallbackArtist"));
    }

    [Fact]
    public void CharacterStore_SwapSnapshot_AndSearch_WorksConsistently()
    {
        var store = new CharacterEmbeddingStore(embeddingDim: 4);

        var characters = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Hero"] = new float[] { 0f, 1f, 0f, 0f },
            ["Villain"] = new float[] { 0f, 0f, 1f, 0f }
        };
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Hero"] = 3,
            ["Villain"] = 5
        };
        var snap = new CharacterStoreSnapshot("c_v1", characters, counts, 4);

        store.SwapSnapshot(snap);

        Assert.Equal(2, store.Count);
        Assert.Equal("c_v1", store.CurrentSnapshot.VersionId);

        var results = store.SearchTop(new float[] { 0.1f, 0.9f, 0f, 0f }, minSimilarity: 0.6);
        Assert.Single(results);
        Assert.Equal("Hero", results[0].CharacterName);
    }
}
