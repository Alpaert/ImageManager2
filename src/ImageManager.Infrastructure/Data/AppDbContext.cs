using Microsoft.Data.Sqlite;

namespace ImageManager.Infrastructure.Data;

/// <summary>
/// [DEPRECATED] Use <see cref="IDbContextFactory"/> for connections and
/// <see cref="DatabaseInitializer"/> for schema initialization.
/// This class will be removed in a future version.
/// </summary>
[Obsolete("Use IDbContextFactory and DatabaseInitializer instead.")]
public class AppDbContext
{
    private readonly DbContextFactory _factory;

    public AppDbContext(string dbPath)
    {
        _factory = new DbContextFactory(dbPath);
        using var conn = _factory.CreateConnection();
        DatabaseInitializer.Initialize(conn);
    }

    [Obsolete("Use IDbContextFactory.CreateConnection() instead.")]
    public SqliteConnection CreateConnection() => _factory.CreateConnection();

    [Obsolete("Use DbContextFactory.Checkpoint() instead.")]
    public void Checkpoint() => _factory.Checkpoint();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
    }
}
