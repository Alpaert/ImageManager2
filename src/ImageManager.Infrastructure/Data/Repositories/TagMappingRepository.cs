using Dapper;
using ImageManager.Core.Models;
using ImageManager.Core.Services;

namespace ImageManager.Infrastructure.Data.Repositories;

public class TagMappingRepository : ITagMappingRepository
{
    private readonly AppDbContext _db;

    public TagMappingRepository(AppDbContext db) => _db = db;

    public async Task<TagMapping?> GetByEnglishNameAsync(string englishName)
    {
        using var conn = _db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<TagMapping>(
            "SELECT * FROM TagMapping WHERE EnglishName = @Name COLLATE NOCASE",
            new { Name = englishName.Trim() });
    }

    public async Task<List<TagMapping>> GetAllAsync()
    {
        using var conn = _db.CreateConnection();
        var results = await conn.QueryAsync<TagMapping>(
            "SELECT * FROM TagMapping ORDER BY EnglishName");
        return results.ToList();
    }

    public async Task UpsertAsync(string englishName, string chineseName)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(@"
            INSERT INTO TagMapping (EnglishName, ChineseName)
            VALUES (@English, @Chinese)
            ON CONFLICT(EnglishName COLLATE NOCASE) DO UPDATE SET
                ChineseName = @Chinese, UpdatedAt = datetime('now')",
            new { English = englishName.Trim(), Chinese = chineseName.Trim() });
    }

    public async Task DeleteAsync(string englishName)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            "DELETE FROM TagMapping WHERE EnglishName = @Name COLLATE NOCASE",
            new { Name = englishName.Trim() });
    }
}
