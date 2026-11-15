using SkiaSharp;
using ImageManager.Infrastructure.Imaging;
using ImageManager.Core.Services;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ImageManager.Infrastructure.Services;

/// <summary>
/// PixAI Tagger v0.9 ONNX — 视觉总监。
/// 模式 A（SingleModel）：EnabledCategories=null，全部类别输出。
/// 模式 B（Ensemble）：EnabledCategories=[0,4]，仅 general + character。
/// </summary>
public class PixaiTagService : OnnxTagServiceBase, IImageEmbeddingExtractor
{
    private int _searchSessions;
    protected override TimeSpan ModelIdleTimeout => Volatile.Read(ref _searchSessions) > 0
        ? TimeSpan.FromMinutes(3) : base.ModelIdleTimeout;

    internal IDisposable BeginSearchSession()
    {
        Interlocked.Increment(ref _searchSessions);
        return new SearchSession(this);
    }

    private sealed class SearchSession(PixaiTagService owner) : IDisposable
    {
        private PixaiTagService? _owner = owner;
        public void Dispose()
        {
            var released = Interlocked.Exchange(ref _owner, null);
            if (released is not null) Interlocked.Decrement(ref released._searchSessions);
        }
    }

    protected override string ModelRepo => "deepghs/pixai-tagger-v0.9-onnx";
    protected override string ModelFileName => "model.onnx";
    protected override string TagsFileName => "selected_tags.csv";
    protected override string ModelSubDir => "pixai";
    protected override int InputSize => 448;
    protected override float[] Mean => [0.5f, 0.5f, 0.5f];
    protected override float[] Std => [0.5f, 0.5f, 0.5f];
    protected override bool PreserveAspectRatio => false;  // white pad + stretch
    protected override int CsvTagIdIndex => 1;   // pixai CSV: id,tag_id,name,category,...
    protected override int CsvCategoryIndex => 3;
    protected override bool NeedsSigmoid => false; // "prediction" 输出已含 sigmoid

    private HashSet<int>? _enabledCategories = [0, 4];  // 默认模式 B
    protected override HashSet<int>? EnabledCategories => _enabledCategories;
    protected override double DefaultThreshold => 0.15;

    /// <summary>切换全类别模式（模式 A）</summary>
    public void SetAllCategoriesMode()
    {
        _enabledCategories = null;
        ImageManager.Common.Helpers.AppLogger.Info("PixaiTagService 切换为全类别模式");
    }

    /// <summary>切换过滤模式（模式 B，仅 0+4）</summary>
    public void SetEnsembleMode()
    {
        _enabledCategories = [0, 4];
        ImageManager.Common.Helpers.AppLogger.Info("PixaiTagService 切换为 Ensemble 模式 (cat 0+4)");
    }

    /// <summary>提取图像 1024维 embedding（用于画师识别）</summary>
    public async Task<float[]?> GetEmbeddingAsync(string imagePath)
    {
        if (_session == null) return null;

        await _inferenceLock.WaitAsync();
        try
        {
            var result = await Task.Run(() =>
            {
                var tensor = Preprocess(imagePath);
                if (tensor == null) return null;

                var inputs = new List<Microsoft.ML.OnnxRuntime.NamedOnnxValue>
                {
                    Microsoft.ML.OnnxRuntime.NamedOnnxValue.CreateFromTensor(_inputName, tensor)
                };

                using var results = _session.Run(inputs);
                var embOut = results.FirstOrDefault(r => r.Name == "embedding");
                if (embOut == null) return null;
                return embOut.AsTensor<float>().ToArray();
            });

            ResetIdleTimer();
            return result;
        }
        finally { _inferenceLock.Release(); }
    }

    internal async Task<float[]> EncodeSearchRegionAsync(
        SKBitmap bitmap, string modelDirectory, CancellationToken ct)
    {
        await _inferenceLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!IsModelLoaded)
                await LoadModelAsync(modelDirectory, ct).ConfigureAwait(false);

            var session = _session ?? throw new InvalidOperationException("PixAI 模型未加载。");
            var vector = await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var tensor = Preprocess(bitmap)
                    ?? throw new InvalidDataException("无法预处理 PixAI 查询选区。");
                using var options = new RunOptions();
                using var cancellation = ct.UnsafeRegister(static state =>
                    ((RunOptions)state!).Terminate = true, options);
                ct.ThrowIfCancellationRequested();
                try
                {
                    var input = NamedOnnxValue.CreateFromTensor(_inputName, tensor);
                    using var output = session.Run([input], ["embedding"], options);
                    ct.ThrowIfCancellationRequested();
                    return output.Single().AsTensor<float>().ToArray();
                }
                catch (OnnxRuntimeException) when (ct.IsCancellationRequested)
                {
                    throw new OperationCanceledException(ct);
                }
            }, ct).ConfigureAwait(false);
            ResetIdleTimer();
            return vector;
        }
        finally { _inferenceLock.Release(); }
    }

    /// <summary>合并推理：一次 session.Run 同时获取 prediction + embedding，省一次 Preprocess+Run</summary>
    public async Task<(List<TagPrediction> tags, float[]? embedding)> PredictWithEmbeddingAsync(string imagePath,
        CancellationToken ct = default)
    {
        using var original = InferenceImageLoader.DecodeForInference(imagePath, InputSize * 2);
        if (original == null)
            return (new List<TagPrediction>(), null);
        return await PredictWithEmbeddingAsync(original, ct);
    }

    public async Task<(List<TagPrediction> tags, float[]? embedding)> PredictWithEmbeddingAsync(SKBitmap original,
        CancellationToken ct = default)
    {
        if (_session == null)
            throw new InvalidOperationException("Pixai: Model not loaded");

        await _inferenceLock.WaitAsync(ct);
        try
        {
            var result = await Task.Run(() =>
            {
                var tensor = Preprocess(original);
                if (tensor == null)
                    return (new List<TagPrediction>(), null);

                var inputs = new List<Microsoft.ML.OnnxRuntime.NamedOnnxValue>
                {
                    Microsoft.ML.OnnxRuntime.NamedOnnxValue.CreateFromTensor(_inputName, tensor)
                };

                using var results = _session.Run(inputs);

                var predOut = results.FirstOrDefault(r => r.Name == _outputName);
                var probs = predOut?.AsTensor<float>().ToArray() ?? Array.Empty<float>();

                var embOut = results.FirstOrDefault(r => r.Name == "embedding");
                var embedding = embOut?.AsTensor<float>().ToArray();

                var tags = Postprocess(probs, DefaultThreshold);
                return (tags, embedding);
            });

            ResetIdleTimer();
            return result;
        }
        finally { _inferenceLock.Release(); }
    }

    private const int MaxBatchSize = 16;

    /// <summary>批量提取嵌入：一次 session.Run 处理多张图，返回 [N, 1024] 的嵌入列表</summary>
    public async Task<List<float[]>> GetEmbeddingsBatchAsync(List<string> imagePaths)
    {
        if (_session == null || imagePaths.Count == 0) return [];

        var paths = imagePaths.Count > MaxBatchSize
            ? imagePaths.Take(MaxBatchSize).ToList()
            : imagePaths;

        await _inferenceLock.WaitAsync();
        try
        {
            var result = await Task.Run(() =>
            {
                // 逐张预处理。Preprocess reuses a cached tensor, so copy each
                // image immediately before the next preprocess overwrites it.
                var tensorBuffers = new List<float[]>();
                foreach (var path in paths)
                {
                    var t = Preprocess(path);
                    if (t != null) tensorBuffers.Add(t.ToArray());
                }
                if (tensorBuffers.Count == 0) return null;

                // 堆叠为 [N, 3, 448, 448]
                int n = tensorBuffers.Count;
                var batch = new DenseTensor<float>(new[] { n, 3, InputSize, InputSize });

                // 估算批处理 Tensor 的内存大小并通知 GC
                long batchMemoryBytes = (long)n * 3 * InputSize * InputSize * sizeof(float);
                GC.AddMemoryPressure(batchMemoryBytes);

                try
                {
                    for (int b = 0; b < n; b++)
                    {
                        var src = tensorBuffers[b];
                        for (int c = 0; c < 3; c++)
                            for (int y = 0; y < InputSize; y++)
                                for (int x = 0; x < InputSize; x++)
                                    batch[b, c, y, x] = src[(c * InputSize + y) * InputSize + x];
                    }

                    var inputs = new List<Microsoft.ML.OnnxRuntime.NamedOnnxValue>
                    {
                        Microsoft.ML.OnnxRuntime.NamedOnnxValue.CreateFromTensor(_inputName, batch)
                    };

                    using var results = _session.Run(inputs);
                    var embOut = results.FirstOrDefault(r => r.Name == "embedding");
                    if (embOut == null) return null;

                    var embTensor = embOut.AsTensor<float>();
                    int embDim = embTensor.Dimensions[1]; // 1024
                    var embeddings = new List<float[]>(n);
                    for (int b = 0; b < n; b++)
                    {
                        var emb = new float[embDim];
                        for (int d = 0; d < embDim; d++)
                            emb[d] = embTensor[b, d];
                        embeddings.Add(emb);
                    }
                    return embeddings;
                }
                finally
                {
                    // 移除内存压力通知
                    GC.RemoveMemoryPressure(batchMemoryBytes);
                }
            });

            ResetIdleTimer();
            return result ?? [];
        }
        finally { _inferenceLock.Release(); }
    }
}
