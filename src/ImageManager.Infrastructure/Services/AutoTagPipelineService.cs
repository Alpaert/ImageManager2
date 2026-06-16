using System.Collections.Concurrent;
using System.Threading.Channels;
using ImageManager.Common.Helpers;
using ImageManager.Core.Models;
using ImageManager.Core.Services;

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
    private readonly ITagRepository _tagRepo;
    private readonly ITagMappingRepository _mappingRepo;
    private readonly IEnsembleTagService _tagService;
    private readonly IAutoTagStateRepository _stateRepo;
    private double _confidenceThreshold = 0.35;
    private int _maxTagsPerImage = 20;
    private int _maxConcurrency = 1;  // GPU 推理时 1，避免显存争抢
    public event Action<AutoTagPipelineProgress>? ProgressChanged;

    // ==================== 内存诊断计数器 ====================
    private static int _pipelineRunCount;

    public AutoTagPipelineService(
        IImageMetaRepository metaRepo,
        ITagRepository tagRepo,
        ITagMappingRepository mappingRepo,
        IEnsembleTagService tagService,
        IAutoTagStateRepository stateRepo)
    {
        _metaRepo = metaRepo;
        _tagRepo = tagRepo;
        _mappingRepo = mappingRepo;
        _tagService = tagService;
        _stateRepo = stateRepo;
    }

    public void Configure(double confidenceThreshold, int maxTagsPerImage, int maxConcurrency = 1)
    {
        _confidenceThreshold = confidenceThreshold;
        _maxTagsPerImage = maxTagsPerImage;
        _maxConcurrency = maxConcurrency;
    }

    // ==================== Situation Assessment ====================

    public async Task<FolderTagActionResult> DetermineActionAsync(long folderId, long folderFileCount)
    {
        var state = await _stateRepo.GetStateAsync(folderId);

        if (state == null)
            return new FolderTagActionResult("Start", "开始对文件夹进行自动打标？", true);

        return state.Status switch
        {
            "Processing" => new FolderTagActionResult("Recover",
                $"检测到上次打标中断（已完成 {state.Processed}/{state.TotalFiles}）。\n是否清理并重新打标？", true),

            "Done" when state.LastFileCount == folderFileCount => new FolderTagActionResult("ReTag",
                "此文件夹已完成打标。\n是否删除所有自动标签并重新打标？\n（手动标签不受影响）", true),

            "Done" when state.LastFileCount < folderFileCount => new FolderTagActionResult("NewFiles",
                $"此文件夹已打标，但检测到 {folderFileCount - state.LastFileCount} 张新图片。是否仅对新图片打标？", true),

            "Done" => new FolderTagActionResult("Blocked",
                "此文件夹已完成打标，无需重复。", false),

            "AwaitingReview" => new FolderTagActionResult("Resume",
                "此文件夹推理已完成，是否直接确认标签？（不会重新推理）\n选择\"否\"可重新打标", true),

            "Failed" => new FolderTagActionResult("Retry",
                $"上次打标失败。错误：{state.ErrorMsg ?? "未知"}\n是否重新打标？", true),

            _ => new FolderTagActionResult("Start", "开始对文件夹进行自动打标？", true)
        };
    }

    // ==================== Inference ====================

    /// <returns>List of file paths that were actually processed (for post-pipeline refresh).</returns>
    public async Task<List<string>> RunInferenceAsync(long folderId, List<(long Id, string FilePath)> metas, string action,
        CancellationToken ct = default)
    {
        bool hasState = folderId > 0;
        int runId = Interlocked.Increment(ref _pipelineRunCount);
        int total = metas.Count;
        int totalBatches = (total + 199) / 200;  // ceil division by batchSize

        // === 内存诊断：Pipeline 入口 ===
        AppLogger.Memory($"Pipeline#{runId}.Start total={total} action={action}");

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
            new BoundedChannelOptions(200) { SingleWriter = false, SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait });

        var ioSemaphore = new SemaphoreSlim(_maxConcurrency);
        int processed = 0;
        var errors = new ConcurrentQueue<string>();
        const int batchSize = 200;

        // Producer: launch in batches to avoid N concurrent Task objects.
        // GC + LOH compaction between batches prevents commit exhaustion (0xc000012d)
        // caused by SKBitmap.Decode LOH fragmentation under 64-bit GC heuristics.
        var producerTask = Task.Run(async () =>
        {
            int batchIdx = 0;
            try
            {
                for (int batchStart = 0; batchStart < metas.Count; batchStart += batchSize)
                {
                    if (ct.IsCancellationRequested) break;
                    batchIdx++;
                    int batchEnd = Math.Min(batchStart + batchSize, metas.Count);
                    var batch = metas.GetRange(batchStart, batchEnd - batchStart);
                    int batchNum = batchIdx;

                    // === 内存诊断：每 batch 推理前 ===
                    AppLogger.Memory($"Batch{batchNum}/{totalBatches}.PreInfer size={batch.Count}");

                    var batchTasks = batch.Select(async meta =>
                    {
                        if (ct.IsCancellationRequested) return;
                        var acquired = false;
                        try
                        {
                            await ioSemaphore.WaitAsync(ct);
                            acquired = true;
                            var predictions = await _tagService.PredictAsync(meta.FilePath);
                            var filtered = predictions
                                .Where(p => p.Confidence >= _confidenceThreshold)
                                .Take(_maxTagsPerImage)
                                .ToList();
                            await channel.Writer.WriteAsync((meta.Id, meta.FilePath, filtered), ct);
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception ex)
                        {
                            errors.Enqueue($"{Path.GetFileName(meta.FilePath)}: {ex.Message}");
                        }
                        finally
                        {
                            if (acquired) ioSemaphore.Release();
                        }
                    });
                    await Task.WhenAll(batchTasks);

                    // === 内存诊断：推理后、GC 前 ===
                    AppLogger.Memory($"Batch{batchNum}/{totalBatches}.PostInfer");

                    // Force Gen2 + LOH compaction after each batch.
                    // Without this, 64-bit GC sees infinite address space and never
                    // compacts LOH, letting SKBitmap fragments exhaust system commit.
                    for (int gcRetry = 0; gcRetry < 2; gcRetry++)
                    {
                        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
                        GC.WaitForPendingFinalizers();
                    }

                    // === 内存诊断：GC 压缩后 ===
                    AppLogger.Memory($"Batch{batchNum}/{totalBatches}.PostGC");
                }
            }
            catch (Exception ex) { AppLogger.Error($"Producer tasks failed: {ex.Message}"); }
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
                                $"正在推理图片标签... {processed}/{metas.Count}"));
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
                    $"推理已停止 ({processed}/{metas.Count})"));
            }

            // Flush remaining stamps
            if (stampBuffer.Count > 0)
                await _metaRepo.SetAutoTagStatusBatchAsync(stampBuffer, 1);

            if (!errors.IsEmpty)
            {
                var msg = string.Join("\n", errors.Take(5));
                if (errors.Count > 5) msg += $"\n... 及其他 {errors.Count - 5} 个错误";
                AppLogger.Error($"Pipeline#{runId} errors={errors.Count}: {msg.Replace('\n', '|')}");
                ProgressChanged?.Invoke(new AutoTagPipelineProgress(
                    "Error", errors.Count, metas.Count, msg));
            }
        });

        await Task.WhenAll(producerTask, consumerTask);

        int finished = Volatile.Read(ref processed);
        // === 内存诊断：Pipeline 结束 ===
        AppLogger.Memory($"Pipeline#{runId}.End processed={finished}/{total} errors={errors.Count}");

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
