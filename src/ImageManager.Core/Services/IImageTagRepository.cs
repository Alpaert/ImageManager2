using ImageManager.Core.Models;

namespace ImageManager.Core.Services;

public interface IImageTagRepository
{
    // Tag associations
    Task SetTagsAsync(long imageId, List<string> tags);
    Task AddAutoTagsAsync(long imageId, List<string> tagNames);
    Task ReplaceAutoTagAsync(long imageId, string englishTagName, long chineseTagId);
    Task ReplaceAutoTagsBatchAsync(List<(long ImageId, string EnglishName, long ChineseId)> replacements);
    Task DeleteAutoTagFromImageAsync(long imageId, string tagName);
    Task<int> DeleteAllAutoTagsByFolderAsync(string folderPath, bool recursive = true);
    Task ClearTagsAndStatusBatchAsync(List<string> filePaths);
    Task<List<TagCount>> GetTagCountsAsync();
    Task<List<string>> GetFilePathsByTagAsync(string tagName);
    Task<List<string>> GetFilePathsByTagsAsync(List<string> tagNames, bool requireAll);
    Task<List<string>> GetFilePathsByTagsExcludingAsync(List<string> includeTags, bool requireAll, List<string> excludeTags);
    Task<List<string>> GetFilePathsByTagAndEachAsync(List<string> baseTags, bool requireAllBase, List<string> eachTags, List<string>? excludeTags = null);
    Task<List<string>> GetFilePathsExcludingTagsAsync(List<string> excludeTags, bool requireAll);
    Task<List<string>> GetFilePathsWithNoTagsAsync();

    // Lightweight tag queries (avoid loading full ImageMeta)
    /// <summary>Returns (FilePath → TagNames) for images under a folder</summary>
    Task<Dictionary<string, List<string>>> GetTagMapByFolderAsync(string folderPath, bool recursive = true);
    /// <summary>Returns (FilePath → TagNames) for a batch of file paths</summary>
    Task<Dictionary<string, List<string>>> GetTagMapByPathsAsync(List<string> filePaths);
    Task<List<TagCount>> GetCoOccurringTagsAsync(List<string> filePaths, List<string>? excludeNames = null, string? nameFilter = null);
    Task<ICoTagQuerySession> CreateCoTagQuerySessionAsync(
        IReadOnlyCollection<string> filePaths,
        IReadOnlyCollection<string>? excludeNames = null,
        CancellationToken cancellationToken = default);

    // Batch tag operations
    Task AddTagToImagesAsync(List<long> imageIds, string tag);
    Task RemoveTagFromImagesAsync(List<long> imageIds, string tag);
    Task ClearTagsFromImagesAsync(List<long> imageIds);
}
