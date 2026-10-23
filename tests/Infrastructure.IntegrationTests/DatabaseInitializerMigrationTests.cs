using Dapper;
using ImageManager.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Infrastructure.IntegrationTests;

public sealed class DatabaseInitializerMigrationTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _dbPath;

    public DatabaseInitializerMigrationTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "IM2-DbInitTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
        _dbPath = Path.Combine(_testRoot, "init_test.db");
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
    public void Initialize_FreshDatabase_CreatesAllTablesAndSetsVersionTo1()
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();

        // Act
        DatabaseInitializer.Initialize(conn);

        // Assert: user_version is 1
        var version = conn.ExecuteScalar<int>("PRAGMA user_version;");
        Assert.Equal(1, version);

        // Tables created
        var tables = conn.Query<string>("SELECT name FROM sqlite_master WHERE type='table'").ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("ImageMeta", tables);
        Assert.Contains("Tag", tables);
        Assert.Contains("ImageTag", tables);
        Assert.Contains("Folder", tables);
        Assert.Contains("FavoriteTag", tables);
        Assert.Contains("AppSetting", tables);
        Assert.Contains("TagMapping", tables);
        Assert.Contains("AutoTagState", tables);
        Assert.Contains("ImageEmbedding", tables);
        Assert.Contains("SuppressedCharacterTag", tables);
        Assert.Contains("MediaMoveJournal", tables);
    }

    [Fact]
    public void Initialize_LegacyDatabaseVersion0_MissingColumns_SuccessfullyMigratesAndPreservesData()
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();

        // 1. Setup a legacy v0 database with missing columns and old tags
        conn.Execute("""
            PRAGMA user_version = 0;

            CREATE TABLE ImageMeta (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                FilePath        TEXT NOT NULL UNIQUE,
                FileHash        TEXT,
                PerceptualHash  TEXT,
                Width           INTEGER DEFAULT 0,
                Height          INTEGER DEFAULT 0,
                FileSize        INTEGER DEFAULT 0,
                LastWriteTicks  INTEGER DEFAULT 0,
                CreatedAt       TEXT NOT NULL DEFAULT (datetime('now')),
                UpdatedAt       TEXT NOT NULL DEFAULT (datetime('now'))
            );

            CREATE TABLE Tag (
                Id   INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL UNIQUE
            );

            CREATE TABLE ImageTag (
                ImageMetaId INTEGER NOT NULL,
                TagId       INTEGER NOT NULL,
                PRIMARY KEY (ImageMetaId, TagId)
            );

            CREATE TABLE Folder (
                Id            INTEGER PRIMARY KEY AUTOINCREMENT,
                Path          TEXT NOT NULL UNIQUE,
                Alias         TEXT,
                SortOrder     INTEGER DEFAULT 0,
                LastPageIndex INTEGER DEFAULT 0
            );

            INSERT INTO ImageMeta (Id, FilePath, FileHash, Width, Height)
            VALUES (1, 'C:\legacy\img1.png', 'hash1', 800, 600);

            INSERT INTO Tag (Id, Name) VALUES (10, 'sensitive');
            INSERT INTO ImageTag (ImageMetaId, TagId) VALUES (1, 10);
            """);

        // Act: Initialize triggers RunMigrations and BackfillSystemRatings
        DatabaseInitializer.Initialize(conn);

        // Assert: Version updated to 1
        var version = conn.ExecuteScalar<int>("PRAGMA user_version;");
        Assert.Equal(1, version);

        // Columns added to ImageMeta
        var columns = conn.Query<string>("SELECT name FROM pragma_table_info('ImageMeta')").ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("FolderId", columns);
        Assert.Contains("SystemRating", columns);
        Assert.Contains("AutoTagStatus", columns);
        Assert.Contains("HashStatus", columns);
        Assert.Contains("Duration", columns);
        Assert.Contains("ThumbnailTimestamp", columns);

        // Folder column added
        var folderColumns = conn.Query<string>("SELECT name FROM pragma_table_info('Folder')").ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("IsArchived", folderColumns);

        // MediaMoveJournal table created
        var tables = conn.Query<string>("SELECT name FROM sqlite_master WHERE type='table'").ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("MediaMoveJournal", tables);

        // Existing data preserved
        var row = conn.QuerySingle("SELECT FilePath, FileHash, Width, Height, SystemRating FROM ImageMeta WHERE Id = 1");
        Assert.Equal(@"C:\legacy\img1.png", (string)row.FilePath);
        Assert.Equal("hash1", (string)row.FileHash);
        Assert.Equal(800, (int)row.Width);
        Assert.Equal(600, (int)row.Height);

        // Legacy tag 'sensitive' was backfilled to SystemRating = 1
        Assert.Equal(1, (int)row.SystemRating);

        // Assert ImageTag and Tag relationships are fully preserved
        var tagCount = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM Tag WHERE Id = 10 AND Name = 'sensitive'");
        Assert.Equal(1, tagCount);
        var imageTagCount = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM ImageTag WHERE ImageMetaId = 1 AND TagId = 10");
        Assert.Equal(1, imageTagCount);

        var joinedTag = conn.QuerySingle<string>(
            "SELECT t.Name FROM Tag t INNER JOIN ImageTag it ON t.Id = it.TagId WHERE it.ImageMetaId = 1");
        Assert.Equal("sensitive", joinedTag);
    }

    [Fact]
    public void Initialize_MigrationFailure_RollsBackTransaction_AndRetainsOriginalVersionAndData()
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();

        // 1. Setup a legacy v0 database with an image and a tag
        conn.Execute("""
            PRAGMA user_version = 0;

            CREATE TABLE ImageMeta (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                FilePath        TEXT NOT NULL UNIQUE,
                FileHash        TEXT,
                PerceptualHash  TEXT,
                Width           INTEGER DEFAULT 0,
                Height          INTEGER DEFAULT 0,
                FileSize        INTEGER DEFAULT 0,
                LastWriteTicks  INTEGER DEFAULT 0,
                CreatedAt       TEXT NOT NULL DEFAULT (datetime('now')),
                UpdatedAt       TEXT NOT NULL DEFAULT (datetime('now'))
            );

            CREATE TABLE Tag (
                Id   INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL UNIQUE
            );

            CREATE TABLE ImageTag (
                ImageMetaId INTEGER NOT NULL,
                TagId       INTEGER NOT NULL,
                PRIMARY KEY (ImageMetaId, TagId)
            );

            INSERT INTO ImageMeta (Id, FilePath, FileHash, Width, Height)
            VALUES (1, 'C:\legacy\fail_test.png', 'hash_orig', 100, 100);

            INSERT INTO Tag (Id, Name) VALUES (5, 'sensitive');
            INSERT INTO ImageTag (ImageMetaId, TagId) VALUES (1, 5);

            -- Install trigger that causes BackfillSystemRatingsFromLegacyTags to fail on UPDATE
            CREATE TRIGGER trg_fail_migration
            BEFORE UPDATE ON ImageMeta
            BEGIN
                SELECT RAISE(ABORT, 'Simulated mid-migration failure');
            END;
            """);

        // Record baseline schema before migration attempt
        var baselineMaster = conn.Query<string>(
            "SELECT sql FROM sqlite_master WHERE type IN ('table', 'index', 'trigger') AND sql IS NOT NULL ORDER BY name").ToList();
        var baselineColumns = conn.Query<string>(
            "SELECT name FROM pragma_table_info('ImageMeta') ORDER BY cid").ToList();

        // Act: Initialize should fail inside the transaction
        var ex = Assert.Throws<SqliteException>(() => DatabaseInitializer.Initialize(conn));
        Assert.Contains("Simulated mid-migration failure", ex.Message);

        // Close connection and reopen a fresh connection to verify on-disk persistent rollback
        conn.Close();
        using var conn2 = new SqliteConnection($"Data Source={_dbPath}");
        conn2.Open();

        // Assert: Entire transaction rolled back!
        // user_version is still 0
        var versionAfterFailure = conn2.ExecuteScalar<int>("PRAGMA user_version;");
        Assert.Equal(0, versionAfterFailure);

        // Complete schema rollback assertion: tables, indices, and triggers match baseline exactly
        var schemaAfterFailure = conn2.Query<string>(
            "SELECT sql FROM sqlite_master WHERE type IN ('table', 'index', 'trigger') AND sql IS NOT NULL ORDER BY name").ToList();
        Assert.Equal(baselineMaster, schemaAfterFailure);

        // Columns of ImageMeta must match baseline; new columns like SystemRating must not leak
        var columnsAfterFailure = conn2.Query<string>(
            "SELECT name FROM pragma_table_info('ImageMeta') ORDER BY cid").ToList();
        Assert.Equal(baselineColumns, columnsAfterFailure);
        Assert.DoesNotContain("SystemRating", columnsAfterFailure);

        // New tables introduced in schema must not have been committed
        var tablesAfterFailure = conn2.Query<string>(
            "SELECT name FROM sqlite_master WHERE type = 'table'").ToList();
        Assert.DoesNotContain("Folder", tablesAfterFailure);

        // Original data is intact
        var row = conn2.QuerySingle("SELECT FilePath, FileHash FROM ImageMeta WHERE Id = 1");
        Assert.Equal(@"C:\legacy\fail_test.png", (string)row.FilePath);
        Assert.Equal("hash_orig", (string)row.FileHash);

        var imageTagCount = conn2.ExecuteScalar<int>("SELECT COUNT(*) FROM ImageTag WHERE ImageMetaId = 1 AND TagId = 5");
        Assert.Equal(1, imageTagCount);

        // Now remove the fault trigger and re-initialize
        conn2.Execute("DROP TRIGGER trg_fail_migration;");

        // Act 2: Re-run initialization
        DatabaseInitializer.Initialize(conn2);

        // Assert: Now version is 1 and system rating was backfilled
        var versionAfterRecovery = conn2.ExecuteScalar<int>("PRAGMA user_version;");
        Assert.Equal(1, versionAfterRecovery);

        var recoveredRow = conn2.QuerySingle("SELECT FilePath, SystemRating FROM ImageMeta WHERE Id = 1");
        Assert.Equal(@"C:\legacy\fail_test.png", (string)recoveredRow.FilePath);
        Assert.Equal(1, (int)recoveredRow.SystemRating);
    }

    [Fact]
    public void Initialize_FutureDatabaseVersion_ThrowsInvalidOperationException()
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();

        conn.Execute("PRAGMA user_version = 999;");

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() => DatabaseInitializer.Initialize(conn));
        Assert.Contains("999", ex.Message);
        Assert.Contains("高于当前程序支持的版本", ex.Message);
    }
}
