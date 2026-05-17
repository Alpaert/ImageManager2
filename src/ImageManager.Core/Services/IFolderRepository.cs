using ImageManager.Core.Models;

namespace ImageManager.Core.Services;

public interface IFolderRepository
{
    Task<List<FolderInfo>> GetAllAsync();
    Task<FolderInfo?> GetByPathAsync(string path);
    Task AddAsync(string path);
    Task UpdateAliasAsync(string path, string? alias);
    Task RemoveAsync(string path);
    Task SetLastPageIndexAsync(string path, int pageIndex);
    Task<int?> GetLastPageIndexAsync(string path);

    /// <summary>Update folder path and all contained image paths after external rename/move</summary>
    Task RelocateFolderAsync(long folderId, string newFolderPath);
}
