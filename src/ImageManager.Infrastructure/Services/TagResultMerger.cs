using ImageManager.Common.Helpers;
using ImageManager.Core.Services;

namespace ImageManager.Infrastructure.Services;

/// <summary>
/// 标签结果合并引擎（模式 B 使用）。
/// 以中文名作为合并 key，处理多英文→一中文的去重。
/// </summary>
public class TagResultMerger
{
    private readonly ChineseTagLibrary _chineseLib;

    public TagResultMerger(ChineseTagLibrary chineseLib)
    {
        _chineseLib = chineseLib;
    }

    public List<TagPrediction> Merge(
        Dictionary<string, List<TagPrediction>> modelResults,
        MergeConfig config)
    {
        var pixaiMerged = MergeOneModel(modelResults.GetValueOrDefault("pixai") ?? new(),
            "pixai", config.TagThresholds.GetValueOrDefault("pixai", 0.3));

        return pixaiMerged
            .Take(config.MaxTags)
            .ToList();
    }

    private List<TagPrediction> MergeOneModel(
        List<TagPrediction> predictions, string modelName, double threshold)
    {
        var zhGroups = new Dictionary<string, ZhMergeGroup>(StringComparer.OrdinalIgnoreCase);

        foreach (var pred in predictions)
        {
            if (pred.Confidence < threshold) continue;

            var zh = _chineseLib.Lookup(pred.TagName) ?? pred.TagName;
            if (!zhGroups.TryGetValue(zh, out var group))
            {
                group = new ZhMergeGroup { ChineseName = zh };
                zhGroups[zh] = group;
            }

            if (pred.Confidence > group.MaxConfidence)
            {
                group.MaxConfidence = pred.Confidence;
                group.BestEnglishName = pred.TagName;
            }
            group.SourceModels.Add(modelName);
            group.EnglishAliases.Add(pred.TagName);
        }

        return zhGroups.Values
            .OrderByDescending(g => g.MaxConfidence)
            .Select(g => new TagPrediction(g.BestEnglishName, g.MaxConfidence)
            {
                ChineseName = g.ChineseName,
                EnglishAliases = g.EnglishAliases.ToList(),
                SourceModels = g.SourceModels.ToList()
            })
            .ToList();
    }

    private class ZhMergeGroup
    {
        public string ChineseName { get; set; } = "";
        public string BestEnglishName { get; set; } = "";
        public double MaxConfidence { get; set; }
        public List<string> EnglishAliases { get; } = new();
        public List<string> SourceModels { get; } = new();
    }
}
