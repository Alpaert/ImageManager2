using System.Text.Json;
using Dapper;
using ImageManager.Infrastructure.Data;
using ImageManager.Infrastructure.Data.Repositories;
using ImageManager.Infrastructure.Hashing;
using ImageManager.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Infrastructure.IntegrationTests;

public sealed class DataMigratorTests : IDisposable
{
    private readonly string _testRoot;
    private readonly DbContextFactory _dbFactory;
    private readonly ImageMetaRepository _metaRepo;
    private readonly TagRepository _tagRepo;
    private readonly FolderRepository _folderRepo;
    private readonly SettingsRepository _settingsRepo;
    private readonly DataMigrator _migrator;

    public DataMigratorTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "IM2-DataMigratorTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
        var dbPath = Path.Combine(_testRoot, "migrator_test.db");
        _dbFactory = new DbContextFactory(dbPath);

        using var conn = _dbFactory.CreateConnection();
        DatabaseInitializer.Initialize(conn);

        var versionTracker = new HashDataVersionTracker();
        _metaRepo = new ImageMetaRepository(_dbFactory, versionTracker);
        _tagRepo = new TagRepository(_dbFactory);
        _folderRepo = new FolderRepository(_dbFactory, versionTracker);
        _settingsRepo = new SettingsRepository(_dbFactory);

        _migrator = new DataMigrator(_metaRepo, _tagRepo, _folderRepo, _settingsRepo);
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
    public async Task MigrateFromJsonAsync_FileNotFound_ReturnsErrorMessage()
    {
        var nonExistentPath = Path.Combine(_testRoot, "missing.json");
        var result = await _migrator.MigrateFromJsonAsync(nonExistentPath);

        Assert.Contains("文件不存在", result);
        Assert.Contains(nonExistentPath, result);
    }

    [Fact]
    public async Task MigrateFromJsonAsync_CorruptedJson_ThrowsJsonException()
    {
        var corruptPath = Path.Combine(_testRoot, "corrupt.json");
        await File.WriteAllTextAsync(corruptPath, "{ not a valid json syntax ... ");

        await Assert.ThrowsAnyAsync<JsonException>(() => _migrator.MigrateFromJsonAsync(corruptPath));
    }

    [Fact]
    public async Task MigrateFromJsonAsync_ValidData_ImportsImagesFoldersFavoritesAndSettings()
    {
        var jsonPath = Path.Combine(_testRoot, "valid_appdata.json");
        var legacyData = new
        {
            Images = new[]
            {
                new
                {
                    FilePath = @"C:\photos\cat.jpg",
                    FileHash = "hash_cat",
                    PerceptualHash = new string('0', 64),
                    Width = 1920,
                    Height = 1080,
                    FileSize = 102400L,
                    LastWriteTicks = 638000000000000000L,
                    Tags = new[] { "cat", "feline", "pet" }
                },
                new
                {
                    FilePath = @"C:\photos\dog.jpg",
                    FileHash = "hash_dog",
                    PerceptualHash = new string('1', 64),
                    Width = 800,
                    Height = 600,
                    FileSize = 51200L,
                    LastWriteTicks = 638000000000000000L,
                    Tags = new[] { "dog", "canine" }
                }
            },
            FolderList = new[] { @"C:\photos", @"C:\wallpapers" },
            FolderAliases = new Dictionary<string, string>
            {
                [@"C:\photos"] = "我的相册"
            },
            FavoriteTags = new[] { "cat", "dog" },
            StartupWidth = 1600.0,
            StartupHeight = 900.0,
            WaterfallMode = "Vertical",
            WallpaperPath = @"C:\photos\bg.png"
        };

        var jsonString = JsonSerializer.Serialize(legacyData);
        await File.WriteAllTextAsync(jsonPath, jsonString);

        // Act
        var result = await _migrator.MigrateFromJsonAsync(jsonPath);

        // Assert summary string
        Assert.Contains("迁移完成：2 张图片、2 个文件夹、2 个常用 Tag。", result);

        // 1. Verify images and tags in SQLite
        var catMeta = await _metaRepo.GetByPathAsync(@"C:\photos\cat.jpg");
        Assert.NotNull(catMeta);
        Assert.Equal("hash_cat", catMeta.FileHash);
        Assert.Equal(1920, catMeta.Width);
        Assert.Equal(3, catMeta.Tags.Count);
        Assert.Contains(catMeta.Tags, t => t.Name == "cat");
        Assert.Contains(catMeta.Tags, t => t.Name == "pet");

        var dogMeta = await _metaRepo.GetByPathAsync(@"C:\photos\dog.jpg");
        Assert.NotNull(dogMeta);
        Assert.Equal("hash_dog", dogMeta.FileHash);
        Assert.Equal(2, dogMeta.Tags.Count);

        // 2. Verify folders and alias
        var folders = await _folderRepo.GetAllAsync();
        Assert.Equal(2, folders.Count);
        var photosFolder = folders.FirstOrDefault(f => f.Path == @"C:\photos");
        Assert.NotNull(photosFolder);
        Assert.Equal("我的相册", photosFolder.Alias);

        // 3. Verify favorite tags
        var favorites = await _tagRepo.GetFavoritesAsync();
        Assert.Contains("cat", favorites);
        Assert.Contains("dog", favorites);

        // 4. Verify settings
        var settings = await _settingsRepo.LoadAsync();
        Assert.Equal(1600.0, settings.StartupWidth);
        Assert.Equal(900.0, settings.StartupHeight);
        Assert.Equal("Vertical", settings.WaterfallMode);
        Assert.Equal(@"C:\photos\bg.png", settings.WallpaperPath);
    }

    [Fact]
    public async Task MigrateFromJsonAsync_PartialFailure_ContinuesAndReportsDetails()
    {
        var jsonPath = Path.Combine(_testRoot, "partial_fail.json");
        var legacyData = new
        {
            Images = new[]
            {
                new
                {
                    FilePath = @"C:\photos\good.jpg",
                    FileHash = "hash_good",
                    Tags = new[] { "tag1" }
                },
                new
                {
                    FilePath = @"C:\photos\trigger_fail.jpg",
                    FileHash = "hash_trigger",
                    Tags = new[] { "tag2" }
                }
            },
            FolderList = new[] { @"C:\photos" },
            FavoriteTags = new[] { "tag1" }
        };

        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(legacyData));

        // Install a trigger that aborts insertion of "trigger_fail.jpg" specifically
        using (var conn = _dbFactory.CreateConnection())
        {
            await conn.ExecuteAsync(@"
                CREATE TRIGGER trg_fail_specific_file
                BEFORE INSERT ON ImageMeta
                FOR EACH ROW
                WHEN NEW.FilePath LIKE '%trigger_fail.jpg'
                BEGIN
                    SELECT RAISE(ABORT, 'Simulated file failure');
                END;");
        }

        // Act
        var result = await _migrator.MigrateFromJsonAsync(jsonPath);

        // Assert: Processed 1 good image, reported 1 failure
        Assert.Contains("迁移完成：1 张图片、1 个文件夹、1 个常用 Tag。", result);
        Assert.Contains("失败 1 项（图片 1、文件夹 0、常用 Tag 0）", result);
        Assert.Contains("trigger_fail.jpg", result);
        Assert.Contains("Simulated file failure", result);

        // Good image was successfully imported
        var good = await _metaRepo.GetByPathAsync(@"C:\photos\good.jpg");
        Assert.NotNull(good);
    }

    [Fact]
    public async Task MigrateFromJsonAsync_UpsertSucceeds_SetTagsFails_RetainsImageRecord()
    {
        var jsonPath = Path.Combine(_testRoot, "tag_fail.json");
        var legacyData = new
        {
            Images = new[]
            {
                new
                {
                    FilePath = @"C:\photos\partial_tag_fail.jpg",
                    FileHash = "hash_partial",
                    Width = 1000,
                    Height = 800,
                    Tags = new[] { "tag_will_fail" }
                }
            }
        };

        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(legacyData));

        // Trigger on ImageTag table causing SetTagsAsync to throw
        using (var conn = _dbFactory.CreateConnection())
        {
            await conn.ExecuteAsync(@"
                CREATE TRIGGER trg_fail_tag_insert
                BEFORE INSERT ON ImageTag
                BEGIN
                    SELECT RAISE(ABORT, 'Simulated tag failure');
                END;");
        }

        // Act
        var result = await _migrator.MigrateFromJsonAsync(jsonPath);

        // Assert: Failure was captured
        Assert.Contains("失败 1 项（图片 1、文件夹 0、常用 Tag 0）", result);
        Assert.Contains("Simulated tag failure", result);

        // Crucial partial commit contract check:
        // Even though SetTagsAsync failed, UpsertAsync had already succeeded and the ImageMeta record is retained!
        var record = await _metaRepo.GetByPathAsync(@"C:\photos\partial_tag_fail.jpg");
        Assert.NotNull(record);
        Assert.Equal("hash_partial", record.FileHash);
        Assert.Equal(1000, record.Width);
    }

    [Fact]
    public async Task MigrateFromJsonAsync_SettingsSaveFails_RetainsPreviouslyImportedRecords()
    {
        var jsonPath = Path.Combine(_testRoot, "settings_fail.json");
        var legacyData = new
        {
            Images = new[]
            {
                new
                {
                    FilePath = @"C:\photos\saved_before_settings_fail.jpg",
                    FileHash = "hash_preserved",
                    Width = 640,
                    Height = 480
                }
            },
            FolderList = new[] { @"C:\photos\subfolder" }
        };

        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(legacyData));

        // Trigger on AppSetting table causing final settings save to fail
        using (var conn = _dbFactory.CreateConnection())
        {
            await conn.ExecuteAsync(@"
                CREATE TRIGGER trg_fail_settings_save
                BEFORE INSERT ON AppSetting
                BEGIN
                    SELECT RAISE(ABORT, 'Simulated settings save failure');
                END;");
        }

        // Act & Assert: Settings save throws exception
        var ex = await Assert.ThrowsAsync<SqliteException>(() => _migrator.MigrateFromJsonAsync(jsonPath));
        Assert.Contains("Simulated settings save failure", ex.Message);

        // Crucial contract check:
        // Previously imported image and folder records are preserved!
        var record = await _metaRepo.GetByPathAsync(@"C:\photos\saved_before_settings_fail.jpg");
        Assert.NotNull(record);
        Assert.Equal("hash_preserved", record.FileHash);

        var folders = await _folderRepo.GetAllAsync();
        Assert.Contains(folders, f => f.Path == @"C:\photos\subfolder");
    }

    [Fact]
    public async Task MigrateFromJsonAsync_JsonLiteralNull_ReturnsParsingFailedMessage()
    {
        var nullJsonPath = Path.Combine(_testRoot, "null_data.json");
        await File.WriteAllTextAsync(nullJsonPath, "null");

        var result = await _migrator.MigrateFromJsonAsync(nullJsonPath);

        Assert.Equal("JSON 数据解析失败。", result);
    }

    [Fact]
    public async Task MigrateFromJsonAsync_EmptyFile_ThrowsJsonException()
    {
        var emptyJsonPath = Path.Combine(_testRoot, "empty.json");
        await File.WriteAllTextAsync(emptyJsonPath, "");

        await Assert.ThrowsAnyAsync<JsonException>(() => _migrator.MigrateFromJsonAsync(emptyJsonPath));
    }

    [Fact]
    public async Task MigrateFromJsonAsync_WhitespaceAndUnicodePaths_HandlesCorrectly()
    {
        var jsonPath = Path.Combine(_testRoot, "paths_test.json");
        var legacyData = new
        {
            Images = new[]
            {
                new { FilePath = "", FileHash = "empty" },
                new { FilePath = "   \t\n  ", FileHash = "whitespace" },
                new { FilePath = @"C:\相册\桜_🌸\cat.jpg", FileHash = "hash_unicode" }
            },
            FolderList = new[] { "", "   ", @"C:\相册\桜_🌸" },
            FavoriteTags = new[] { "", "   ", "🌸桜" }
        };

        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(legacyData));

        var result = await _migrator.MigrateFromJsonAsync(jsonPath);

        Assert.Contains("迁移完成：1 张图片、1 个文件夹、1 个常用 Tag。", result);

        var unicodeImage = await _metaRepo.GetByPathAsync(@"C:\相册\桜_🌸\cat.jpg");
        Assert.NotNull(unicodeImage);
        Assert.Equal("hash_unicode", unicodeImage.FileHash);

        var folders = await _folderRepo.GetAllAsync();
        Assert.Contains(folders, f => f.Path == @"C:\相册\桜_🌸");

        var favorites = await _tagRepo.GetFavoritesAsync();
        Assert.Contains("🌸桜", favorites);
    }

    [Fact]
    public async Task MigrateFromJsonAsync_FailureSummary_TruncatesAtTenDetails()
    {
        var jsonPath = Path.Combine(_testRoot, "many_failures.json");
        var images = Enumerable.Range(1, 15).Select(i => new
        {
            FilePath = $@"C:\photos\fail_{i}.jpg",
            FileHash = $"hash_{i}"
        }).ToArray();

        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(new { Images = images }));

        using (var conn = _dbFactory.CreateConnection())
        {
            await conn.ExecuteAsync(@"
                CREATE TRIGGER trg_fail_all_images
                BEFORE INSERT ON ImageMeta
                BEGIN
                    SELECT RAISE(ABORT, 'Simulated bulk failure');
                END;");
        }

        var result = await _migrator.MigrateFromJsonAsync(jsonPath);

        Assert.Contains("失败 15 项（图片 15、文件夹 0、常用 Tag 0）", result);
        Assert.Contains("示例：", result);

        // Semicolon separator should split exactly 10 examples (details.Count < 10 threshold)
        var exampleSubstring = result.Substring(result.IndexOf("示例：", StringComparison.Ordinal) + 3);
        var detailParts = exampleSubstring.Split('；');
        Assert.Equal(10, detailParts.Length);
    }

    [Fact]
    public async Task MigrateFromJsonAsync_RepeatedMigrationWithExistingData_UpdatesCleanly()
    {
        var jsonPath1 = Path.Combine(_testRoot, "run1.json");
        var legacyData1 = new
        {
            Images = new[]
            {
                new
                {
                    FilePath = @"C:\photos\repeat_test.jpg",
                    FileHash = "hash_v1",
                    Width = 800,
                    Height = 600,
                    Tags = new[] { "tag1" }
                }
            },
            FolderList = new[] { @"C:\photos" }
        };

        await File.WriteAllTextAsync(jsonPath1, JsonSerializer.Serialize(legacyData1));
        var result1 = await _migrator.MigrateFromJsonAsync(jsonPath1);
        Assert.Contains("迁移完成：1 张图片、1 个文件夹、0 个常用 Tag。", result1);

        var meta1 = await _metaRepo.GetByPathAsync(@"C:\photos\repeat_test.jpg");
        Assert.NotNull(meta1);
        Assert.Equal(800, meta1.Width);

        // Run 2: updated data
        var jsonPath2 = Path.Combine(_testRoot, "run2.json");
        var legacyData2 = new
        {
            Images = new[]
            {
                new
                {
                    FilePath = @"C:\photos\repeat_test.jpg",
                    FileHash = "hash_v2",
                    Width = 1920,
                    Height = 1080,
                    Tags = new[] { "tag1", "tag2" }
                }
            },
            FolderList = new[] { @"C:\photos" }
        };

        await File.WriteAllTextAsync(jsonPath2, JsonSerializer.Serialize(legacyData2));
        var result2 = await _migrator.MigrateFromJsonAsync(jsonPath2);
        Assert.Contains("迁移完成：1 张图片、1 个文件夹、0 个常用 Tag。", result2);

        var meta2 = await _metaRepo.GetByPathAsync(@"C:\photos\repeat_test.jpg");
        Assert.NotNull(meta2);
        Assert.Equal("hash_v2", meta2.FileHash);
        Assert.Equal(1920, meta2.Width);
        Assert.Equal(2, meta2.Tags.Count);
    }
}

