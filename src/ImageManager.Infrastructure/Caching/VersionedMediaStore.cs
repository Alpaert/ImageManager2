using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ImageManager.Common.Constants;
using ImageManager.Core.Models;
using ImageManager.Infrastructure.Audio;

namespace ImageManager.Infrastructure.Caching;

/// <summary>A complete encoded image and metadata are atomically committed as one envelope.</summary>
internal sealed class VersionedMediaStore(string root, string? fallbackRoot = null)
{
    internal string? FallbackRoot { get; set; } = fallbackRoot;
    internal sealed record Entry(string Version, byte[]? Data, int Width, int Height,
        bool NoArtwork = false, ArtworkOrigin Origin = ArtworkOrigin.None, string? ContentHash = null);
    private sealed class KnownState(string key)
    {
        public readonly string Key = key;
        public string? Version;
        public bool Invalidating;
        public int InvalidationCount;
        public long LastCleanupTicks;
        public LinkedListNode<KnownState>? Node;
    }
    private static readonly object KnownGate = new();
    private static readonly Dictionary<string, KnownState> Known = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<KnownState> KnownLru = new();
    private static string VolatileEpoch = "0";
    private static int BlockedInvalidations;
    private const int KnownCapacity = 8192;
    private string DirectoryFor(string path) => Path.Combine(root, "media_v2", Hash(Path.GetFullPath(path).ToUpperInvariant()));
    private string TokenPath(string path) => Path.Combine(DirectoryFor(path), "epoch");
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    // Caller holds KnownGate. Invalidating entries remain pinned; excessive concurrent invalidations fail closed.
    private static KnownState? GetState(string key)
    {
        if (Known.TryGetValue(key, out var state))
        {
            KnownLru.Remove(state.Node!);
            state.Node = KnownLru.AddFirst(state);
            return state;
        }
        if (Known.Count >= KnownCapacity)
        {
            var victim = KnownLru.Last;
            while (victim != null && victim.Value.Invalidating) victim = victim.Previous;
            if (victim == null) return null;
            Known.Remove(victim.Value.Key);
            KnownLru.Remove(victim);
        }
        state = new(key);
        state.Node = KnownLru.AddFirst(state);
        Known[key] = state;
        return state;
    }

    public string? Version(string path)
    {
        try
        {
            var tokenPath = TokenPath(path);
            KnownState? state;
            string volatileEpoch;
            lock (KnownGate)
            {
                if (BlockedInvalidations > 0) return null;
                state = GetState(tokenPath);
                if (state == null || state.Invalidating) return null;
                volatileEpoch = VolatileEpoch;
            }
            var file = new FileInfo(path);
            if (!file.Exists) return null;
            var token = File.Exists(tokenPath) ? File.ReadAllText(tokenPath) : "0";
            var identity = $"{Path.GetFullPath(path).ToUpperInvariant()}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|{token}|{volatileEpoch}";
            if (FileTypeConstants.IsMusicFile(path))
            {
                var artwork = MusicArtworkResolver.FindSidecarArtwork(path);
                if (artwork != null)
                {
                    var info = new FileInfo(artwork);
                    identity += $"|{info.FullName.ToUpperInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
                }
            }
            var version = Hash(identity);
            lock (KnownGate)
            {
                if (BlockedInvalidations > 0 || VolatileEpoch != volatileEpoch || state.Invalidating ||
                    !Known.TryGetValue(tokenPath, out var current) || !ReferenceEquals(current, state)) return null;
                state.Version = version;
            }
            return version;
        }
        catch { return null; }
    }

    public bool Invalidate(string path)
    {
        var tokenPath = TokenPath(path);
        KnownState? state;
        lock (KnownGate)
        {
            var outstanding = 0;
            // Replace object identity: a pre-invalidation stat cannot reauthorize the new entry.
            if (Known.TryGetValue(tokenPath, out var previous))
            {
                outstanding = previous.InvalidationCount;
                Known.Remove(tokenPath);
                KnownLru.Remove(previous.Node!);
            }
            state = GetState(tokenPath);
            if (state != null) { state.Invalidating = true; state.InvalidationCount = outstanding + 1; }
            else BlockedInvalidations++;
        }
        var persisted = false;
        try
        {
            using var held = Lock(path);
            AtomicWrite(tokenPath, Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N")));
            persisted = true;
            return true;
        }
        catch { return false; }
        finally
        {
            lock (KnownGate)
            {
                if (!persisted)
                {
                    // No unbounded per-path tombstones when disk is unwritable. Conservatively revoke all
                    // current-process versions; caller reports that persistence did not survive restart.
                    VolatileEpoch = Guid.NewGuid().ToString("N");
                    foreach (var known in Known.Values) known.Version = null;
                }
                if (state != null && Known.TryGetValue(tokenPath, out var current))
                {
                    current.InvalidationCount--;
                    current.Invalidating = current.InvalidationCount > 0;
                    current.Version = null;
                }
                else BlockedInvalidations--;
            }
        }
    }

    public bool IsKnownCurrent(string path, string? version)
    {
        if (version == null) return false;
        var key = TokenPath(path);
        lock (KnownGate)
            return BlockedInvalidations == 0 && Known.TryGetValue(key, out var known) &&
                !known.Invalidating && known.Version == version;
    }

    public Entry? Load(string path, string kind, string version)
    {
        var local = LoadAt(path, kind, version, DirectoryFor(path));
        if (local != null || string.IsNullOrWhiteSpace(FallbackRoot)) return local;
        // Read-only fallback is reusable only if its committed version matches this root's current identity.
        var directory = Path.Combine(FallbackRoot, "media_v2", Hash(Path.GetFullPath(path).ToUpperInvariant()));
        return LoadAt(path, kind, version, directory);
    }

    public Entry? LoadAnyWidth(string path, int preferredWidth, string version, Func<Entry, bool> validate)
    {
        var preferred = Load(path, "w" + preferredWidth, version);
        if (preferred != null && validate(preferred)) return preferred;
        var directories = new List<string> { DirectoryFor(path) };
        if (!string.IsNullOrWhiteSpace(FallbackRoot))
            directories.Add(Path.Combine(FallbackRoot, "media_v2", Hash(Path.GetFullPath(path).ToUpperInvariant())));
        foreach (var directory in directories)
        {
            try
            {
                if (!Directory.Exists(directory)) continue;
                foreach (var file in Directory.EnumerateFiles(directory, "w*.json"))
                {
                    var kind = Path.GetFileNameWithoutExtension(file);
                    if (!int.TryParse(kind.AsSpan(1), out var width) || width <= 0 || width == preferredWidth) continue;
                    var entry = LoadAt(path, kind, version, directory);
                    if (entry != null && validate(entry)) return entry;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return null;
    }

    private Entry? LoadAt(string path, string kind, string version, string directory)
    {
        try
        {
            var cache = Path.Combine(directory, kind + ".json");
            using var input = new FileStream(cache, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (input.Length > 48L * 1024 * 1024) return null;
            var entry = JsonSerializer.Deserialize<Entry>(input);
            if (entry?.Version != version || Version(path) != version) return null;
            if (!entry.NoArtwork && (entry.Data is not { Length: > 0 } || entry.Width <= 0 || entry.Height <= 0)) return null;
            if (!entry.NoArtwork && (entry.ContentHash == null || entry.ContentHash != Convert.ToHexString(SHA256.HashData(entry.Data!)))) return null;
            return entry;
        }
        catch { return null; }
    }

    public PreviewPersistence Save(string path, string kind, Entry entry)
    {
        try
        {
            using var held = Lock(path);
            if (Version(path) != entry.Version) return PreviewPersistence.NotApplicable;
            // Envelope replacement is the commit point: readers cannot mix JPG and metadata generations.
            var committed = entry with { ContentHash = entry.Data == null ? null : Convert.ToHexString(SHA256.HashData(entry.Data)) };
            AtomicWrite(Path.Combine(DirectoryFor(path), kind + ".json"), JsonSerializer.SerializeToUtf8Bytes(committed));
            return Version(path) == entry.Version ? PreviewPersistence.Saved : PreviewPersistence.NotApplicable;
        }
        catch { return PreviewPersistence.MemoryOnly; }
    }

    public bool ExportFrame(string source, Entry entry, string destination)
    {
        using var held = Lock(source);
        if (Version(source) != entry.Version || entry.Data == null) return false;
        AtomicWrite(destination, entry.Data);
        if (Version(source) != entry.Version) return false;
        foreach (var old in Directory.EnumerateFiles(DirectoryFor(source), "frame-*.jpg"))
            if (!string.Equals(old, destination, StringComparison.OrdinalIgnoreCase))
                try { File.Delete(old); } catch { /* An active legacy reader pins its version. */ }
        return true;
    }

    private FileStream Lock(string path)
    {
        var directory = DirectoryFor(path);
        Directory.CreateDirectory(directory);
        var until = Environment.TickCount64 + 1500;
        while (true)
        {
            try
            {
                var held = new FileStream(Path.Combine(directory, "commit.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                CleanupAbandonedTemps(path, directory);
                return held;
            }
            catch (IOException) when (Environment.TickCount64 < until) { Thread.Sleep(15); }
        }
    }

    // Every writer creates its temporary file only while holding this same commit lock.
    // A bounded, per-path sweep never touches read-only fallback roots or another namespace.
    private void CleanupAbandonedTemps(string path, string directory)
    {
        lock (KnownGate)
        {
            if (!Known.TryGetValue(TokenPath(path), out var state)) return;
            var now = DateTime.UtcNow.Ticks;
            if (now - state.LastCleanupTicks < TimeSpan.TicksPerHour) return;
            state.LastCleanupTicks = now;
        }
        try
        {
            var deleted = 0;
            foreach (var candidate in Directory.EnumerateFiles(directory, "*.tmp").Take(64))
            {
                if (deleted >= 16) break;
                var name = Path.GetFileName(candidate);
                if (!System.Text.RegularExpressions.Regex.IsMatch(name,
                    @"^(?:epoch|cover\.json|original\.json|w\d+\.json|frame-[A-Fa-f0-9]+\.jpg)\.[A-Fa-f0-9]{32}\.tmp$")) continue;
                try
                {
                    if ((File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0) continue;
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(candidate) < TimeSpan.FromHours(24)) continue;
                    using var owned = new FileStream(candidate, FileMode.Open, FileAccess.ReadWrite, FileShare.None,
                        4096, FileOptions.DeleteOnClose);
                    deleted++;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void AtomicWrite(string path, byte[] bytes)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(bytes);
                output.Flush(true);
            }
            File.Move(temp, path, true);
        }
        finally { try { File.Delete(temp); } catch { } }
    }
}
