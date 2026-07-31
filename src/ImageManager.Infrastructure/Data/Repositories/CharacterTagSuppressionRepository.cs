using Dapper;
using ImageManager.Core.Services;

namespace ImageManager.Infrastructure.Data.Repositories;

public sealed class CharacterTagSuppressionRepository : ICharacterTagSuppressionRepository
{
    private readonly IDbContextFactory _dbFactory;

    public CharacterTagSuppressionRepository(IDbContextFactory dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public Task SuppressAsync(long imageMetaId, IEnumerable<string> tagNames)
    {
        var rows = tagNames
            .Select(t => t.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(t => (imageMetaId, t))
            .ToList();
        return SuppressBatchAsync(rows);
    }

    public async Task SuppressBatchAsync(IReadOnlyList<(long ImageMetaId, string TagName)> suppressions)
    {
        if (suppressions.Count == 0) return;

        using var conn = _dbFactory.CreateConnection();
        using var txn = conn.BeginTransaction();
        foreach (var (imageMetaId, tagName) in Normalize(suppressions))
        {
            await conn.ExecuteAsync(@"
                INSERT OR IGNORE INTO SuppressedCharacterTag (ImageMetaId, TagName)
                VALUES (@ImageMetaId, @TagName)",
                new { ImageMetaId = imageMetaId, TagName = tagName },
                txn);
        }
        txn.Commit();
    }

    public Task UnsuppressAsync(long imageMetaId, IEnumerable<string> tagNames)
    {
        var rows = tagNames
            .Select(t => t.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(t => (imageMetaId, t))
            .ToList();
        return UnsuppressBatchAsync(rows);
    }

    public async Task UnsuppressBatchAsync(IReadOnlyList<(long ImageMetaId, string TagName)> suppressions)
    {
        if (suppressions.Count == 0) return;

        using var conn = _dbFactory.CreateConnection();
        using var txn = conn.BeginTransaction();
        foreach (var (imageMetaId, tagName) in Normalize(suppressions))
        {
            await conn.ExecuteAsync(@"
                DELETE FROM SuppressedCharacterTag
                WHERE ImageMetaId = @ImageMetaId
                  AND TagName = @TagName COLLATE NOCASE",
                new { ImageMetaId = imageMetaId, TagName = tagName },
                txn);
        }
        txn.Commit();
    }

    public async Task<HashSet<string>> GetSuppressedTagsAsync(long imageMetaId)
    {
        using var conn = _dbFactory.CreateConnection();
        var tags = await conn.QueryAsync<string>(
            "SELECT TagName FROM SuppressedCharacterTag WHERE ImageMetaId = @ImageMetaId",
            new { ImageMetaId = imageMetaId });
        return tags.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<Dictionary<long, HashSet<string>>> GetSuppressedTagsAsync(IEnumerable<long> imageMetaIds)
    {
        var ids = imageMetaIds.Distinct().ToList();
        var result = new Dictionary<long, HashSet<string>>();
        if (ids.Count == 0) return result;

        using var conn = _dbFactory.CreateConnection();
        foreach (var chunk in ids.Chunk(900))
        {
            var rows = await conn.QueryAsync<(long ImageMetaId, string TagName)>(@"
                SELECT ImageMetaId, TagName
                FROM SuppressedCharacterTag
                WHERE ImageMetaId IN @Ids",
                new { Ids = chunk });

            foreach (var (imageMetaId, tagName) in rows)
            {
                if (!result.TryGetValue(imageMetaId, out var tags))
                {
                    tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    result[imageMetaId] = tags;
                }
                tags.Add(tagName);
            }
        }

        return result;
    }

    private static IEnumerable<(long ImageMetaId, string TagName)> Normalize(
        IEnumerable<(long ImageMetaId, string TagName)> rows)
    {
        return rows
            .Where(r => r.ImageMetaId > 0 && !string.IsNullOrWhiteSpace(r.TagName))
            .Select(r => (r.ImageMetaId, r.TagName.Trim()))
            .Distinct();
    }
}
