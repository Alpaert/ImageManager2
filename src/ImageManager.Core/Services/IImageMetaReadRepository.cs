using ImageManager.Core.Models;

namespace ImageManager.Core.Services;

public interface IImageMetaReadRepository
{
    Task<ImageMeta?> GetByIdAsync(long id);
    Task<ImageMeta?> GetByPathAsync(string filePath);
    async Task<ImageMeta?> GetByPathAsync(string filePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await GetByPathAsync(filePath).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }
    Task<List<ImageMeta>> GetByFolderAsync(string folderPath, bool recursive = true);
    Task<List<ImageMeta>> GetByFolderIdAsync(long folderId);
    /// <summary>Paths with the exact FolderId membership, without materializing metadata or tags.</summary>
    Task<IReadOnlyList<string>> GetFilePathsByFolderIdAsync(long folderId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ImageSortMetadata>> GetSortMetadataByFolderAsync(
        string folderPath,
        bool recursive,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ImageSortMetadata>> GetSortMetadataByPathsAsync(
        IReadOnlyCollection<string> filePaths,
        CancellationToken cancellationToken = default);
    Task<int> CountByFolderIdAsync(long folderId);
    Task<List<ImageMeta>> GetAllAsync();
    Task<List<ImageMeta>> GetAllUnlinkedAsync();
    Task<Dictionary<string, (int Width, int Height)>> GetDimensionsByPathsAsync(List<string> filePaths);
    Task<Dictionary<string, int>> GetSystemRatingsByPathsAsync(List<string> filePaths);
    Task<Dictionary<string, (long Id, int Status)>> GetStatusMapByPathsAsync(List<string> filePaths,
        CancellationToken cancellationToken = default, Action<int>? progress = null);
    Task<Dictionary<string, long>> GetIdsByPathsAsync(List<string> filePaths);
}
