using Avalonia.Threading;
using ImageManager.App.Services;
using ImageManager.Common.Constants;
using ImageManager.Common.Helpers;
using ImageManager.Core.Services;

namespace ImageManager.App.ViewModels;

public partial class MainWindowViewModel
{
    private readonly IdleMaintenanceScheduler _automaticMaintenance = new();
    private AutomaticMaintenanceSnapshot? _automaticMaintenanceSnapshot;
    private int _automaticMaintenanceRevision;

    // UI-thread entry point, also called for scrollbar/direct-offset activity.
    internal void NotifyGalleryInteraction() => _automaticMaintenance.NotifyInteraction();

    private void InvalidateAutomaticMaintenance()
    {
        Interlocked.Increment(ref _automaticMaintenanceRevision);
        _automaticMaintenanceSnapshot = null;
        _automaticMaintenance.Request();
        ScheduleAutomaticMaintenance();
    }

    private void ScheduleAutomaticMaintenance(TimeSpan minimumDelay = default)
    {
        if (!_automaticMaintenance.HasPendingWork || string.IsNullOrEmpty(CurrentFolder)) return;
        var delay = _automaticMaintenance.RemainingIdle;
        if (delay < minimumDelay) delay = minimumDelay;
        // Gate contention and already-cancelled parent scopes must not busy-loop.
        if (delay < TimeSpan.FromSeconds(1)) delay = TimeSpan.FromSeconds(1);
        _idleTimer?.Stop();
        _idleTimer = new DispatcherTimer(delay, DispatcherPriority.Background, OnIdleTimerTick);
        _idleTimer.Start();
    }

    private async Task<bool> RunAutomaticMaintenanceAsync(AutomaticMaintenanceSnapshot snapshot, CancellationToken token)
    {
        var gateEntered = false;
        try
        {
            EnsureCurrent();
            if (AutoTagRuntimeState.IsRunning || !await _maintenanceGate.WaitAsync(0, token).ConfigureAwait(false))
                return false;
            gateEntered = true;
            var folderInfo = await _folderRepo.GetByPathAsync(snapshot.Folder).ConfigureAwait(false);
            EnsureCurrent();

            if (snapshot.Cleanup != null)
            {
                await snapshot.Cleanup.RunAsync(token, EnsureCurrent).ConfigureAwait(false);
                snapshot.Cleanup = null; // Drop the buffered lightweight rows once the stage is complete.
            }

            if (folderInfo != null)
            {
                if (!snapshot.HashesComplete)
                {
                    // Do not use PrecomputeHashesAsync here: it cancels the manual hash CTS.
                    if (!await _hashGate.WaitAsync(0, token).ConfigureAwait(false)) return false;
                    try
                    {
                        EnsureCurrent();
                        if (AutoTagRuntimeState.IsRunning) return false;
                        await PrecomputeHashesCoreAsync(token, folderInfo.Id, snapshot.Files).ConfigureAwait(false);
                        EnsureCurrent();
                        snapshot.HashesComplete = true;
                    }
                    finally { _hashGate.Release(); }
                }
                EnsureCurrent();
                await SyncFolderAsync(snapshot.Folder, folderInfo.Id, FileTypeConstants.AllMediaExtensions.ToArray(),
                    token, recursive: snapshot.Recursive,
                    isCurrent: () => !token.IsCancellationRequested &&
                        Volatile.Read(ref _automaticMaintenanceRevision) == snapshot.Revision,
                    propagateErrors: true).ConfigureAwait(false);
                EnsureCurrent();
            }
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            token.ThrowIfCancellationRequested();
            var delay = _automaticMaintenance.RecordFailure(token);
            AppLogger.Warn($"Automatic maintenance failed; retry after {delay.TotalSeconds:F0}s: {ex}");
            return false;
        }
        finally { if (gateEntered) _maintenanceGate.Release(); }

        void EnsureCurrent()
        {
            token.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _automaticMaintenanceRevision) != snapshot.Revision)
                throw new OperationCanceledException("The maintenance snapshot is no longer current.", token);
        }
    }

    private sealed class AutomaticMaintenanceSnapshot(
        int revision, string folder, bool recursive, string[] files, IImageMetaRepository repository)
    {
        public int Revision { get; } = revision;
        public string Folder { get; } = folder;
        public bool Recursive { get; } = recursive;
        public string[] Files { get; } = files;
        public FolderMetadataCleanup? Cleanup { get; set; } = new(repository, folder, recursive, files);
        public bool HashesComplete { get; set; }
    }
}
