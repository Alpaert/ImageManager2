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
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
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
                UpdatedAt       TEXT NOT NULL DEFAULT (datetime('now'))
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
        ";
        cmd.ExecuteNonQuery();

        // Migration: add FolderId column (must run before index creation)
        RunMigration(conn);

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

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
    }
}
