using Dapper;
using ImageManager.Core.Models;
using ImageManager.Core.Services;

namespace ImageManager.Infrastructure.Data.Repositories;

public class FolderRepository : IFolderRepository
{
    private readonly IDbContextFactory _dbFactory;

    public FolderRepository(IDbContextFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<List<FolderInfo>> GetAllAsync()
    {
        using var conn = _dbFactory.CreateConnection();
        var results = await conn.QueryAsync<FolderInfo>(
            "SELECT * FROM Folder ORDER BY SortOrder, Id");
        return results.ToList();
    }

    public async Task<FolderInfo?> GetByPathAsync(string path)
    {
        using var conn = _dbFactory.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<FolderInfo>(
            "SELECT * FROM Folder WHERE Path = @Path COLLATE NOCASE", new { Path = path });
    }

    public async Task AddAsync(string path)
    {
        using var conn = _dbFactory.CreateConnection();
        await conn.ExecuteAsync(
            "INSERT OR IGNORE INTO Folder (Path) VALUES (@Path)", new { Path = path });
    }

    public async Task UpdateAliasAsync(string path, string? alias)
    {
        using var conn = _dbFactory.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE Folder SET Alias = @Alias WHERE Path = @Path COLLATE NOCASE",
            new { Path = path, Alias = alias });
    }

    public async Task RemoveAsync(string path)
    {
        using var conn = _dbFactory.CreateConnection();
        await conn.ExecuteAsync(
            "DELETE FROM Folder WHERE Path = @Path COLLATE NOCASE", new { Path = path });
    }

    public async Task SetLastPageIndexAsync(string path, int pageIndex)
    {
        using var conn = _dbFactory.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE Folder SET LastPageIndex = @PageIndex WHERE Path = @Path COLLATE NOCASE",
            new { Path = path, PageIndex = pageIndex });
    }

    public async Task<int?> GetLastPageIndexAsync(string path)
    {
        using var conn = _dbFactory.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<int?>(
            "SELECT LastPageIndex FROM Folder WHERE Path = @Path COLLATE NOCASE",
            new { Path = path });
    }

    public async Task RelocateFolderAsync(long folderId, string newFolderPath)
    {
        using var conn = _dbFactory.CreateConnection();
        using var txn = conn.BeginTransaction();

        // Get old path
        var oldPath = await conn.QuerySingleOrDefaultAsync<string>(
            "SELECT Path FROM Folder WHERE Id = @Id", new { Id = folderId }, txn);
        if (string.IsNullOrEmpty(oldPath)) return;

        var oldPrefix = Common.Helpers.PathHelper.NormalizeFolderPath(oldPath);
        var newPrefix = Common.Helpers.PathHelper.NormalizeFolderPath(newFolderPath);

        // Update Folder
        await conn.ExecuteAsync(
            "UPDATE Folder SET Path = @Path WHERE Id = @Id",
            new { Path = newFolderPath, Id = folderId }, txn);

        // Batch update all ImageMeta paths
        // SQLite REPLACE in UPDATE is applied per-row, so we use LIKE to match old prefix
        await conn.ExecuteAsync(
            "UPDATE ImageMeta SET FilePath = @NewPrefix || SUBSTR(FilePath, @OldLen + 1) WHERE FolderId = @FolderId AND FilePath LIKE @OldLike",
            new { NewPrefix = newPrefix, OldLen = oldPrefix.Length, FolderId = folderId, OldLike = oldPrefix + "%" }, txn);

        txn.Commit();
    }
}
