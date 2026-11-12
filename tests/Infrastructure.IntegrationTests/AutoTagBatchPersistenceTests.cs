using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using ImageManager.Infrastructure.Data;
using ImageManager.Infrastructure.Data.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Infrastructure.IntegrationTests;

public class AutoTagBatchPersistenceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DbContextFactory _dbFactory;

    public AutoTagBatchPersistenceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"batch_persist_test_{Guid.NewGuid():N}.db");
        _dbFactory = new DbContextFactory(_dbPath);

        using var conn = _dbFactory.CreateConnection();
        conn.Open();
        DatabaseInitializer.Initialize(conn);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (File.Exists(_dbPath)) File.Delete(_dbPath);
        }
        catch { /* ignore */ }
    }

    [Fact]
    public async Task SetSystemRatingBatchAsync_UpdatesRatingsCorrectly()
    {
        var repo = new ImageMetaRepository(_dbFactory);

        // 插入 5 条元数据
        using (var conn = _dbFactory.CreateConnection())
        {
            for (int i = 1; i <= 5; i++)
            {
                await conn.ExecuteAsync(@"
                    INSERT INTO ImageMeta (Id, FilePath, SystemRating, CreatedAt, UpdatedAt)
                    VALUES (@Id, @Path, -1, datetime('now'), datetime('now'))",
                    new { Id = i, Path = $"C:\\test\\img{i}.jpg" });
            }
        }

        // 批量更新评级
        var batch = new List<(long ImageId, int Rating)>
        {
            (1, 0), // General
            (2, 1), // Sensitive
            (3, 2), // Questionable
            (4, 3), // Explicit
            (5, 99) // 无效 rating，应被安全忽略
        };

        await repo.SetSystemRatingBatchAsync(batch);

        using (var conn = _dbFactory.CreateConnection())
        {
            var ratings = (await conn.QueryAsync<(long Id, int SystemRating)>(
                "SELECT Id, SystemRating FROM ImageMeta ORDER BY Id")).ToDictionary(x => x.Id, x => x.SystemRating);

            Assert.Equal(0, ratings[1]);
            Assert.Equal(1, ratings[2]);
            Assert.Equal(2, ratings[3]);
            Assert.Equal(3, ratings[4]);
            Assert.Equal(-1, ratings[5]); // 未被非法值修改
        }
    }

    [Fact]
    public async Task AddAutoTagsBatchAsync_InsertsTagsAndLinksEquivalently()
    {
        var repo = new ImageMetaRepository(_dbFactory);

        using (var conn = _dbFactory.CreateConnection())
        {
            for (int i = 1; i <= 3; i++)
            {
                await conn.ExecuteAsync(@"
                    INSERT INTO ImageMeta (Id, FilePath, CreatedAt, UpdatedAt)
                    VALUES (@Id, @Path, datetime('now'), datetime('now'))",
                    new { Id = i, Path = $"C:\\test\\pic{i}.jpg" });
            }
        }

        var batch = new List<(long ImageId, IReadOnlyList<string> TagNames)>
        {
            (1, new List<string> { "1girl", "smile", "blue_hair" }),
            (2, new List<string> { "1girl", "solo", "smile" }),
            (3, new List<string> { "landscape", "outdoors" })
        };

        await repo.AddAutoTagsBatchAsync(batch);

        using (var conn = _dbFactory.CreateConnection())
        {
            // 验证 Tag 表中的总去重数
            var allTags = (await conn.QueryAsync<string>("SELECT Name FROM Tag")).ToList();
            Assert.Contains("1girl", allTags);
            Assert.Contains("smile", allTags);
            Assert.Contains("blue_hair", allTags);
            Assert.Contains("solo", allTags);
            Assert.Contains("landscape", allTags);
            Assert.Contains("outdoors", allTags);
            Assert.Equal(6, allTags.Count);

            // 验证各图片的标签关联
            var img1Tags = (await conn.QueryAsync<string>(@"
                SELECT t.Name FROM Tag t
                INNER JOIN ImageTag it ON t.Id = it.TagId
                WHERE it.ImageMetaId = 1 AND it.Source = 'AutoTag'")).ToList();

            Assert.Equal(3, img1Tags.Count);
            Assert.Contains("1girl", img1Tags);
            Assert.Contains("smile", img1Tags);
            Assert.Contains("blue_hair", img1Tags);

            var img2Tags = (await conn.QueryAsync<string>(@"
                SELECT t.Name FROM Tag t
                INNER JOIN ImageTag it ON t.Id = it.TagId
                WHERE it.ImageMetaId = 2 AND it.Source = 'AutoTag'")).ToList();

            Assert.Equal(3, img2Tags.Count);
            Assert.Contains("1girl", img2Tags);
            Assert.Contains("solo", img2Tags);
            Assert.Contains("smile", img2Tags);
        }
    }
}
