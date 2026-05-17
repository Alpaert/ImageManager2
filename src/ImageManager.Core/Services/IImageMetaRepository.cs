using ImageManager.Core.Models;

namespace ImageManager.Core.Services;

public interface IImageMetaRepository
{
    Task<ImageMeta?> GetByIdAsync(long id);
    Task<ImageMeta?> GetByPathAsync(string filePath);
    Task<List<ImageMeta>> GetByFolderAsync(string folderPath);
    Task<List<ImageMeta>> GetByFolderIdAsync(long folderId);
    Task<int> CountByFolderIdAsync(long folderId);
    Task SetFolderIdAsync(string filePath, long folderId);
    Task<List<ImageMeta>> GetAllAsync();
    Task<long> UpsertAsync(ImageMeta meta);
    Task BulkUpsertAsync(List<ImageMeta> metas);
    Task<int> DeleteAsync(long id);
    Task<int> DeleteByPathAsync(string filePath);
    Task<int> DeleteByFolderAsync(string folderPath);

    // Tag associations
    Task SetTagsAsync(long imageId, List<string> tags);
    Task<List<TagCount>> GetTagCountsAsync();
    Task<List<string>> GetFilePathsByTagAsync(string tagName);
    Task<List<string>> GetFilePathsByTagsAsync(List<string> tagNames, bool requireAll);
    Task<List<string>> GetFilePathsByTagsExcludingAsync(List<string> includeTags, bool requireAll, List<string> excludeTags);
    Task<List<string>> GetFilePathsByTagAndEachAsync(List<string> baseTags, bool requireAllBase, List<string> eachTags, List<string>? excludeTags = null);
    Task<List<TagCount>> GetCoOccurringTagsAsync(List<string> filePaths, List<string>? excludeNames = null);

    /// <summary>Batch-load perceptual hashes for a specific set of file paths (lightweight, no tags)</summary>
    Task<Dictionary<string, string>> GetPerceptualHashesByPathsAsync(List<string> filePaths);
}
