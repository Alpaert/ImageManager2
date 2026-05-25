using Microsoft.Data.Sqlite;

namespace ImageManager.Infrastructure.Data;

public class AppDbContext : IDisposable
{
    private readonly string _connectionString;

    public AppDbContext(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _connectionString = $"Data Source={dbPath}";
        InitializeDatabase();
    }

    public SqliteConnection CreateConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        // Enable WAL mode for concurrent read/write
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA cache_size=-8192; PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    private void InitializeDatabase()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS ImageMeta (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                FilePath        TEXT NOT NULL UNIQUE,
                FileHash        TEXT,
                PerceptualHash  TEXT,
                Width           INTEGER DEFAULT 0,
                Height          INTEGER DEFAULT 0,
                FileSize        INTEGER DEFAULT 0,
                LastWriteTicks  INTEGER DEFAULT 0,
                CreatedAt       TEXT NOT NULL DEFAULT (datetime('now')),
                UpdatedAt       TEXT NOT NULL DEFAULT (datetime('now')),
                SystemRating    INTEGER DEFAULT -1
            );

            CREATE INDEX IF NOT EXISTS idx_imagemeta_filepath ON ImageMeta(FilePath);
            CREATE INDEX IF NOT EXISTS idx_imagemeta_filehash ON ImageMeta(FileHash);

            CREATE TABLE IF NOT EXISTS Tag (
                Id   INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL UNIQUE
            );

            CREATE TABLE IF NOT EXISTS ImageTag (
                ImageMetaId INTEGER NOT NULL REFERENCES ImageMeta(Id) ON DELETE CASCADE,
                TagId       INTEGER NOT NULL REFERENCES Tag(Id) ON DELETE CASCADE,
                PRIMARY KEY (ImageMetaId, TagId)
            );

            CREATE INDEX IF NOT EXISTS idx_imagetag_image ON ImageTag(ImageMetaId);
            CREATE INDEX IF NOT EXISTS idx_imagetag_tag ON ImageTag(TagId);

            CREATE TABLE IF NOT EXISTS Folder (
                Id            INTEGER PRIMARY KEY AUTOINCREMENT,
                Path          TEXT NOT NULL UNIQUE,
                Alias         TEXT,
                SortOrder     INTEGER DEFAULT 0,
                LastPageIndex INTEGER DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS FavoriteTag (
                Id   INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL UNIQUE
            );

            CREATE TABLE IF NOT EXISTS AppSetting (
                Key   TEXT PRIMARY KEY,
                Value TEXT
            );

            CREATE TABLE IF NOT EXISTS TagMapping (
                Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                EnglishName TEXT NOT NULL UNIQUE COLLATE NOCASE,
                ChineseName TEXT NOT NULL,
                ConfirmedAt TEXT NOT NULL DEFAULT (datetime('now')),
                UpdatedAt   TEXT NOT NULL DEFAULT (datetime('now'))
            );
            CREATE INDEX IF NOT EXISTS idx_tagmapping_english ON TagMapping(EnglishName COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS AutoTagState (
                FolderId      INTEGER PRIMARY KEY,
                Status        TEXT NOT NULL DEFAULT 'Pending',
                TotalFiles    INTEGER DEFAULT 0,
                Processed     INTEGER DEFAULT 0,
                LastFileCount INTEGER DEFAULT 0,
                StartedAt     TEXT,
                CompletedAt   TEXT,
                ErrorMsg      TEXT,
                FOREIGN KEY (FolderId) REFERENCES Folder(Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS AutoTagTranslation (
                Id                 INTEGER PRIMARY KEY AUTOINCREMENT,
                FolderId           INTEGER NOT NULL,
                EnglishTag         TEXT NOT NULL,
                ChineseTranslation TEXT,
                UserEditedText     TEXT,
                IsConfirmed        INTEGER DEFAULT 0,
                IsExistingMapping  INTEGER DEFAULT 0,
                CreatedAt          TEXT NOT NULL DEFAULT (datetime('now')),
                FOREIGN KEY (FolderId) REFERENCES Folder(Id) ON DELETE CASCADE,
                UNIQUE(FolderId, EnglishTag COLLATE NOCASE)
            );
            CREATE INDEX IF NOT EXISTS idx_autotagtrans_folder ON AutoTagTranslation(FolderId);
        ";
        cmd.ExecuteNonQuery();

        // Migration: add FolderId column (must run before index creation)
        RunMigration(conn);
        RunMigrationV2(conn);
        RunMigrationV3(conn);
        RunMigrationV4(conn);

        // Create FolderId index after column exists
        using var idxCmd = conn.CreateCommand();
        idxCmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_imagemeta_folderid ON ImageMeta(FolderId);";
        idxCmd.ExecuteNonQuery();
    }

    private static void RunMigration(SqliteConnection conn)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "ALTER TABLE ImageMeta ADD COLUMN FolderId INTEGER;";
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.Message.Contains("duplicate column"))
        {
            // Column already exists
        }
    }

    private static void RunMigrationV2(SqliteConnection conn)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "ALTER TABLE ImageTag ADD COLUMN Source TEXT DEFAULT NULL;";
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.Message.Contains("duplicate column"))
        {
            // Column already exists
        }
    }

    private static void RunMigrationV3(SqliteConnection conn)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "ALTER TABLE ImageMeta ADD COLUMN SystemRating INTEGER DEFAULT -1;";
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.Message.Contains("duplicate column"))
        {
        }
    }

    private static void RunMigrationV4(SqliteConnection conn)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "ALTER TABLE ImageMeta ADD COLUMN AutoTagStatus INTEGER DEFAULT 0;";
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.Message.Contains("duplicate column"))
        {
        }
    }

    public void Checkpoint()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
    }
}
