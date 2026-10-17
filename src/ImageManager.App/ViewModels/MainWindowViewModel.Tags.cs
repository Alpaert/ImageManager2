using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageManager.App.Models;
using ImageManager.Common.Constants;
using ImageManager.Common.Helpers;
using ImageManager.Core.Models;
using ImageManager.Core.Services;

namespace ImageManager.App.ViewModels;

public partial class MainWindowViewModel
{
    // ==================== Tag Management ====================

    public async Task<RenameResult> RenameTagAsync(string oldName, string newName)
    {
        return await Task.Run(async () =>
        {
            var result = await _tagRepo.RenameTagAsync(oldName, newName);
            if (result == RenameResult.Conflict) return RenameResult.Conflict;

            lock (_tagCacheLock)
            {
                foreach (var tags in _tagCacheByPath.Values)
                {
                    for (int i = 0; i < tags.Count; i++)
                        if (string.Equals(tags[i], oldName, StringComparison.OrdinalIgnoreCase))
                            tags[i] = newName;
                }
            }

            InvalidateTagCountsCache();
            await RefreshTagCountsAsync(forceRefresh: true);
            SyncArtistName(oldName, newName);
            return RenameResult.Success;
        });
    }

    public async Task MergeTagsAsync(string oldName, string newName)
    {
        await Task.Run(async () =>
        {
            await _tagRepo.MergeTagsAsync(oldName, newName);

            lock (_tagCacheLock)
            {
                foreach (var tags in _tagCacheByPath.Values)
                {
                    for (int i = tags.Count - 1; i >= 0; i--)
                    {
                        if (string.Equals(tags[i], oldName, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!tags.Contains(newName, StringComparer.OrdinalIgnoreCase))
                                tags[i] = newName;
                            else
                                tags.RemoveAt(i);
                        }
                    }
                }
            }

            InvalidateTagCountsCache();
            await RefreshTagCountsAsync(forceRefresh: true);
            SyncArtistName(oldName, newName);
        });
    }

    private void SyncArtistName(string oldName, string newName)
    {
        var modelsDir = System.IO.Path.Combine(_thumbCache.CacheDirectory, "models");
        var embPath = System.IO.Path.Combine(modelsDir, "artist_embeddings.bin");
        var namesPath = System.IO.Path.Combine(modelsDir, "artist_names.txt");

        // 更新嵌入库中的画师名
        var emb = _artistStore.Artists.GetValueOrDefault(oldName);
        if (emb != null)
        {
            _artistStore.Add(newName, emb, _artistStore.GetImageCount(oldName));
            // 如果新旧不同名，移除旧条目
            if (!string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
            {
                _artistStore.Remove(oldName);
            }
            _artistStore.Save(embPath);
        }

        // 更新中文库映射
        _chineseLib.Register(newName, newName);
        if (!string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
        {
            _chineseLib.RemoveArtistName(oldName);
        }
        _chineseLib.SaveArtistNames(namesPath);
    }

    public async Task RefreshTagCountsAsync(bool forceRefresh = false)
    {
        lock (_tagCountsCacheLock)
        {
            if (!forceRefresh && _cachedTagCounts != null &&
                DateTime.Now - _tagCountsCacheTime < _tagCountsCacheDuration)
            {
                _tagSearch.AllTagCounts = _cachedTagCounts;
                return;
            }
        }

        var counts = await Task.Run(() => _tagRepo.GetAllTagCountsAsync());

        lock (_tagCountsCacheLock)
        {
            _cachedTagCounts = counts;
            _tagCountsCacheTime = DateTime.Now;
        }

        _tagSearch.AllTagCounts = counts;
        if (_isTagSearchInputFocused &&
            _tagSuggestionMode == TagSuggestionMode.TypedSuggestions &&
            !string.IsNullOrWhiteSpace(TagSearchText))
        {
            await _dispatcher.InvokeAsync(() => UpdateTagSuggestions(TagSearchText));
        }
    }

    public async Task RefreshTagsAfterExternalWriteAsync(IEnumerable<string> affectedPaths, bool refreshCounts = true)
    {
        var paths = affectedPaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (paths.Count > 0)
        {
            var tagMap = await _metaRepo.GetTagMapByPathsAsync(paths);

            lock (_tagCacheLock)
            {
                foreach (var path in paths)
                {
                    _tagCacheByPath[path] = tagMap.TryGetValue(path, out var tags)
                        ? new List<string>(tags)
                        : new List<string>();
                }
            }

            await _dispatcher.InvokeAsync(() =>
            {
                foreach (var item in Images)
                {
                    if (!tagMap.TryGetValue(item.FilePath, out var tags))
                        continue;

                    item.Tags = new List<string>(tags);
                    item.NotifyAll();
                }
            });
        }

        if (refreshCounts)
        {
            InvalidateTagCountsCache();
            await RefreshTagCountsAsync(forceRefresh: true);
        }

        if (_tagSearch.IsSuggestionCoTagMode)
        {
            InvalidateCoTagSuggestionCache();
            await _tagSearch.RefreshCurrentCoTagSuggestionsAsync();
        }
    }

    public List<TagCount> GetAllTagCounts()
    {
        lock (_tagCountsCacheLock)
        {
            if (_cachedTagCounts != null &&
                DateTime.Now - _tagCountsCacheTime < _tagCountsCacheDuration)
            {
                return _cachedTagCounts;
            }
        }
        return _tagSearch.AllTagCounts;
    }

    public List<TagCount> GetAllTagCountsSnapshot()
    {
        var counts = GetAllTagCounts();
        if (counts.Count == 0)
            _ = RefreshTagCountsAsync();
        return counts;
    }

    private void InvalidateTagCountsCache()
    {
        lock (_tagCountsCacheLock)
        {
            _cachedTagCounts = null;
            _tagCountsCacheTime = DateTime.MinValue;
        }
    }

    public async Task DeleteTagFromAllImagesAsync(string tagName)
    {
        await _tagRepo.DeleteTagAsync(tagName);

        // Clear from in-memory caches
        lock (_tagCacheLock)
        {
            foreach (var kv in _tagCacheByPath)
                kv.Value.RemoveAll(t => string.Equals(t, tagName, StringComparison.OrdinalIgnoreCase));
        }

        // Update displayed images
        foreach (var img in Images)
        {
            img.Tags.RemoveAll(t => string.Equals(t, tagName, StringComparison.OrdinalIgnoreCase));
            img.NotifyAll();
        }

        InvalidateTagCountsCache();
        await RefreshTagCountsAsync(forceRefresh: true);

        if (_tagSearch.IsSuggestionCoTagMode)
        {
            InvalidateCoTagSuggestionCache();
            await _tagSearch.RefreshCurrentCoTagSuggestionsAsync();
        }
    }

    public async Task RefreshImageTagsAsync(string filePath)
    {
        var meta = await Task.Run(() => _metaRepo.GetByPathAsync(filePath));
        if (meta == null) return;

        var tags = meta.Tags.Select(t => t.Name).ToList();
        lock (_tagCacheLock)
        {
            _tagCacheByPath[filePath] = tags;
        }

        // Update the displayed ImageViewItem if present
        var imgItem = Images.FirstOrDefault(i =>
            string.Equals(i.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (imgItem != null)
        {
            imgItem.Tags = tags;
            imgItem.NotifyAll();
        }
    }

    public async Task SetImageTagsAsync(string filePath, List<string> tags)
    {
        var normalizedTags = tags
            .Select(t => t.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        await Task.Run(async () =>
        {
            var meta = await _metaRepo.GetByPathAsync(filePath);
            if (meta != null)
            {
                var oldTags = meta.Tags.Select(t => t.Name).ToList();
                await UpdateCharacterTagSuppressionsAsync(meta.Id, oldTags, normalizedTags);
                await _metaRepo.SetTagsAsync(meta.Id, normalizedTags);
            }
        });

        lock (_tagCacheLock)
        {
            _tagCacheByPath[filePath] = normalizedTags;
        }
    }

    public async Task AddTagToImageAsync(string filePath, string tag)
    {
        List<string> tags;
        lock (_tagCacheLock)
        {
            tags = _tagCacheByPath.GetValueOrDefault(filePath) ?? new List<string>();
        }
        if (!tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
        {
            tags.Add(tag);
            await SetImageTagsAsync(filePath, tags);
        }
    }

    public async Task RemoveTagFromImageAsync(string filePath, string tag)
    {
        List<string>? tags;
        lock (_tagCacheLock)
        {
            tags = _tagCacheByPath.GetValueOrDefault(filePath);
        }
        if (tags != null && tags.RemoveAll(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            await SetImageTagsAsync(filePath, tags);
        }
    }

    public async Task AddTagToImagesBatchAsync(List<string> filePaths, string tag)
    {
        lock (_tagCacheLock)
        {
            foreach (var path in filePaths)
            {
                var tags = _tagCacheByPath.GetValueOrDefault(path) ?? new List<string>();
                if (!tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                {
                    tags.Add(tag);
                    _tagCacheByPath[path] = tags;
                }
            }
        }

        var pathToId = await _metaRepo.GetIdsByPathsAsync(filePaths);
        if (pathToId.Count > 0)
        {
            if (IsCharacterTag(tag))
            {
                await _characterTagSuppressionRepo.UnsuppressBatchAsync(
                    pathToId.Values.Select(id => (id, tag)).ToList());
            }
            await _metaRepo.AddTagToImagesAsync(pathToId.Values.ToList(), tag);
        }

        InvalidateTagCountsCache();
        await RefreshTagCountsAsync(forceRefresh: true);
    }

    public async Task RemoveTagFromImagesBatchAsync(List<string> filePaths, string tag)
    {
        lock (_tagCacheLock)
        {
            foreach (var path in filePaths)
            {
                if (_tagCacheByPath.TryGetValue(path, out var tags))
                    tags.RemoveAll(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
            }
        }

        var pathToId = await _metaRepo.GetIdsByPathsAsync(filePaths);
        if (pathToId.Count > 0)
        {
            await _metaRepo.RemoveTagFromImagesAsync(pathToId.Values.ToList(), tag);
            if (IsCharacterTag(tag))
            {
                await _characterTagSuppressionRepo.SuppressBatchAsync(
                    pathToId.Values.Select(id => (id, tag)).ToList());
            }
        }

        InvalidateTagCountsCache();
        await RefreshTagCountsAsync(forceRefresh: true);
    }

    public async Task ClearTagsFromImagesBatchAsync(List<string> filePaths)
    {
        lock (_tagCacheLock)
        {
            foreach (var path in filePaths)
                _tagCacheByPath[path] = new List<string>();
        }

        var pathToId = await _metaRepo.GetIdsByPathsAsync(filePaths);
        if (pathToId.Count > 0)
        {
            await _metaRepo.ClearTagsFromImagesAsync(pathToId.Values.ToList());
            foreach (var path in filePaths)
                await _metaRepo.SetAutoTagStatusByPathAsync(path, 0);
        }

        InvalidateTagCountsCache();
        await RefreshTagCountsAsync(forceRefresh: true);
    }

    public List<string> GetTagsForFile(string filePath)
    {
        lock (_tagCacheLock)
        {
            if (_tagCacheByPath.TryGetValue(filePath, out var tags))
                return new List<string>(tags);
            // Cache miss — preload will fill before page renders
            return new List<string>();
        }
    }

    /// <summary>Batch-preload tag cache for a page's worth of files (for subfolder files not yet cached)</summary>
    public async Task PreloadTagsForFilesAsync(List<string> paths)
    {
        List<string> missing;
        lock (_tagCacheLock)
        {
            missing = paths.Where(p => !_tagCacheByPath.ContainsKey(p)).ToList();
        }
        if (missing.Count == 0) return;
        Dictionary<string, List<string>> loadedTags = new(StringComparer.OrdinalIgnoreCase);
        await Task.Run(async () =>
        {
            var tagMap = await _metaRepo.GetTagMapByPathsAsync(missing);
            lock (_tagCacheLock)
            {
                foreach (var path in missing)
                {
                    var tags = tagMap.TryGetValue(path, out var found)
                        ? found : new List<string>();
                    _tagCacheByPath[path] = tags;
                    loadedTags[path] = tags;
                }
            }
        });
        if (loadedTags.Count == 0) return;

        await _dispatcher.InvokeAsync(() =>
        {
            foreach (var item in Images)
            {
                if (loadedTags.TryGetValue(item.FilePath, out var tags))
                {
                    item.Tags = new List<string>(tags);
                    item.NotifyAll();
                }
            }
        });
    }

    public void ClearTagCacheForPath(string filePath)
    {
        lock (_tagCacheLock)
        {
            _tagCacheByPath[filePath] = new List<string>();
        }
    }

    public void InvalidatePageCache() => _pageManager.InvalidateCache();

    public async Task<List<string>> GetTagsForFileAsync(string filePath)
    {
        var meta = await Task.Run(() => _metaRepo.GetByPathAsync(filePath));
        return meta?.Tags.Select(t => t.Name).ToList() ?? new List<string>();
    }

    /// <summary>确保文件列表的 Tag 已加载，返回 FilePath → Tags 字典</summary>
    public async Task<Dictionary<string, List<string>>> EnsureTagsLoadedAsync(List<string> filePaths)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var missingPaths = new List<string>();

        lock (_tagCacheLock)
        {
            foreach (var path in filePaths)
            {
                if (_tagCacheByPath.TryGetValue(path, out var tags))
                    result[path] = tags;
                else
                    missingPaths.Add(path);
            }
        }

        if (missingPaths.Count > 0)
        {
            try
            {
                var tagMap = await _metaRepo.GetTagMapByPathsAsync(missingPaths);
                lock (_tagCacheLock)
                {
                    foreach (var kv in tagMap)
                    {
                        _tagCacheByPath[kv.Key] = kv.Value;
                        result[kv.Key] = kv.Value;
                    }
                    // 对于数据库中也没有的文件，标记为空列表
                    foreach (var path in missingPaths)
                    {
                        if (!result.ContainsKey(path))
                            result[path] = new List<string>();
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error($"EnsureTagsLoadedAsync failed: {ex.Message}");
            }
        }

        return result;
    }

    private async Task UpdateCharacterTagSuppressionsAsync(
        long imageId,
        IReadOnlyCollection<string> oldTags,
        IReadOnlyCollection<string> newTags)
    {
        var oldSet = oldTags.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newSet = newTags.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var removedCharacters = oldSet
            .Where(t => !newSet.Contains(t) && IsCharacterTag(t))
            .ToList();
        if (removedCharacters.Count > 0)
            await _characterTagSuppressionRepo.SuppressAsync(imageId, removedCharacters);

        var addedCharacters = newSet
            .Where(IsCharacterTag)
            .ToList();
        if (addedCharacters.Count > 0)
            await _characterTagSuppressionRepo.UnsuppressAsync(imageId, addedCharacters);
    }

    private bool IsCharacterTag(string tag)
    {
        EnsureCharacterStoreLoaded();
        return !string.IsNullOrWhiteSpace(tag) &&
               _characterStore.Characters.ContainsKey(tag.Trim());
    }

    private void EnsureCharacterStoreLoaded()
    {
        if (_characterStore.Count > 0 || _characterStoreLoadAttempted)
            return;

        _characterStoreLoadAttempted = true;
        var dbPath = Path.Combine(_thumbCache.CacheDirectory, "models", "character_embeddings.bin");
        _characterStore.Load(dbPath);
    }


}
