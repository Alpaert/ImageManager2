using ImageManager.Core.Services;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ImageManager.Infrastructure.Services;

/// <summary>
/// PixAI Tagger v0.9 ONNX — 视觉总监。
/// 模式 A（SingleModel）：EnabledCategories=null，全部类别输出。
/// 模式 B（Ensemble）：EnabledCategories=[0,4]，仅 general + character。
/// </summary>
public class PixaiTagService : OnnxTagServiceBase
{
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

        return await Task.Run(() =>
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
    }

    /// <summary>合并推理：一次 session.Run 同时获取 prediction + embedding，省一次 Preprocess+Run</summary>
    public async Task<(List<TagPrediction> tags, float[]? embedding)> PredictWithEmbeddingAsync(string imagePath)
    {
        if (_session == null)
            throw new InvalidOperationException("Pixai: Model not loaded");

        return await Task.Run(() =>
        {
            var tensor = Preprocess(imagePath);
            if (tensor == null)
                return (new List<TagPrediction>(), null);

            var inputs = new List<Microsoft.ML.OnnxRuntime.NamedOnnxValue>
            {
                Microsoft.ML.OnnxRuntime.NamedOnnxValue.CreateFromTensor(_inputName, tensor)
            };

            using var results = _session.Run(inputs);

            // 取 prediction
            var predOut = results.FirstOrDefault(r => r.Name == _outputName);
            var probs = predOut?.AsTensor<float>().ToArray() ?? Array.Empty<float>();

            // 取 embedding
            var embOut = results.FirstOrDefault(r => r.Name == "embedding");
            var embedding = embOut?.AsTensor<float>().ToArray();

            var tags = Postprocess(probs, DefaultThreshold);
            return (tags, embedding);
        });
    }

    /// <summary>批量提取嵌入：一次 session.Run 处理多张图，返回 [N, 1024] 的嵌入列表</summary>
    public async Task<List<float[]>?> GetEmbeddingsBatchAsync(List<string> imagePaths)
    {
        if (_session == null || imagePaths.Count == 0) return null;

        return await Task.Run(() =>
        {
            // 逐张预处理
            var tensors = new List<DenseTensor<float>>();
            foreach (var path in imagePaths)
            {
                var t = Preprocess(path);
                if (t != null) tensors.Add(t);
            }
            if (tensors.Count == 0) return null;

            // 堆叠为 [N, 3, 448, 448]
            int n = tensors.Count;
            var batch = new DenseTensor<float>(new[] { n, 3, InputSize, InputSize });
            for (int b = 0; b < n; b++)
            {
                var src = tensors[b];
                for (int c = 0; c < 3; c++)
                    for (int y = 0; y < InputSize; y++)
                        for (int x = 0; x < InputSize; x++)
                            batch[b, c, y, x] = src[0, c, y, x];
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
        });
    }
}
