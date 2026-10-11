using Microsoft.Data.Sqlite;

namespace ImageManager.Infrastructure.Data;

public static class CacheDatabaseMigration
{
    public static void MigrateIfMissing(string destinationPath, params string[] sourceDirectories)
    {
        if (File.Exists(destinationPath)) return;
        foreach (var directory in sourceDirectories)
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            var sourcePath = Path.Combine(directory, "data.db");
            if (!File.Exists(sourcePath)) continue;

            // SQLite backup includes committed WAL data and never overwrites an
            // existing destination database. Raw copying db/wal/shm is not a snapshot.
            var temporaryPath = destinationPath + "." + Guid.NewGuid().ToString("N") + ".migrating";
            try
            {
                using (var source = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = sourcePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
                }.ToString()))
                using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = temporaryPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false
                }.ToString()))
                {
                    source.Open();
                    destination.Open();
                    source.BackupDatabase(destination);
                    using var command = destination.CreateCommand();
                    command.CommandText = "PRAGMA journal_mode=DELETE;";
                    command.ExecuteNonQuery();
                }
                File.Move(temporaryPath, destinationPath);
                return;
            }
            catch (Exception ex)
            {
                throw new IOException($"无法将数据库从 {sourcePath} 复制到 {destinationPath}，原有数据库已保留。", ex);
            }
            finally
            {
                foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
                {
                    try { File.Delete(temporaryPath + suffix); }
                    catch { /* Preserve the original migration failure. */ }
                }
            }
        }
    }
}
