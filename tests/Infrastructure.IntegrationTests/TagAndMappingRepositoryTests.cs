using ImageManager.Infrastructure.Data;
using ImageManager.Infrastructure.Data.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Infrastructure.IntegrationTests;

public sealed class TagAndMappingRepositoryTests : IDisposable
{
    private readonly string _testRoot;
    private readonly DbContextFactory _dbFactory;
    private readonly TagRepository _tagRepo;
    private readonly TagMappingRepository _mappingRepo;

    public TagAndMappingRepositoryTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "IM2-TagRepoTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
        var dbPath = Path.Combine(_testRoot, "tags.db");
        _dbFactory = new DbContextFactory(dbPath);

        using var conn = _dbFactory.CreateConnection();
        DatabaseInitializer.Initialize(conn);

        _tagRepo = new TagRepository(_dbFactory);
        _mappingRepo = new TagMappingRepository(_dbFactory);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_testRoot))
                Directory.Delete(_testRoot, true);
        }
        catch { }
    }

    [Fact]
    public async Task TagMappingRepository_Upsert_InsertsAndUpdatesOnConflictCaseInsensitive()
    {
        // Act 1: Insert new mapping
        await _mappingRepo.UpsertAsync("1girl", "单人少女");

        var map1 = await _mappingRepo.GetByEnglishNameAsync("1girl");
        Assert.NotNull(map1);
        Assert.Equal("单人少女", map1.ChineseName);

        // Act 2: Case-insensitive query
        var mapCase = await _mappingRepo.GetByEnglishNameAsync("1GIRL");
        Assert.NotNull(mapCase);
        Assert.Equal("单人少女", mapCase.ChineseName);

        // Act 3: Upsert with different casing updates in-place
        await _mappingRepo.UpsertAsync("1Girl", "独生女");
        var mapUpdated = await _mappingRepo.GetByEnglishNameAsync("1girl");
        Assert.NotNull(mapUpdated);
        Assert.Equal("独生女", mapUpdated.ChineseName);

        var all = await _mappingRepo.GetAllAsync();
        Assert.Single(all);

        // Act 4: Delete mapping
        await _mappingRepo.DeleteAsync("1GIRL");
        var mapDeleted = await _mappingRepo.GetByEnglishNameAsync("1girl");
        Assert.Null(mapDeleted);
    }

    [Fact]
    public async Task TagRepository_GetOrCreateTagId_AssignsAndReturnsSameId()
    {
        var id1 = await _tagRepo.GetOrCreateTagIdAsync("landscape");
        var id2 = await _tagRepo.GetOrCreateTagIdAsync("landscape");

        Assert.True(id1 > 0);
        Assert.Equal(id1, id2);

        var search = await _tagRepo.SearchTagsAsync("land");
        Assert.Contains("landscape", search);
    }

    [Fact]
    public async Task TagRepository_Favorites_AddRemoveAndQuery()
    {
        await _tagRepo.AddFavoriteAsync("fav_tag");

        var favs = await _tagRepo.GetFavoritesAsync();
        Assert.Contains("fav_tag", favs);

        await _tagRepo.RemoveFavoriteAsync("fav_tag");
        favs = await _tagRepo.GetFavoritesAsync();
        Assert.DoesNotContain("fav_tag", favs);
    }
}
