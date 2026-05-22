using ImageManager.Common.Helpers;
using ImageManager.Core.Services;

namespace ImageManager.Infrastructure.Services;

/// <summary>
/// 模式 A：单模型打标服务。
/// 使用 PixAI Tagger 全类别输出（EnabledCategories=null），
/// 直接查 cnname 得中文，无 Rating 分级，无 camie 参与。
/// </summary>
public class SingleModelTagService : IEnsembleTagService
{
    private readonly PixaiTagService _pixai;
    private readonly ChineseTagLibrary _chineseLib;
    private double _minConfidence = 0.15;
    private string _modelsRootDir = string.Empty;

    public TagMode Mode => TagMode.SingleModel;
    public bool IsModelLoaded => _pixai.IsModelLoaded;

    public event Action<AutoTagProgress>? ProgressChanged;

    public SingleModelTagService(PixaiTagService pixai, ChineseTagLibrary chineseLib)
    {
        _pixai = pixai;
        _chineseLib = chineseLib;
        _pixai.ProgressChanged += p => ProgressChanged?.Invoke(p);
    }

    public void Configure(double minConfidence)
    {
        _minConfidence = minConfidence;
        AppLogger.Info($"SingleModel 配置: minConfidence={minConfidence}");
    }

    public async Task LoadModelAsync(string modelsRootDir)
    {
        _modelsRootDir = modelsRootDir;
        AppLogger.Info("=== 模式 A: 单模型打标 加载开始 ===");

        _pixai.SetAllCategoriesMode();
        var pixaiDir = Path.Combine(modelsRootDir, "pixai");
        await _pixai.LoadModelAsync(pixaiDir);

        var csvPath = Path.Combine(pixaiDir, "selected_tags.csv");
        _chineseLib.LoadFromModelCsv("pixai", csvPath);

        AppLogger.Info($"=== 模式 A 加载完成 pixai={_pixai.IsModelLoaded} zhTags={_chineseLib.Count} ===");
    }

    public Task<SystemRating> PredictRatingAsync(string imagePath)
    {
        AppLogger.Tag("Rating", $"模式 A 不支持 Rating，返回 Unknown image={Path.GetFileName(imagePath)}");
        return Task.FromResult(SystemRating.Unknown);
    }

    public async Task<List<TagPrediction>> PredictAsync(string imagePath)
    {
        AppLogger.Tag("Predict", $"模式 A 单模型推理 image={Path.GetFileName(imagePath)}");
        var preds = await _pixai.PredictAsync(imagePath);

        var filtered = preds
            .Where(p => p.Confidence >= _minConfidence)
            .ToList();

        for (int i = 0; i < filtered.Count; i++)
        {
            filtered[i] = filtered[i] with { ChineseName = _chineseLib.Lookup(filtered[i].TagName) };
        }

        AppLogger.Tag("Predict", $"模式 A 结果: raw={preds.Count} filtered={filtered.Count} threshold={_minConfidence}");
        return filtered;
    }

    public async Task<EnsembleResult> PredictWithSourcesAsync(string imagePath)
    {
        var preds = await PredictAsync(imagePath);
        var sourceTags = new Dictionary<string, List<TagPrediction>>
        {
            ["pixai"] = preds
        };
        return new EnsembleResult(SystemRating.Unknown, preds, sourceTags);
    }

    public IReadOnlyList<ModelStatus> GetModelStatuses()
    {
        return new List<ModelStatus>
        {
            new("pixai", _pixai.IsModelLoaded, "v0.9", _pixai.TagNames.Length)
        };
    }

    public void Dispose()
    {
        AppLogger.Info("SingleModelTagService Disposed");
        _pixai.Dispose();
    }
}
