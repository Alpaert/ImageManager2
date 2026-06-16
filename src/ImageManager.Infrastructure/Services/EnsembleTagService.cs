using System.Collections.Concurrent;
using ImageManager.Common.Helpers;
using ImageManager.Core.Services;

namespace ImageManager.Infrastructure.Services;

/// <summary>
/// 模式 B：三模型专家流水线服务。
/// WD (裁判官) → Rating 分级
/// PixAI (视觉总监) → Category 0(general) + 4(character)
/// Camie (档案管理员) → Category 1(artist) + 3(copyright)
/// 三模型并行推理 → TagResultMerger 中文层去重合并。
/// </summary>
public class EnsembleTagService : IEnsembleTagService, IDisposable
{
    private readonly WdRatingService _wd;
    private readonly PixaiTagService _pixai;
    private readonly TagResultMerger _merger;
    private readonly ChineseTagLibrary _chineseLib;
    private readonly ArtistEmbeddingStore _artistStore;
    private MergeConfig _mergeConfig = new();
    private string _modelsRootDir = string.Empty;

    public TagMode Mode => TagMode.Ensemble;
    public bool IsModelLoaded => _wd.IsLoaded && _pixai.IsModelLoaded;

    // === 内存诊断采样计数器 ===
    private int _predictCount;
    private const int MemSampleInterval = 50;

    public event Action<AutoTagProgress>? ProgressChanged;

    public EnsembleTagService(
        WdRatingService wd,
        PixaiTagService pixai,
        TagResultMerger merger,
        ChineseTagLibrary chineseLib,
        ArtistEmbeddingStore artistStore)
    {
        _wd = wd;
        _pixai = pixai;
        _merger = merger;
        _chineseLib = chineseLib;
        _artistStore = artistStore;

        _pixai.ProgressChanged += p => ProgressChanged?.Invoke(p);
    }

    public void Configure(MergeConfig config)
    {
        _mergeConfig = config;
        AppLogger.Info($"Ensemble 配置: maxTags={config.MaxTags} thresholds={string.Join(",", config.ModelThresholds.Select(kv => $"{kv.Key}={kv.Value}"))}");
    }

    public async Task LoadModelAsync(string modelsRootDir, CancellationToken ct = default)
    {
        _modelsRootDir = modelsRootDir;
        AppLogger.Info("=== 模式 B: 三模型专家流水线 加载开始 ===");

        _pixai.SetEnsembleMode();

        // 双模型并行加载
        var wdTask = _wd.LoadAsync(Path.Combine(modelsRootDir, "wd14"));
        var pixaiTask = _pixai.LoadModelAsync(Path.Combine(modelsRootDir, "pixai"));

        await Task.WhenAll(wdTask, pixaiTask);

        // 加载中文标签库
        _chineseLib.LoadFromModelCsv("pixai", Path.Combine(modelsRootDir, "pixai", "selected_tags.csv"));

        // 加载画师嵌入库 + 中文名映射
        var artistDbPath = Path.Combine(modelsRootDir, "artist_embeddings.bin");
        _artistStore.Load(artistDbPath);
        _chineseLib.LoadArtistNames(Path.Combine(modelsRootDir, "artist_names.txt"));

        AppLogger.Info($"=== 模式 B 加载完成 wd={_wd.IsLoaded} pixai={_pixai.IsModelLoaded} zhTags={_chineseLib.Count} artists={_artistStore.Count} ===");
    }

    public async Task<SystemRating> PredictRatingAsync(string imagePath, CancellationToken ct = default)
        => await _wd.PredictRatingAsync(imagePath);

    public async Task<List<TagPrediction>> PredictAsync(string imagePath, CancellationToken ct = default)
    {
        var result = await PredictWithSourcesAsync(imagePath, ct);
        // artist 已在 PredictWithSourcesAsync 中插入 MergedTags 首位
        return result.MergedTags;
    }

    public async Task<EnsembleResult> PredictWithSourcesAsync(string imagePath, CancellationToken ct = default)
    {
        var fileName = Path.GetFileName(imagePath);
        int callId = Interlocked.Increment(ref _predictCount);
        bool sample = callId % MemSampleInterval == 0;

        if (sample) AppLogger.Memory($"Ensemble#{callId}.Start {fileName}");

        // 双模型并行推理（PixAI 合并 prediction+embedding 一次 Run）
        var ratingTask = _wd.PredictRatingAsync(imagePath);
        var pixaiTask = _pixai.PredictWithEmbeddingAsync(imagePath);

        await Task.WhenAll(ratingTask, pixaiTask);

        if (sample) AppLogger.Memory($"Ensemble#{callId}.AfterParallel {fileName}");

        var rating = await ratingTask;
        var (pixaiPreds, embedding) = await pixaiTask;

        // 画师识别
        string? artistName = null;
        double artistConf = 0;
        if (embedding != null && _artistStore.Count > 0)
        {
            var match = _artistStore.Search(embedding, minSimilarity: _mergeConfig.ArtistMatchThreshold);
            if (match.HasValue)
            {
                artistName = match.Value.artistName;
                artistConf = match.Value.similarity;
            }
        }



        var sourceTags = new Dictionary<string, List<TagPrediction>>
        {
            ["pixai"] = pixaiPreds
        };

        var merged = _merger.Merge(sourceTags, _mergeConfig);

        // WD Rating 分级标签插入首位
        var ratingNames = new[] { "general", "sensitive", "questionable", "explicit" };
        var ratingCnNames = new[] { "全年龄", "敏感", "大尺度", "R18" };
        int rIdx = (int)rating;
        if (rIdx >= 0 && rIdx < 4)
        {
            merged.Insert(0, new TagPrediction(ratingNames[rIdx], 1.0)
            {
                ChineseName = ratingCnNames[rIdx],
                SourceModels = new List<string> { "wd" }
            });
        }

        // 画师识别结果追加到标签列表
        if (artistName != null)
        {
            var artistZh = _chineseLib.Lookup(artistName) ?? artistName;
            merged.Insert(1, new TagPrediction(artistName, artistConf)
            {
                ChineseName = artistZh,
                SourceModels = new List<string> { "embedding" }
            });
        }

        return new EnsembleResult(rating, merged, sourceTags)
        {
            ArtistName = artistName,
            ArtistConfidence = artistConf
        };
    }

    public IReadOnlyList<ModelStatus> GetModelStatuses()
    {
        return new List<ModelStatus>
        {
            new("wd", _wd.IsLoaded, "swinv2-v3", 4),
            new("pixai", _pixai.IsModelLoaded, "v0.9", _pixai.TagNames.Length)
        };
    }

    public void Dispose()
    {
        _pixai?.Dispose();
        _wd?.Dispose();
        _chineseLib?.Clear();
        _artistStore?.Clear();
        AppLogger.Info("EnsembleTagService Disposed");
    }
}
