using Microsoft.Data.Sqlite;

namespace ImageManager.Infrastructure.Data;

/// <summary>
/// One-shot database schema initialization and migration.
/// Called at application startup before any repositories are used.
/// </summary>
public static class DatabaseInitializer
{
    public static void Initialize(SqliteConnection conn)
    {
        CreateSchema(conn);
        RunMigrations(conn);
        CreateIndexes(conn);
    }

    private static void CreateSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
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
            """;
        cmd.ExecuteNonQuery();
    }

    private static void RunMigrations(SqliteConnection conn)
    {
        TryAddColumn(conn, "ALTER TABLE ImageMeta ADD COLUMN FolderId INTEGER;");
        TryAddColumn(conn, "ALTER TABLE ImageTag ADD COLUMN Source TEXT DEFAULT NULL;");
        TryAddColumn(conn, "ALTER TABLE ImageMeta ADD COLUMN SystemRating INTEGER DEFAULT -1;");
        TryAddColumn(conn, "ALTER TABLE ImageMeta ADD COLUMN AutoTagStatus INTEGER DEFAULT 0;");
        TryAddColumns(conn, """
            ALTER TABLE ImageMeta ADD COLUMN Duration REAL DEFAULT NULL;
            ALTER TABLE ImageMeta ADD COLUMN ThumbnailTimestamp REAL DEFAULT NULL;
            """);
    }

    private static void CreateIndexes(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_imagemeta_folderid ON ImageMeta(FolderId);";
        cmd.ExecuteNonQuery();
    }

    private static void TryAddColumn(SqliteConnection conn, string sql)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.Message.Contains("duplicate column"))
        {
            // Column already exists — migration already applied
        }
    }

    private static void TryAddColumns(SqliteConnection conn, string sql)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.Message.Contains("duplicate column"))
        {
            // Columns already exist — migration already applied
        }
    }
}
