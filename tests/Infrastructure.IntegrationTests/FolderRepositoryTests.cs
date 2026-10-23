using ImageManager.Infrastructure.Data;
using ImageManager.Infrastructure.Data.Repositories;
using ImageManager.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Infrastructure.IntegrationTests;

public sealed class FolderRepositoryTests : IDisposable
{
    private readonly string _testRoot;
    private readonly DbContextFactory _dbFactory;
    private readonly HashDataVersionTracker _versionTracker;
    private readonly FolderRepository _folderRepo;
    private readonly ImageMetaRepository _metaRepo;

    public FolderRepositoryTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "IM2-FolderRepoTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
        var dbPath = Path.Combine(_testRoot, "folders.db");
        _dbFactory = new DbContextFactory(dbPath);

        using var conn = _dbFactory.CreateConnection();
        DatabaseInitializer.Initialize(conn);

        _versionTracker = new HashDataVersionTracker();
        _folderRepo = new FolderRepository(_dbFactory, _versionTracker);
        _metaRepo = new ImageMetaRepository(_dbFactory, _versionTracker);
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
    public async Task AddAsync_And_GetAllAsync_PersistsFolders()
    {
        var path = Path.Combine(_testRoot, "my_folder");
        await _folderRepo.AddAsync(path);

        var folders = await _folderRepo.GetAllAsync();
        Assert.Single(folders);
        Assert.Equal(path, folders[0].Path);
        Assert.False(folders[0].IsArchived);

        var byPath = await _folderRepo.GetByPathAsync(path);
        Assert.NotNull(byPath);
        Assert.Equal(path, byPath.Path);

        // Case-insensitive query
        var byUpper = await _folderRepo.GetByPathAsync(path.ToUpperInvariant());
        Assert.NotNull(byUpper);
    }

    [Fact]
    public async Task UpdateAliasAsync_UpdatesFolderAlias()
    {
        var path = Path.Combine(_testRoot, "work");
        await _folderRepo.AddAsync(path);

        await _folderRepo.UpdateAliasAsync(path, "工作资料");
        var folder = await _folderRepo.GetByPathAsync(path);
        Assert.Equal("工作资料", folder!.Alias);

        await _folderRepo.UpdateAliasAsync(path, null);
        folder = await _folderRepo.GetByPathAsync(path);
        Assert.Null(folder!.Alias);
    }

    [Fact]
    public async Task SetArchivedAsync_TogglesIsArchived()
    {
        var path = Path.Combine(_testRoot, "archive_me");
        await _folderRepo.AddAsync(path);
        var folder = await _folderRepo.GetByPathAsync(path);

        await _folderRepo.SetArchivedAsync(folder!.Id, true);
        folder = await _folderRepo.GetByPathAsync(path);
        Assert.True(folder!.IsArchived);

        await _folderRepo.SetArchivedAsync(folder.Id, false);
        folder = await _folderRepo.GetByPathAsync(path);
        Assert.False(folder!.IsArchived);
    }

    [Fact]
    public async Task SetLastPageIndexAsync_StoresAndRetrieves()
    {
        var path = Path.Combine(_testRoot, "paged");
        await _folderRepo.AddAsync(path);

        await _folderRepo.SetLastPageIndexAsync(path, 5);
        var page = await _folderRepo.GetLastPageIndexAsync(path);
        Assert.Equal(5, page);
    }

    [Fact]
    public async Task RelocateFolderAsync_UpdatesFolderAndAllContainedImageMetaPaths()
    {
        var oldFolder = Path.Combine(_testRoot, "old_dir");
        var newFolder = Path.Combine(_testRoot, "new_dir");
        await _folderRepo.AddAsync(oldFolder);
        var folder = await _folderRepo.GetByPathAsync(oldFolder);

        // Add 2 images under oldFolder
        var img1 = Path.Combine(oldFolder, "sub", "1.png");
        var img2 = Path.Combine(oldFolder, "2.png");
        await _metaRepo.UpsertAsync(new ImageManager.Core.Models.ImageMeta { FilePath = img1, FolderId = folder!.Id });
        await _metaRepo.UpsertAsync(new ImageManager.Core.Models.ImageMeta { FilePath = img2, FolderId = folder.Id });

        // Act: Relocate folder
        await _folderRepo.RelocateFolderAsync(folder.Id, newFolder);

        // Assert: Folder updated
        var updatedFolder = await _folderRepo.GetByPathAsync(newFolder);
        Assert.NotNull(updatedFolder);
        Assert.Equal(folder.Id, updatedFolder.Id);

        // Images updated with new path prefix
        var newImg1 = Path.Combine(newFolder, "sub", "1.png");
        var newImg2 = Path.Combine(newFolder, "2.png");
        var meta1 = await _metaRepo.GetByPathAsync(newImg1);
        var meta2 = await _metaRepo.GetByPathAsync(newImg2);
        Assert.NotNull(meta1);
        Assert.NotNull(meta2);

        // Old paths no longer exist in meta
        Assert.Null(await _metaRepo.GetByPathAsync(img1));
        Assert.Null(await _metaRepo.GetByPathAsync(img2));
    }
}
