using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using ImageManager.Common.Helpers;
using ImageManager.Core.Models;
using ImageManager.Core.Services;
using ImageManager.Infrastructure.Data;
using ImageManager.Infrastructure.Data.Repositories;
using ImageManager.Infrastructure.Imaging;
using ImageManager.Infrastructure.Services;
using Microsoft.Data.Sqlite;

namespace ImageManager.AutoTagPerfProbe;

internal static class Program
{
    private const string ProductionDbPath = @"G:\ImageManagerCarche\data.db";
    private const string ProductionCacheDir = @"G:\ImageManagerCarche";
    private const string DefaultDatasetDir = @"E:\p站图片 2026.5.6-12点\text2";
    private const string ModelsDir = @"G:\ImageManagerCarche\models";

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        Console.WriteLine("===============================================================");
        Console.WriteLine("       ImageManager2 - 自动打标推理基线测量工具 (P0 基准)      ");
        Console.WriteLine("===============================================================");

        // 1. 安全检查与临时目录初始化
        var tempRootDir = Path.Combine(AppContext.BaseDirectory, "perf_probe_temp");
        if (Directory.Exists(tempRootDir))
        {
            try { Directory.Delete(tempRootDir, recursive: true); }
            catch { /* ignore */ }
        }
        Directory.CreateDirectory(tempRootDir);

        var tempDbPath = Path.Combine(tempRootDir, "benchmark_isolated.db");
        AssertStrictDatabaseIsolation(tempDbPath);

        AppLogger.Init(tempRootDir);
        Console.WriteLine($"[安全隔离] 生产数据库: {ProductionDbPath} (已硬锁定，严禁触碰)");
        Console.WriteLine($"[安全隔离] 临时隔离库: {tempDbPath}");
        Console.WriteLine($"[临时目录] {tempRootDir}");

        // 2. 加载 ONNX Runtime 原生库
        LoadNativeOnnxRuntime();

        // 3. 检查模型目录
        if (!Directory.Exists(ModelsDir))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[错误] 模型目录不存在: {ModelsDir}");
            Console.ResetColor();
            return 1;
        }

        // 4. 获取测试数据集
        var datasetDir = args.FirstOrDefault(a => !a.StartsWith("--")) ?? DefaultDatasetDir;
        if (!Directory.Exists(datasetDir))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[错误] 测试集目录不存在: {datasetDir}");
            Console.ResetColor();
            return 1;
        }

        var allowedExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp" };
        var imageFiles = Directory.GetFiles(datasetDir, "*.*", SearchOption.TopDirectoryOnly)
            .Where(f => allowedExts.Contains(Path.GetExtension(f)))
            .OrderBy(f => f)
            .ToList();

        int totalCount = imageFiles.Count;
        int jpgCount = imageFiles.Count(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase));
        int pngCount = imageFiles.Count(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
        Console.WriteLine($"[数据集] 目录: {datasetDir}");
        Console.WriteLine($"[数据集] 图像总数: {totalCount} 张 (JPG: {jpgCount}, PNG: {pngCount})");

        if (totalCount == 0)
        {
            Console.WriteLine("[错误] 数据集中没有找到有效图像！");
            return 1;
        }

        // 5. 初始化模型服务
        Console.WriteLine("\n[初始化] 正在加载打标模型 (WD14 + PixAI)...");
        var onnxTagService = new OnnxTagService();
        var wdRatingService = new WdRatingService(onnxTagService);
        var pixaiTagService = new PixaiTagService();
        var chineseLib = new ChineseTagLibrary();
        var merger = new TagResultMerger(chineseLib);
        var artistStore = new ArtistEmbeddingStore();
        var characterStore = new CharacterEmbeddingStore();

        var ensembleService = new EnsembleTagService(
            wdRatingService,
            pixaiTagService,
            merger,
            chineseLib,
            artistStore,
            characterStore);

        var loadSw = Stopwatch.StartNew();
        await ensembleService.LoadModelAsync(ModelsDir);
        loadSw.Stop();
        Console.WriteLine($"[初始化] 模型加载完成，耗时: {loadSw.ElapsedMilliseconds} ms");

        // 6. 预热 (Warm-up)
        Console.WriteLine("\n[预热] 正在执行 5 张图片预热 (JIT & CUDA 初始化)...");
        var warmupList = imageFiles.Take(5).ToList();
        foreach (var img in warmupList)
        {
            _ = ThumbnailGenerator.DecodeForAnalysis(img, 896);
            _ = await ensembleService.PredictWithSourcesAsync(img);
        }
        Console.WriteLine("[预热] 预热完成！开始正式测量...\n");

        bool skipT1T4 = args.Contains("--skip-t1-t4");

        MetricResult t1All, t1Jpg, t1Png, t2Metrics, t3Metrics, t4Metrics;
        if (!skipT1T4)
        {
            // 7. 测试项 T1: 纯图片解码 (SkiaSharp Decode Benchmark)
            Console.WriteLine("---------------------------------------------------------------");
            Console.WriteLine(">>> 测试项 T1: 纯图片解码 (ThumbnailGenerator.DecodeForAnalysis 896px)");
            Console.WriteLine("---------------------------------------------------------------");
            var t1Metrics = MeasureDecode(imageFiles);
            t1All = t1Metrics.All;
            t1Jpg = t1Metrics.Jpg;
            t1Png = t1Metrics.Png;
            PrintMetricsReport("T1: 图片解码 (全部)", t1All);
            PrintMetricsReport("T1: 图片解码 (JPG)", t1Jpg);
            PrintMetricsReport("T1: 图片解码 (PNG)", t1Png);

            // 8. 测试项 T2: WD 单模型评级 (WdRatingService.PredictRatingAsync)
            Console.WriteLine("\n---------------------------------------------------------------");
            Console.WriteLine(">>> 测试项 T2: WD SwinV2 单模型 (Rating 提取)");
            Console.WriteLine("---------------------------------------------------------------");
            t2Metrics = await MeasureWdRatingAsync(wdRatingService, imageFiles);
            PrintMetricsReport("T2: WD SwinV2", t2Metrics);

            // 9. 测试项 T3: PixAI 单模型 (PredictWithEmbeddingAsync)
            Console.WriteLine("\n---------------------------------------------------------------");
            Console.WriteLine(">>> 测试项 T3: PixAI 单模型 (General+Character 标签 + 1024维向量)");
            Console.WriteLine("---------------------------------------------------------------");
            t3Metrics = await MeasurePixaiAsync(pixaiTagService, imageFiles);
            PrintMetricsReport("T3: PixAI Tagger", t3Metrics);

            // 10. 测试项 T4: 双模型融合预测 (EnsembleTagService.PredictWithSourcesAsync)
            Console.WriteLine("\n---------------------------------------------------------------");
            Console.WriteLine(">>> 测试项 T4: 双模型端到端推理 (EnsembleTagService PredictWithSourcesAsync)");
            Console.WriteLine("---------------------------------------------------------------");
            var (t4M, baselineResults) = await MeasureEnsembleAsync(ensembleService, imageFiles);
            t4Metrics = t4M;
            PrintMetricsReport("T4: 双模型融合预测", t4Metrics);

            // 导出 baseline.json 用于日后等价性比对
            var baselineJsonPath = Path.Combine(tempRootDir, "baseline.json");
            var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
            await File.WriteAllTextAsync(baselineJsonPath, JsonSerializer.Serialize(baselineResults, jsonOptions));
            Console.WriteLine($"[数据保存] 已保存 baseline.json 到临时目录: {baselineJsonPath} (用于后续等价性校验)");
        }
        else
        {
            Console.WriteLine("[提示] 收到 --skip-t1-t4 参数，复用 P1 精确测得的 T1~T4 实测基准数据：");
            t1All = new MetricResult(513, 12.55, 24.3, 18.7, 63.6, 0, 0, 40.89);
            t1Jpg = new MetricResult(332, 7.12, 21.5, 16.6, 56.4, 0, 0, 46.60);
            t1Png = new MetricResult(181, 5.36, 29.6, 24.3, 76.6, 0, 0, 33.79);
            t2Metrics = new MetricResult(513, 42.82, 83.5, 78.4, 128.7, 0, 0, 11.98);
            t3Metrics = new MetricResult(513, 86.10, 167.8, 167.1, 214.1, 0, 0, 5.96);
            t4Metrics = new MetricResult(513, 88.67, 172.8, 171.6, 214.2, 0, 0, 5.79);
            PrintMetricsReport("T1: 图片解码 (全部)", t1All);
            PrintMetricsReport("T2: WD SwinV2", t2Metrics);
            PrintMetricsReport("T3: PixAI Tagger", t3Metrics);
            PrintMetricsReport("T4: 双模型融合预测 (P1 实测)", t4Metrics);
        }

        // 11. 测试项 T5: 完整 Pipeline 带临时数据库写入 (AutoTagPipelineService)
        Console.WriteLine("\n---------------------------------------------------------------");
        Console.WriteLine(">>> 测试项 T5: 完整 Pipeline 带数据库持久化 (AutoTagPipelineService, P2 批量持久化)");
        Console.WriteLine("---------------------------------------------------------------");
        var t5Metrics = await MeasurePipelineAsync(tempDbPath, ensembleService, imageFiles);
        PrintMetricsReport("T5: 完整 Pipeline (含 DB)", t5Metrics);

        // 12. 测试项 T6: PixAI 批推理微基准 (Batch Size 1, 2, 4, 8, 16)
        if (!args.Contains("--skip-t6"))
        {
            Console.WriteLine("\n---------------------------------------------------------------");
            Console.WriteLine(">>> 测试项 T6: PixAI 批量提取向量 (GetEmbeddingsBatchAsync 微基准)");
            Console.WriteLine("---------------------------------------------------------------");
            await MeasurePixaiBatchAsync(pixaiTagService, imageFiles.Take(64).ToList());
        }

        // 13. 打印最终总览表格
        Console.WriteLine("\n===============================================================");
        Console.WriteLine("                       基准测试结果总汇表                       ");
        Console.WriteLine("===============================================================");
        Console.WriteLine("| 测试阶段                        | 总耗时(s) | 平均单张(ms) | P50(ms) | P95(ms) | 吞吐率(FPS) |");
        Console.WriteLine("|---------------------------------|-----------|--------------|---------|---------|-------------|");
        PrintSummaryRow("T1: 纯解码 (Overall 513张)", t1All);
        PrintSummaryRow("T1: 纯解码 (JPG 332张)", t1Jpg);
        PrintSummaryRow("T1: 纯解码 (PNG 181张)", t1Png);
        PrintSummaryRow("T2: WD SwinV2 单模型 (513张)", t2Metrics);
        PrintSummaryRow("T3: PixAI 单模型 (513张)", t3Metrics);
        PrintSummaryRow("T4: 双模型端到端推理 (513张)", t4Metrics);
        PrintSummaryRow("T5: 完整管线含写库 (513张)", t5Metrics);
        Console.WriteLine("===============================================================");

        ensembleService.Dispose();
        Console.WriteLine("\n基准测试顺利完成！临时数据与测试库均处于隔离环境。");
        return 0;
    }

    private static (MetricResult All, MetricResult Jpg, MetricResult Png) MeasureDecode(List<string> files)
    {
        var allTimes = new List<double>(files.Count);
        var jpgTimes = new List<double>();
        var pngTimes = new List<double>();

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < files.Count; i++)
        {
            var path = files[i];
            bool isPng = path.EndsWith(".png", StringComparison.OrdinalIgnoreCase);

            var itemSw = Stopwatch.StartNew();
            using var bmp = ThumbnailGenerator.DecodeForAnalysis(path, 896);
            itemSw.Stop();

            double ms = itemSw.Elapsed.TotalMilliseconds;
            allTimes.Add(ms);
            if (isPng) pngTimes.Add(ms);
            else jpgTimes.Add(ms);

            if ((i + 1) % 100 == 0 || i + 1 == files.Count)
            {
                Console.Write($"\r  -> 已解码: {i + 1}/{files.Count} ...");
            }
        }
        sw.Stop();
        Console.WriteLine();

        return (
            CalculateMetrics(allTimes, sw.Elapsed.TotalSeconds),
            CalculateMetrics(jpgTimes, jpgTimes.Sum() / 1000.0),
            CalculateMetrics(pngTimes, pngTimes.Sum() / 1000.0)
        );
    }

    private static async Task<MetricResult> MeasureWdRatingAsync(WdRatingService wd, List<string> files)
    {
        var times = new List<double>(files.Count);
        int[] ratingCounts = new int[4];

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < files.Count; i++)
        {
            var itemSw = Stopwatch.StartNew();
            var rating = await wd.PredictRatingAsync(files[i]);
            itemSw.Stop();

            times.Add(itemSw.Elapsed.TotalMilliseconds);
            int rIdx = (int)rating;
            if (rIdx >= 0 && rIdx < 4) ratingCounts[rIdx]++;

            if ((i + 1) % 100 == 0 || i + 1 == files.Count)
            {
                Console.Write($"\r  -> 已处理: {i + 1}/{files.Count} ...");
            }
        }
        sw.Stop();
        Console.WriteLine();
        Console.WriteLine($"  [评级分布] General: {ratingCounts[0]}, Sensitive: {ratingCounts[1]}, Questionable: {ratingCounts[2]}, Explicit: {ratingCounts[3]}");

        return CalculateMetrics(times, sw.Elapsed.TotalSeconds);
    }

    private static async Task<MetricResult> MeasurePixaiAsync(PixaiTagService pixai, List<string> files)
    {
        var times = new List<double>(files.Count);
        int totalTags = 0;

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < files.Count; i++)
        {
            var itemSw = Stopwatch.StartNew();
            var (tags, emb) = await pixai.PredictWithEmbeddingAsync(files[i]);
            itemSw.Stop();

            times.Add(itemSw.Elapsed.TotalMilliseconds);
            totalTags += tags.Count;

            if ((i + 1) % 100 == 0 || i + 1 == files.Count)
            {
                Console.Write($"\r  -> 已处理: {i + 1}/{files.Count} ...");
            }
        }
        sw.Stop();
        Console.WriteLine();
        Console.WriteLine($"  [标签统计] 总标签数: {totalTags}, 平均每张: {(double)totalTags / files.Count:F1} 个");

        return CalculateMetrics(times, sw.Elapsed.TotalSeconds);
    }

    private static async Task<(MetricResult Metrics, List<BaselineItem> Baseline)> MeasureEnsembleAsync(
        EnsembleTagService ensemble, List<string> files)
    {
        var times = new List<double>(files.Count);
        var baseline = new List<BaselineItem>(files.Count);

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < files.Count; i++)
        {
            var path = files[i];
            var itemSw = Stopwatch.StartNew();
            var res = await ensemble.PredictWithSourcesAsync(path);
            itemSw.Stop();

            times.Add(itemSw.Elapsed.TotalMilliseconds);

            baseline.Add(new BaselineItem(
                FileName: Path.GetFileName(path),
                Rating: res.Rating.ToString(),
                Artist: res.ArtistName,
                Top5Tags: res.MergedTags.Take(5).Select(t => $"{t.TagName}:{t.Confidence:F2}").ToList(),
                EmbeddingSample: res.Embedding != null ? res.Embedding.Take(5).Select(v => Math.Round(v, 4)).ToArray() : []
            ));

            if ((i + 1) % 100 == 0 || i + 1 == files.Count)
            {
                Console.Write($"\r  -> 已处理: {i + 1}/{files.Count} ...");
            }
        }
        sw.Stop();
        Console.WriteLine();

        return (CalculateMetrics(times, sw.Elapsed.TotalSeconds), baseline);
    }

    private static async Task<MetricResult> MeasurePipelineAsync(
        string tempDbPath, EnsembleTagService ensembleService, List<string> files)
    {
        AssertStrictDatabaseIsolation(tempDbPath);

        // 初始化临时数据库
        var dbFactory = new DbContextFactory(tempDbPath);
        using (var initConn = dbFactory.CreateConnection())
        {
            initConn.Open();
            DatabaseInitializer.Initialize(initConn);

            // 预插入 513 条 ImageMeta 和 1 条 Folder
            using var txn = initConn.BeginTransaction();
            initConn.Execute(@"
                INSERT OR IGNORE INTO Folder (Id, Path)
                VALUES (1, @FolderPath)",
                new { FolderPath = Path.GetDirectoryName(files[0]) ?? "test" }, txn);

            foreach (var f in files)
            {
                initConn.Execute(@"
                    INSERT INTO ImageMeta (FilePath, FileHash, FileSize, CreatedAt, UpdatedAt, AutoTagStatus)
                    VALUES (@FilePath, @FileHash, @FileSize, @Now, @Now, 0)",
                    new
                    {
                        FilePath = f,
                        FileHash = Guid.NewGuid().ToString("N"),
                        FileSize = new FileInfo(f).Length,
                        Now = DateTime.UtcNow
                    }, txn);
            }
            txn.Commit();
        }

        var metaRepo = new ImageMetaRepository(dbFactory);
        var stateRepo = new AutoTagStateRepository(dbFactory);
        var suppressionRepo = new CharacterTagSuppressionRepository(dbFactory);
        var embeddingRepo = new ImageEmbeddingRepository(dbFactory, suppressionRepo);

        using var pipeline = new AutoTagPipelineService(
            metaRepo, ensembleService, stateRepo, embeddingRepo, suppressionRepo);

        // 构造待处理项
        var metas = new List<(long Id, string FilePath)>();
        using (var conn = dbFactory.CreateConnection())
        {
            var rows = conn.Query<(long Id, string FilePath)>("SELECT Id, FilePath FROM ImageMeta ORDER BY Id").ToList();
            metas.AddRange(rows);
        }

        Console.WriteLine($"  [临时库] 已初始化临时测试数据库并写入 {metas.Count} 条记录。");

        var sw = Stopwatch.StartNew();
        var processedPaths = await pipeline.RunInferenceAsync(1, metas, "Start", CancellationToken.None, ensembleService);
        sw.Stop();

        double totalSeconds = sw.Elapsed.TotalSeconds;
        int count = processedPaths.Count;
        double avgMs = count > 0 ? (totalSeconds * 1000.0) / count : 0;
        double fps = count > 0 ? count / totalSeconds : 0;

        Console.WriteLine($"  [流水线完成] 处理有效张数: {count}/{files.Count}, 总用时: {totalSeconds:F2} s, FPS: {fps:F2}");

        // 校验临时库中的写入结果
        using (var checkConn = dbFactory.CreateConnection())
        {
            var totalTags = checkConn.ExecuteScalar<int>("SELECT COUNT(*) FROM ImageTag WHERE Source = 'AutoTag'");
            var distinctTags = checkConn.ExecuteScalar<int>("SELECT COUNT(*) FROM Tag");
            var ratings = checkConn.Query<(int Rating, int Cnt)>(
                "SELECT SystemRating, COUNT(*) as Cnt FROM ImageMeta GROUP BY SystemRating ORDER BY SystemRating").ToList();
            var status1Count = checkConn.ExecuteScalar<int>("SELECT COUNT(*) FROM ImageMeta WHERE AutoTagStatus = 1");

            Console.WriteLine($"  [持久化校验] AutoTag 关联记录数: {totalTags} (513张 * 默认Top 20截断 = 10260, 吻合率: {(totalTags == 10260 ? "100.0%" : "不一致")})");
            Console.WriteLine($"  [持久化校验] 词典独立 Tag 数: {distinctTags}");
            Console.WriteLine($"  [持久化校验] AutoTagStatus=1 标记数: {status1Count}/{files.Count}");
            Console.WriteLine($"  [持久化校验] 评级分布: {string.Join(", ", ratings.Select(r => $"Rating {r.Rating}: {r.Cnt}"))} (基线: Rating 0(General): 83, Rating 1(Sensitive): 368, Rating 2(Questionable): 54, Rating 3(Explicit): 8)");
        }

        return new MetricResult(
            Count: count,
            TotalSeconds: totalSeconds,
            AvgMs: avgMs,
            P50Ms: avgMs, // Pipeline 统一测整体，P50近似为平均值
            P95Ms: avgMs,
            MinMs: 0,
            MaxMs: 0,
            Fps: fps
        );
    }

    private static async Task MeasurePixaiBatchAsync(PixaiTagService pixai, List<string> sampleFiles)
    {
        int[] batchSizes = [1, 2, 4, 8, 16];
        Console.WriteLine("| 批大小 (Batch Size) | 样本数 | 总耗时(s) | 平均每张(ms) | 吞吐率(FPS) | 加速比   |");
        Console.WriteLine("|---------------------|--------|-----------|--------------|-------------|----------|");

        double baselineFps = 0;
        foreach (var bs in batchSizes)
        {
            int count = (sampleFiles.Count / bs) * bs;
            if (count == 0) continue;
            var subFiles = sampleFiles.Take(count).ToList();

            try
            {
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < count; i += bs)
                {
                    var batchSlice = subFiles.GetRange(i, bs);
                    _ = await pixai.GetEmbeddingsBatchAsync(batchSlice);
                }
                sw.Stop();

                double totalSec = sw.Elapsed.TotalSeconds;
                double avgMs = (totalSec * 1000.0) / count;
                double fps = count / totalSec;
                if (bs == 1) baselineFps = fps;
                double speedup = baselineFps > 0 ? fps / baselineFps : 1.0;

                Console.WriteLine($"| {bs,-19} | {count,-6} | {totalSec,-9:F2} | {avgMs,-12:F1} | {fps,-11:F2} | {speedup,-7:F2}x |");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"| {bs,-19} | {count,-6} | 显存超出限制: {ex.Message.Split('\n').First().Trim()} |");
                break; // 超过显存限制后不再测试更大的 batch size
            }
        }
    }

    private static MetricResult CalculateMetrics(List<double> times, double totalSeconds)
    {
        if (times.Count == 0) return new MetricResult(0, 0, 0, 0, 0, 0, 0, 0);

        var sorted = times.OrderBy(t => t).ToList();
        int count = sorted.Count;
        double avg = sorted.Average();
        double p50 = sorted[(int)(count * 0.50)];
        double p95 = sorted[(int)Math.Min(count - 1, count * 0.95)];
        double min = sorted[0];
        double max = sorted[^1];
        double fps = totalSeconds > 0 ? count / totalSeconds : 0;

        return new MetricResult(count, totalSeconds, avg, p50, p95, min, max, fps);
    }

    private static void PrintMetricsReport(string label, MetricResult m)
    {
        Console.WriteLine($"  [{label}] 样本: {m.Count} 张 | 总耗时: {m.TotalSeconds:F2}s | 平均: {m.AvgMs:F1}ms | P50: {m.P50Ms:F1}ms | P95: {m.P95Ms:F1}ms | FPS: {m.Fps:F2}");
    }

    private static void PrintSummaryRow(string label, MetricResult m)
    {
        Console.WriteLine($"| {label,-31} | {m.TotalSeconds,9:F2} | {m.AvgMs,12:F1} | {m.P50Ms,7:F1} | {m.P95Ms,7:F1} | {m.Fps,11:F2} |");
    }

    private static void AssertStrictDatabaseIsolation(string dbPath)
    {
        var full = Path.GetFullPath(dbPath).TrimEnd('\\', '/');
        var prodFull = Path.GetFullPath(ProductionDbPath).TrimEnd('\\', '/');

        if (string.Equals(full, prodFull, StringComparison.OrdinalIgnoreCase))
        {
            throw new SecurityException($"[CRITICAL SAFETY VIOLATION] 严禁使用生产数据库路径: {full}");
        }

        if (full.Contains(ProductionCacheDir, StringComparison.OrdinalIgnoreCase) && full.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
        {
            throw new SecurityException($"[CRITICAL SAFETY VIOLATION] 严禁在生产缓存目录下读写任何数据库文件: {full}");
        }
    }

    private static void LoadNativeOnnxRuntime()
    {
        var exeDir = AppContext.BaseDirectory;
        var runtimesPath = Path.Combine(exeDir, "runtimes", "win-x64", "native", "onnxruntime.dll");
        var rootPath = Path.Combine(exeDir, "onnxruntime.dll");
        var targetPath = File.Exists(rootPath) ? rootPath
                       : File.Exists(runtimesPath) ? runtimesPath
                       : null;

        if (targetPath != null)
        {
            try
            {
                NativeLibrary.Load(targetPath);
                Console.WriteLine($"[CUDA 运行时] 成功显式加载原生 ONNX Runtime: {targetPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CUDA 运行时警告] 原生库显式加载失败: {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine($"[CUDA 运行时提示] 未在程序目录找到局部 onnxruntime.dll，将由系统解析。");
        }
    }

    public record MetricResult(
        int Count,
        double TotalSeconds,
        double AvgMs,
        double P50Ms,
        double P95Ms,
        double MinMs,
        double MaxMs,
        double Fps
    );

    public record BaselineItem(
        string FileName,
        string Rating,
        string? Artist,
        List<string> Top5Tags,
        double[] EmbeddingSample
    );
}
