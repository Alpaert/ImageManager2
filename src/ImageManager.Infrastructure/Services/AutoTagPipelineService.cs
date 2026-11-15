using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using ImageManager.Common.Helpers;
using ImageManager.Core.Models;
using ImageManager.Core.Services;
using ImageManager.Infrastructure.Imaging;


namespace ImageManager.Infrastructure.Services;

public readonly record struct AutoTagPipelineProgress(
    string Phase, int Processed, int Total, string StatusText);

public readonly record struct FolderTagActionResult(
    string Action,   // "Start" / "Resume" / "NewFiles" / "Blocked" / "Busy" / "Retry"
    string Message,  // User-facing message
    bool CanProceed);

public class AutoTagPipelineService : IDisposable
{
    private const string EmbeddingModelKey = "pixai";
    private const string EmbeddingModelVersion = "v0.9";
    private readonly IImageMetaRepository _metaRepo;
    private readonly IEnsembleTagService _tagService;
    private readonly IAutoTagStateRepository _stateRepo;
    private readonly IImageEmbeddingRepository _embeddingRepo;
    private readonly SscdEmbeddingService? _sscd;
    private readonly SscdIndexWriter? _sscdWriter;
    private readonly ICharacterTagSuppressionRepository _suppressionRepo;
    private double _confidenceThreshold = 0.35;
    private int _maxTagsPerImage = 20;
    public event Action<AutoTagPipelineProgress>? ProgressChanged;

    // ==================== 鍐呭瓨璇婃柇璁℃暟鍣?====================
    private static int _pipelineRunCount;

    public AutoTagPipelineService(
        IImageMetaRepository metaRepo,
        IEnsembleTagService tagService,
        IAutoTagStateRepository stateRepo,
        IImageEmbeddingRepository embeddingRepo,
        ICharacterTagSuppressionRepository suppressionRepo,
        SscdEmbeddingService? sscd = null, SscdIndexWriter? sscdWriter = null)
    {
        _metaRepo = metaRepo;
        _tagService = tagService;
        _stateRepo = stateRepo;
        _embeddingRepo = embeddingRepo;
        _suppressionRepo = suppressionRepo;
        _sscd = sscd;
        _sscdWriter = sscdWriter;
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
        var sscdEnabled = _sscd?.IsAvailable == true && _sscdWriter != null;
        var validSscdIds = new HashSet<long>();
        if (sscdEnabled)
        {
            try { validSscdIds = await _sscdWriter!.GetValidIdsAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                sscdEnabled = false;
                AppLogger.Warn($"Unable to read SSCD coverage during tagging: {ex.Message}");
            }
        }

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

        var channel = Channel.CreateBounded<(long ImageId, string FilePath, List<TagPrediction> Predictions, float[]? Embedding, SystemRating Rating, SscdIndexWrite? Sscd)>(
            new BoundedChannelOptions(16) { SingleWriter = true, SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait });

        int processed = 0;
        var errors = new ConcurrentQueue<string>();
        long totalInferenceTicks = 0;
        long totalFlushTicks = 0;
        long totalSuppressionTicks = 0;
        long totalAutoTagsTicks = 0;
        long totalRatingTicks = 0;
        long totalStatusTicks = 0;
        long totalEmbeddingTicks = 0;

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
                        List<TagPrediction> predictions;
                        float[]? embedding = null;
                        var rating = SystemRating.Unknown;
                        SscdIndexWrite? sscdWrite = null;
                        var inferSw = Stopwatch.StartNew();
                        if (activeTagService is IEnsembleTagService ensemble)
                        {
                            var (result, pendingSscd) = await PredictWithOptionalSscdAsync(
                                ensemble, meta.Id, meta.FilePath, sscdEnabled && !validSscdIds.Contains(meta.Id), ct);
                            sscdWrite = pendingSscd;
                            predictions = result.MergedTags;
                            embedding = result.Embedding;
                            rating = result.Rating;
                        }
                        else
                        {
                            predictions = await activeTagService.PredictAsync(meta.FilePath, ct);
                        }
                        inferSw.Stop();
                        Interlocked.Add(ref totalInferenceTicks, inferSw.ElapsedTicks);

                        var filtered = predictions
                            .Where(p => p.Confidence >= _confidenceThreshold)
                            .Take(_maxTagsPerImage)
                            .ToList();
                        await channel.Writer.WriteAsync((meta.Id, meta.FilePath, filtered, embedding, rating, sscdWrite), ct);
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

        // Consumer: batch persist tags + ratings + embeddings + status
        var processedPaths = new List<string>();
        var consumerTask = Task.Run(async () =>
        {
            var completed = 0;
            const int BatchChunkSize = 32;
            var chunkBuffer = new List<(long ImageId, string FilePath, List<TagPrediction> Predictions, float[]? Embedding, SystemRating Rating, SscdIndexWrite? Sscd)>(BatchChunkSize);

            async Task FlushChunkAsync(List<(long ImageId, string FilePath, List<TagPrediction> Predictions, float[]? Embedding, SystemRating Rating, SscdIndexWrite? Sscd)> buffer)
            {
                if (buffer.Count == 0) return;
                var flushSw = Stopwatch.StartNew();

                // 1. 批量查询抑制标签
                var imageIdsWithTags = buffer
                    .Where(x => x.Predictions.Count > 0)
                    .Select(x => x.ImageId)
                    .Distinct()
                    .ToList();

                Dictionary<long, HashSet<string>> suppressedMap;
                var swStep = Stopwatch.StartNew();
                if (imageIdsWithTags.Count > 0)
                {
                    suppressedMap = await _suppressionRepo.GetSuppressedTagsAsync(imageIdsWithTags);
                }
                else
                {
                    suppressedMap = new Dictionary<long, HashSet<string>>();
                }
                swStep.Stop();
                totalSuppressionTicks += swStep.ElapsedTicks;

                // 2. 组装并批量写入自动标签
                var autoTagsBatch = new List<(long ImageId, IReadOnlyList<string> TagNames)>();
                foreach (var item in buffer)
                {
                    if (item.Predictions.Count == 0) continue;
                    suppressedMap.TryGetValue(item.ImageId, out var suppressed);
                    suppressed ??= [];

                    var tagNames = item.Predictions
                        .Where(p => !IsSuppressedCharacterPrediction(p, suppressed))
                        .Select(p => p.ChineseName ?? p.TagName)
                        .ToList();

                    if (tagNames.Count > 0)
                    {
                        autoTagsBatch.Add((item.ImageId, tagNames));
                    }
                }

                if (autoTagsBatch.Count > 0)
                {
                    swStep.Restart();
                    await _metaRepo.AddAutoTagsBatchAsync(autoTagsBatch);
                    swStep.Stop();
                    totalAutoTagsTicks += swStep.ElapsedTicks;
                }

                // 3. 组装并批量更新评级
                var ratingBatch = new List<(long ImageId, int Rating)>();
                foreach (var item in buffer)
                {
                    if (item.Rating is >= SystemRating.General and <= SystemRating.Explicit)
                    {
                        ratingBatch.Add((item.ImageId, (int)item.Rating));
                    }
                }

                if (ratingBatch.Count > 0)
                {
                    swStep.Restart();
                    await _metaRepo.SetSystemRatingBatchAsync(ratingBatch);
                    swStep.Stop();
                    totalRatingTicks += swStep.ElapsedTicks;
                }

                // 4. 组装并批量更新 AutoTagStatus = 1
                var stampPaths = buffer.Select(x => x.FilePath).ToList();
                swStep.Restart();
                await _metaRepo.SetAutoTagStatusBatchAsync(stampPaths, 1);
                swStep.Stop();
                totalStatusTicks += swStep.ElapsedTicks;

                // 5. 组装并批量更新 Embedding
                var embeddingBatch = new List<(long ImageMetaId, string? FileHash, float[] Embedding)>();
                foreach (var item in buffer)
                {
                    if (item.Embedding is { Length: > 0 })
                    {
                        embeddingBatch.Add((item.ImageId, null, item.Embedding));
                    }
                }

                if (embeddingBatch.Count > 0)
                {
                    swStep.Restart();
                    await FlushEmbeddingBufferAsync(embeddingBatch);
                    swStep.Stop();
                    totalEmbeddingTicks += swStep.ElapsedTicks;
                }
                if (_sscdWriter is not null)
                {
                    var sscdBatch = buffer.Where(item => item.Sscd is not null).Select(item => item.Sscd!).ToArray();
                    if (sscdBatch.Length > 0)
                    {
                        try { await _sscdWriter.WriteAsync(sscdBatch, ct); }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            AppLogger.Warn($"SSCD vectors can be backfilled later: {ex.Message}");
                        }
                    }
                }


                buffer.Clear();
                flushSw.Stop();
                totalFlushTicks += flushSw.ElapsedTicks;
            }

            try
            {
                await foreach (var item in channel.Reader.ReadAllAsync(ct))
                {
                    try
                    {
                        chunkBuffer.Add(item);
                        processedPaths.Add(item.FilePath);
                        completed++;
                        Interlocked.Increment(ref processed);

                        if (chunkBuffer.Count >= BatchChunkSize)
                        {
                            await FlushChunkAsync(chunkBuffer);
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

            // Flush remaining buffer
            try
            {
                if (chunkBuffer.Count > 0)
                {
                    await FlushChunkAsync(chunkBuffer);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error($"Consumer final flush failed: {ex.Message}");
                errors.Enqueue($"consumer: {ex.Message}");
            }

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

        var timingSummary = $"纯模型推理累加: {TimeSpan.FromTicks(totalInferenceTicks).TotalSeconds:F2}s | DB批量写入累加: {TimeSpan.FromTicks(totalFlushTicks).TotalSeconds:F2}s (抑制: {TimeSpan.FromTicks(totalSuppressionTicks).TotalMilliseconds:F0}ms, 标签: {TimeSpan.FromTicks(totalAutoTagsTicks).TotalMilliseconds:F0}ms, 评级: {TimeSpan.FromTicks(totalRatingTicks).TotalMilliseconds:F0}ms, 向量: {TimeSpan.FromTicks(totalEmbeddingTicks).TotalMilliseconds:F0}ms, 状态: {TimeSpan.FromTicks(totalStatusTicks).TotalMilliseconds:F0}ms)";
        Console.WriteLine($"  [耗时拆解] {timingSummary}");
        AppLogger.Info($"Pipeline#{runId} {timingSummary}");

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

    internal async Task<EnsembleResult> PredictSingleWithSscdAsync(
        IEnsembleTagService ensemble, long id, string path, CancellationToken ct)
    {
        if (_sscd?.IsAvailable != true || _sscdWriter is null || id <= 0)
            return await ensemble.PredictWithSourcesAsync(path, ct);

        using var tagRun = AutoTagRuntimeState.Enter();
        var includeSscd = true;
        try
        {
            var cached = await _embeddingRepo.GetValidSearchEmbeddingsByPathsAsync(
                SscdEmbeddingService.ModelKey, SscdEmbeddingService.ModelVersion, [path], ct);
            includeSscd = !cached.Any(row => RegionSearchService.IsUsableVector(row.Embedding, SscdEmbeddingService.Dimension)
                && RegionSearchService.MatchesFileState(path, row.SourceFileSize, row.SourceLastWriteTicks));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            includeSscd = false;
            AppLogger.Warn($"Unable to read SSCD coverage during tagging: {ex.Message}");
        }

        var (result, pending) = await PredictWithOptionalSscdAsync(ensemble, id, path, includeSscd, ct);
        if (pending is not null)
        {
            try { await _sscdWriter.WriteAsync([pending], ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppLogger.Warn($"SSCD vector can be backfilled later: {ex.Message}");
            }
        }
        return result;
    }

    private async Task<(EnsembleResult Result, SscdIndexWrite? Write)> PredictWithOptionalSscdAsync(
        IEnsembleTagService ensemble, long id, string path, bool includeSscd, CancellationToken ct)
    {
        if (!includeSscd || _sscd?.IsAvailable != true ||
            (ensemble is not EnsembleTagService && ensemble is not SingleModelTagService))
            return (await ensemble.PredictWithSourcesAsync(path, ct), null);

        var source = await TryOpenSscdSourceAsync(id, path, ct);
        using var sourceLock = source?.Stream;
        using var bitmap = source is null ? null : InferenceImageLoader.DecodeForInference(path);
        if (bitmap is null || source is null)
            return (await ensemble.PredictWithSourcesAsync(path, ct), null);

        var result = ensemble is EnsembleTagService dual
            ? await dual.PredictWithSourcesAsync(bitmap, Path.GetFileName(path), ct)
            : await ((SingleModelTagService)ensemble).PredictWithSourcesAsync(bitmap, ct);
        if (!RegionSearchService.MatchesFileState(path, source.Size, source.Ticks))
            return (result, null);

        try
        {
            var vector = await _sscd.EncodeAsync(bitmap, ct);
            return (result, new SscdIndexWrite(id, path, source.Size, source.Ticks, vector, source.Hash));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLogger.Warn($"SSCD vector can be backfilled later: {ex.Message}");
            return (result, null);
        }
    }

    private sealed record SscdTagSource(FileStream Stream, long Size, long Ticks, string? Hash);

    private async Task<SscdTagSource?> TryOpenSscdSourceAsync(long id, string path, CancellationToken ct)
    {
        FileStream? stream = null;
        try
        {
            var state = await _sscdWriter!.GetSourceAsync(id, ct);
            if (state is not { } row || !string.Equals(row.FilePath, path, StringComparison.OrdinalIgnoreCase))
                return null;
            stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length != row.FileSize || File.GetLastWriteTimeUtc(path).Ticks != row.LastWriteTicks)
                return null;
            var source = new SscdTagSource(stream, row.FileSize, row.LastWriteTicks, row.FileHash);
            stream = null;
            return source;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLogger.Warn($"SSCD source will be backfilled later: {ex.Message}");
            return null;
        }
        finally { stream?.Dispose(); }
    }

    private async Task FlushEmbeddingBufferAsync(List<(long ImageMetaId, string? FileHash, float[] Embedding)> buffer)
    {
        if (buffer.Count == 0) return;

        try
        {
            await _embeddingRepo.UpsertBatchAsync(buffer, EmbeddingModelKey, EmbeddingModelVersion);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Embedding batch save failed count={buffer.Count}: {ex.Message}");
        }
        finally
        {
            buffer.Clear();
        }
    }

    private static bool IsSuppressedCharacterPrediction(TagPrediction prediction, HashSet<string> suppressed)
    {
        if (suppressed.Count == 0 ||
            prediction.SourceModels?.Contains("character_embedding", StringComparer.OrdinalIgnoreCase) != true)
            return false;

        var name = prediction.ChineseName ?? prediction.TagName;
        return suppressed.Contains(name);
    }

    public void Dispose() => ProgressChanged = null;
}
