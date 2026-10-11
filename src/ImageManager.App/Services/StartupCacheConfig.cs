namespace ImageManager.App.Services;

public sealed class StartupCacheConfig
{
    private static readonly string BootDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImageManager");
    public static string DefaultCacheDirectory => Path.Combine(BootDirectory, "Cache");
    public static string ConfigPath => Path.Combine(BootDirectory, "config.json");
    private static string LegacyPortableConfigPath => Path.Combine(AppContext.BaseDirectory, "startup-cache.config");
    private readonly string _configPath;
    private string? _activePreviousDirectory;

    public string CacheDirectory { get; set; } = DefaultCacheDirectory;
    public string PreviousCacheDirectory { get; set; } = string.Empty;
    public bool CachePromptShown { get; set; }
    public bool ConfigExists { get; private set; }

    public StartupCacheConfig() : this(ConfigPath) { }
    internal StartupCacheConfig(string configPath) => _configPath = configPath;

    public static StartupCacheConfig Load() => Load(ConfigPath, LegacyPortableConfigPath);

    internal static StartupCacheConfig Load(string configPath, string legacyPortableConfigPath)
    {
        // A single per-user choice is shared by Debug, Release and installed builds.
        // Import portable configuration only if no shared file exists. Loading never probes writes.
        var config = new StartupCacheConfig(configPath);
        var source = File.Exists(configPath) ? configPath
            : File.Exists(legacyPortableConfigPath) ? legacyPortableConfigPath : null;
        if (source == null) return config;

        foreach (var line in File.ReadAllLines(source))
        {
            var index = line.IndexOf('=');
            if (index < 0) continue;
            var key = line[..index].Trim();
            var value = line[(index + 1)..].Trim();
            if (key.Equals(nameof(CacheDirectory), StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(value))
                config.CacheDirectory = value;
            else if (key.Equals(nameof(PreviousCacheDirectory), StringComparison.OrdinalIgnoreCase))
                config.PreviousCacheDirectory = value;
            else if (key.Equals(nameof(CachePromptShown), StringComparison.OrdinalIgnoreCase)
                && bool.TryParse(value, out var shown))
                config.CachePromptShown = shown;
        }
        config.ConfigExists = true;
        return config;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
        var content = $"CacheDirectory={CacheDirectory}{Environment.NewLine}"
            + $"PreviousCacheDirectory={PreviousCacheDirectory}{Environment.NewLine}"
            + $"CachePromptShown={CachePromptShown.ToString().ToLowerInvariant()}";
        // Commit the actual configuration atomically; never silently redirect on failure.
        var temporaryPath = _configPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, content);
            File.Move(temporaryPath, _configPath, overwrite: true);
            ConfigExists = true;
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public static string NormalizeDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("缓存位置不能为空。");
        if (path.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new ArgumentException("缓存位置包含无效字符。");
        var expanded = Environment.ExpandEnvironmentVariables(path.Trim());
        if (!Path.IsPathFullyQualified(expanded))
            throw new ArgumentException("请选择完整的缓存目录路径。");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded));
    }

    public static bool TryPrepareDirectory(string path, out string error)
    {
        error = string.Empty;
        try
        {
            var fullPath = NormalizeDirectory(path);
            if (!Directory.Exists(fullPath)) Directory.CreateDirectory(fullPath);
            return true;
        }
        catch (Exception ex)
        {
            error = $"无法使用缓存目录：{path}；{ex.Message}";
            return false;
        }
    }

    public void SetCacheDirectory(string path, bool promptShown)
    {
        var normalized = NormalizeDirectory(path);
        if (!string.Equals(CacheDirectory.TrimEnd('\\', '/'), normalized.TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase))
        {
            PreviousCacheDirectory = CacheDirectory;
            CacheDirectory = normalized;
        }
        CachePromptShown = promptShown;
    }

    public void RequestDirectoryChange(string path, string activeDirectory)
    {
        if (!TryPrepareDirectory(path, out var error)) throw new IOException(error);
        var selected = NormalizeDirectory(path);
        var active = NormalizeDirectory(activeDirectory);
        // Repeated saves before restart must migrate from the live database.
        _activePreviousDirectory ??= PreviousCacheDirectory;
        var previous = string.Equals(selected, active, StringComparison.OrdinalIgnoreCase)
            ? _activePreviousDirectory : active;
        var next = new StartupCacheConfig(_configPath)
        {
            CacheDirectory = selected,
            PreviousCacheDirectory = previous,
            CachePromptShown = true
        };
        next.Save();
        CacheDirectory = selected;
        PreviousCacheDirectory = previous;
        CachePromptShown = true;
        ConfigExists = true;
    }
}
