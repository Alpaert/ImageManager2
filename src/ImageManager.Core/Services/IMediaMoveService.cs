using ImageManager.Core.Models;

namespace ImageManager.Core.Services;

public interface IMediaMoveService
{
    /// <summary>
    /// Moves a batch of individual media files to target directory.
    /// Each file is an independent completion unit.
    /// </summary>
    Task<MediaMoveBatchResult> MoveFilesAsync(
        MediaMoveRequest request,
        IProgress<MediaMoveProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves an entire directory to a target parent directory.
    /// The directory is a single atomic completion unit.
    /// </summary>
    Task<MediaMoveItemResult> MoveDirectoryAsync(
        DirectoryMoveRequest request,
        IProgress<MediaMoveProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks and recovers any interrupted moves from the move journal on startup.
    /// </summary>
    Task<IReadOnlyList<MediaMoveItemResult>> RecoverPendingMovesAsync(
        CancellationToken cancellationToken = default);
}
