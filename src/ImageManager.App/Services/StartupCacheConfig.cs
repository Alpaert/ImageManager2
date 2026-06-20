namespace ImageManager.App.Services;

public sealed class StartupCacheConfig
{
    public const string DefaultCacheDirectory = @"C:\ImageManagerCache";

    private static readonly string BootDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ImageManager");

    public static string ConfigPath => Path.Combine(BootDirectory, "config.json");

    public string CacheDirectory { get; set; } = DefaultCacheDirectory;
    public string PreviousCacheDirectory { get; set; } = string.Empty;
    public bool CachePromptShown { get; set; }
    public bool ConfigExists { get; private set; }

    public static StartupCacheConfig Load()
    {
        Directory.CreateDirectory(BootDirectory);

        var config = new StartupCacheConfig
        {
            ConfigExists = File.Exists(ConfigPath)
        };
        config.CachePromptShown = config.ConfigExists;

        if (!config.ConfigExists)
            return config;

        try
        {
            foreach (var line in File.ReadAllLines(ConfigPath))
            {
                var idx = line.IndexOf('=');
                if (idx < 0) continue;

                var key = line[..idx].Trim();
                var value = line[(idx + 1)..].Trim();

                if (key.Equals(nameof(CacheDirectory), StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(value))
                    config.CacheDirectory = value;
                else if (key.Equals(nameof(PreviousCacheDirectory), StringComparison.OrdinalIgnoreCase))
                    config.PreviousCacheDirectory = value;
                else if (key.Equals(nameof(CachePromptShown), StringComparison.OrdinalIgnoreCase)
                    && bool.TryParse(value, out var shown))
                    config.CachePromptShown = shown;
            }
        }
        catch
        {
            config.CacheDirectory = DefaultCacheDirectory;
            config.PreviousCacheDirectory = string.Empty;
            config.CachePromptShown = false;
        }

        return config;
    }

    public void Save()
    {
        Directory.CreateDirectory(BootDirectory);
        File.WriteAllText(ConfigPath,
            $"CacheDirectory={CacheDirectory}{Environment.NewLine}" +
            $"PreviousCacheDirectory={PreviousCacheDirectory}{Environment.NewLine}" +
            $"CachePromptShown={CachePromptShown.ToString().ToLowerInvariant()}");
        ConfigExists = true;
    }

    public static bool TryValidateWritableDirectory(string path, out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "缓存位置不能为空。";
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            Directory.CreateDirectory(fullPath);

            var probe = Path.Combine(fullPath, $".imagemanager_write_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex)
        {
            error = $"无法使用该目录：{ex.Message}";
            return false;
        }
    }

    public void SetCacheDirectory(string path, bool promptShown)
    {
        var normalized = Path.GetFullPath(path);
        if (!string.Equals(
                CacheDirectory.TrimEnd('\\', '/'),
                normalized.TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase))
        {
            PreviousCacheDirectory = CacheDirectory;
            CacheDirectory = normalized;
        }

        CachePromptShown = promptShown;
    }
}
