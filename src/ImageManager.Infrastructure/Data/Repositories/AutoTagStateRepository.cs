using Dapper;
using ImageManager.Core.Models;
using ImageManager.Core.Services;

namespace ImageManager.Infrastructure.Data.Repositories;

public class AutoTagStateRepository : IAutoTagStateRepository
{
    private readonly AppDbContext _db;

    public AutoTagStateRepository(AppDbContext db) => _db = db;

    public async Task<AutoTagState?> GetStateAsync(long folderId)
    {
        using var conn = _db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<AutoTagState>(
            "SELECT * FROM AutoTagState WHERE FolderId = @FolderId",
            new { FolderId = folderId });
    }

    public async Task UpsertStateAsync(AutoTagState state)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(@"
            INSERT INTO AutoTagState (FolderId, Status, TotalFiles, Processed, LastFileCount,
                StartedAt, CompletedAt, ErrorMsg)
            VALUES (@FolderId, @Status, @TotalFiles, @Processed, @LastFileCount,
                @StartedAt, @CompletedAt, @ErrorMsg)
            ON CONFLICT(FolderId) DO UPDATE SET
                Status = @Status, TotalFiles = @TotalFiles, Processed = @Processed,
                LastFileCount = @LastFileCount, StartedAt = @StartedAt,
                CompletedAt = @CompletedAt, ErrorMsg = @ErrorMsg",
            state);
    }

    public async Task DeleteStateAsync(long folderId)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            "DELETE FROM AutoTagState WHERE FolderId = @FolderId",
            new { FolderId = folderId });
    }

    public async Task<List<(string EnglishTag, string? ChineseTranslation, string? UserEditedText,
        bool IsConfirmed, bool IsExistingMapping)>> GetTranslationsAsync(long folderId)
    {
        using var conn = _db.CreateConnection();
        var rows = await conn.QueryAsync<dynamic>(
            "SELECT EnglishTag, ChineseTranslation, UserEditedText, IsConfirmed, IsExistingMapping " +
            "FROM AutoTagTranslation WHERE FolderId = @FolderId ORDER BY EnglishTag",
            new { FolderId = folderId });

        return rows.Select(r => (
            EnglishTag: (string)r.EnglishTag,
            ChineseTranslation: (string?)r.ChineseTranslation,
            UserEditedText: (string?)r.UserEditedText,
            IsConfirmed: (long)r.IsConfirmed != 0,
            IsExistingMapping: (long)r.IsExistingMapping != 0
        )).ToList();
    }

    public async Task SaveTranslationAsync(long folderId, string englishTag,
        string? chineseTranslation, string? userEditedText,
        bool isConfirmed, bool isExistingMapping)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(@"
            INSERT INTO AutoTagTranslation (FolderId, EnglishTag, ChineseTranslation,
                UserEditedText, IsConfirmed, IsExistingMapping)
            VALUES (@FolderId, @EnglishTag, @ChineseTranslation,
                @UserEditedText, @IsConfirmed, @IsExistingMapping)
            ON CONFLICT(FolderId, EnglishTag COLLATE NOCASE) DO UPDATE SET
                ChineseTranslation = @ChineseTranslation,
                UserEditedText = @UserEditedText,
                IsConfirmed = @IsConfirmed,
                IsExistingMapping = @IsExistingMapping",
            new { FolderId = folderId, EnglishTag = englishTag, ChineseTranslation = chineseTranslation,
                UserEditedText = userEditedText, IsConfirmed = isConfirmed ? 1 : 0,
                IsExistingMapping = isExistingMapping ? 1 : 0 });
    }

    public async Task DeleteTranslationsAsync(long folderId)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            "DELETE FROM AutoTagTranslation WHERE FolderId = @FolderId",
            new { FolderId = folderId });
    }

    public async Task DeleteTranslationAsync(long folderId, string englishTag)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            "DELETE FROM AutoTagTranslation WHERE FolderId = @FolderId AND EnglishTag = @Tag COLLATE NOCASE",
            new { FolderId = folderId, Tag = englishTag });
    }
}
