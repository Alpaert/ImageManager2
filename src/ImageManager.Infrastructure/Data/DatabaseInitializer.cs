using Microsoft.Data.Sqlite;

namespace ImageManager.Infrastructure.Data;

/// <summary>
/// Database schema initialization and versioned migration.
/// Called at application startup before any repositories are used.
/// </summary>
public static class DatabaseInitializer
{
    private const int CurrentSchemaVersion = 1;

    public static void Initialize(SqliteConnection conn)
    {
        using var transaction = conn.BeginTransaction();
        CreateSchema(conn, transaction);

        var version = GetUserVersion(conn, transaction);
        if (version > CurrentSchemaVersion)
            throw new InvalidOperationException(
                $"数据库版本 {version} 高于当前程序支持的版本 {CurrentSchemaVersion}。请先升级程序。");

        if (version < CurrentSchemaVersion)
            RunMigrations(conn, transaction);

        // Schema migrations are version-gated, but this data repair is deliberately
        // rerun on every startup. The UPDATE only targets unknown ratings and is idempotent.
        BackfillSystemRatingsFromLegacyTags(conn, transaction);
        CreateIndexes(conn, transaction);
        SetUserVersion(conn, transaction, CurrentSchemaVersion);
        transaction.Commit();
    }

    private static void CreateSchema(SqliteConnection conn, SqliteTransaction transaction)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
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
                LastPageIndex INTEGER DEFAULT 0,
                IsArchived    INTEGER NOT NULL DEFAULT 0
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

            CREATE TABLE IF NOT EXISTS ImageEmbedding (
                ImageMetaId   INTEGER NOT NULL REFERENCES ImageMeta(Id) ON DELETE CASCADE,
                ModelKey      TEXT NOT NULL,
                ModelVersion  TEXT NOT NULL,
                EmbeddingDim  INTEGER NOT NULL,
                EmbeddingBlob BLOB NOT NULL,
                FileHash      TEXT,
                SourceFileSize INTEGER,
                SourceLastWriteTicks INTEGER,
                UpdatedAt     TEXT NOT NULL DEFAULT (datetime('now')),
                PRIMARY KEY (ImageMetaId, ModelKey, ModelVersion)
            );

            CREATE INDEX IF NOT EXISTS idx_imageembedding_model
                ON ImageEmbedding(ModelKey, ModelVersion);

            CREATE TABLE IF NOT EXISTS SuppressedCharacterTag (
                ImageMetaId INTEGER NOT NULL REFERENCES ImageMeta(Id) ON DELETE CASCADE,
                TagName     TEXT NOT NULL COLLATE NOCASE,
                CreatedAt   TEXT NOT NULL DEFAULT (datetime('now')),
                PRIMARY KEY (ImageMetaId, TagName)
            );

            """;
        cmd.ExecuteNonQuery();
    }

    private static void RunMigrations(SqliteConnection conn, SqliteTransaction transaction)
    {
        TryAddColumn(conn, transaction, "ImageMeta", "FolderId", "INTEGER");
        TryAddColumn(conn, transaction, "ImageTag", "Source", "TEXT DEFAULT NULL");
        TryAddColumn(conn, transaction, "ImageMeta", "SystemRating", "INTEGER DEFAULT -1");
        TryAddColumn(conn, transaction, "ImageMeta", "AutoTagStatus", "INTEGER DEFAULT 0");
        TryAddColumn(conn, transaction, "ImageMeta", "HashStatus", "INTEGER DEFAULT 0");
        TryAddColumn(conn, transaction, "ImageEmbedding", "SourceFileSize", "INTEGER");
        TryAddColumn(conn, transaction, "ImageEmbedding", "SourceLastWriteTicks", "INTEGER");
        TryAddColumn(conn, transaction, "Folder", "IsArchived", "INTEGER NOT NULL DEFAULT 0");
        TryAddColumn(conn, transaction, "ImageMeta", "Duration", "REAL DEFAULT NULL");
        TryAddColumn(conn, transaction, "ImageMeta", "ThumbnailTimestamp", "REAL DEFAULT NULL");
    }

    private static void CreateIndexes(SqliteConnection conn, SqliteTransaction transaction)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            CREATE INDEX IF NOT EXISTS idx_imagemeta_folderid ON ImageMeta(FolderId);
            CREATE INDEX IF NOT EXISTS idx_imagemeta_filepath_nocase ON ImageMeta(FilePath COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS idx_suppressedcharactertag_image
                ON SuppressedCharacterTag(ImageMetaId);
            """;
        cmd.ExecuteNonQuery();
    }

    private static void BackfillSystemRatingsFromLegacyTags(SqliteConnection conn, SqliteTransaction transaction)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            WITH RatingCandidates AS (
                SELECT im.Id, 0 AS Rating
                FROM ImageMeta im
                WHERE im.SystemRating = -1 AND EXISTS (
                    SELECT 1 FROM ImageTag it INNER JOIN Tag t ON t.Id = it.TagId
                    WHERE it.ImageMetaId = im.Id AND t.Name COLLATE NOCASE IN ('general', '全年龄')
                )
                UNION ALL
                SELECT im.Id, 1
                FROM ImageMeta im
                WHERE im.SystemRating = -1 AND EXISTS (
                    SELECT 1 FROM ImageTag it INNER JOIN Tag t ON t.Id = it.TagId
                    WHERE it.ImageMetaId = im.Id AND t.Name COLLATE NOCASE IN ('sensitive', '敏感')
                )
                UNION ALL
                SELECT im.Id, 2
                FROM ImageMeta im
                WHERE im.SystemRating = -1 AND EXISTS (
                    SELECT 1 FROM ImageTag it INNER JOIN Tag t ON t.Id = it.TagId
                    WHERE it.ImageMetaId = im.Id AND t.Name COLLATE NOCASE IN ('questionable', '大尺度')
                )
                UNION ALL
                SELECT im.Id, 3
                FROM ImageMeta im
                WHERE im.SystemRating = -1 AND EXISTS (
                    SELECT 1 FROM ImageTag it INNER JOIN Tag t ON t.Id = it.TagId
                    WHERE it.ImageMetaId = im.Id AND t.Name COLLATE NOCASE IN ('explicit', 'r18', 'r-18')
                )
            ),
            UnambiguousRatings AS (
                SELECT Id, MIN(Rating) AS Rating
                FROM RatingCandidates
                GROUP BY Id
                HAVING COUNT(*) = 1
            )
            UPDATE ImageMeta
            SET SystemRating = (
                SELECT Rating FROM UnambiguousRatings WHERE UnambiguousRatings.Id = ImageMeta.Id
            )
            WHERE SystemRating = -1
              AND Id IN (SELECT Id FROM UnambiguousRatings);
            """;
        cmd.ExecuteNonQuery();
    }

    private static int GetUserVersion(SqliteConnection conn, SqliteTransaction transaction)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static void SetUserVersion(SqliteConnection conn, SqliteTransaction transaction, int version)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"PRAGMA user_version = {version};";
        cmd.ExecuteNonQuery();
    }

    private static void TryAddColumn(
        SqliteConnection conn,
        SqliteTransaction transaction,
        string table,
        string column,
        string definition)
    {
        if (HasColumn(conn, transaction, table, column))
            return;

        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        cmd.ExecuteNonQuery();
    }

    private static bool HasColumn(
        SqliteConnection conn,
        SqliteTransaction transaction,
        string table,
        string column)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
