using System.Collections.Concurrent;
using System.Reflection;
using Avalonia;
using CommunityToolkit.Mvvm.Messaging;
using ImageManager.App.Models;
using ImageManager.App.Services;
using ImageManager.App.ViewModels;
using ImageManager.Core.Models;
using ImageManager.Core.Services;
using ImageManager.Infrastructure.Caching;
using ImageManager.Infrastructure.Imaging;
using ImageManager.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ContinuousDisplay.UnitTests;

[CollectionDefinition("MainWindowStaticSerial", DisableParallelization = true)]
public class MainWindowStaticSerialCollectionDefinition { }

/// <summary>
/// Immediate dispatcher executing actions and callbacks synchronously on the calling thread.
/// </summary>
public sealed class ImmediateDispatcher : ImageManager.Core.Services.IDispatcher
{
    public void Post(Action action) => action();

    public Task InvokeAsync(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    public async Task InvokeAsync(Func<Task> callback) => await callback();

    public Task<T> InvokeAsync<T>(Func<T> callback) => Task.FromResult(callback());

    public async Task<T> InvokeAsync<T>(Func<Task<T>> callback) => await callback();
}

public sealed class FakeSettingsRepository : ISettingsRepository
{
    public AppSettings Settings { get; set; } = new();

    public Task<AppSettings> LoadAsync() => Task.FromResult(Settings);

    public Task SaveAsync(AppSettings settings)
    {
        Settings = settings;
        return Task.CompletedTask;
    }
}

public sealed class FakeFolderRepository : IFolderRepository
{
    public Dictionary<string, FolderInfo> Folders { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> AddedPaths { get; } = new();
    public List<string> RemovedPaths { get; } = new();

    public Func<string, Task<FolderInfo?>>? GetByPathFunc { get; set; }
    public TaskCompletionSource<FolderInfo?>? GetByPathTcs { get; set; }
    public Exception? ThrowOnGetByPath { get; set; }

    public Task<List<FolderInfo>> GetAllAsync() => Task.FromResult(Folders.Values.ToList());

    public async Task<FolderInfo?> GetByPathAsync(string path)
    {
        if (ThrowOnGetByPath != null)
            throw ThrowOnGetByPath;

        if (GetByPathTcs != null)
            return await GetByPathTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));

        if (GetByPathFunc != null)
            return await GetByPathFunc(path);

        Folders.TryGetValue(path, out var info);
        return info;
    }

    public Task AddAsync(string path)
    {
        AddedPaths.Add(path);
        if (!Folders.ContainsKey(path))
        {
            var info = new FolderInfo
            {
                Id = Folders.Count + 1,
                Path = path
            };
            Folders[path] = info;
        }
        return Task.CompletedTask;
    }

    public Task UpdateAliasAsync(string path, string? alias)
    {
        if (Folders.TryGetValue(path, out var info))
        {
            info.Alias = alias;
        }
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string path)
    {
        RemovedPaths.Add(path);
        Folders.Remove(path);
        return Task.CompletedTask;
    }

    public Task SetLastPageIndexAsync(string path, int pageIndex) => Task.CompletedTask;

    public Task<int?> GetLastPageIndexAsync(string path) => Task.FromResult<int?>(null);

    public Task SetArchivedAsync(long folderId, bool archived)
    {
        var match = Folders.Values.FirstOrDefault(f => f.Id == folderId);
        if (match != null) match.IsArchived = archived;
        return Task.CompletedTask;
    }

    public Task RelocateFolderAsync(long folderId, string newFolderPath)
    {
        var match = Folders.Values.FirstOrDefault(f => f.Id == folderId);
        if (match != null)
        {
            Folders.Remove(match.Path);
            match.Path = newFolderPath;
            Folders[newFolderPath] = match;
        }
        return Task.CompletedTask;
    }
}

public class TestMetaProxy : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?>? CustomHandler { get; set; }
    public Dictionary<string, List<string>> TagMap { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, long> IdMap { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ImageMeta> MetaByPath { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<(long ImageId, List<string> Tags)> SetTagsCalls { get; } = new();
    public List<(List<long> ImageIds, string Tag)> AddTagToImagesCalls { get; } = new();
    public List<(List<long> ImageIds, string Tag)> RemoveTagFromImagesCalls { get; } = new();
    public List<List<long>> ClearTagsFromImagesCalls { get; } = new();

    private long _nextId = 1;

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method == null) return null;
        if (CustomHandler != null)
        {
            var customResult = CustomHandler(method, args);
            if (customResult != null) return customResult;
        }

        switch (method.Name)
        {
            case "GetSortMetadataByFolderAsync":
                return Task.FromResult<IReadOnlyList<ImageSortMetadata>>(Array.Empty<ImageSortMetadata>());
            case "GetSortMetadataByPathsAsync":
                return Task.FromResult<IReadOnlyList<ImageSortMetadata>>(Array.Empty<ImageSortMetadata>());
            case "GetDimensionsByPathsAsync":
                return Task.FromResult(args?[0] is List<string> list
                    ? list.ToDictionary(p => p, _ => (100, 100))
                    : new Dictionary<string, (int, int)>());
            case "GetSystemRatingsByPathsAsync":
                return Task.FromResult(new Dictionary<string, int>());
            case "GetTagMapByPathsAsync":
                var paths = (args?[0] as IEnumerable<string>) ?? Array.Empty<string>();
                var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in paths)
                {
                    if (TagMap.TryGetValue(p, out var t))
                        result[p] = new List<string>(t);
                }
                return Task.FromResult(result);
            case "GetTagMapByFolderAsync":
                return Task.FromResult(new Dictionary<string, List<string>>());
            case "GetCoOccurringTagsAsync":
                return Task.FromResult(new List<TagCount>());
            case "GetByFolderAsync":
                return Task.FromResult(new List<ImageMeta>());
            case "GetByPathAsync":
                var pathStr = args?[0] as string;
                if (pathStr != null && MetaByPath.TryGetValue(pathStr, out var m))
                    return Task.FromResult<ImageMeta?>(m);
                if (pathStr != null)
                {
                    if (!IdMap.TryGetValue(pathStr, out var id))
                    {
                        id = _nextId++;
                        IdMap[pathStr] = id;
                    }
                    var tags = TagMap.TryGetValue(pathStr, out var existingTags) ? existingTags : new List<string>();
                    return Task.FromResult<ImageMeta?>(new ImageMeta
                    {
                        Id = id,
                        FilePath = pathStr,
                        Tags = tags.Select(t => new TagCount { Name = t }).ToList()
                    });
                }
                return Task.FromResult<ImageMeta?>(null);
            case "GetIdsByPathsAsync":
                var queryPaths = (args?[0] as IEnumerable<string>) ?? Array.Empty<string>();
                var idResult = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in queryPaths)
                {
                    if (!IdMap.TryGetValue(p, out var id))
                    {
                        id = _nextId++;
                        IdMap[p] = id;
                    }
                    idResult[p] = id;
                }
                return Task.FromResult(idResult);
            case "SetTagsAsync":
                if (args?[0] is long imgId && args?[1] is List<string> newTags)
                {
                    SetTagsCalls.Add((imgId, newTags));
                    var matchedPath = IdMap.FirstOrDefault(kv => kv.Value == imgId).Key;
                    if (matchedPath != null)
                    {
                        TagMap[matchedPath] = new List<string>(newTags);
                    }
                }
                return Task.CompletedTask;
            case "AddTagToImagesAsync":
                if (args?[0] is List<long> addIds && args?[1] is string addTag)
                {
                    AddTagToImagesCalls.Add((addIds, addTag));
                    foreach (var id in addIds)
                    {
                        var p = IdMap.FirstOrDefault(kv => kv.Value == id).Key;
                        if (p != null)
                        {
                            if (!TagMap.TryGetValue(p, out var t)) { t = new List<string>(); TagMap[p] = t; }
                            if (!t.Contains(addTag, StringComparer.OrdinalIgnoreCase)) t.Add(addTag);
                        }
                    }
                }
                return Task.CompletedTask;
            case "RemoveTagFromImagesAsync":
                if (args?[0] is List<long> remIds && args?[1] is string remTag)
                {
                    RemoveTagFromImagesCalls.Add((remIds, remTag));
                    foreach (var id in remIds)
                    {
                        var p = IdMap.FirstOrDefault(kv => kv.Value == id).Key;
                        if (p != null && TagMap.TryGetValue(p, out var t))
                        {
                            t.RemoveAll(x => string.Equals(x, remTag, StringComparison.OrdinalIgnoreCase));
                        }
                    }
                }
                return Task.CompletedTask;
            case "ClearTagsFromImagesAsync":
                if (args?[0] is List<long> clrIds)
                {
                    ClearTagsFromImagesCalls.Add(clrIds);
                    foreach (var id in clrIds)
                    {
                        var p = IdMap.FirstOrDefault(kv => kv.Value == id).Key;
                        if (p != null)
                        {
                            TagMap[p] = new List<string>();
                        }
                    }
                }
                return Task.CompletedTask;
            case "SetAutoTagStatusByPathAsync":
                return Task.CompletedTask;
            case "UnlinkFolderIdAsync":
                return Task.FromResult(0);
            default:
                if (method.ReturnType == typeof(Task))
                    return Task.CompletedTask;
                if (method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
                    return CreateDefaultTask(method.ReturnType.GetGenericArguments()[0]);
                return null;
        }
    }

    private static object CreateDefaultTask(Type innerType)
    {
        var defaultVal = innerType.IsValueType ? Activator.CreateInstance(innerType) : null;
        var fromResult = typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(innerType);
        return fromResult.Invoke(null, new[] { defaultVal })!;
    }
}

public sealed class FakeSimilarImageService : ISimilarImageService
{
    public List<SimilaritySearchResult> ResultsToReturn { get; set; } = new();
    public Func<string, IEnumerable<string>, SimilaritySearchMode, int, CancellationToken, Task<List<SimilaritySearchResult>>>? OnSearchByImage { get; set; }
    public Func<string, IEnumerable<string>, int, CancellationToken, Task<List<SimilaritySearchResult>>>? OnSearchByText { get; set; }
    public Exception? ThrowOnSearch { get; set; }
    public TaskCompletionSource<List<SimilaritySearchResult>>? SearchTcs { get; set; }
    public int SearchCount { get; private set; }

    public void InvalidateVectorSnapshots() { }
    public void InvalidatePerceptualSnapshot() { }

    public async Task<List<SimilaritySearchResult>> SearchByImageAsync(
        string baseFilePath,
        IEnumerable<string> candidates,
        SimilaritySearchMode mode,
        int limit = 50,
        CancellationToken ct = default)
    {
        SearchCount++;
        ct.ThrowIfCancellationRequested();

        if (ThrowOnSearch != null)
            throw ThrowOnSearch;

        if (SearchTcs != null)
        {
            using (ct.Register(() => SearchTcs.TrySetCanceled(ct)))
            {
                return await SearchTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        if (OnSearchByImage != null)
            return await OnSearchByImage(baseFilePath, candidates, mode, limit, ct);

        return ResultsToReturn;
    }

    public async Task<List<SimilaritySearchResult>> SearchByTextAsync(
        string query,
        IEnumerable<string> candidates,
        int limit = 50,
        CancellationToken ct = default)
    {
        SearchCount++;
        ct.ThrowIfCancellationRequested();

        if (ThrowOnSearch != null)
            throw ThrowOnSearch;

        if (SearchTcs != null)
        {
            using (ct.Register(() => SearchTcs.TrySetCanceled(ct)))
            {
                return await SearchTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        if (OnSearchByText != null)
            return await OnSearchByText(query, candidates, limit, ct);

        return ResultsToReturn;
    }
}

public sealed class FakeTagRepository : ITagRepository
{
    public Dictionary<string, long> TagIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<TagCount> TagCounts { get; set; } = new();
    public List<string> Favorites { get; } = new();
    public List<(string OldName, string NewName)> RenameCalls { get; } = new();
    public List<(string OldName, string NewName)> MergeCalls { get; } = new();
    public List<string> DeleteCalls { get; } = new();
    public RenameResult NextRenameResult { get; set; } = RenameResult.Success;

    private long _nextId = 1;

    public Task<long> GetOrCreateTagIdAsync(string name)
    {
        if (!TagIds.TryGetValue(name, out var id))
        {
            id = _nextId++;
            TagIds[name] = id;
        }
        return Task.FromResult(id);
    }

    public Task<List<TagCount>> GetAllTagCountsAsync()
    {
        return Task.FromResult(TagCounts.ToList());
    }

    public Task AddFavoriteAsync(string name)
    {
        if (!Favorites.Contains(name, StringComparer.OrdinalIgnoreCase))
            Favorites.Add(name);
        return Task.CompletedTask;
    }

    public Task RemoveFavoriteAsync(string name)
    {
        Favorites.RemoveAll(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase));
        return Task.CompletedTask;
    }

    public Task<List<string>> GetFavoritesAsync()
    {
        return Task.FromResult(Favorites.ToList());
    }

    public Task<RenameResult> RenameTagAsync(string oldName, string newName)
    {
        RenameCalls.Add((oldName, newName));
        return Task.FromResult(NextRenameResult);
    }

    public Task MergeTagsAsync(string oldName, string newName)
    {
        MergeCalls.Add((oldName, newName));
        return Task.CompletedTask;
    }

    public Task DeleteTagAsync(string tagName)
    {
        DeleteCalls.Add(tagName);
        return Task.CompletedTask;
    }

    public Task<List<string>> SearchTagsAsync(string keyword, int limit = 50)
    {
        return Task.FromResult(TagIds.Keys
            .Where(k => k.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .Take(limit)
            .ToList());
    }
}

public sealed class FakeCharacterTagSuppressionRepository : ICharacterTagSuppressionRepository
{
    public Dictionary<long, HashSet<string>> Suppressed { get; } = new();
    public List<(long ImageId, List<string> Tags)> SuppressCalls { get; } = new();
    public List<(long ImageId, List<string> Tags)> UnsuppressCalls { get; } = new();

    public Task SuppressAsync(long imageMetaId, IEnumerable<string> tagNames)
    {
        var list = tagNames.ToList();
        SuppressCalls.Add((imageMetaId, list));
        if (!Suppressed.TryGetValue(imageMetaId, out var set))
        {
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Suppressed[imageMetaId] = set;
        }
        foreach (var tag in list)
            set.Add(tag);
        return Task.CompletedTask;
    }

    public Task SuppressBatchAsync(IReadOnlyList<(long ImageMetaId, string TagName)> suppressions)
    {
        foreach (var (imgId, tag) in suppressions)
        {
            SuppressCalls.Add((imgId, new List<string> { tag }));
            if (!Suppressed.TryGetValue(imgId, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                Suppressed[imgId] = set;
            }
            set.Add(tag);
        }
        return Task.CompletedTask;
    }

    public Task UnsuppressAsync(long imageMetaId, IEnumerable<string> tagNames)
    {
        var list = tagNames.ToList();
        UnsuppressCalls.Add((imageMetaId, list));
        if (Suppressed.TryGetValue(imageMetaId, out var set))
        {
            foreach (var tag in list)
                set.Remove(tag);
        }
        return Task.CompletedTask;
    }

    public Task UnsuppressBatchAsync(IReadOnlyList<(long ImageMetaId, string TagName)> suppressions)
    {
        foreach (var (imgId, tag) in suppressions)
        {
            UnsuppressCalls.Add((imgId, new List<string> { tag }));
            if (Suppressed.TryGetValue(imgId, out var set))
            {
                set.Remove(tag);
            }
        }
        return Task.CompletedTask;
    }

    public Task<HashSet<string>> GetSuppressedTagsAsync(long imageMetaId)
    {
        return Task.FromResult(Suppressed.TryGetValue(imageMetaId, out var set)
            ? new HashSet<string>(set, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    public Task<Dictionary<long, HashSet<string>>> GetSuppressedTagsAsync(IEnumerable<long> imageMetaIds)
    {
        var result = new Dictionary<long, HashSet<string>>();
        foreach (var id in imageMetaIds)
        {
            if (Suppressed.TryGetValue(id, out var set))
                result[id] = new HashSet<string>(set, StringComparer.OrdinalIgnoreCase);
        }
        return Task.FromResult(result);
    }
}

public sealed class FakeArtistEmbeddingStore : IArtistEmbeddingStore
{
    public Dictionary<string, float[]> ArtistsDict { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> ImageCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, float[]> Artists => ArtistsDict;
    public int Count => ArtistsDict.Count;
    public ArtistStoreSnapshot CurrentSnapshot => new("v1", ArtistsDict, ImageCounts);

    public void SwapSnapshot(ArtistStoreSnapshot newSnapshot) { }
    public int GetImageCount(string artistName) => ImageCounts.GetValueOrDefault(artistName, 1);
    public void Clear() { ArtistsDict.Clear(); ImageCounts.Clear(); }
    public void Remove(string artistName) { ArtistsDict.Remove(artistName); ImageCounts.Remove(artistName); }
    public void Add(string artistName, float[] embedding, int imageCount = 1)
    {
        ArtistsDict[artistName] = embedding;
        ImageCounts[artistName] = imageCount;
    }
    public (string artistName, double similarity)? Search(float[] queryEmbedding, double minSimilarity = 0.6) => null;
    public void Save(string path) { }
    public void Load(string path) { }
    public void LoadVersioned(string storeDir) { }
}

public sealed class FakeChineseTagLibrary : IChineseTagLibrary
{
    public Dictionary<string, string> ArtistNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int Count => ArtistNames.Count;

    public void LoadFromModelCsv(string modelName, string csvPath) { }
    public string? Lookup(string englishTag) => ArtistNames.GetValueOrDefault(englishTag);
    public void RemoveArtistName(string name) => ArtistNames.Remove(name);
    public void Register(string englishName, string chineseName) => ArtistNames[englishName] = chineseName;
    public void ReplaceArtistNames(IReadOnlyDictionary<string, string> artistNames)
    {
        ArtistNames.Clear();
        foreach (var (k, v) in artistNames) ArtistNames[k] = v;
    }
    public void SaveArtistNames(string path) { }
    public void LoadArtistNames(string path) { }
    public void Clear() => ArtistNames.Clear();
    public IReadOnlyList<string> GetEnglishAliases(string chineseTag) => Array.Empty<string>();
    public Dictionary<string, string?> LookupBatch(IEnumerable<string> englishTags) =>
        englishTags.ToDictionary(t => t, t => ArtistNames.GetValueOrDefault(t), StringComparer.OrdinalIgnoreCase);
}

public sealed class FakeCharacterEmbeddingStore : ICharacterEmbeddingStore
{
    public Dictionary<string, float[]> CharactersDict { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> ImageCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, float[]> Characters => CharactersDict;
    public int Count => CharactersDict.Count;
    public CharacterStoreSnapshot CurrentSnapshot => new("v1", CharactersDict, ImageCounts);

    public void SwapSnapshot(CharacterStoreSnapshot newSnapshot) { }
    public int GetImageCount(string characterName) => ImageCounts.GetValueOrDefault(characterName, 1);
    public void Clear() { CharactersDict.Clear(); ImageCounts.Clear(); }
    public void Remove(string characterName) { CharactersDict.Remove(characterName); ImageCounts.Remove(characterName); }
    public void Add(string characterName, float[] embedding, int imageCount = 1)
    {
        CharactersDict[characterName] = embedding;
        ImageCounts[characterName] = imageCount;
    }
    public IReadOnlyList<(string CharacterName, double Similarity)> SearchTop(
        float[] queryEmbedding, double minSimilarity = 0.35, int maxResults = 1) => Array.Empty<(string, double)>();
    public void Save(string path) { }
    public void Load(string path) { }
    public void LoadVersioned(string storeDir) { }
}

public sealed class FakeDuplicateService : IDuplicateService
{
    public (int exactCount, int fuzzyCount) ResultToReturn { get; set; } = (0, 0);
    public Func<IEnumerable<string>, string, CancellationToken, Task<(int exactCount, int fuzzyCount)>>? OnDetect { get; set; }

    public async Task<(int exactCount, int fuzzyCount)> DetectAndMoveDuplicatesAsync(
        IEnumerable<string> filePaths, string targetDir, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (OnDetect != null)
            return await OnDetect(filePaths, targetDir, ct);
        return ResultToReturn;
    }
}

public sealed class FakeMediaMoveService : IMediaMoveService
{
    public Func<MediaMoveRequest, IProgress<MediaMoveProgress>?, CancellationToken, Task<MediaMoveBatchResult>>? OnMoveFiles { get; set; }
    public Func<DirectoryMoveRequest, IProgress<MediaMoveProgress>?, CancellationToken, Task<MediaMoveItemResult>>? OnMoveDirectory { get; set; }

    public static MediaMoveBatchResult CreateBatchResult(IReadOnlyList<MediaMoveItemResult> items)
    {
        int success = items.Count(i => i.Status == MediaMoveItemStatus.Success);
        int failed = items.Count(i => i.Status == MediaMoveItemStatus.Failed);
        int skipped = items.Count(i => i.Status == MediaMoveItemStatus.Skipped);
        int cancelled = items.Count(i => i.Status == MediaMoveItemStatus.Cancelled);
        int compFailed = items.Count(i => i.Status == MediaMoveItemStatus.CompensationFailed);
        return new MediaMoveBatchResult(items, items.Count, success, failed, skipped, cancelled, compFailed);
    }

    public async Task<MediaMoveBatchResult> MoveFilesAsync(
        MediaMoveRequest request,
        IProgress<MediaMoveProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (OnMoveFiles != null)
            return await OnMoveFiles(request, progress, cancellationToken);

        var items = new List<MediaMoveItemResult>();
        int total = request.SourcePaths.Count;
        int processed = 0;
        foreach (var source in request.SourcePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.Combine(request.TargetDirectory, Path.GetFileName(source));
            if (File.Exists(source))
            {
                Directory.CreateDirectory(request.TargetDirectory);
                bool moved = false;
                for (int retry = 0; retry < 5; retry++)
                {
                    try
                    {
                        if (File.Exists(target))
                            File.Delete(target);
                        File.Move(source, target);
                        moved = true;
                        break;
                    }
                    catch (IOException) when (retry < 4)
                    {
                        await Task.Delay(50, cancellationToken);
                    }
                }
                if (moved)
                {
                    items.Add(new MediaMoveItemResult(source, target, MediaMoveItemStatus.Success));
                }
                else
                {
                    items.Add(new MediaMoveItemResult(source, null, MediaMoveItemStatus.Failed, "File locked"));
                }
            }
            else
            {
                items.Add(new MediaMoveItemResult(source, null, MediaMoveItemStatus.Failed, "File not found"));
            }
            processed++;
            progress?.Report(new MediaMoveProgress(processed, total, "Moving"));
        }
        return CreateBatchResult(items);
    }

    public async Task<MediaMoveItemResult> MoveDirectoryAsync(
        DirectoryMoveRequest request,
        IProgress<MediaMoveProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (OnMoveDirectory != null)
            return await OnMoveDirectory(request, progress, cancellationToken);

        var target = Path.Combine(request.TargetParentDirectory, Path.GetFileName(request.SourceDirectory));
        if (Directory.Exists(request.SourceDirectory))
        {
            Directory.CreateDirectory(request.TargetParentDirectory);
            if (Directory.Exists(target))
                Directory.Delete(target, true);
            Directory.Move(request.SourceDirectory, target);
            return new MediaMoveItemResult(request.SourceDirectory, target, MediaMoveItemStatus.Success);
        }
        return new MediaMoveItemResult(request.SourceDirectory, null, MediaMoveItemStatus.Failed, "Source directory not found");
    }

    public Task<IReadOnlyList<MediaMoveItemResult>> RecoverPendingMovesAsync(
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<MediaMoveItemResult>>(Array.Empty<MediaMoveItemResult>());
    }
}

/// <summary>
/// Encapsulates a test instance of MainWindowViewModel along with its mock / fake dependencies,
/// providing deterministic control and automatic temporary directory cleanup.
/// </summary>
public sealed class MainWindowTestContext : IDisposable
{
    private readonly List<string> _managedTempDirectories = new();

    public MainWindowViewModel ViewModel { get; }
    public FakeFolderRepository FolderRepo { get; }
    public FakeSettingsRepository SettingsRepo { get; }
    public IImageMetaRepository MetaRepo { get; }
    public TestMetaProxy MetaProxy { get; }
    public FakeTagRepository TagRepo { get; }
    public FakeCharacterTagSuppressionRepository SuppressionRepo { get; }
    public FakeArtistEmbeddingStore ArtistStore { get; }
    public FakeChineseTagLibrary ChineseLib { get; }
    public FakeCharacterEmbeddingStore CharacterStore { get; }
    public FakeDuplicateService DuplicateService { get; }
    public FakeMediaMoveService MediaMoveService { get; }
    public ThumbnailCacheService CacheService { get; }
    public PageManager PageManager { get; }
    public TagSearchEngine TagSearchEngine { get; }
    public FakeSimilarImageService SimilarService { get; }
    public ImmediateDispatcher Dispatcher { get; }
    public WeakReferenceMessenger Messenger { get; }
    public string TempCacheDirectory { get; }

    private static readonly object _staticAppLock = new();
    private readonly IServiceProvider? _prevServices;
    private readonly ImageManager.Core.Services.IDispatcher? _prevUi;
    private readonly ServiceProvider _serviceProvider;

    public MainWindowTestContext(
        FakeFolderRepository? folderRepo = null,
        FakeSettingsRepository? settingsRepo = null,
        FakeSimilarImageService? similarService = null,
        FakeTagRepository? tagRepo = null,
        FakeCharacterTagSuppressionRepository? suppressionRepo = null,
        FakeMediaMoveService? mediaMoveService = null)
    {
        TempCacheDirectory = Path.Combine(Path.GetTempPath(), "IM2-TestCache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(TempCacheDirectory);
        _managedTempDirectories.Add(TempCacheDirectory);

        FolderRepo = folderRepo ?? new FakeFolderRepository();
        SettingsRepo = settingsRepo ?? new FakeSettingsRepository();
        SimilarService = similarService ?? new FakeSimilarImageService();
        TagRepo = tagRepo ?? new FakeTagRepository();
        SuppressionRepo = suppressionRepo ?? new FakeCharacterTagSuppressionRepository();
        ArtistStore = new FakeArtistEmbeddingStore();
        ChineseLib = new FakeChineseTagLibrary();
        CharacterStore = new FakeCharacterEmbeddingStore();
        DuplicateService = new FakeDuplicateService();
        MediaMoveService = mediaMoveService ?? new FakeMediaMoveService();

        var repoInstance = DispatchProxy.Create<IImageMetaRepository, TestMetaProxy>();
        MetaRepo = repoInstance;
        MetaProxy = (TestMetaProxy)(object)repoInstance;

        Messenger = new WeakReferenceMessenger();
        CacheService = new ThumbnailCacheService(new MediaProcessorFactory(null!), TempCacheDirectory);
        PageManager = new PageManager(CacheService, FolderRepo, MetaRepo);
        Dispatcher = new ImmediateDispatcher();

        lock (_staticAppLock)
        {
            _prevServices = (IServiceProvider?)typeof(ImageManager.App.App).GetProperty("Services")?.GetValue(null);
            _prevUi = (ImageManager.Core.Services.IDispatcher?)typeof(ImageManager.App.App).GetProperty("UI")?.GetValue(null);

            _serviceProvider = new ServiceCollection().AddSingleton(PageManager).BuildServiceProvider();
            typeof(ImageManager.App.App).GetProperty("Services")!
                .SetValue(null, _serviceProvider);
            typeof(ImageManager.App.App).GetProperty("UI")!
                .SetValue(null, Dispatcher);
        }

        TagSearchEngine = new TagSearchEngine(MetaRepo, Messenger, ImagePaging.Default);

        ViewModel = new MainWindowViewModel(
            SettingsRepo,
            FolderRepo,
            MetaRepo,
            TagRepo,
            SuppressionRepo,
            SimilarService,
            DuplicateService,
            CacheService,
            PageManager,
            TagSearchEngine,
            ArtistStore,
            ChineseLib,
            CharacterStore,
            Messenger,
            Dispatcher,
            characterMatchingService: null,
            mediaMoveService: MediaMoveService);
    }


    /// <summary>
    /// Creates a unique temporary folder containing <paramref name="imageCount"/> valid PNG files.
    /// The folder will be deleted upon disposing this context.
    /// </summary>
    public string CreateTempFolderWithImages(int imageCount = 2, string? prefix = null)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"IM2-TestFolder-{prefix ?? ""}" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        _managedTempDirectories.Add(folder);

        using var bitmap = new SkiaSharp.SKBitmap(100, 100);
        using (var canvas = new SkiaSharp.SKCanvas(bitmap))
        {
            canvas.Clear(SkiaSharp.SKColors.MediumSlateBlue);
        }
        using var encoded = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);

        for (int i = 0; i < imageCount; i++)
        {
            var filePath = Path.Combine(folder, $"img_{i}.png");
            using var stream = File.Create(filePath);
            encoded.SaveTo(stream);
        }

        return folder;
    }

    /// <summary>
    /// Creates a unique empty temporary folder.
    /// The folder will be deleted upon disposing this context.
    /// </summary>
    public string CreateEmptyTempFolder(string? prefix = null)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"IM2-EmptyFolder-{prefix ?? ""}" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        _managedTempDirectories.Add(folder);
        return folder;
    }

    /// <summary>
    /// Explicitly shuts down and drains ViewModel background tasks with a bounded timeout.
    /// </summary>
    public async Task DrainBackgroundTasksAsync(TimeSpan timeout = default)
    {
        if (timeout == default) timeout = TimeSpan.FromSeconds(2);
        try
        {
            ViewModel.ShutdownBackgroundWork();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[MainWindowTestContext] Error shutting down ViewModel background work: {ex.Message}");
        }

        await Task.Delay(30);
    }

    public void Dispose()
    {
        try
        {
            ViewModel.ShutdownBackgroundWork();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[MainWindowTestContext] Error shutting down ViewModel background work: {ex.Message}");
        }

        // Bounded wait for in-flight continuations and background tasks to settle before disposing dependencies
        try
        {
            Thread.Sleep(30);
        }
        catch { }

        try
        {
            PageManager.Dispose();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[MainWindowTestContext] Error disposing PageManager: {ex.Message}");
        }

        try
        {
            _serviceProvider.Dispose();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[MainWindowTestContext] Error disposing ServiceProvider: {ex.Message}");
        }

        lock (_staticAppLock)
        {
            try
            {
                typeof(ImageManager.App.App).GetProperty("Services")?.SetValue(null, _prevServices);
                typeof(ImageManager.App.App).GetProperty("UI")?.SetValue(null, _prevUi);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[MainWindowTestContext] Error restoring static App.Services/UI: {ex.Message}");
            }
        }

        foreach (var dir in _managedTempDirectories)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[MainWindowTestContext] Error deleting temp directory {dir}: {ex.Message}");
            }
        }
    }
}
