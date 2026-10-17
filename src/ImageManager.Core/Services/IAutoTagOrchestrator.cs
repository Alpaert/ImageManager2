using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ImageManager.Core.Services;

public interface IAutoTagOrchestrator : IDisposable
{
    bool LastRunCancelled { get; }
    int LastPreparationFailed { get; }
    int LastSkippedCount { get; }
    List<string> LastProcessedPaths { get; }

    void Configure(
        TagMode mode,
        double confidenceThreshold,
        int maxTagsPerImage,
        double pixaiThreshold,
        double artistMatchThreshold,
        bool enableCharacterRecognition,
        double characterMatchThreshold,
        int characterMaxMatchesPerImage,
        string? apiKey);

    Task RunFolderAsync(long folderId, string folderPath, bool recursive, CancellationToken cancellationToken = default);
    Task RunSelectedImagesAsync(List<string> filePaths, CancellationToken cancellationToken = default);
    Task CancelAsync();
}
