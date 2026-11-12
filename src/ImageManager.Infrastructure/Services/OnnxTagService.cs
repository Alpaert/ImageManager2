using System.Diagnostics;
using ImageManager.Common.Helpers;
using ImageManager.Core.Services;
using ImageManager.Infrastructure.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace ImageManager.Infrastructure.Services;

public class OnnxTagService : IAutoTagService, IDisposable
{
    private const string ModelRepo = "SmilingWolf/wd-swinv2-tagger-v3";
    private const string ModelFile = "model.onnx";
    private const string TagsFile = "selected_tags.csv";
    private const int InputSize = 448;

    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan IdleCheckInterval = TimeSpan.FromSeconds(15);
    private static readonly string[] ExpectedRatingTags = ["general", "sensitive", "questionable", "explicit"];

    private InferenceSession? _session;
    private string _inputName = string.Empty;
    private string _outputName = string.Empty;
    private string _modelShapeInfo = string.Empty;
    private string[] _tagNames = Array.Empty<string>();
    private bool _canUseRatingFastPath;
    private int _predictCount;
    private static readonly HttpClient _http = new();
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _inferenceLock = new(1, 1);
    private string _modelDir = string.Empty;
    private DenseTensor<float>? _cachedTensor;
    private SKBitmap? _scratchBitmap;
    private CancellationTokenSource? _idleTimerCts;
    private readonly object _idleLock = new();
    private long _lastUsedTimestamp = Stopwatch.GetTimestamp();
    private bool _disposed;

    internal bool CanUseRatingFastPath => _canUseRatingFastPath;

    // === 鍐呭瓨璇婃柇閲囨牱璁℃暟鍣?===
    private int _preprocessCount;
    private const int MemSampleInterval = 100;

    public event Action<AutoTagProgress>? ProgressChanged;
    public bool IsModelLoaded => _session != null;

    public async Task LoadModelAsync(string modelPath, CancellationToken ct = default)
    {
        await _initLock.WaitAsync(ct);
        try
        {
            StopIdleTimer();

            if (_session != null)
            {
                Volatile.Write(ref _lastUsedTimestamp, Stopwatch.GetTimestamp());
                EnsureIdleTimerStarted();
                return;
            }

            _modelDir = modelPath;
            Directory.CreateDirectory(_modelDir);

            var onnxPath = Path.Combine(_modelDir, ModelFile);
            var tagsPath = Path.Combine(_modelDir, TagsFile);

            if (!File.Exists(onnxPath))
                await DownloadFileAsync($"{ModelRepo}/resolve/main/{ModelFile}", onnxPath, "\u6a21\u578b");
            if (!File.Exists(tagsPath))
                await DownloadFileAsync($"{ModelRepo}/resolve/main/{TagsFile}", tagsPath, "\u6807\u7b7e");

            _session = await Task.Run(() =>
            {
                return OnnxSessionFactory.Create(onnxPath, "wd");
            });
            _inputName = _session.InputNames[0];
            _outputName = _session.OutputNames[0];
            var inMeta = _session.InputMetadata[_inputName];
            _modelShapeInfo = $"[{string.Join(",", inMeta.Dimensions)}]";
            _tagNames = await Task.Run(() => ParseTagsCsv(tagsPath));

            if (_tagNames.Length >= 4 &&
                string.Equals(_tagNames[0], ExpectedRatingTags[0], StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_tagNames[1], ExpectedRatingTags[1], StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_tagNames[2], ExpectedRatingTags[2], StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_tagNames[3], ExpectedRatingTags[3], StringComparison.OrdinalIgnoreCase))
            {
                _canUseRatingFastPath = true;
            }
            else
            {
                _canUseRatingFastPath = false;
                AppLogger.Warn($"[wd] WD 模型前 4 个标签不符合预期评级格式 ({string.Join(", ", _tagNames.Take(4))})，禁用 Rating 快速路径");
            }

            Volatile.Write(ref _lastUsedTimestamp, Stopwatch.GetTimestamp());
            EnsureIdleTimerStarted();

            ProgressChanged?.Invoke(new AutoTagProgress(0, 0,
                $"\u6a21\u578b\u5df2\u52a0\u8f7d \u8f93\u5165:{_inputName} \u5f62\u72b6:{_modelShapeInfo} \u6807\u7b7e:{_tagNames.Length}\u4e2a"));
        }
        finally { _initLock.Release(); }
    }

    public async Task<List<TagPrediction>> PredictAsync(string imagePath, CancellationToken ct = default)
    {
        using var original = InferenceImageLoader.DecodeForInference(imagePath, InputSize * 2);
        if (original == null)
        {
            if (Interlocked.Increment(ref _predictCount) == 1)
                ProgressChanged?.Invoke(new AutoTagProgress(0, 0, "预处理失败: 图片无法解码"));
            return new List<TagPrediction>();
        }
        return await PredictAsync(original, ct);
    }

    public async Task<List<TagPrediction>> PredictAsync(SKBitmap original, CancellationToken ct = default)
    {
        await _inferenceLock.WaitAsync(ct);
        try
        {
            var session = _session;
            if (session == null)
                throw new InvalidOperationException("Model not loaded");

            var result = await Task.Run(() =>
            {
                var tensor = Preprocess(original);
                if (tensor == null)
                {
                    if (Interlocked.Increment(ref _predictCount) == 1)
                        ProgressChanged?.Invoke(new AutoTagProgress(0, 0, "\u9884\u5904\u7406\u5931\u8d25: \u56fe\u7247\u65e0\u6cd5\u89e3\u7801"));
                    return new List<TagPrediction>();
                }

                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor(_inputName, tensor)
                };

                using var results = session.Run(inputs);
                var output = results[0].AsTensor<float>();
                var probs = output.ToArray(); // model already has sigmoid built-in

                int aboveThreshold = 0;
                float maxProb = 0;
                var predictions = new List<TagPrediction>(probs.Length);
                for (int i = 0; i < probs.Length && i < _tagNames.Length; i++)
                {
                    if (probs[i] > maxProb) maxProb = probs[i];
                    if (probs[i] >= 0.01f)
                    {
                        predictions.Add(new TagPrediction(_tagNames[i], probs[i]));
                        aboveThreshold++;
                    }
                }

                // Report diagnostic for first image
                if (Interlocked.Increment(ref _predictCount) == 1)
                {
                    var sample = predictions.Take(3).Select(p => $"{p.TagName}({p.Confidence:F2})");
                    ProgressChanged?.Invoke(new AutoTagProgress(0, 0,
                        $"\u9996\u56fe\u63a8\u7406: maxProb={maxProb:F4} aboveThreshold={aboveThreshold} " +
                        $"top3=[{string.Join(", ", sample)}] shape={_modelShapeInfo}"));
                }

                predictions.Sort((a, b) => b.Confidence.CompareTo(a.Confidence));
                return predictions;
            });

            ResetIdleTimer();
            return result;
        }
        finally { _inferenceLock.Release(); }
    }

    public async Task<SystemRating> PredictRatingOnlyAsync(string imagePath, CancellationToken ct = default)
    {
        using var original = InferenceImageLoader.DecodeForInference(imagePath, InputSize * 2);
        if (original == null)
        {
            return SystemRating.General;
        }
        return await PredictRatingOnlyAsync(original, ct);
    }

    public async Task<SystemRating> PredictRatingOnlyAsync(SKBitmap original, CancellationToken ct = default)
    {
        await _inferenceLock.WaitAsync(ct);
        try
        {
            var session = _session;
            if (session == null)
                throw new InvalidOperationException("Model not loaded");

            var rating = await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var tensor = Preprocess(original);
                if (tensor == null)
                {
                    return SystemRating.General;
                }

                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor(_inputName, tensor)
                };

                using var results = session.Run(inputs);
                var output = results[0].AsTensor<float>();

                if (_canUseRatingFastPath)
                {
                    ReadOnlySpan<float> span = output is DenseTensor<float> dense
                        ? dense.Buffer.Span
                        : (ReadOnlySpan<float>)output.ToArray();

                    float best = 0f;
                    int bestIdx = 0;
                    for (int i = 0; i < 4 && i < span.Length; i++)
                    {
                        float prob = span[i];
                        if (prob > best)
                        {
                            best = prob;
                            bestIdx = i;
                        }
                    }

                    if (best <= 0f)
                    {
                        return SystemRating.General;
                    }

                    return (SystemRating)bestIdx;
                }
                else
                {
                    var probs = output.ToArray();
                    float best = 0f;
                    int bestIdx = 0;
                    for (int i = 0; i < ExpectedRatingTags.Length; i++)
                    {
                        string targetTag = ExpectedRatingTags[i];
                        for (int j = 0; j < probs.Length && j < _tagNames.Length; j++)
                        {
                            if (string.Equals(_tagNames[j], targetTag, StringComparison.OrdinalIgnoreCase))
                            {
                                float prob = probs[j];
                                if (prob > best)
                                {
                                    best = prob;
                                    bestIdx = i;
                                }
                                break;
                            }
                        }
                    }

                    if (best <= 0f)
                    {
                        return SystemRating.General;
                    }

                    return (SystemRating)bestIdx;
                }
            }, ct);

            ResetIdleTimer();
            return rating;
        }
        finally
        {
            _inferenceLock.Release();
        }
    }

    private void ResetIdleTimer()
    {
        Volatile.Write(ref _lastUsedTimestamp, Stopwatch.GetTimestamp());
        EnsureIdleTimerStarted();
    }

    private void EnsureIdleTimerStarted()
    {
        var cts = Volatile.Read(ref _idleTimerCts);
        if (cts != null && !cts.IsCancellationRequested)
            return;

        lock (_idleLock)
        {
            if (_disposed || _session == null) return;
            if (_idleTimerCts == null || _idleTimerCts.IsCancellationRequested)
            {
                _idleTimerCts?.Dispose();
                _idleTimerCts = new CancellationTokenSource();
                var ct = _idleTimerCts.Token;
                _ = Task.Run(() => IdleCheckLoopAsync(ct), ct);
            }
        }
    }

    private void StopIdleTimer()
    {
        lock (_idleLock)
        {
            if (_idleTimerCts != null)
            {
                try
                {
                    _idleTimerCts.Cancel();
                    _idleTimerCts.Dispose();
                }
                catch { }
                _idleTimerCts = null;
            }
        }
    }

    private async Task IdleCheckLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(IdleCheckInterval);
        try
        {
            while (!ct.IsCancellationRequested && await timer.WaitForNextTickAsync(ct))
            {
                if (_session == null)
                    break;

                long lastUsed = Volatile.Read(ref _lastUsedTimestamp);
                if (Stopwatch.GetElapsedTime(lastUsed) >= IdleTimeout)
                {
                    DisposeSession();
                    if (_session == null)
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[wd] 空闲检查循环异常: {ex.Message}");
        }
    }

    public void ReleaseSession()
    {
        StopIdleTimer();
        DisposeSession(force: true);
    }

    private void DisposeSession(bool force = false)
    {
        _inferenceLock.Wait();
        try
        {
            lock (_idleLock)
            {
                if (_disposed || _session == null) return;
                if (!force && Stopwatch.GetElapsedTime(Volatile.Read(ref _lastUsedTimestamp)) < IdleTimeout)
                {
                    return;
                }

                AppLogger.Info("[wd] 1 分钟未使用，释放 GPU 显存");
                _session.Dispose();
                _session = null;
                _cachedTensor = null;
                _scratchBitmap?.Dispose();
                _scratchBitmap = null;
                GC.Collect(2, GCCollectionMode.Optimized, false);
                AppLogger.Memory("[wd] AfterIdleDispose");
            }
        }
        finally { _inferenceLock.Release(); }
    }

    // Preprocessing: square white-pad 鈫?448 resize 鈫?float32 0-255 鈫?BGR 鈫?NHWC [1,448,448,3]
    public DenseTensor<float>? Preprocess(string imagePath)
    {
        using var original = InferenceImageLoader.DecodeForInference(imagePath, InputSize * 2);
        if (original == null) return null;
        return Preprocess(original);
    }

    public DenseTensor<float>? Preprocess(SKBitmap original)
    {
        int callId = Interlocked.Increment(ref _preprocessCount);
        try
        {
            var resized = DrawToModelBitmap(original);

            // NHWC tensor: [1, 448, 448, 3], float32, 0-255 range, BGR order
            // Cached across inferences 鈥?same pattern as OnnxTagServiceBase._cachedTensor
            if (_cachedTensor == null)
                _cachedTensor = new DenseTensor<float>(new[] { 1, InputSize, InputSize, 3 });
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
                        float b, g, r;
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

                        tensor[0, y, x, 0] = b; // B
                        tensor[0, y, x, 1] = g; // G
                        tensor[0, y, x, 2] = r; // R
                    }
                }
            }

            return tensor;
        }
        catch
        {
            return null;
        }
    }

    private SKBitmap DrawToModelBitmap(SKBitmap src)
    {
        var target = GetScratchBitmap();
        target.Erase(SKColors.White);

        int maxDim = Math.Max(src.Width, src.Height);
        float scale = (float)InputSize / maxDim;
        float drawW = src.Width * scale;
        float drawH = src.Height * scale;
        var dest = new SKRect((InputSize - drawW) / 2f, (InputSize - drawH) / 2f,
            (InputSize + drawW) / 2f, (InputSize + drawH) / 2f);

        using var canvas = new SKCanvas(target);
        using var paint = new SKPaint { IsAntialias = false };
        canvas.DrawBitmap(src, dest, paint);
        return target;
    }

    private SKBitmap GetScratchBitmap()
    {
        if (_scratchBitmap == null ||
            _scratchBitmap.Width != InputSize ||
            _scratchBitmap.Height != InputSize ||
            _scratchBitmap.ColorType != SKColorType.Rgba8888)
        {
            _scratchBitmap?.Dispose();
            _scratchBitmap = new SKBitmap(InputSize, InputSize, SKColorType.Rgba8888, SKAlphaType.Opaque);
        }

        return _scratchBitmap;
    }

    private static string[] ParseTagsCsv(string csvPath)
    {
        var tags = new List<string>();
        foreach (var line in File.ReadLines(csvPath))
        {
            var parts = line.Split(',', 3);
            if (parts.Length >= 2 && int.TryParse(parts[0], out _))
                tags.Add(parts[1].Trim('"'));
        }
        return tags.ToArray();
    }

    private async Task DownloadFileAsync(string repoFile, string destPath, string label)
    {
        var url = $"https://huggingface.co/{repoFile}";
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
                    $"\u4e0b\u8f7d{label}... {totalRead / 1024 / 1024}/{totalBytes / 1024 / 1024} MB"));
            }
        }
    }

    public void Dispose()
    {
        lock (_idleLock)
        {
            if (_disposed) return;
            _disposed = true;

            _idleTimerCts?.Cancel();
            _idleTimerCts?.Dispose();
            _idleTimerCts = null;
        }

        // Wait for any in-flight inference to finish before disposing _session
        _inferenceLock.Wait();
        try
        {
            lock (_idleLock)
            {
                _session?.Dispose();
                _session = null;
                _cachedTensor = null;
                _scratchBitmap?.Dispose();
                _scratchBitmap = null;
            }
        }
        finally { _inferenceLock.Release(); }

        _inferenceLock.Dispose();
        _initLock.Dispose();
    }
}
