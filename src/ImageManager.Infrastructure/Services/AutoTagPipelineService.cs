using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using ImageManager.Common.Helpers;
using ImageManager.Core.Models;
using ImageManager.Core.Services;
using ImageManager.Infrastructure.Helpers;

namespace ImageManager.Infrastructure.Services;

public readonly record struct AutoTagPipelineProgress(
    string Phase, int Processed, int Total, string StatusText);

public readonly record struct FolderTagActionResult(
    string Action,   // "Start" / "Resume" / "NewFiles" / "Blocked" / "Busy" / "Retry"
    string Message,  // User-facing message
    bool CanProceed);

public class AutoTagPipelineService : IDisposable
{
    private readonly IImageMetaRepository _metaRepo;
    private readonly IEnsembleTagService _tagService;
    private readonly IAutoTagStateRepository _stateRepo;
    private double _confidenceThreshold = 0.35;
    private int _maxTagsPerImage = 20;
    public event Action<AutoTagPipelineProgress>? ProgressChanged;

    // ==================== 鍐呭瓨璇婃柇璁℃暟鍣?====================
    private static int _pipelineRunCount;

    public AutoTagPipelineService(
        IImageMetaRepository metaRepo,
        IEnsembleTagService tagService,
        IAutoTagStateRepository stateRepo)
    {
        _metaRepo = metaRepo;
        _tagService = tagService;
        _stateRepo = stateRepo;
    }

    public void Configure(double confidenceThreshold, int maxTagsPerImage)
    {
        _confidenceThreshold = confidenceThreshold;
        _maxTagsPerImage = maxTagsPerImage;
    }

    // ==================== Situation Assessment ====================

    public async Task<FolderTagActionResult> DetermineActionAsync(long folderId, long folderFileCount)
    {
        var state = await _stateRepo.GetStateAsync(folderId);

        if (state == null)
            return new FolderTagActionResult("Start", "\u5f00\u59cb\u5bf9\u6587\u4ef6\u5939\u8fdb\u884c\u81ea\u52a8\u6253\u6807\uff1f", true);

        return state.Status switch
        {
            "Processing" => new FolderTagActionResult("Recover",
                $"\u68c0\u6d4b\u5230\u4e0a\u6b21\u6253\u6807\u4e2d\u65ad\uff08\u5df2\u5b8c\u6210 {state.Processed}/{state.TotalFiles}\uff09\u3002\n\u662f\u5426\u6e05\u7406\u5e76\u91cd\u65b0\u6253\u6807\uff1f", true),

            "Done" when state.LastFileCount == folderFileCount => new FolderTagActionResult("ReTag",
                "\u6b64\u6587\u4ef6\u5939\u5df2\u5b8c\u6210\u6253\u6807\u3002\n\u662f\u5426\u5220\u9664\u6240\u6709\u81ea\u52a8\u6807\u7b7e\u5e76\u91cd\u65b0\u6253\u6807\uff1f\n\uff08\u624b\u52a8\u6807\u7b7e\u4e0d\u53d7\u5f71\u54cd\uff09", true),

            "Done" when state.LastFileCount < folderFileCount => new FolderTagActionResult("NewFiles",
                $"\u6b64\u6587\u4ef6\u5939\u5df2\u6253\u6807\uff0c\u4f46\u68c0\u6d4b\u5230 {folderFileCount - state.LastFileCount} \u5f20\u65b0\u56fe\u7247\u3002\u662f\u5426\u4ec5\u5bf9\u65b0\u56fe\u7247\u6253\u6807\uff1f", true),

            "Done" => new FolderTagActionResult("Blocked",
                "\u6b64\u6587\u4ef6\u5939\u5df2\u5b8c\u6210\u6253\u6807\uff0c\u65e0\u9700\u91cd\u590d\u3002", false),

            "AwaitingReview" => new FolderTagActionResult("Resume",
                "\u6b64\u6587\u4ef6\u5939\u63a8\u7406\u5df2\u5b8c\u6210\uff0c\u662f\u5426\u76f4\u63a5\u786e\u8ba4\u6807\u7b7e\uff1f\uff08\u4e0d\u4f1a\u91cd\u65b0\u63a8\u7406\uff09\n\u9009\u62e9\"\u5426\"\u53ef\u91cd\u65b0\u6253\u6807", true),

            "Failed" => new FolderTagActionResult("Retry",
                $"\u4e0a\u6b21\u6253\u6807\u5931\u8d25\u3002\u9519\u8bef\uff1a{state.ErrorMsg ?? "\u672a\u77e5"}\n\u662f\u5426\u91cd\u65b0\u6253\u6807\uff1f", true),

            _ => new FolderTagActionResult("Start", "\u5f00\u59cb\u5bf9\u6587\u4ef6\u5939\u8fdb\u884c\u81ea\u52a8\u6253\u6807\uff1f", true)
        };
    }

    // ==================== Inference ====================

    /// <returns>List of file paths that were actually processed (for post-pipeline refresh).</returns>
    public async Task<List<string>> RunInferenceAsync(long folderId, List<(long Id, string FilePath)> metas, string action,
        CancellationToken ct = default, IEnsembleTagService? tagService = null)
    {
        using var autoTagRun = AutoTagRuntimeState.Enter();
        var activeTagService = tagService ?? _tagService;
        bool hasState = folderId > 0;
        int runId = Interlocked.Increment(ref _pipelineRunCount);
        int total = metas.Count;
        var runWatch = Stopwatch.StartNew();

        // Memory diagnostics: pipeline start.
        double commitStart = MemoryPressureMonitor.CommitChargeMB;
        double fragStart = MemoryPressureMonitor.FragmentationScore;
        AppLogger.Memory($"Pipeline#{runId}.Start total={total} action={action} " +
            $"commit={commitStart:F0}MB frag={fragStart:F1}");

        if (hasState)
        {
            var state = new AutoTagState
            {
                FolderId = folderId, Status = "Processing",
                TotalFiles = metas.Count, Processed = 0,
                LastFileCount = metas.Count, StartedAt = DateTime.UtcNow
            };
            await _stateRepo.UpsertStateAsync(state);
        }

        var channel = Channel.CreateBounded<(long ImageId, string FilePath, List<TagPrediction> Predictions)>(
            new BoundedChannelOptions(16) { SingleWriter = true, SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait });

        int processed = 0;
        var errors = new ConcurrentQueue<string>();

        var producerTask = Task.Run(async () =>
        {
            try
            {
                for (int i = 0; i < metas.Count; i++)
                {
                    if (ct.IsCancellationRequested) break;

                    // Check memory pressure every 10 images.
                    if (i > 0 && i % 10 == 0)
                    {
                        var level = MemoryPressureMonitor.Current;

                        if (level == MemoryPressureMonitor.PressureLevel.Critical)
                        {
                            AppLogger.Memory($"Pipeline#{runId} Critical pressure at {i}/{total}, emergency cleanup");
                            MemoryPressureMonitor.EmergencyCleanup();
                            await Task.Delay(200, ct);
                        }
                        else if (level == MemoryPressureMonitor.PressureLevel.High)
                        {
                            AppLogger.Memory($"Pipeline#{runId} High pressure at {i}/{total}, triggering LOH compact");
                            MemoryPressureMonitor.CompactLoh();
                        }

                        // Log memory and throughput every 50 images
                        if (i % 50 == 0)
                        {
                            var secondsPer10 = i > 0 ? runWatch.Elapsed.TotalSeconds / i * 10.0 : 0;
                            AppLogger.Memory($"Pipeline#{runId} progress {i}/{total} " +
                                $"level={level} commit={MemoryPressureMonitor.CommitChargeMB:F0}MB " +
                                $"frag={MemoryPressureMonitor.FragmentationScore:F1} " +
                                $"secPer10={secondsPer10:F1}");
                        }
                    }

                    var meta = metas[i];
                    try
                    {
                        var predictions = await activeTagService.PredictAsync(meta.FilePath, ct);
                        var filtered = predictions
                            .Where(p => p.Confidence >= _confidenceThreshold)
                            .Take(_maxTagsPerImage)
                            .ToList();
                        await channel.Writer.WriteAsync((meta.Id, meta.FilePath, filtered), ct);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (OutOfMemoryException ex)
                    {
                        AppLogger.Error($"Pipeline#{runId} OOM at {i}/{total}: {ex.Message}");
                        MemoryPressureMonitor.EmergencyCleanup();
                        errors.Enqueue($"{Path.GetFileName(meta.FilePath)}: \u5185\u5b58\u4e0d\u8db3");
                        continue;
                    }
                    catch (Exception ex)
                    {
                        errors.Enqueue($"{Path.GetFileName(meta.FilePath)}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex) { AppLogger.Error($"Producer failed: {ex.Message}"); }
            finally { channel.Writer.Complete(); }
        });

        // Consumer: save tags + batch-stamp AutoTagStatus every 100 to survive mid-run exit
        var processedPaths = new List<string>();
        var consumerTask = Task.Run(async () =>
        {
            var completed = 0;
            var stampBuffer = new List<string>(100);
            try
            {
                await foreach (var item in channel.Reader.ReadAllAsync(ct))
                {
                    try
                    {
                        if (item.Predictions.Count > 0)
                        {
                            var tagNames = item.Predictions.Select(p => p.ChineseName ?? p.TagName).ToList();
                            await _metaRepo.AddAutoTagsAsync(item.ImageId, tagNames);
                        }
                        Interlocked.Increment(ref processed);
                        completed++;
                        stampBuffer.Add(item.FilePath);
                        processedPaths.Add(item.FilePath);

                        if (stampBuffer.Count >= 100)
                        {
                            AppLogger.Memory($"Consumer.Stamp {completed}/{total}");
                            await _metaRepo.SetAutoTagStatusBatchAsync(stampBuffer, 1);
                            stampBuffer.Clear();
                        }

                        if (completed % 10 == 0 || completed == metas.Count)
                        {
                            if (hasState)
                            {
                                await _stateRepo.UpsertStateAsync(new AutoTagState
                                {
                                    FolderId = folderId, Status = "Processing",
                                    TotalFiles = metas.Count, Processed = processed,
                                    LastFileCount = metas.Count
                                });
                            }
                            ProgressChanged?.Invoke(new AutoTagPipelineProgress(
                                "Inference", processed, metas.Count,
                                $"\u6b63\u5728\u63a8\u7406\u56fe\u7247\u6807\u7b7e... {processed}/{metas.Count}"));
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error($"Consumer save failed: {ex.Message}");
                        errors.Enqueue($"consumer: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                ProgressChanged?.Invoke(new AutoTagPipelineProgress(
                    "Stopped", processed, metas.Count,
                    $"\u63a8\u7406\u5df2\u505c\u6b62 ({processed}/{metas.Count})"));
            }

            // Flush remaining stamps
            if (stampBuffer.Count > 0)
                await _metaRepo.SetAutoTagStatusBatchAsync(stampBuffer, 1);

            if (!errors.IsEmpty)
            {
                var msg = string.Join("\n", errors.Take(5));
                if (errors.Count > 5) msg += $"\n... \u53ca\u5176\u4ed6 {errors.Count - 5} \u4e2a\u9519\u8bef";
                AppLogger.Error($"Pipeline#{runId} errors={errors.Count}: {msg.Replace('\n', '|')}");
                ProgressChanged?.Invoke(new AutoTagPipelineProgress(
                    "Error", errors.Count, metas.Count, msg));
            }
        });

        await Task.WhenAll(producerTask, consumerTask);

        int finished = Volatile.Read(ref processed);
        // Memory diagnostics: pipeline end.
        double commitEnd = MemoryPressureMonitor.CommitChargeMB;
        double fragEnd = MemoryPressureMonitor.FragmentationScore;
        double memDelta = commitEnd - commitStart;
        var secondsPer10End = finished > 0 ? runWatch.Elapsed.TotalSeconds / finished * 10.0 : 0;
        AppLogger.Memory($"Pipeline#{runId}.End processed={finished}/{total} errors={errors.Count} " +
            $"commit={commitStart:F0}->{commitEnd:F0}MB (delta={memDelta:+0;-0}MB) " +
            $"frag={fragStart:F1}->{fragEnd:F1} secPer10={secondsPer10End:F1}");

        if (hasState)
        {
            var status = finished >= metas.Count ? "AwaitingReview" : "Processing";
            await _stateRepo.UpsertStateAsync(new AutoTagState
            {
                FolderId = folderId, Status = status,
                TotalFiles = metas.Count, Processed = finished,
                LastFileCount = metas.Count,
                CompletedAt = finished >= metas.Count ? DateTime.UtcNow : null
            });
        }

        return processedPaths;
    }

    public void Dispose() => ProgressChanged = null;
}
