using ImageManager.Core.Services;
using Microsoft.Data.Sqlite;

namespace ImageManager.Infrastructure.Data;

/// <summary>
/// Provides transient <see cref="SqliteConnection"/> instances with WAL mode
/// and optimal pragma settings for concurrent read/write workloads.
/// </summary>
public class DbContextFactory : IDbContextFactory
{
    private readonly string _connectionString;

    public DbContextFactory(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _connectionString = $"Data Source={dbPath}";
    }

    public SqliteConnection CreateConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=2000;
            PRAGMA foreign_keys=ON;
            PRAGMA cache_size=-8192;
            PRAGMA synchronous=NORMAL;
            PRAGMA temp_store=MEMORY;
            """;
        cmd.ExecuteNonQuery();

        return conn;
    }

    /// <summary>
    /// Force a WAL checkpoint to truncate the WAL file. Call periodically
    /// or before backup to keep the WAL file from growing unbounded.
    /// </summary>
    public void Checkpoint()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        cmd.ExecuteNonQuery();
    }
}
