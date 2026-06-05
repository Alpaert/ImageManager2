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
    Task AddAutoTagsAsync(long imageId, List<string> tagNames);
    Task ReplaceAutoTagAsync(long imageId, string englishTagName, long chineseTagId);
    Task ReplaceAutoTagsBatchAsync(List<(long ImageId, string EnglishName, long ChineseId)> replacements);
    Task DeleteAutoTagFromImageAsync(long imageId, string tagName);
    Task<int> DeleteAllAutoTagsByFolderAsync(string folderPath);
    Task ClearTagsAndStatusBatchAsync(List<string> filePaths);
    Task<List<TagCount>> GetTagCountsAsync();
    Task<List<string>> GetFilePathsByTagAsync(string tagName);
    Task<List<string>> GetFilePathsByTagsAsync(List<string> tagNames, bool requireAll);
    Task<List<string>> GetFilePathsByTagsExcludingAsync(List<string> includeTags, bool requireAll, List<string> excludeTags);
    Task<List<string>> GetFilePathsByTagAndEachAsync(List<string> baseTags, bool requireAllBase, List<string> eachTags, List<string>? excludeTags = null);
    Task<List<string>> GetFilePathsWithNoTagsAsync();
    Task<List<TagCount>> GetCoOccurringTagsAsync(List<string> filePaths, List<string>? excludeNames = null, string? nameFilter = null);

    /// <summary>Batch-load perceptual hashes for a specific set of file paths (lightweight, no tags)</summary>
    Task<Dictionary<string, string>> GetPerceptualHashesByPathsAsync(List<string> filePaths);

    /// <summary>Batch-load FileHash for a set of file paths.</summary>
    Task<Dictionary<string, string>> GetFileHashesByPathsAsync(List<string> filePaths);

    /// <summary>Find a record by its MD5 file hash.</summary>
    Task<ImageMeta?> GetByFileHashAsync(string fileHash);

    /// <summary>Update FilePath and FolderId for a moved file (preserves Id and tags).</summary>
    Task UpdateFilePathAsync(long id, string newPath, long newFolderId);

    /// <summary>Batch-load (FilePath, Id, AutoTagStatus) for a list of paths. Only returns records where AutoTagStatus=0 or no record exists.</summary>
    Task<Dictionary<string, (long Id, int Status)>> GetStatusMapByPathsAsync(List<string> filePaths);

    /// <summary>Set AutoTagStatus for a given file path.</summary>
    Task SetAutoTagStatusByPathAsync(string filePath, int status);

    /// <summary>Batch set AutoTagStatus for multiple file paths.</summary>
    Task SetAutoTagStatusBatchAsync(List<string> filePaths, int status);

    /// <summary>Get all records with no folder link (externally deleted).</summary>
    Task<List<ImageMeta>> GetAllUnlinkedAsync();

    /// <summary>Batch-load Width/Height for a set of file paths. Only returns entries with non-zero dimensions.</summary>
    Task<Dictionary<string, (int Width, int Height)>> GetDimensionsByPathsAsync(List<string> filePaths);

    // Batch tag operations
    Task<Dictionary<string, long>> GetIdsByPathsAsync(List<string> filePaths);
    Task AddTagToImagesAsync(List<long> imageIds, string tag);
    Task RemoveTagFromImagesAsync(List<long> imageIds, string tag);
    Task ClearTagsFromImagesAsync(List<long> imageIds);
}
