using CommunityToolkit.Mvvm.Messaging;
using ImageManager.Core.Models;
using ImageManager.Infrastructure.Services;
using ImageManager.Infrastructure.Data;
using ImageManager.Infrastructure.Data.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace TagSearchEngine.UnitTests;

public sealed class TagSearchEngineTests
{
    [Fact]
    public void TypedSuggestionsUseActiveTokenAndPrefixPriority()
    {
        var engine = new ImageManager.Infrastructure.Services.TagSearchEngine(null!, WeakReferenceMessenger.Default);
        engine.SetAllTagCounts(new List<TagCount>
        {
            new() { Name = "wildcat", Count = 100 },
            new() { Name = "cat", Count = 1 },
            new() { Name = "cater", Count = 50 }
        });

        var results = engine.GetTypedSuggestions("cats ca");

        Assert.Equal(new[] { "cater", "cat", "wildcat" }, results.Select(x => x.Name));
    }

    [Fact]
    public void BlankInputReturnsNoSuggestions()
    {
        var engine = new ImageManager.Infrastructure.Services.TagSearchEngine(null!, WeakReferenceMessenger.Default);
        engine.SetAllTagCounts(new List<TagCount> { new() { Name = "cat", Count = 1 } });

        Assert.Empty(engine.GetTypedSuggestions("   "));
    }

    [Fact]
    public async Task CoTagSessionFindsTagOutsideOverviewSample()
    {
        var tempDirectory = Path.Combine(AppContext.BaseDirectory, "TestData");
        Directory.CreateDirectory(tempDirectory);
        var databasePath = Path.Combine(tempDirectory, $"imagemanager-cotag-{Guid.NewGuid():N}.db");
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE ImageMeta (
                        Id INTEGER PRIMARY KEY,
                        FilePath TEXT NOT NULL UNIQUE COLLATE NOCASE
                    );
                    CREATE TABLE Tag (
                        Id INTEGER PRIMARY KEY,
                        Name TEXT NOT NULL UNIQUE COLLATE NOCASE
                    );
                    CREATE TABLE ImageTag (
                        ImageMetaId INTEGER NOT NULL,
                        TagId INTEGER NOT NULL,
                        PRIMARY KEY (ImageMetaId, TagId)
                    );
                    CREATE INDEX idx_imagetag_image ON ImageTag(ImageMetaId);
                    CREATE INDEX idx_imagetag_tag ON ImageTag(TagId);

                    WITH RECURSIVE ids(value) AS (
                        SELECT 1
                        UNION ALL
                        SELECT value + 1 FROM ids WHERE value < 6001
                    )
                    INSERT INTO ImageMeta(Id, FilePath)
                    SELECT value, printf('image-%04d.jpg', value) FROM ids;

                    WITH RECURSIVE tag_ids(value) AS (
                        SELECT 1
                        UNION ALL
                        SELECT value + 1 FROM tag_ids WHERE value < 301
                    )
                    INSERT INTO Tag(Id, Name)
                    SELECT value, printf('common-%03d', value) FROM tag_ids;

                    INSERT INTO Tag(Id, Name) VALUES (302, 'rare-target');

                    WITH RECURSIVE tag_ids(value) AS (
                        SELECT 1
                        UNION ALL
                        SELECT value + 1 FROM tag_ids WHERE value < 301
                    )
                    INSERT INTO ImageTag(ImageMetaId, TagId)
                    SELECT 1, value FROM tag_ids;

                    INSERT INTO ImageTag(ImageMetaId, TagId) VALUES (6001, 302);
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var repository = new ImageMetaRepository(new DbContextFactory(databasePath));
            var paths = Enumerable.Range(1, 6001)
                .Select(i => $"image-{i:0000}.jpg")
                .ToList();

            await using (var session = await repository.CreateCoTagQuerySessionAsync(paths))
            {
                var results = await session.QueryAsync("rare");
                var match = Assert.Single(results);
                Assert.Equal("rare-target", match.Name);
                Assert.Equal(1, match.Count);
            }

            await using (var secondSession = await repository.CreateCoTagQuerySessionAsync(paths))
            {
                var secondResults = await secondSession.QueryAsync("rare");
                Assert.Single(secondResults);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
        }
    }
}
