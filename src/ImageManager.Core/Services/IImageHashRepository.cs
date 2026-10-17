using ImageManager.Core.Models;

namespace ImageManager.Core.Services;

public interface IImageHashRepository
{
    Task<List<ImageMeta>> GetHashMetadataByPathsAsync(IReadOnlyCollection<string> filePaths);

    /// <summary>Batch-load valid perceptual hashes for a specific set of file paths (lightweight, no tags)</summary>
    Task<Dictionary<string, string>> GetPerceptualHashesByPathsAsync(List<string> filePaths);

    /// <summary>Batch-load FileHash for a set of file paths.</summary>
    Task<Dictionary<string, string>> GetFileHashesByPathsAsync(List<string> filePaths);

    /// <summary>Returns paths where a usable perceptual hash was computed.</summary>
    Task<HashSet<string>> GetHashedPathsAsync(List<string> filePaths);

    /// <summary>Reset HashStatus to 0 for all records (used when hash algorithm version changes).</summary>
    Task ResetHashStatusAsync();

    /// <summary>Reset failed hash records under a folder and return the paths to retry.</summary>
    Task<List<string>> ResetFailedHashStatusByFolderAsync(string folderPath, bool recursive = true);

    /// <summary>Find a record by its MD5 file hash.</summary>
    Task<ImageMeta?> GetByFileHashAsync(string fileHash);

    /// <summary>
    /// Updates image hash metadata for existing records only, verifying ID, path, and file write version.
    /// Never inserts new rows. Returns result indicating successfully updated vs stale items.
    /// </summary>
    Task<ImageHashUpdateBatchResult> UpdateHashBatchAsync(IReadOnlyList<ImageHashUpdateItem> items);

    /// <summary>
    /// Explicitly registers newly discovered files in the database if they exist on disk and are not yet registered.
    /// </summary>
    Task<int> RegisterNewFilesBatchAsync(IReadOnlyList<ImageMeta> newFiles);
}
