using ImageManager.Common.Helpers;
using ImageManager.Core.Services;

namespace ImageManager.Infrastructure.Services;

/// <summary>
/// 标签结果合并引擎（模式 B 使用）。
/// 以中文名作为合并 key，处理多英文→一中文的去重。
/// camie (artist+copyright) 置信度天然偏低，采用保底槽位策略确保不被 pixai 高分标签挤出。
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
        var camieRaw = modelResults.GetValueOrDefault("camie") ?? new();
        var camieMerged = MergeOneModel(camieRaw, "camie", config.ModelThresholds.GetValueOrDefault("camie", 0.001));
        var pixaiMerged = MergeOneModel(modelResults.GetValueOrDefault("pixai") ?? new(),
            "pixai", config.ModelThresholds.GetValueOrDefault("pixai", 0.3));

        // camie 全部保留（数量少 1-3 个），pixai 填剩余槽位
        int camieTake = camieMerged.Count;
        int pixaiMax = Math.Max(0, config.MaxTags - camieTake);
        int pixaiTake = Math.Min(pixaiMax, pixaiMerged.Count);

        var result = new List<TagPrediction>(camieTake + pixaiTake);
        result.AddRange(camieMerged);  // camie 全部
        result.AddRange(pixaiMerged.Take(pixaiTake));  // pixai 高分部分

        // 按置信度排序（camie 低位在前也能被看到）
        result.Sort((a, b) => b.Confidence.CompareTo(a.Confidence));

        return result;
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
