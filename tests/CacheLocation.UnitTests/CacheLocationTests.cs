using ImageManager.App.Services;
using ImageManager.Infrastructure.Caching;
using ImageManager.Infrastructure.Data;
using ImageManager.Infrastructure.Video;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CacheLocation.UnitTests;

public sealed class CacheLocationTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "ImageManager-cache-tests-" + Guid.NewGuid().ToString("N"));
    private string At(string name) => Path.Combine(_root, name);
    private StartupCacheConfig Load() => StartupCacheConfig.Load(At("settings/config.json"), At("portable.config"));

    [Fact]
    public void FirstLaunchUsesLocalAppDataWithoutCreatingCacheOrConfiguration()
    {
        var config = Load();
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ImageManager", "Cache"), config.CacheDirectory);
        Assert.False(config.ConfigExists);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void SharedChoiceWinsOverNewerPortableRecoveryConfiguration()
    {
        var config = Load();
        config.RequestDirectoryChange(At("chosen"), At("active"));
        File.WriteAllText(At("portable.config"), $"CacheDirectory={At("CacheRecovery")}");
        File.SetLastWriteTimeUtc(At("portable.config"), DateTime.UtcNow.AddHours(1));
        Assert.Equal(At("chosen"), Load().CacheDirectory);
        Assert.False(Directory.Exists(At("CacheRecovery")));
    }

    [Fact]
    public void RepeatedSelectionBeforeRestartKeepsActualDatabaseSource()
    {
        var config = Load();
        config.RequestDirectoryChange(At("first"), At("active"));
        config.RequestDirectoryChange(At("second"), At("active"));
        var reloaded = Load();
        Assert.Equal(At("second"), reloaded.CacheDirectory);
        Assert.Equal(At("active"), reloaded.PreviousCacheDirectory);
        Assert.True(reloaded.CachePromptShown);
        Assert.Empty(Directory.EnumerateFileSystemEntries(At("second")));
    }

    [Fact]
    public void CancelPendingSelectionRestoresOriginalCacheSource()
    {
        var config = Load();
        config.CacheDirectory = At("active");
        config.PreviousCacheDirectory = At("original");
        config.RequestDirectoryChange(At("pending"), At("active"));
        config.RequestDirectoryChange(At("active"), At("active"));
        Assert.Equal(At("original"), Load().PreviousCacheDirectory);
    }

    [Fact]
    public async Task ExistingDirectoryAcceptedWithoutCreatingProbeFiles()
    {
        Directory.CreateDirectory(At("chosen"));
        using var watcher = new FileSystemWatcher(At("chosen"));
        var created = new System.Collections.Concurrent.ConcurrentBag<string>();
        watcher.Created += (_, e) => created.Add(e.Name!);
        watcher.EnableRaisingEvents = true;
        Assert.True(StartupCacheConfig.TryPrepareDirectory(At("chosen"), out var error), error);
        await Task.Delay(100); // Allow the filesystem event queue to deliver any probe creation.
        Assert.Empty(Directory.EnumerateFileSystemEntries(At("chosen")));
        Assert.Empty(created);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative-cache")]
    [InlineData("G:\\bad\nCacheDirectory=E:\\wrong")]
    public void InvalidSelectionDoesNotReplaceSavedChoice(string path)
    {
        var config = Load();
        config.RequestDirectoryChange(At("chosen"), At("active"));
        Assert.ThrowsAny<Exception>(() => config.RequestDirectoryChange(path, At("active")));
        Assert.Equal(At("chosen"), config.CacheDirectory);
        Assert.Equal(At("chosen"), Load().CacheDirectory);
    }

    [Fact]
    public void FileAsDestinationIsReportedAndDoesNotChangeChoice()
    {
        var config = Load();
        config.RequestDirectoryChange(At("chosen"), At("active"));
        File.WriteAllText(At("occupied"), "user file");
        Assert.Throws<IOException>(() => config.RequestDirectoryChange(At("occupied"), At("active")));
        Assert.Equal(At("chosen"), Load().CacheDirectory);
        Assert.Equal("user file", File.ReadAllText(At("occupied")));
    }

    [Fact]
    public void SaveFailureLeavesPreviousConfigurationAndInMemoryChoiceIntact()
    {
        var config = Load();
        config.RequestDirectoryChange(At("chosen"), At("active"));
        using (File.Open(At("settings/config.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = Record.Exception(() => config.RequestDirectoryChange(At("another"), At("active")));
            Assert.True(error is IOException or UnauthorizedAccessException);
        }
        Assert.Equal(At("chosen"), config.CacheDirectory);
        Assert.Equal(At("chosen"), Load().CacheDirectory);
        Assert.Single(Directory.GetFiles(At("settings")));
    }

    [Fact]
    public void DatabaseMigrationPreservesCommittedWalDataAndSource()
    {
        Directory.CreateDirectory(At("source"));
        Directory.CreateDirectory(At("destination"));
        using var source = Open(At("source/data.db"));
        using var command = source.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE test(value TEXT); INSERT INTO test VALUES ('saved');";
        command.ExecuteNonQuery();
        Assert.True(File.Exists(At("source/data.db-wal")));
        CacheDatabaseMigration.MigrateIfMissing(At("destination/data.db"), At("source"));
        using var destination = Open(At("destination/data.db"));
        using var query = destination.CreateCommand();
        query.CommandText = "SELECT value FROM test;";
        Assert.Equal("saved", query.ExecuteScalar());
        command.CommandText = "SELECT value FROM test;";
        Assert.Equal("saved", command.ExecuteScalar());
        Assert.DoesNotContain(Directory.GetFiles(At("destination")), p => p.Contains(".migrating"));
    }

    [Fact]
    public void ExistingDestinationDatabaseIsNeverOverwritten()
    {
        Directory.CreateDirectory(At("source"));
        Directory.CreateDirectory(At("destination"));
        File.WriteAllText(At("source/data.db"), "source");
        File.WriteAllText(At("destination/data.db"), "destination");
        CacheDatabaseMigration.MigrateIfMissing(At("destination/data.db"), At("source"));
        Assert.Equal("destination", File.ReadAllText(At("destination/data.db")));
        Assert.Equal("source", File.ReadAllText(At("source/data.db")));
    }

    [Fact]
    public void FailedDatabaseMigrationDoesNotLeavePartialDatabaseOrRemoveSource()
    {
        Directory.CreateDirectory(At("source"));
        Directory.CreateDirectory(At("destination"));
        File.WriteAllText(At("source/data.db"), "invalid sqlite input");
        Assert.Throws<IOException>(() =>
            CacheDatabaseMigration.MigrateIfMissing(At("destination/data.db"), At("source")));
        Assert.Empty(Directory.GetFiles(At("destination")));
        Assert.Equal("invalid sqlite input", File.ReadAllText(At("source/data.db")));
    }

    [Fact]
    public async Task RestartedServicesUseSelectedRootAndReuseExistingThumbnailsAndFrames()
    {
        var config = Load();
        config.RequestDirectoryChange(At("chosen"), At("deleted-old-cache"));
        var root = Load().CacheDirectory;
        var file = At("video.mp4");
        var disk = new DiskThumbnailCache(root, 512);
        byte[] bytes = [1, 2, 3];
        disk.Save(file, bytes);
        var frames = new VideoOriginalFrameCacheService(root);
        var framePath = frames.GetOriginalFramePath(file);
        Directory.CreateDirectory(Path.GetDirectoryName(framePath)!);
        File.WriteAllBytes(framePath, bytes);
        Assert.Equal(bytes, new DiskThumbnailCache(root, 512).Load(file));
        var frame = await new VideoOriginalFrameCacheService(root).EnsureAsync(file);
        Assert.True(frame.AlreadyExisted);
        Assert.Equal(framePath, frame.Path);
        Assert.False(Directory.Exists(At("deleted-old-cache")));
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
