using Dapper;
using ImageManager.Common.Helpers;
using ImageManager.Core.Models;
using ImageManager.Core.Services;
using ImageManager.Infrastructure.Data;
using ImageManager.Infrastructure.Data.Helpers;

namespace ImageManager.Infrastructure.Services;

public sealed class MediaMoveService : IMediaMoveService
{
    private readonly IDbContextFactory _dbFactory;
    private readonly IImageMetaRepository _metaRepo;
    private readonly IFolderRepository _folderRepo;
    private readonly IThumbnailCacheService _thumbCache;
    private readonly IFileSystemOperations _fileSystem;
    private readonly Action<string>? _dbFaultHook;
    private readonly IHashDataVersionTracker? _versionTracker;

    public MediaMoveService(
        IDbContextFactory dbFactory,
        IImageMetaRepository metaRepo,
        IFolderRepository folderRepo,
        IThumbnailCacheService thumbCache,
        IFileSystemOperations? fileSystem = null,
        Action<string>? dbFaultHook = null,
        IHashDataVersionTracker? versionTracker = null)
    {
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
        _metaRepo = metaRepo ?? throw new ArgumentNullException(nameof(metaRepo));
        _folderRepo = folderRepo ?? throw new ArgumentNullException(nameof(folderRepo));
        _thumbCache = thumbCache ?? throw new ArgumentNullException(nameof(thumbCache));
        _fileSystem = fileSystem ?? DefaultFileSystemOperations.Instance;
        _dbFaultHook = dbFaultHook;
        _versionTracker = versionTracker;
    }

    public async Task<MediaMoveBatchResult> MoveFilesAsync(
        MediaMoveRequest request,
        IProgress<MediaMoveProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sourcePaths = request.SourcePaths ?? [];
        var targetDir = request.TargetDirectory;
        var opId = request.OperationId ?? Guid.NewGuid().ToString("N");

        var items = new List<MediaMoveItemResult>(sourcePaths.Count);
        int successCount = 0;
        int failedCount = 0;
        int skippedCount = 0;
        int cancelledCount = 0;
        int compensationFailedCount = 0;

        _fileSystem.CreateDirectory(targetDir);
        var managedFolders = await _folderRepo.GetAllAsync();

        for (int i = 0; i < sourcePaths.Count; i++)
        {
            var sourcePath = sourcePaths[i];

            // 1. Cancellation check between completion units
            if (cancellationToken.IsCancellationRequested)
            {
                for (int j = i; j < sourcePaths.Count; j++)
                {
                    items.Add(new MediaMoveItemResult(sourcePaths[j], null, MediaMoveItemStatus.Cancelled, "Operation cancelled."));
                    cancelledCount++;
                }
                break;
            }

            progress?.Report(new MediaMoveProgress(i, sourcePaths.Count, "MovingFiles", sourcePath));

            // 2. Validate source
            if (!_fileSystem.FileExists(sourcePath))
            {
                items.Add(new MediaMoveItemResult(sourcePath, null, MediaMoveItemStatus.Skipped, "Source file does not exist."));
                skippedCount++;
                continue;
            }

            var srcDir = Path.GetDirectoryName(sourcePath) ?? string.Empty;
            if (string.Equals(Path.GetFullPath(srcDir).TrimEnd('\\', '/'),
                              Path.GetFullPath(targetDir).TrimEnd('\\', '/'),
                              StringComparison.OrdinalIgnoreCase))
            {
                items.Add(new MediaMoveItemResult(sourcePath, sourcePath, MediaMoveItemStatus.Skipped, "Source is already in target directory."));
                skippedCount++;
                continue;
            }

            // 3. Determine actual non-conflicting destination path
            var fileName = Path.GetFileName(sourcePath);
            var initialTarget = Path.Combine(targetDir, fileName);
            var actualTarget = GetNonConflictingDestination(initialTarget);

            // 4. Determine target FolderId
            var targetFolderId = ResolveFolderId(actualTarget, managedFolders);

            // 6. Record journal before filesystem move
            long journalId = await InsertJournalAsync(opId, "File", sourcePath, actualTarget, targetFolderId, "Moving");

            bool fileMoved = false;
            try
            {
                using var lease = await MetadataWriteCoordinator.EnterAsync(_dbFactory, cancellationToken);

                // Check if ImageMeta record exists under write lease
                var existingMeta = await _metaRepo.GetByPathAsync(sourcePath);

                // Double check destination doesn't exist
                if (_fileSystem.FileExists(actualTarget))
                {
                    actualTarget = GetNonConflictingDestination(actualTarget);
                    await UpdateJournalTargetPathAsync(journalId, actualTarget);
                }

                _fileSystem.MoveFile(sourcePath, actualTarget);
                fileMoved = true;
                await UpdateJournalStateAsync(journalId, "FilesystemMoved");

                // Update database
                if (existingMeta != null)
                {
                    _dbFaultHook?.Invoke(actualTarget);

                    using var conn = _dbFactory.CreateConnection();
                    using var txn = conn.BeginTransaction();

                    var now = DateTime.UtcNow;
                    var affected = await conn.ExecuteAsync(@"
                        UPDATE ImageMeta
                        SET FilePath = @NewPath, FolderId = @FolderId, UpdatedAt = @Now
                        WHERE Id = @Id AND FilePath = @ExpectedOldPath COLLATE NOCASE",
                        new
                        {
                            NewPath = actualTarget,
                            FolderId = targetFolderId,
                            Now = now,
                            Id = existingMeta.Id,
                            ExpectedOldPath = sourcePath
                        }, txn);

                    if (affected != 1)
                    {
                        throw new InvalidOperationException(
                            $"Concurrent update conflict for ImageMeta {existingMeta.Id} (expected '{sourcePath}').");
                    }

                    await DeleteJournalAsync(conn, txn, journalId);
                    txn.Commit();
                    _versionTracker?.IncrementVersion();
                }
                else
                {
                    // No database record to update, remove journal entry
                    using var conn = _dbFactory.CreateConnection();
                    using var txn = conn.BeginTransaction();
                    await DeleteJournalAsync(conn, txn, journalId);
                    txn.Commit();
                }

                // Cache migration / invalidation
                try
                {
                    _thumbCache.MoveDiskCache(sourcePath, actualTarget);
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"Failed to migrate thumbnail cache from {sourcePath} to {actualTarget}: {ex.Message}");
                }

                items.Add(new MediaMoveItemResult(sourcePath, actualTarget, MediaMoveItemStatus.Success));
                successCount++;
            }
            catch (Exception ex)
            {
                if (fileMoved)
                {
                    // Compensation restore: must NOT use cancelled token!
                    bool restored = false;
                    try
                    {
                        _fileSystem.MoveFile(actualTarget, sourcePath);
                        restored = true;
                    }
                    catch (Exception restoreEx)
                    {
                        AppLogger.Error($"Compensation file restore failed: {actualTarget} -> {sourcePath}: {restoreEx.Message}");
                        await UpdateJournalStateAsync(journalId, "CompensationFailed", $"{ex.Message} | Restore failed: {restoreEx.Message}");
                    }

                    if (restored)
                    {
                        await UpdateJournalStateAsync(journalId, "Failed", ex.Message);
                        items.Add(new MediaMoveItemResult(sourcePath, actualTarget, MediaMoveItemStatus.Failed, ex.Message));
                        failedCount++;
                    }
                    else
                    {
                        items.Add(new MediaMoveItemResult(sourcePath, actualTarget, MediaMoveItemStatus.CompensationFailed,
                            $"Database failed and file could not be restored to original position: {ex.Message}"));
                        compensationFailedCount++;
                    }
                }
                else
                {
                    await UpdateJournalStateAsync(journalId, "Failed", ex.Message);
                    items.Add(new MediaMoveItemResult(sourcePath, actualTarget, MediaMoveItemStatus.Failed, ex.Message));
                    failedCount++;
                }
            }
        }

        progress?.Report(new MediaMoveProgress(sourcePaths.Count, sourcePaths.Count, "Completed"));
        return new MediaMoveBatchResult(
            items,
            sourcePaths.Count,
            successCount,
            failedCount,
            skippedCount,
            cancelledCount,
            compensationFailedCount);
    }

    public async Task<MediaMoveItemResult> MoveDirectoryAsync(
        DirectoryMoveRequest request,
        IProgress<MediaMoveProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sourceDir = request.SourceDirectory;
        var targetParentDir = request.TargetParentDirectory;
        var opId = request.OperationId ?? Guid.NewGuid().ToString("N");

        if (!_fileSystem.DirectoryExists(sourceDir))
            return new MediaMoveItemResult(sourceDir, null, MediaMoveItemStatus.Failed, "Source directory does not exist.");

        if (!PathHelper.IsSafeCopyOrMoveDestination(sourceDir, targetParentDir))
            return new MediaMoveItemResult(sourceDir, null, MediaMoveItemStatus.Failed, "Cannot move directory into itself or its subdirectory.");

        _fileSystem.CreateDirectory(targetParentDir);
        var folderName = Path.GetFileName(sourceDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var initialDestination = Path.Combine(targetParentDir, folderName);
        var destination = GetNonConflictingDirectoryDestination(initialDestination);

        var managedFolders = await _folderRepo.GetAllAsync();
        var targetFolderId = ResolveFolderId(destination, managedFolders);

        long journalId = await InsertJournalAsync(opId, "Directory", sourceDir, destination, targetFolderId, "Moving");

        bool dirMoved = false;
        try
        {
            using var lease = await MetadataWriteCoordinator.EnterAsync(_dbFactory, cancellationToken);

            _fileSystem.MoveDirectory(sourceDir, destination);
            dirMoved = true;
            await UpdateJournalStateAsync(journalId, "FilesystemMoved");

            _dbFaultHook?.Invoke(destination);

            using var conn = _dbFactory.CreateConnection();
            using var txn = conn.BeginTransaction();

            var oldPrefix = SqlPathHelper.NormalizeFolderPath(sourceDir);
            var newPrefix = SqlPathHelper.NormalizeFolderPath(destination);
            var oldLike = SqlPathHelper.GetFolderLikePattern(oldPrefix);
            var newLike = SqlPathHelper.GetFolderLikePattern(newPrefix);

            var cleanSource = sourceDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var cleanDest = destination.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var now = DateTime.UtcNow;

            // 1. Update Folder table if this directory or any subdirectories were registered
            await conn.ExecuteAsync(@"
                UPDATE Folder
                SET Path = @NewPrefix || SUBSTR(Path, length(@OldPrefix) + 1)
                WHERE Path LIKE @OldLike ESCAPE '\' COLLATE NOCASE",
                new
                {
                    NewPrefix = newPrefix,
                    OldPrefix = oldPrefix,
                    OldLike = oldLike
                }, txn);

            await conn.ExecuteAsync(@"
                UPDATE Folder
                SET Path = @NewDest
                WHERE Path = @OldSource COLLATE NOCASE",
                new
                {
                    NewDest = cleanDest,
                    OldSource = cleanSource
                }, txn);

            // 2. Update ImageMeta paths
            await conn.ExecuteAsync(@"
                UPDATE ImageMeta
                SET FilePath = @NewPrefix || SUBSTR(FilePath, length(@OldPrefix) + 1),
                    UpdatedAt = @Now
                WHERE FilePath LIKE @OldLike ESCAPE '\' COLLATE NOCASE",
                new
                {
                    NewPrefix = newPrefix,
                    OldPrefix = oldPrefix,
                    Now = now,
                    OldLike = oldLike
                }, txn);

            // 3. Update FolderId only if source directory was not itself a registered folder
            var selfFolder = managedFolders.FirstOrDefault(f =>
                string.Equals(SqlPathHelper.NormalizeFolderPath(f.Path), oldPrefix, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(f.Path.TrimEnd('\\', '/'), cleanSource, StringComparison.OrdinalIgnoreCase));

            if (selfFolder == null)
            {
                await conn.ExecuteAsync(@"
                    UPDATE ImageMeta
                    SET FolderId = @FolderId,
                        UpdatedAt = @Now
                    WHERE FilePath LIKE @NewLike ESCAPE '\' COLLATE NOCASE
                      AND (FolderId IS NULL OR FolderId NOT IN (
                          SELECT Id FROM Folder WHERE Path LIKE @NewLike ESCAPE '\' COLLATE NOCASE
                      ))",
                    new
                    {
                        FolderId = targetFolderId,
                        Now = now,
                        NewLike = newLike
                    }, txn);
            }

            await DeleteJournalAsync(conn, txn, journalId);
            txn.Commit();
            _versionTracker?.IncrementVersion();

            return new MediaMoveItemResult(sourceDir, destination, MediaMoveItemStatus.Success);
        }
        catch (Exception ex)
        {
            if (dirMoved)
            {
                // Rollback directory move using uncancelled context
                bool restored = false;
                try
                {
                    _fileSystem.MoveDirectory(destination, sourceDir);
                    restored = true;
                }
                catch (Exception restoreEx)
                {
                    AppLogger.Error($"Compensation directory restore failed: {destination} -> {sourceDir}: {restoreEx.Message}");
                    await UpdateJournalStateAsync(journalId, "CompensationFailed", $"{ex.Message} | Restore failed: {restoreEx.Message}");
                }

                if (restored)
                {
                    await UpdateJournalStateAsync(journalId, "Failed", ex.Message);
                    return new MediaMoveItemResult(sourceDir, destination, MediaMoveItemStatus.Failed, ex.Message);
                }
                else
                {
                    return new MediaMoveItemResult(sourceDir, destination, MediaMoveItemStatus.CompensationFailed,
                        $"Database failed and directory could not be restored: {ex.Message}");
                }
            }
            else
            {
                await UpdateJournalStateAsync(journalId, "Failed", ex.Message);
                return new MediaMoveItemResult(sourceDir, destination, MediaMoveItemStatus.Failed, ex.Message);
            }
        }
    }

    public async Task<IReadOnlyList<MediaMoveItemResult>> RecoverPendingMovesAsync(
        CancellationToken cancellationToken = default)
    {
        using var conn = _dbFactory.CreateConnection();
        var pending = (await conn.QueryAsync<(long Id, string OperationId, string ItemType, string SourcePath, string TargetPath, long? TargetFolderId, string State)>(@"
            SELECT Id, OperationId, ItemType, SourcePath, TargetPath, TargetFolderId, State
            FROM MediaMoveJournal
            WHERE State IN ('Moving', 'FilesystemMoved', 'CompensationFailed')
            ORDER BY Id")).ToList();

        var results = new List<MediaMoveItemResult>();
        if (pending.Count == 0) return results;

        var managedFolders = await _folderRepo.GetAllAsync();

        foreach (var entry in pending)
        {
            if (entry.ItemType == "File")
            {
                bool sourceExists = _fileSystem.FileExists(entry.SourcePath);
                bool targetExists = _fileSystem.FileExists(entry.TargetPath);

                if (sourceExists && !targetExists)
                {
                    // Filesystem move never completed or already rolled back; DB is still at SourcePath
                    await DeleteJournalEntryAsync(entry.Id);
                    results.Add(new MediaMoveItemResult(entry.SourcePath, entry.SourcePath, MediaMoveItemStatus.Success, "Recovered to source state."));
                }
                else if (!sourceExists && targetExists)
                {
                    // Filesystem moved, but DB not updated; complete DB update
                    var meta = await _metaRepo.GetByPathAsync(entry.SourcePath);
                    if (meta != null)
                    {
                        using var txn = conn.BeginTransaction();
                        var now = DateTime.UtcNow;
                        await conn.ExecuteAsync(@"
                            UPDATE ImageMeta
                            SET FilePath = @NewPath, FolderId = @FolderId, UpdatedAt = @Now
                            WHERE Id = @Id AND FilePath = @OldPath COLLATE NOCASE",
                            new
                            {
                                NewPath = entry.TargetPath,
                                FolderId = entry.TargetFolderId ?? 0,
                                Now = now,
                                Id = meta.Id,
                                OldPath = entry.SourcePath
                            }, txn);
                        await DeleteJournalAsync(conn, txn, entry.Id);
                        txn.Commit();
                        _versionTracker?.IncrementVersion();
                    }
                    else
                    {
                        await DeleteJournalEntryAsync(entry.Id);
                    }
                    results.Add(new MediaMoveItemResult(entry.SourcePath, entry.TargetPath, MediaMoveItemStatus.Success, "Recovered and completed metadata update."));
                }
                else
                {
                    // Conflict: both exist or neither exists!
                    await UpdateJournalStateAsync(entry.Id, "CompensationFailed", "External collision: file exists at both source and target or neither.");
                    results.Add(new MediaMoveItemResult(entry.SourcePath, entry.TargetPath, MediaMoveItemStatus.CompensationFailed, "Conflict detected during recovery. Manual resolution required."));
                }
            }
            else if (entry.ItemType == "Directory")
            {
                bool sourceExists = _fileSystem.DirectoryExists(entry.SourcePath);
                bool targetExists = _fileSystem.DirectoryExists(entry.TargetPath);

                if (sourceExists && !targetExists)
                {
                    await DeleteJournalEntryAsync(entry.Id);
                    results.Add(new MediaMoveItemResult(entry.SourcePath, entry.SourcePath, MediaMoveItemStatus.Success, "Recovered directory to source state."));
                }
                else if (!sourceExists && targetExists)
                {
                    using var txn = conn.BeginTransaction();
                    var oldPrefix = SqlPathHelper.NormalizeFolderPath(entry.SourcePath);
                    var newPrefix = SqlPathHelper.NormalizeFolderPath(entry.TargetPath);
                    var oldLike = SqlPathHelper.GetFolderLikePattern(oldPrefix);
                    var newLike = SqlPathHelper.GetFolderLikePattern(newPrefix);
                    var cleanSource = entry.SourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var cleanDest = entry.TargetPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var now = DateTime.UtcNow;

                    // 1. Update Folder paths
                    await conn.ExecuteAsync(@"
                        UPDATE Folder
                        SET Path = @NewPrefix || SUBSTR(Path, length(@OldPrefix) + 1)
                        WHERE Path LIKE @OldLike ESCAPE '\' COLLATE NOCASE",
                        new
                        {
                            NewPrefix = newPrefix,
                            OldPrefix = oldPrefix,
                            OldLike = oldLike
                        }, txn);

                    await conn.ExecuteAsync(@"
                        UPDATE Folder
                        SET Path = @NewDest
                        WHERE Path = @OldSource COLLATE NOCASE",
                        new { NewDest = cleanDest, OldSource = cleanSource }, txn);

                    // 2. Update ImageMeta paths
                    await conn.ExecuteAsync(@"
                        UPDATE ImageMeta
                        SET FilePath = @NewPrefix || SUBSTR(FilePath, length(@OldPrefix) + 1),
                            UpdatedAt = @Now
                        WHERE FilePath LIKE @OldLike ESCAPE '\' COLLATE NOCASE",
                        new
                        {
                            NewPrefix = newPrefix,
                            OldPrefix = oldPrefix,
                            Now = now,
                            OldLike = oldLike
                        }, txn);

                    // 3. Update FolderId only if source directory was not itself a registered folder
                    var selfFolder = managedFolders.FirstOrDefault(f =>
                        string.Equals(SqlPathHelper.NormalizeFolderPath(f.Path), oldPrefix, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(f.Path.TrimEnd('\\', '/'), cleanSource, StringComparison.OrdinalIgnoreCase));

                    if (selfFolder == null)
                    {
                        var targetFolderId = ResolveFolderId(entry.TargetPath, managedFolders);
                        await conn.ExecuteAsync(@"
                            UPDATE ImageMeta
                            SET FolderId = @FolderId,
                                UpdatedAt = @Now
                            WHERE FilePath LIKE @NewLike ESCAPE '\' COLLATE NOCASE
                              AND (FolderId IS NULL OR FolderId NOT IN (
                                  SELECT Id FROM Folder WHERE Path LIKE @NewLike ESCAPE '\' COLLATE NOCASE
                              ))",
                            new { FolderId = targetFolderId, Now = now, NewLike = newLike }, txn);
                    }

                    await DeleteJournalAsync(conn, txn, entry.Id);
                    txn.Commit();
                    _versionTracker?.IncrementVersion();

                    results.Add(new MediaMoveItemResult(entry.SourcePath, entry.TargetPath, MediaMoveItemStatus.Success, "Recovered and completed directory metadata update."));
                }
                else
                {
                    await UpdateJournalStateAsync(entry.Id, "CompensationFailed", "Conflict detected for directory recovery.");
                    results.Add(new MediaMoveItemResult(entry.SourcePath, entry.TargetPath, MediaMoveItemStatus.CompensationFailed, "Conflict detected during directory recovery."));
                }
            }
        }

        return results;
    }

    private string GetNonConflictingDestination(string targetPath)
    {
        if (!_fileSystem.FileExists(targetPath))
            return targetPath;

        var dir = Path.GetDirectoryName(targetPath) ?? Environment.CurrentDirectory;
        var name = Path.GetFileNameWithoutExtension(targetPath);
        var ext = Path.GetExtension(targetPath);

        int index = 1;
        string newPath;
        do
        {
            newPath = Path.Combine(dir, $"{name} ({index}){ext}");
            index++;
        } while (_fileSystem.FileExists(newPath));

        return newPath;
    }

    private string GetNonConflictingDirectoryDestination(string targetPath)
    {
        if (!_fileSystem.DirectoryExists(targetPath))
            return targetPath;

        var parent = Path.GetDirectoryName(targetPath.TrimEnd('\\', '/')) ?? Environment.CurrentDirectory;
        var name = Path.GetFileName(targetPath.TrimEnd('\\', '/'));

        int index = 1;
        string newPath;
        do
        {
            newPath = Path.Combine(parent, $"{name} ({index})");
            index++;
        } while (_fileSystem.DirectoryExists(newPath));

        return newPath;
    }

    private static long ResolveFolderId(string path, IEnumerable<FolderInfo> managedFolders)
    {
        var normalizedTarget = SqlPathHelper.NormalizeFolderPath(
            Path.GetDirectoryName(path) ?? path);

        FolderInfo? bestMatch = null;
        int bestLength = -1;

        foreach (var folder in managedFolders)
        {
            var normalizedFolder = SqlPathHelper.NormalizeFolderPath(folder.Path);
            if (normalizedTarget.StartsWith(normalizedFolder, StringComparison.OrdinalIgnoreCase))
            {
                if (normalizedFolder.Length > bestLength)
                {
                    bestMatch = folder;
                    bestLength = normalizedFolder.Length;
                }
            }
        }

        return bestMatch?.Id ?? 0L;
    }

    private async Task<long> InsertJournalAsync(
        string operationId,
        string itemType,
        string sourcePath,
        string targetPath,
        long? targetFolderId,
        string state)
    {
        using var conn = _dbFactory.CreateConnection();
        return await conn.ExecuteScalarAsync<long>(@"
            INSERT INTO MediaMoveJournal (OperationId, ItemType, SourcePath, TargetPath, TargetFolderId, State)
            VALUES (@OperationId, @ItemType, @SourcePath, @TargetPath, @TargetFolderId, @State);
            SELECT last_insert_rowid();",
            new
            {
                OperationId = operationId,
                ItemType = itemType,
                SourcePath = sourcePath,
                TargetPath = targetPath,
                TargetFolderId = targetFolderId,
                State = state
            });
    }

    private async Task UpdateJournalStateAsync(long journalId, string state, string? error = null)
    {
        using var conn = _dbFactory.CreateConnection();
        var now = DateTime.UtcNow;
        await conn.ExecuteAsync(@"
            UPDATE MediaMoveJournal
            SET State = @State, ErrorMessage = @Error, UpdatedAt = @Now
            WHERE Id = @Id",
            new { State = state, Error = error, Now = now, Id = journalId });
    }

    private async Task UpdateJournalTargetPathAsync(long journalId, string targetPath)
    {
        using var conn = _dbFactory.CreateConnection();
        var now = DateTime.UtcNow;
        await conn.ExecuteAsync(@"
            UPDATE MediaMoveJournal
            SET TargetPath = @TargetPath, UpdatedAt = @Now
            WHERE Id = @Id",
            new { TargetPath = targetPath, Now = now, Id = journalId });
    }

    private static Task DeleteJournalAsync(System.Data.IDbConnection conn, System.Data.IDbTransaction txn, long journalId)
    {
        return conn.ExecuteAsync("DELETE FROM MediaMoveJournal WHERE Id = @Id", new { Id = journalId }, txn);
    }

    private async Task DeleteJournalEntryAsync(long journalId)
    {
        using var conn = _dbFactory.CreateConnection();
        await conn.ExecuteAsync("DELETE FROM MediaMoveJournal WHERE Id = @Id", new { Id = journalId });
    }
}
