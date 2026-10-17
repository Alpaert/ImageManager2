using ImageManager.Common.Helpers;

namespace ImageManager.Infrastructure.Data.Helpers;

/// <summary>
/// Unified SQL path matching helper for SQLite repositories.
/// Provides safe folder boundary normalization, LIKE wildcard escaping,
/// and consistent direct vs recursive scope SQL predicates.
/// </summary>
public static class SqlPathHelper
{
    /// <summary>
    /// Escapes SQLite LIKE special characters ('\', '%', '_') using '\' as the escape character.
    /// </summary>
    public static string EscapeLikePattern(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_");
    }

    /// <summary>
    /// Normalizes a folder path to ensure a trailing directory separator.
    /// </summary>
    public static string NormalizeFolderPath(string folderPath)
    {
        return PathHelper.NormalizeFolderPath(folderPath);
    }

    /// <summary>
    /// Returns the escaped LIKE prefix pattern for a normalized folder path:
    /// e.g. "C:\Path\To\Folder\" -> "C:\\Path\\To\\Folder\\%"
    /// </summary>
    public static string GetFolderLikePattern(string normalizedFolderPath)
    {
        return EscapeLikePattern(normalizedFolderPath) + "%";
    }

    /// <summary>
    /// Generates SQL predicate and parameter object for matching files in a folder scope.
    /// Supports both recursive (folder + all subdirectories) and non-recursive (direct files only).
    /// </summary>
    /// <param name="columnName">SQL column name (e.g. "FilePath" or "im.FilePath")</param>
    /// <param name="folderPath">Input folder path</param>
    /// <param name="recursive">True for recursive (descendants included), false for direct files only</param>
    /// <returns>A tuple of (SQL where predicate snippet, parameter object)</returns>
    public static (string Predicate, object Parameters) CreateFolderPredicate(
        string columnName,
        string folderPath,
        bool recursive = true)
    {
        ArgumentNullException.ThrowIfNull(folderPath);

        var normalized = NormalizeFolderPath(folderPath);
        var likePattern = GetFolderLikePattern(normalized);

        if (recursive)
        {
            var sql = $"{columnName} LIKE @ScopeLike ESCAPE '\\' COLLATE NOCASE";
            return (sql, new { ScopeLike = likePattern });
        }
        else
        {
            var sql = $"{columnName} LIKE @ScopeLike ESCAPE '\\' COLLATE NOCASE " +
                      $"AND instr(substr({columnName}, length(@ScopePrefix) + 1), '\\') = 0 " +
                      $"AND instr(substr({columnName}, length(@ScopePrefix) + 1), '/') = 0";
            return (sql, new { ScopePrefix = normalized, ScopeLike = likePattern });
        }
    }
}
