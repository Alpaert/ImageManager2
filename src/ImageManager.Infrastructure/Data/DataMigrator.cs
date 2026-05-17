using System.Text.Json;
using ImageManager.Core.Models;
using ImageManager.Core.Services;

namespace ImageManager.Infrastructure.Data;

public class DataMigrator
{
    private readonly IImageMetaRepository _metaRepo;
    private readonly ITagRepository _tagRepo;
    private readonly IFolderRepository _folderRepo;
    private readonly ISettingsRepository _settingsRepo;

    public DataMigrator(
        IImageMetaRepository metaRepo,
        ITagRepository tagRepo,
        IFolderRepository folderRepo,
        ISettingsRepository settingsRepo)
    {
        _metaRepo = metaRepo;
        _tagRepo = tagRepo;
        _folderRepo = folderRepo;
        _settingsRepo = settingsRepo;
    }

    /// <summary>
    /// Migrate from old WPF JSON file (image_data.json) to SQLite.
    /// Returns summary of migrated items.
    /// </summary>
    public async Task<string> MigrateFromJsonAsync(string jsonFilePath)
    {
        if (!File.Exists(jsonFilePath))
            return $"文件不存在: {jsonFilePath}";

        var json = await File.ReadAllTextAsync(jsonFilePath);
        var oldData = JsonSerializer.Deserialize<LegacyAppData>(json);
        if (oldData == null)
            return "JSON 数据解析失败。";

        int imageCount = 0;
        int folderCount = 0;
        int favoriteCount = 0;

        // Migrate images and their tags
        if (oldData.Images != null)
        {
            foreach (var oldMeta in oldData.Images)
            {
                if (string.IsNullOrWhiteSpace(oldMeta.FilePath))
                    continue;

                try
                {
                    var meta = new ImageMeta
                    {
                        FilePath = oldMeta.FilePath,
                        FileHash = oldMeta.FileHash ?? "",
                        PerceptualHash = oldMeta.PerceptualHash ?? "",
                        Width = oldMeta.Width,
                        Height = oldMeta.Height,
                        FileSize = oldMeta.FileSize,
                        LastWriteTicks = oldMeta.LastWriteTicks
                    };

                    var id = await _metaRepo.UpsertAsync(meta);

                    if (oldMeta.Tags is { Count: > 0 })
                        await _metaRepo.SetTagsAsync(id, oldMeta.Tags);

                    imageCount++;
                }
                catch { }
            }
        }

        // Migrate folders
        if (oldData.FolderList != null)
        {
            foreach (var folder in oldData.FolderList)
            {
                if (string.IsNullOrWhiteSpace(folder)) continue;
                try
                {
                    await _folderRepo.AddAsync(folder);

                    if (oldData.FolderAliases != null &&
                        oldData.FolderAliases.TryGetValue(folder, out var alias) &&
                        !string.IsNullOrWhiteSpace(alias))
                    {
                        await _folderRepo.UpdateAliasAsync(folder, alias);
                    }

                    folderCount++;
                }
                catch { }
            }
        }

        // Migrate favorite tags
        if (oldData.FavoriteTags != null)
        {
            foreach (var tag in oldData.FavoriteTags)
            {
                if (string.IsNullOrWhiteSpace(tag)) continue;
                try
                {
                    await _tagRepo.AddFavoriteAsync(tag);
                    favoriteCount++;
                }
                catch { }
            }
        }

        // Migrate settings
        var settings = new AppSettings();
        if (oldData.StartupWidth > 0) settings.StartupWidth = oldData.StartupWidth;
        if (oldData.StartupHeight > 0) settings.StartupHeight = oldData.StartupHeight;
        if (oldData.PreviewWidth > 0) settings.PreviewWidth = oldData.PreviewWidth;
        if (oldData.PreviewHeight > 0) settings.PreviewHeight = oldData.PreviewHeight;
        settings.PreviewLeft = oldData.PreviewLeft;
        settings.PreviewTop = oldData.PreviewTop;
        settings.WallpaperPath = oldData.WallpaperPath ?? "";
        settings.WallpaperStretch = oldData.WallpaperStretch ?? "Uniform";
        settings.WallpaperAlignment = oldData.WallpaperAlignment ?? "Center";
        settings.WallpaperOpacity = oldData.WallpaperOpacity > 0 ? oldData.WallpaperOpacity : 0.25;
        settings.ShowThumbnailFileName = oldData.ShowThumbnailFileName;
        settings.ShowThumbnailTags = oldData.ShowThumbnailTags;
        settings.ShowThumbnailOrientation = oldData.ShowThumbnailOrientation;
        settings.ThumbnailAspectRatio = oldData.ThumbnailAspectRatio > 0 ? oldData.ThumbnailAspectRatio : 1.0;
        settings.ThumbnailNoTextStretch = oldData.ThumbnailNoTextStretch ?? "Uniform";
        settings.ThumbnailNoTextKeepPadding = oldData.ThumbnailNoTextKeepPadding;
        settings.WaterfallMode = oldData.WaterfallMode ?? "None";
        settings.ThumbnailBorderColor = oldData.ThumbnailBorderColor ?? "#FF808080";
        settings.ThumbnailBackgroundColor = oldData.ThumbnailBackgroundColor ?? "#CCFFFFFF";
        settings.ThumbnailOpacity = oldData.ThumbnailOpacity > 0 ? oldData.ThumbnailOpacity : 1.0;
        settings.ThumbnailCacheMaxMB = oldData.ThumbnailCacheMaxMB > 0 ? oldData.ThumbnailCacheMaxMB : 512;
        settings.DiskCacheDirectory = oldData.DiskCacheDirectory ?? @"C:\ImageManagerCache";
        settings.MaxTagSuggestionCount = oldData.MaxTagSuggestionCount > 0 ? oldData.MaxTagSuggestionCount : 30;
        settings.LastFolder = oldData.LastFolder ?? "";

        if (oldData.FavoriteTags != null)
            settings.FavoriteTags = oldData.FavoriteTags;

        await _settingsRepo.SaveAsync(settings);

        return $"迁移完成：{imageCount} 张图片、{folderCount} 个文件夹、{favoriteCount} 个常用 Tag。";
    }

    // Legacy JSON model matching old AppData structure
    private class LegacyAppData
    {
        public List<LegacyImageMeta>? Images { get; set; }
        public List<string>? FolderList { get; set; }
        public Dictionary<string, string>? FolderAliases { get; set; }
        public List<string>? FavoriteTags { get; set; }
        public int MaxTagSuggestionCount { get; set; } = 30;
        public string? LastFolder { get; set; }
        public double StartupWidth { get; set; }
        public double StartupHeight { get; set; }
        public double PreviewWidth { get; set; }
        public double PreviewHeight { get; set; }
        public double PreviewLeft { get; set; } = -1;
        public double PreviewTop { get; set; } = -1;
        public string? WallpaperPath { get; set; }
        public string? WallpaperStretch { get; set; }
        public string? WallpaperAlignment { get; set; }
        public double WallpaperOpacity { get; set; } = 0.25;
        public bool ShowThumbnailFileName { get; set; } = true;
        public bool ShowThumbnailTags { get; set; } = true;
        public bool ShowThumbnailOrientation { get; set; } = true;
        public double ThumbnailAspectRatio { get; set; } = 1.0;
        public string? ThumbnailNoTextStretch { get; set; }
        public bool ThumbnailNoTextKeepPadding { get; set; } = true;
        public string? WaterfallMode { get; set; }
        public string? ThumbnailBorderColor { get; set; }
        public string? ThumbnailBackgroundColor { get; set; }
        public double ThumbnailOpacity { get; set; } = 1.0;
        public int ThumbnailCacheMaxMB { get; set; } = 512;
        public string? DiskCacheDirectory { get; set; }
    }

    private class LegacyImageMeta
    {
        public string? FilePath { get; set; }
        public List<string>? Tags { get; set; }
        public string? FileHash { get; set; }
        public string? PerceptualHash { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public long FileSize { get; set; }
        public long LastWriteTicks { get; set; }
    }
}
