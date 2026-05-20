using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Markup.Xaml;
using ImageManager.App.Services;
using ImageManager.App.ViewModels;
using ImageManager.App.Views;
using ImageManager.Core.Services;
using ImageManager.Infrastructure.Caching;
using ImageManager.Infrastructure.Data;
using ImageManager.Infrastructure.Data.Repositories;
using ImageManager.Infrastructure.Hashing;
using ImageManager.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace ImageManager.App;

public partial class App : Application
{
    public static ServiceProvider Services { get; private set; } = null!;
    public static string CacheDirectoryPath { get; private set; } = @"C:\ImageManagerCache";

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // Boot config: always at %LocalAppData%\ImageManager\config.json
        var bootDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ImageManager");
        System.IO.Directory.CreateDirectory(bootDir);
        var configPath = System.IO.Path.Combine(bootDir, "config.json");

        string cacheDir = @"C:\ImageManagerCache";
        string prevCacheDir = "";
        if (System.IO.File.Exists(configPath))
        {
            try
            {
                var lines = System.IO.File.ReadAllLines(configPath);
                foreach (var line in lines)
                {
                    var idx = line.IndexOf('=');
                    if (idx < 0) continue;
                    var key = line[..idx].Trim();
                    var val = line[(idx + 1)..].Trim();
                    if (key.Equals("CacheDirectory", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(val))
                        cacheDir = val;
                    else if (key.Equals("PreviousCacheDirectory", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(val))
                        prevCacheDir = val;
                }
            }
            catch { }
        }
        else
        {
            System.IO.File.WriteAllText(configPath, $"CacheDirectory={cacheDir}");
        }

        CacheDirectoryPath = cacheDir;

        var dbPath = System.IO.Path.Combine(cacheDir, "data.db");
        System.IO.Directory.CreateDirectory(cacheDir);

        // Migrate DB from previous cache dir (if user switched dirs) or old boot dir
        if (!System.IO.File.Exists(dbPath))
        {
            string[] candidateDirs = [
                prevCacheDir,
                System.IO.Path.Combine(bootDir), // %LocalAppData%\ImageManager (legacy)
            ];
            foreach (var srcDir in candidateDirs)
            {
                if (string.IsNullOrWhiteSpace(srcDir)) continue;
                var srcDb = System.IO.Path.Combine(srcDir, "data.db");
                if (!System.IO.File.Exists(srcDb)) continue;
                try
                {
                    System.IO.File.Copy(srcDb, dbPath);
                    foreach (var suffix in new[] { "-wal", "-shm" })
                    {
                        if (System.IO.File.Exists(srcDb + suffix))
                        {
                            try { System.IO.File.Copy(srcDb + suffix, dbPath + suffix); } catch { }
                        }
                    }
                    break;
                }
                catch { }
            }
        }

        // Open DB with recovery: if corrupt, delete files and start fresh
        AppDbContext? dbContext = null;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                dbContext = new AppDbContext(dbPath);
                break;
            }
            catch
            {
                SqliteConnection.ClearAllPools();
                // Wipe everything related to this DB and let SQLite create fresh
                foreach (var path in new[] { dbPath, dbPath + "-wal", dbPath + "-shm", dbPath + ".bak" })
                {
                    for (int retry = 0; retry < 3; retry++)
                    {
                        try
                        {
                            if (System.IO.File.Exists(path))
                                System.IO.File.Delete(path);
                            break;
                        }
                        catch { System.Threading.Thread.Sleep(50); }
                    }
                }
            }
        }

        if (dbContext == null)
            throw new InvalidOperationException("Unable to open or create database at " + dbPath);

        services.AddSingleton(dbContext);

        services.AddSingleton<IImageMetaRepository, ImageMetaRepository>();
        services.AddSingleton<ITagRepository, TagRepository>();
        services.AddSingleton<ITagMappingRepository, TagMappingRepository>();
        services.AddSingleton<IFolderRepository, FolderRepository>();
        services.AddSingleton<ISettingsRepository, SettingsRepository>();

        services.AddSingleton<IHashService, HashService>();
        services.AddSingleton<ISimilarImageService, SimilarImageService>();
        services.AddSingleton<IDuplicateService, DuplicateService>();

        services.AddSingleton<ThumbnailCacheService>();
        services.AddSingleton<IThumbnailCacheService>(sp => sp.GetRequiredService<ThumbnailCacheService>());

        services.AddSingleton<OnnxTagService>();
        services.AddSingleton<IAutoTagService>(sp => sp.GetRequiredService<OnnxTagService>());
        services.AddSingleton<DeepSeekTranslationService>();
        services.AddSingleton<ITranslationService>(sp => sp.GetRequiredService<DeepSeekTranslationService>());
        services.AddSingleton<IAutoTagStateRepository, AutoTagStateRepository>();
        services.AddSingleton<AutoTagPipelineService>();
        services.AddSingleton<AutoTagController>();
        services.AddSingleton<DeepSeekRecommendService>();
        services.AddSingleton<IAiRecommendService>(sp => sp.GetRequiredService<DeepSeekRecommendService>());

        services.AddSingleton<PageManager>();
        services.AddSingleton<TagSearchController>();
        services.AddSingleton<MainWindowViewModel>();

        return services.BuildServiceProvider();
    }

    public override void Initialize()
    {
        Services = ConfigureServices();
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            ApplySavedTheme();

            var vm = Services.GetRequiredService<MainWindowViewModel>();
            desktop.MainWindow = new MainWindow { DataContext = vm };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ApplySavedTheme()
    {
        var settings = Services.GetRequiredService<ISettingsRepository>().LoadAsync().Result;
        bool dark = !string.Equals(settings.ThemeVariant, "Light", StringComparison.OrdinalIgnoreCase);
        ApplyColors(dark);
    }

    public static void ApplyColors(bool dark)
    {
        var app = Current!;
        app.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        var resources = app.Resources;

        if (dark)
        {
            resources["AppBgBrush"]          = new SolidColorBrush(Color.Parse("#1A1A24"));
            resources["AppFgBrush"]          = new SolidColorBrush(Color.Parse("#E4E4E7"));
            resources["CtrlBgBrush"]         = new SolidColorBrush(Color.Parse("#3F3F46"));
            resources["CtrlFgBrush"]         = new SolidColorBrush(Color.Parse("#E4E4E7"));
            resources["CtrlHoverBgBrush"]    = new SolidColorBrush(Color.Parse("#52525B"));
            resources["CtrlBorderBrush"]     = new SolidColorBrush(Color.Parse("#52525B"));
            resources["InputBgBrush"]        = new SolidColorBrush(Color.Parse("#1A1A24"));
            resources["AccentBrush"]         = new SolidColorBrush(Color.Parse("#6366F1"));
            resources["SidebarBgBrush"]      = new SolidColorBrush(Color.Parse("#E8252530"));
            resources["SurfaceBrush"]        = new SolidColorBrush(Color.Parse("#252530"));
            resources["SelectedBgBrush"]     = new SolidColorBrush(Color.Parse("#4338CA"));
            resources["HoverBgBrush"]        = new SolidColorBrush(Color.Parse("#363648"));
            resources["SecondaryFgBrush"]    = new SolidColorBrush(Color.Parse("#A1A1AA"));
            resources["MutedFgBrush"]        = new SolidColorBrush(Color.Parse("#71717A"));
        }
        else
        {
            resources["AppBgBrush"]          = new SolidColorBrush(Color.Parse("#F3F3F3"));
            resources["AppFgBrush"]          = new SolidColorBrush(Color.Parse("#1A1A1A"));
            resources["CtrlBgBrush"]         = new SolidColorBrush(Color.Parse("#F9F9F9"));
            resources["CtrlFgBrush"]         = new SolidColorBrush(Color.Parse("#1A1A1A"));
            resources["CtrlHoverBgBrush"]    = new SolidColorBrush(Color.Parse("#E8E8E8"));
            resources["CtrlBorderBrush"]     = new SolidColorBrush(Color.Parse("#D0D0D0"));
            resources["InputBgBrush"]        = new SolidColorBrush(Color.Parse("#FFFFFF"));
            resources["AccentBrush"]         = new SolidColorBrush(Color.Parse("#6366F1"));
            resources["SidebarBgBrush"]      = new SolidColorBrush(Color.Parse("#E8E8EC"));
            resources["SurfaceBrush"]        = new SolidColorBrush(Color.Parse("#FFFFFF"));
            resources["SelectedBgBrush"]     = new SolidColorBrush(Color.Parse("#C7DBFF"));
            resources["HoverBgBrush"]        = new SolidColorBrush(Color.Parse("#E8E8EC"));
            resources["SecondaryFgBrush"]    = new SolidColorBrush(Color.Parse("#666666"));
            resources["MutedFgBrush"]        = new SolidColorBrush(Color.Parse("#999999"));
        }
    }
}
