using ImageManager.Common.Helpers;
using ImageManager.Core.Services;

namespace ImageManager.Infrastructure.Services;

/// <summary>
/// WD Tagger Rating 提取器。
/// 复用现有 OnnxTagService 的推理能力，但只读取前 4 个输出概率值，
/// 通过 Max 函数确定 SystemRating 分级。
/// Rating 中文名硬编码，不查 CSV。
/// </summary>
public class WdRatingService : IDisposable
{
    private readonly OnnxTagService _wdService;
    private static readonly string[] RatingNames = ["general", "sensitive", "questionable", "explicit"];

    public bool IsLoaded => _wdService.IsModelLoaded;

    public WdRatingService(OnnxTagService wdService)
    {
        _wdService = wdService;
    }

    public void Dispose()
    {
        // Do NOT dispose _wdService — it is a DI singleton shared with IAutoTagService.
        // Its lifecycle is managed by the DI container.
    }

    public async Task LoadAsync(string modelPath, CancellationToken ct = default)
    {
        AppLogger.Info("加载 WD Rating 服务...");
        await _wdService.LoadModelAsync(modelPath);
        AppLogger.Info("WD Rating 服务加载完成");
    }

    public async Task<SystemRating> PredictRatingAsync(string imagePath, CancellationToken ct = default)
    {
        if (!_wdService.IsModelLoaded)
        {
            AppLogger.Warn("WD 模型未加载，返回 Unknown");
            return SystemRating.Unknown;
        }

        try
        {
            var allPredictions = await _wdService.PredictAsync(imagePath);

            float best = 0;
            int bestIdx = 0;
            for (int i = 0; i < 4; i++)
            {
                var pred = allPredictions.FirstOrDefault(p =>
                    string.Equals(p.TagName, RatingNames[i], StringComparison.OrdinalIgnoreCase));
                var prob = (float)pred.Confidence;
                if (prob > best) { best = prob; bestIdx = i; }
            }

            var rating = (SystemRating)bestIdx;
            return rating;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Rating 预测失败: {ex.Message}");
            return SystemRating.Unknown;
        }
    }
}
