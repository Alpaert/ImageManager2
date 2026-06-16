using Dapper;
using ImageManager.Core.Models;
using ImageManager.Core.Services;

namespace ImageManager.Infrastructure.Data.Repositories;

public class AutoTagStateRepository : IAutoTagStateRepository
{
    private readonly IDbContextFactory _dbFactory;

    public AutoTagStateRepository(IDbContextFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<AutoTagState?> GetStateAsync(long folderId)
    {
        using var conn = _dbFactory.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<AutoTagState>(
            "SELECT * FROM AutoTagState WHERE FolderId = @FolderId",
            new { FolderId = folderId });
    }

    public async Task UpsertStateAsync(AutoTagState state)
    {
        using var conn = _dbFactory.CreateConnection();
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
        using var conn = _dbFactory.CreateConnection();
        await conn.ExecuteAsync(
            "DELETE FROM AutoTagState WHERE FolderId = @FolderId",
            new { FolderId = folderId });
    }
}
