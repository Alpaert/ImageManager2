using Microsoft.Data.Sqlite;

namespace ImageManager.Infrastructure.Data;

/// <summary>
/// Factory for creating transient <see cref="SqliteConnection"/> instances.
/// Each call returns a new open connection with WAL mode and optimal pragma settings.
/// Consumers MUST dispose the returned connection.
/// </summary>
public interface IDbContextFactory
{
    SqliteConnection CreateConnection();
}
