using ImageManager.Core.Models;

namespace ImageManager.Core.Services;

public interface IImageMetaWriteRepository
{
    Task<long> UpsertAsync(ImageMeta meta);
    Task BulkUpsertAsync(List<ImageMeta> metas);
    Task<int> DeleteAsync(long id);
    Task<int> DeleteByPathAsync(string filePath);
    Task<int> DeleteByFolderAsync(string folderPath, bool recursive = true);
    Task SetFolderIdAsync(string filePath, long folderId);
    Task<int> UnlinkFolderIdAsync(long folderId);
    Task<int> UnlinkOrphanFolderIdsAsync();
    Task UpdateFilePathAsync(long id, string newPath, long newFolderId);
    Task UpdateDimensionsByPathsAsync(IReadOnlyDictionary<string, (int Width, int Height)> dimensions);
    Task SetSystemRatingByPathAsync(string filePath, int rating);
    Task SetAutoTagStatusByPathAsync(string filePath, int status);
    Task SetAutoTagStatusBatchAsync(List<string> filePaths, int status);
    Task<Dictionary<string, (long Id, int Status)>> RegisterAutoTagFilesAsync(
        IReadOnlyList<AutoTagFileRegistration> files, CancellationToken cancellationToken = default);
}
