using Dapper;
using ImageManager.Core.Models;
using ImageManager.Core.Services;

namespace ImageManager.Infrastructure.Data.Repositories;

public class TagRepository : ITagRepository
{
    private readonly AppDbContext _db;

    public TagRepository(AppDbContext db) => _db = db;

    public async Task<long> GetOrCreateTagIdAsync(string name)
    {
        using var conn = _db.CreateConnection();
        var id = await conn.ExecuteScalarAsync<long?>(@"
            INSERT OR IGNORE INTO Tag (Name) VALUES (@Name);
            SELECT Id FROM Tag WHERE Name = @Name;",
            new { Name = name.Trim() });
        return id ?? 0;
    }

    public async Task<List<TagCount>> GetAllTagCountsAsync()
    {
        using var conn = _db.CreateConnection();
        var results = await conn.QueryAsync<TagCount>(@"
            SELECT t.Name, COUNT(it.ImageMetaId) as Count
            FROM Tag t
            INNER JOIN ImageTag it ON t.Id = it.TagId
            GROUP BY t.Id, t.Name
            ORDER BY Count DESC, t.Name");
        return results.ToList();
    }

    public async Task AddFavoriteAsync(string name)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            "INSERT OR IGNORE INTO FavoriteTag (Name) VALUES (@Name)",
            new { Name = name.Trim() });
    }

    public async Task RemoveFavoriteAsync(string name)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            "DELETE FROM FavoriteTag WHERE Name = @Name",
            new { Name = name.Trim() });
    }

    public async Task<RenameResult> RenameTagAsync(string oldName, string newName)
    {
        using var conn = _db.CreateConnection();
        try
        {
            var affected = await conn.ExecuteAsync(
                "UPDATE Tag SET Name = @NewName WHERE Name = @OldName",
                new { OldName = oldName.Trim(), NewName = newName.Trim() });
            if (affected > 0)
            {
                await conn.ExecuteAsync(
                    "UPDATE FavoriteTag SET Name = @NewName WHERE Name = @OldName",
                    new { OldName = oldName.Trim(), NewName = newName.Trim() });
                return RenameResult.Success;
            }
            return RenameResult.Success; // no rows affected means tag didn't exist, but that's fine
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19) // SQLITE_CONSTRAINT
        {
            return RenameResult.Conflict;
        }
    }

    public async Task MergeTagsAsync(string oldName, string newName)
    {
        using var conn = _db.CreateConnection();
        var oldId = await conn.ExecuteScalarAsync<long?>(
            "SELECT Id FROM Tag WHERE Name = @Name", new { Name = oldName.Trim() });
        var newId = await conn.ExecuteScalarAsync<long?>(
            "SELECT Id FROM Tag WHERE Name = @Name", new { Name = newName.Trim() });
        if (oldId == null || newId == null) return;

        await conn.ExecuteAsync(
            "UPDATE OR IGNORE ImageTag SET TagId = @NewId WHERE TagId = @OldId",
            new { OldId = oldId, NewId = newId });
        await conn.ExecuteAsync(
            "DELETE FROM ImageTag WHERE TagId = @OldId",
            new { OldId = oldId });
        await conn.ExecuteAsync(
            "DELETE FROM Tag WHERE Id = @OldId",
            new { OldId = oldId });
        await conn.ExecuteAsync(
            "DELETE FROM FavoriteTag WHERE Name = @OldName",
            new { OldName = oldName.Trim() });
    }

    public async Task<List<string>> GetFavoritesAsync()
    {
        using var conn = _db.CreateConnection();
        var results = await conn.QueryAsync<string>(
            "SELECT Name FROM FavoriteTag ORDER BY Name");
        return results.ToList();
    }

    public async Task<List<string>> SearchTagsAsync(string keyword, int limit = 50)
    {
        using var conn = _db.CreateConnection();
        var results = await conn.QueryAsync<string>(@"
            SELECT Name FROM Tag
            WHERE Name LIKE @Keyword
            ORDER BY Name
            LIMIT @Limit",
            new { Keyword = $"%{keyword}%", Limit = limit });
        return results.ToList();
    }
}
