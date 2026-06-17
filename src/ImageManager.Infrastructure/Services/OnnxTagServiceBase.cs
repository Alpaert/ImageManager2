using ImageManager.Common.Helpers;
using ImageManager.Core.Services;
using ImageManager.Infrastructure.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace ImageManager.Infrastructure.Services;

/// <summary>
/// ONNX 打标模型抽象基类。
/// 参数化预处理、类别过滤、模型下载，子类只需提供模型特定常量。
/// 预处理：RGB → NCHW → 归一化（与旧 WD 模型的 BGR/NHWC/0-255 不同）。
/// </summary>
public abstract class OnnxTagServiceBase : IDisposable
{
    // ==================== 子类必须提供 ====================
    protected abstract string ModelRepo { get; }
    protected abstract string ModelFileName { get; }
    protected abstract string TagsFileName { get; }
    protected abstract string ModelSubDir { get; }
    protected abstract int InputSize { get; }
    protected abstract float[] Mean { get; }
    protected abstract float[] Std { get; }
    protected abstract bool PreserveAspectRatio { get; }

    /// <summary>null=全类别输出；HashSet 如 [0,4]=只输出 general+character</summary>
    protected virtual HashSet<int>? EnabledCategories => null;

    /// <summary>默认最低置信度（可在 Postprocess 中覆盖）</summary>
    protected virtual double DefaultThreshold => 0.15;

    /// <summary>CSV 中 tag_id 列索引（WD 为 0，pixai/camie 为 1）</summary>
    protected virtual int CsvTagIdIndex => 0;

    /// <summary>CSV 中 category 列索引（WD 为 2，pixai/camie 为 3）</summary>
    protected virtual int CsvCategoryIndex => 2;

    /// <summary>模型输出是否需要手动 sigmoid（WD 内置 sigmoid→false，pixai/camie→false）</summary>
    protected virtual bool NeedsSigmoid => false;

    /// <summary>期望的输出名称（子类可覆盖），null=自动查找</summary>
    protected virtual string? PreferredOutputName => null;

    /// <summary>每张图最多返回标签数，0=不限制（子类可覆盖）</summary>
    protected virtual int MaxResults => 0;

    // ==================== 内部状态 ====================
    protected InferenceSession? _session;
    protected string _inputName = string.Empty;
    protected string _outputName = string.Empty;
    protected string[] _tagNames = [];
    protected int[] _tagCategories = [];
    protected int _totalTagCount;

    private static readonly HttpClient _http = new();
    private readonly SemaphoreSlim _initLock = new(1, 1);
    protected readonly SemaphoreSlim _inferenceLock = new(1, 1);
    private string _modelDir = string.Empty;
    private DenseTensor<float>? _cachedTensor;
    private CancellationTokenSource? _idleCts;
    private readonly object _idleLock = new();

    // === 内存诊断采样计数器 ===
    private int _preprocessCount;
    private int _inferenceCount;
    private const int MemSampleInterval = 100;

    public event Action<AutoTagProgress>? ProgressChanged;
    public bool IsModelLoaded => _session != null && _tagNames.Length > 0;
    public string[] TagNames => _tagNames;
    public int[] TagCategories => _tagCategories;

    // ==================== 模型加载 ====================

    public async Task LoadModelAsync(string modelDir, CancellationToken ct = default)
    {
        await _initLock.WaitAsync();
        try
        {
            if (_session != null) return;
            _modelDir = modelDir;
            Directory.CreateDirectory(_modelDir);

            var onnxPath = Path.Combine(_modelDir, ModelFileName);
            var tagsPath = Path.Combine(_modelDir, TagsFileName);

            if (!File.Exists(onnxPath))
            {
                AppLogger.Info($"下载模型 {ModelRepo}/{ModelFileName} → {onnxPath}");
                await DownloadFileAsync($"{ModelRepo}/resolve/main/{ModelFileName}", onnxPath, $"{ModelSubDir} 模型");
            }
            if (!File.Exists(tagsPath))
            {
                var tagsRepoPath = ModelFileName.Contains('/')
                    ? $"{ModelRepo}/resolve/main/{TagsFileName}"
                    : $"{ModelRepo}/resolve/main/{TagsFileName}";
                AppLogger.Info($"下载标签 {ModelRepo}/{TagsFileName} → {tagsPath}");
                await DownloadFileAsync(tagsRepoPath, tagsPath, $"{ModelSubDir} 标签");
            }

            AppLogger.Info($"创建 InferenceSession (CUDA): {onnxPath}");
            _session = await Task.Run(() => CreateSession(onnxPath));
            _inputName = _session.InputNames[0];
            var inMeta = _session.InputMetadata[_inputName];
            var shape = $"[{string.Join(",", inMeta.Dimensions)}]";

            // Resolve output: prefer PreferredOutputName, then search for "output"/"prediction"/"logits"
            var allOuts = _session.OutputNames;
            if (PreferredOutputName != null && allOuts.Contains(PreferredOutputName))
                _outputName = PreferredOutputName;
            else if (allOuts.Contains("output"))
                _outputName = "output";
            else if (allOuts.Contains("prediction"))
                _outputName = "prediction";
            else if (allOuts.Contains("logits"))
                _outputName = "logits";
            else
                _outputName = allOuts[0];
            AppLogger.Info($"模型 {ModelSubDir} 输出选择: '{_outputName}' (可用: [{string.Join(", ", allOuts)}])");

            (_tagNames, _tagCategories) = await Task.Run(() => ParseTagsCsv(tagsPath));
            _totalTagCount = _tagNames.Length;

            int enabledCount = EnabledCategories == null
                ? _totalTagCount
                : _tagCategories.Count(c => EnabledCategories.Contains(c));

            AppLogger.Info($"模型 {ModelSubDir} 加载完成 input={_inputName} shape={shape} tags={_totalTagCount} enabled={enabledCount}");
            ProgressChanged?.Invoke(new AutoTagProgress(0, 0,
                $"[{ModelSubDir}] 已加载 输入:{_inputName} 形状:{shape} 标签:{_totalTagCount} 启用:{enabledCount}"));
        }
        finally { _initLock.Release(); }
    }

    // ==================== 推理 ====================

    public virtual async Task<List<TagPrediction>> PredictAsync(string imagePath, CancellationToken ct = default)
    {
        if (_session == null)
            throw new InvalidOperationException($"{ModelSubDir}: Model not loaded");

        await _inferenceLock.WaitAsync();
        try
        {
            var result = await Task.Run(() =>
            {
                var tensor = Preprocess(imagePath);
                if (tensor == null)
                {
                    return new List<TagPrediction>();
                }

                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor(_inputName, tensor)
                };

                // === 内存诊断：session.Run 前后（采样） ===
                int inferId = Interlocked.Increment(ref _inferenceCount);
                if (inferId % MemSampleInterval == 0)
                    AppLogger.Memory($"Inference.Run #{inferId} {Path.GetFileName(imagePath)}");

                using var results = _session.Run(inputs);
                var output = results.First(r => r.Name == _outputName);
                var probs = output.AsTensor<float>().ToArray();

                return Postprocess(probs, DefaultThreshold);
            });

            ResetIdleTimer();
            return result;
        }
        finally
        {
            _inferenceLock.Release();
        }
    }

    protected void ResetIdleTimer()
    {
        lock (_idleLock)
        {
            _idleCts?.Cancel();
            _idleCts?.Dispose();
            _idleCts = new CancellationTokenSource();
            var ct = _idleCts.Token;
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(TimeSpan.FromMinutes(1), ct); }
                catch { return; }
                if (!ct.IsCancellationRequested)
                    DisposeSession();
            });
        }
    }

    private void DisposeSession()
    {
        lock (_idleLock)
        {
            if (_session == null) return;
            AppLogger.Info($"[{ModelSubDir}] 1 分钟未使用，释放 GPU 显存");
            _session.Dispose();
            _session = null;
            _cachedTensor = null;
        }
    }

    private InferenceSession CreateSession(string onnxPath)
    {
        InferenceSession? session = null;
        try
        {
            var opts = new SessionOptions();
            opts.AppendExecutionProvider_CUDA(0);
            opts.EnableMemoryPattern = true;
            session = new InferenceSession(onnxPath, opts);
            AppLogger.Info($"[{ModelSubDir}] CUDA GPU 加速已启用");
            return session;
        }
        catch (Exception ex)
        {
            session?.Dispose();
            AppLogger.Warn($"[{ModelSubDir}] CUDA 不可用，回退 CPU: {ex.Message}");
            return new InferenceSession(onnxPath);
        }
    }

    // ==================== 预处理（NCHW + RGB + 归一化） ====================

    // ==================== 预处理（NCHW + RGB + 归一化） ====================

    protected virtual DenseTensor<float>? Preprocess(string imagePath)
    {
        int callId = Interlocked.Increment(ref _preprocessCount);
        bool sample = callId % MemSampleInterval == 0;
        if (sample) AppLogger.Memory($"Preprocess.Enter #{callId} {Path.GetFileName(imagePath)}");

        try
        {
            using var original = ThumbnailGenerator.DecodeForAnalysis(imagePath, 2048);
            if (original == null) return null;

            // 转换为 RGB 的 SKBitmap
            using var rgb = ConvertToRgbBitmap(original);

            // 填充到正方形
            using var squared = PreserveAspectRatio
                ? ResizeKeepAspect(rgb, InputSize)
                : WhitePadToSquare(rgb);

            // 缩放到目标尺寸
            using var resized = squared.Resize(new SKSizeI(InputSize, InputSize),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            if (resized == null) return null;

            // NCHW tensor: [1, 3, InputSize, InputSize] — reused across inferences
            if (_cachedTensor == null)
                _cachedTensor = new DenseTensor<float>(new[] { 1, 3, InputSize, InputSize });
            var tensor = _cachedTensor;

            unsafe
            {
                byte* ptr = (byte*)resized.GetPixels().ToPointer();
                int stride = resized.RowBytes;
                int bpp = resized.BytesPerPixel;

                for (int y = 0; y < InputSize; y++)
                {
                    for (int x = 0; x < InputSize; x++)
                    {
                        int offset = y * stride + x * bpp;
                        float r, g, b;
                        if (resized.ColorType == SKColorType.Bgra8888)
                        {
                            b = ptr[offset];
                            g = ptr[offset + 1];
                            r = ptr[offset + 2];
                        }
                        else
                        {
                            r = ptr[offset];
                            g = ptr[offset + 1];
                            b = ptr[offset + 2];
                        }

                        // 归一化: (pixel/255 - mean) / std
                        tensor[0, 0, y, x] = (r / 255f - Mean[0]) / Std[0];  // R
                        tensor[0, 1, y, x] = (g / 255f - Mean[1]) / Std[1];  // G
                        tensor[0, 2, y, x] = (b / 255f - Mean[2]) / Std[2];  // B
                    }
                }
            }

            if (sample) AppLogger.Memory($"Preprocess.Exit #{callId}");
            return tensor;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Preprocess exception: {ex.Message}");
            return null;
        }
    }

    // ==================== 后处理 ====================

    protected virtual List<TagPrediction> Postprocess(float[] probs, double threshold)
    {
        var results = new List<TagPrediction>();
        int catSkipped = 0, thresSkipped = 0;
        for (int i = 0; i < probs.Length && i < _tagNames.Length; i++)
        {
            if (EnabledCategories != null && !EnabledCategories.Contains(_tagCategories[i]))
            {
                catSkipped++;
                continue;
            }

            float prob = probs[i];
            if (NeedsSigmoid)
                prob = 1.0f / (1.0f + MathF.Exp(-prob));  // logit → probability

            if (prob >= threshold)
                results.Add(new TagPrediction(_tagNames[i], prob));
            else
                thresSkipped++;
        }
        results.Sort((a, b) => b.Confidence.CompareTo(a.Confidence));

        if (MaxResults > 0 && results.Count > MaxResults)
            results = results.Take(MaxResults).ToList();

        if (results.Count == 0)
        {
        }
        return results;
    }

    // ==================== 标签 CSV 解析 ====================

    protected virtual (string[] names, int[] categories) ParseTagsCsv(string csvPath)
    {
        try
        {
            var names = new List<string>();
            var categories = new List<int>();
            int tagIdIdx = CsvTagIdIndex;
            int catIdx = CsvCategoryIndex;
            int nameIdx = tagIdIdx + 1;  // name 紧跟在 tag_id 后面

            using var reader = new StreamReader(csvPath, System.Text.Encoding.GetEncoding(936));
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var parts = line.Split(',');
                if (parts.Length <= Math.Max(tagIdIdx, catIdx)) continue;
                if (!int.TryParse(parts[tagIdIdx], out _)) continue;  // header or invalid

                var name = parts[nameIdx].Trim('"');

                if (!int.TryParse(parts[catIdx], out var cat))
                    cat = -1;

                names.Add(name);
                categories.Add(cat);
            }

            var distinctCats = categories.Distinct().OrderBy(c => c).ToArray();
            AppLogger.Info($"解析标签 CSV: {csvPath} tagIdIdx={tagIdIdx} catIdx={catIdx} tags={names.Count} categories=[{string.Join(",", distinctCats)}]");
            return (names.ToArray(), categories.ToArray());
        }
        catch (Exception ex)
        {
            AppLogger.Error($"解析标签 CSV 失败: {csvPath} err={ex.Message} stack={ex.StackTrace}");
            throw;
        }
    }

    // ==================== 图像预处理辅助 ====================

    private static SKBitmap ConvertToRgbBitmap(SKBitmap original)
    {
        var rgb = new SKBitmap(original.Width, original.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(rgb);
        canvas.DrawBitmap(original, 0, 0);
        return rgb;
    }

    private static SKBitmap WhitePadToSquare(SKBitmap src)
    {
        int w = src.Width, h = src.Height;
        int maxDim = Math.Max(w, h);
        var padded = new SKBitmap(maxDim, maxDim, src.ColorType, SKAlphaType.Opaque);
        padded.Erase(SKColors.White);
        using var canvas = new SKCanvas(padded);
        int offX = (maxDim - w) / 2, offY = (maxDim - h) / 2;
        canvas.DrawBitmap(src, offX, offY);
        return padded;
    }

    private static SKBitmap ResizeKeepAspect(SKBitmap src, int targetSize)
    {
        int w = src.Width, h = src.Height;
        float scale = (float)targetSize / Math.Max(w, h);
        int newW = (int)(w * scale), newH = (int)(h * scale);

        using var resized = src.Resize(new SKSizeI(newW, newH),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        if (resized == null) return src;

        var padded = new SKBitmap(targetSize, targetSize, src.ColorType, SKAlphaType.Opaque);
        padded.Erase(new SKColor(0, 0, 0));  // 黑色填充（ImageNet 惯例）
        using var canvas = new SKCanvas(padded);
        int offX = (targetSize - newW) / 2, offY = (targetSize - newH) / 2;
        canvas.DrawBitmap(resized, offX, offY);
        return padded;
    }

    // ==================== 下载 ====================

    private async Task DownloadFileAsync(string repoFile, string destPath, string label)
    {
        var url = $"https://huggingface.co/{repoFile}";
        AppLogger.Info($"开始下载: {url}");
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            var totalBytes = response.Content.Headers.ContentLength ?? -1;

            using var stream = await response.Content.ReadAsStreamAsync();
            using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write);

            var buffer = new byte[8192];
            long totalRead = 0;
            int read;
            int reportStep = 0;

            while ((read = await stream.ReadAsync(buffer)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, read));
                totalRead += read;
                if (++reportStep % 100 == 0 && totalBytes > 0)
                {
                    ProgressChanged?.Invoke(new AutoTagProgress(
                        (int)(totalRead / 1024 / 1024), (int)(totalBytes / 1024 / 1024),
                        $"下载{label}... {totalRead / 1024 / 1024}/{totalBytes / 1024 / 1024} MB"));
                }
            }
            AppLogger.Info($"下载完成: {destPath} size={totalRead} bytes");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"下载失败: {url} err={ex.Message}");
            throw;
        }
    }

    // ==================== Dispose ====================

    public virtual void Dispose()
    {
        AppLogger.Info($"Dispose {ModelSubDir}");

        // 1. Cancel idle timer to prevent DisposeSession() from racing
        lock (_idleLock)
        {
            _idleCts?.Cancel();
            _idleCts?.Dispose();
            _idleCts = null;
        }

        // 2. Wait for any in-flight inference to finish before touching _session
        _inferenceLock.Wait();
        try
        {
            lock (_idleLock)
            {
                _session?.Dispose();
                _session = null;
                _cachedTensor = null;
            }
        }
        finally
        {
            _inferenceLock.Release();
        }

        _inferenceLock.Dispose();
        _initLock.Dispose();
    }
}
