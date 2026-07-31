namespace ImageManager.Core.Models;

public class AppSettings
{
    public double StartupWidth { get; set; }
    public double StartupHeight { get; set; }
    public double PreviewWidth { get; set; }
    public double PreviewHeight { get; set; }
    public double PreviewLeft { get; set; } = double.NaN;
    public double PreviewTop { get; set; } = double.NaN;
    public string WallpaperPath { get; set; } = string.Empty;
    public string WallpaperStretch { get; set; } = "UniformToFill";
    public string WallpaperAlignment { get; set; } = "Center";
    public double WallpaperOpacity { get; set; } = 0.25;
    public bool ShowThumbnailFileName { get; set; } = true;
    public bool ShowThumbnailTags { get; set; } = true;
    public bool ShowThumbnailOrientation { get; set; } = true;
    public double ThumbnailAspectRatio { get; set; } = 1.0;
    public string ThumbnailNoTextStretch { get; set; } = "Uniform";
    public bool ThumbnailNoTextKeepPadding { get; set; } = false;
    public double ThumbnailCornerRadius { get; set; }
    public string ThemeVariant { get; set; } = "Dark";
    public string WaterfallMode { get; set; } = "None";
    public double GridZoomLevel { get; set; } = 1;
    public double VerticalZoomLevel { get; set; } = 1;
    public double HorizontalZoomLevel { get; set; } = 1;
    public string ThumbnailBorderColor { get; set; } = "#FF808080";
    public string ThumbnailBackgroundColor { get; set; } = "#CCFFFFFF";
    public double ThumbnailOpacity { get; set; } = 1.0;
    public string DiskCacheDirectory { get; set; } = @"C:\ImageManagerCache";
    public int MaxTagSuggestionCount { get; set; } = 30;
    public string LastFolder { get; set; } = string.Empty;
    public List<string> FavoriteTags { get; set; } = new();
    public Dictionary<string, string> FolderAliases { get; set; } = new();
    public Dictionary<string, string> ShortcutBindings { get; set; } = new();
    public PerceptualSearchResultMode PerceptualSearchResultMode { get; set; } = PerceptualSearchResultMode.Ranked;
    public int SimilaritySearchResultLimit { get; set; } = 50;

    /// <summary>Hash algorithm version — bump to force re-computation of all perceptual hashes</summary>
    public string HashVersion { get; set; } = "1";

    // ==================== Auto-Tag Settings ====================
    public string DeepSeekApiKey { get; set; } = string.Empty;

    // ==================== Ensemble Tag Settings ====================
    public int TagMode { get; set; } = 1;                    // 0=单模型(PixAI), 1=双模型(WD+PixAI)
    public int EnsembleMaxTagsPerImage { get; set; } = 75;
    public double EnsemblePixaiMinConfidence { get; set; } = 0.30;   // PixAI 打标置信度
    public double ArtistMatchThreshold { get; set; } = 0.35;          // 画师嵌入匹配阈值
    public bool EnableCharacterRecognition { get; set; } = true;
    public double CharacterMatchThreshold { get; set; } = 0.35;
    public int CharacterMaxMatchesPerImage { get; set; } = 1;
    public double SingleModelMinConfidence { get; set; } = 0.15;
}
