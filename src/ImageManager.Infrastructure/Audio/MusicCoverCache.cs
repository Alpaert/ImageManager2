using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ImageManager.Infrastructure.Audio;

internal readonly record struct MusicSourceFingerprint(
    long Length,
    long LastWriteUtcTicks,
    long ArtworkLength,
    long ArtworkLastWriteUtcTicks,
    string? ArtworkFileName);
internal enum MusicCoverCacheSaveResult { Saved, SourceChanged, WriteFailed }

internal sealed class MusicCoverCache
{
    private const string CacheVersion = "v1";
    private readonly string _cacheRoot;

    private sealed record Metadata(
        long Length,
        long LastWriteUtcTicks,
        int Width,
        int Height,
        bool NoArtwork,
        long ArtworkLength = 0,
        long ArtworkLastWriteUtcTicks = 0,
        string? ArtworkFileName = null);
    internal readonly record struct LoadResult(byte[]? Cover, int Width, int Height, bool IsFinalWithoutThumbnail, bool IsStale);

    public MusicCoverCache(string cacheRoot) => _cacheRoot = cacheRoot;

    public LoadResult Load(string filePath)
    {
        if (!TryGetFingerprint(filePath, out var fingerprint))
            return default;

        var metadata = LoadMetadata(filePath);
        if (metadata == null)
            return default;
        if (metadata.Length != fingerprint.Length ||
            metadata.LastWriteUtcTicks != fingerprint.LastWriteUtcTicks ||
            metadata.ArtworkLength != fingerprint.ArtworkLength ||
            metadata.ArtworkLastWriteUtcTicks != fingerprint.ArtworkLastWriteUtcTicks ||
            !string.Equals(metadata.ArtworkFileName, fingerprint.ArtworkFileName, StringComparison.OrdinalIgnoreCase))
            return new(null, 0, 0, false, true);
        if (metadata.NoArtwork)
            return new(null, 0, 0, true, false);

        try
        {
            var coverPath = GetCoverPath(filePath);
            if (!File.Exists(coverPath))
                return new(null, 0, 0, false, true);
            var cover = File.ReadAllBytes(coverPath);
            return cover.Length > 0
                ? new(cover, metadata.Width, metadata.Height, false, false)
                : new(null, 0, 0, false, true);
        }
        catch
        {
            return default;
        }
    }

    public MusicCoverCacheSaveResult SaveCover(
        string filePath, MusicSourceFingerprint fingerprint, byte[] jpeg, int width, int height)
    {
        if (!IsCurrent(filePath, fingerprint))
            return MusicCoverCacheSaveResult.SourceChanged;
        try
        {
            var coverPath = GetCoverPath(filePath);
            Directory.CreateDirectory(Path.GetDirectoryName(coverPath)!);
            File.WriteAllBytes(coverPath, jpeg);
            if (!IsCurrent(filePath, fingerprint))
            {
                Delete(filePath);
                return MusicCoverCacheSaveResult.SourceChanged;
            }

            SaveMetadata(filePath, CreateMetadata(fingerprint, width, height, noArtwork: false));
            if (IsCurrent(filePath, fingerprint))
                return MusicCoverCacheSaveResult.Saved;

            Delete(filePath);
            return MusicCoverCacheSaveResult.SourceChanged;
        }
        catch
        {
            Delete(filePath);
            return MusicCoverCacheSaveResult.WriteFailed;
        }
    }

    public MusicCoverCacheSaveResult SaveNoArtwork(string filePath, MusicSourceFingerprint fingerprint)
    {
        if (!IsCurrent(filePath, fingerprint))
            return MusicCoverCacheSaveResult.SourceChanged;
        try
        {
            DeleteFile(GetCoverPath(filePath));
            SaveMetadata(filePath, CreateMetadata(fingerprint, 0, 0, noArtwork: true));
            if (IsCurrent(filePath, fingerprint))
                return MusicCoverCacheSaveResult.Saved;

            Delete(filePath);
            return MusicCoverCacheSaveResult.SourceChanged;
        }
        catch
        {
            Delete(filePath);
            return MusicCoverCacheSaveResult.WriteFailed;
        }
    }

    public void Delete(string filePath)
    {
        DeleteFile(GetCoverPath(filePath));
        DeleteFile(GetMetadataPath(filePath));
    }

    public void Move(string oldPath, string newPath)
    {
        MoveFile(GetCoverPath(oldPath), GetCoverPath(newPath));
        MoveFile(GetMetadataPath(oldPath), GetMetadataPath(newPath));
    }

    private void SaveMetadata(string filePath, Metadata metadata)
    {
        var path = GetMetadataPath(filePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(metadata));
    }

    private Metadata? LoadMetadata(string filePath)
    {
        try
        {
            var path = GetMetadataPath(filePath);
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Metadata>(File.ReadAllText(path))
                : null;
        }
        catch { return null; }
    }

    private string GetCoverPath(string filePath) => Path.Combine(GetFolderPath(filePath), GetFileHash(filePath) + ".jpg");
    private string GetMetadataPath(string filePath) => Path.Combine(GetFolderPath(filePath), GetFileHash(filePath) + ".json");
    private string GetFolderPath(string filePath) => Path.Combine(_cacheRoot, "music_covers", CacheVersion, GetFolderHash(filePath));

    public static bool TryGetFingerprint(string filePath, out MusicSourceFingerprint fingerprint)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists) { fingerprint = default; return false; }
            var artworkPath = MusicArtworkResolver.FindSidecarArtwork(filePath);
            if (artworkPath == null)
            {
                fingerprint = new(info.Length, info.LastWriteTimeUtc.Ticks, 0, 0, null);
                return true;
            }

            var artwork = new FileInfo(artworkPath);
            fingerprint = new(
                info.Length,
                info.LastWriteTimeUtc.Ticks,
                artwork.Exists ? artwork.Length : 0,
                artwork.Exists ? artwork.LastWriteTimeUtc.Ticks : 0,
                artwork.Exists ? artwork.Name : null);
            return true;
        }
        catch { fingerprint = default; return false; }
    }

    private static bool IsCurrent(string filePath, MusicSourceFingerprint expected) =>
        TryGetFingerprint(filePath, out var actual) && actual == expected;

    private static Metadata CreateMetadata(
        MusicSourceFingerprint fingerprint,
        int width,
        int height,
        bool noArtwork) =>
        new(
            fingerprint.Length,
            fingerprint.LastWriteUtcTicks,
            width,
            height,
            noArtwork,
            fingerprint.ArtworkLength,
            fingerprint.ArtworkLastWriteUtcTicks,
            fingerprint.ArtworkFileName);

    private static string GetFolderHash(string filePath) => Hash(Path.GetDirectoryName(filePath) ?? "_root")[..8];
    private static string GetFileHash(string filePath) => Hash(filePath);
    private static string Hash(string value) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant()))).ToLowerInvariant();

    private static void DeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void MoveFile(string source, string destination)
    {
        try
        {
            if (!File.Exists(source)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (File.Exists(destination)) File.Delete(destination);
            File.Move(source, destination);
        }
        catch { }
    }
}
