using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Markup.Xaml;
using CommunityToolkit.Mvvm.Messaging;
using ImageManager.App.Helpers;
using ImageManager.App.Services;
using ImageManager.App.ViewModels;
using ImageManager.App.Views;
using ImageManager.Common.Helpers;
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
    /// <summary>UI-thread dispatcher for safe cross-thread UI access. Set after DI container is built.</summary>
    public static IDispatcher UI { get; private set; } = null!;
    public static string CacheDirectoryPath { get; private set; } =
        System.IO.Path.Combine(AppContext.BaseDirectory, "Cache");

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

        // Register GBK encoding provider for CSV reading
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

        // Initialize ensemble logger
        Common.Helpers.AppLogger.Init(cacheDir);

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

        // Create connection factory
        var dbFactory = new DbContextFactory(dbPath);

        // Initialize schema + migrations (one-shot, no retry)
        using (var initConn = dbFactory.CreateConnection())
        {
            DatabaseInitializer.Initialize(initConn);
        }

        // Open DB with recovery: if corrupt, delete files and start fresh
        bool dbReady = false;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var testConn = dbFactory.CreateConnection();
                dbReady = true;
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
                // Re-initialize after wiping
                using var freshConn = dbFactory.CreateConnection();
                DatabaseInitializer.Initialize(freshConn);
            }
        }

        if (!dbReady)
            throw new InvalidOperationException("Unable to open or create database at " + dbPath);

        services.AddSingleton<IDbContextFactory>(dbFactory);

        // UI dispatcher abstraction (allows Infrastructure services to marshal to UI thread)
        services.AddSingleton<IDispatcher, Helpers.AvaloniaDispatcher>();

        // Event aggregator for cross-component communication
        services.AddSingleton<CommunityToolkit.Mvvm.Messaging.IMessenger>(
            CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default);

        services.AddSingleton<IImageMetaRepository, ImageMetaRepository>();
        services.AddSingleton<ITagRepository, TagRepository>();
        services.AddSingleton<ITagMappingRepository, TagMappingRepository>();
        services.AddSingleton<IFolderRepository, FolderRepository>();
        services.AddSingleton<ISettingsRepository, SettingsRepository>();

        services.AddSingleton<IHashService, HashService>();
        services.AddSingleton<ISimilarImageService, SimilarImageService>();
        services.AddSingleton<IDuplicateService, DuplicateService>();

        // ==================== Media Processor Factory ====================
        services.AddSingleton<IMediaProcessorFactory>(new MediaProcessorFactory(cacheDir));

        services.AddSingleton<ThumbnailCacheService>(sp =>
            new ThumbnailCacheService(
                sp.GetRequiredService<IMediaProcessorFactory>(),
                cacheDir,
                200));
        services.AddSingleton<IThumbnailCacheService>(sp => sp.GetRequiredService<ThumbnailCacheService>());

        // Existing WD service
        services.AddSingleton<OnnxTagService>();
        services.AddSingleton<IAutoTagService>(sp => sp.GetRequiredService<OnnxTagService>());

        // ==================== Ensemble Tag Services ====================
        services.AddSingleton<ChineseTagLibrary>();
        services.AddSingleton<ArtistEmbeddingStore>();
        services.AddSingleton<PixaiTagService>();
        services.AddSingleton<WdRatingService>();
        services.AddSingleton<TagResultMerger>();
        services.AddSingleton<SingleModelTagService>();
        services.AddSingleton<EnsembleTagService>();
        services.AddSingleton<TagServiceFactory>();
        // Default → Ensemble mode, AutoTagController switches via TagServiceFactory
        services.AddSingleton<IEnsembleTagService>(sp => sp.GetRequiredService<EnsembleTagService>());

        services.AddSingleton<IAutoTagStateRepository, AutoTagStateRepository>();
        services.AddSingleton<AutoTagPipelineService>();
        services.AddSingleton<AutoTagOrchestrator>();
        services.AddSingleton<DeepSeekRecommendService>();
        services.AddSingleton<IAiRecommendService>(sp => sp.GetRequiredService<DeepSeekRecommendService>());


        services.AddSingleton<PageManager>();
        services.AddSingleton<TagSearchEngine>();
        services.AddSingleton<Services.ImagePreloader>();
        services.AddSingleton<MainWindowViewModel>();

        return services.BuildServiceProvider();
    }

    public override void Initialize()
    {
        Services = ConfigureServices();
        AvaloniaXamlLoader.Load(this);
    }

    public override async void OnFrameworkInitializationCompleted()
    {
        UI = Services.GetRequiredService<IDispatcher>();

        // 显式加载 onnxruntime.dll，绕过 LoadLibrary → System32 优先级问题
        var exeDir = AppContext.BaseDirectory;
        var runtimesPath = System.IO.Path.Combine(exeDir, "runtimes", "win-x64", "native", "onnxruntime.dll");
        var rootPath = System.IO.Path.Combine(exeDir, "onnxruntime.dll");
        var targetPath = System.IO.File.Exists(rootPath) ? rootPath
                       : System.IO.File.Exists(runtimesPath) ? runtimesPath
                       : null;
        if (targetPath != null)
        {
            try
            {
                var fi = new System.IO.FileInfo(targetPath);
                System.Runtime.InteropServices.NativeLibrary.Load(targetPath);
                Common.Helpers.AppLogger.Info($"NativeLibrary.Load OK: {targetPath} size={fi.Length}");
            }
            catch (Exception ex)
            {
                var inner = ex;
                var chain = new System.Text.StringBuilder();
                while (inner != null)
                {
                    chain.AppendLine($"[{inner.GetType().Name}] {inner.Message}");
                    inner = inner.InnerException;
                }
                Common.Helpers.AppLogger.Error($"NativeLibrary.Load FAIL: path={targetPath} hResult=0x{ex.HResult:X8}\nException chain:\n{chain}\nFull:\n{ex}");
            }
        }
        else { Common.Helpers.AppLogger.Error($"onnxruntime.dll NOT FOUND in exeDir={exeDir}"); }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            await ApplySavedThemeAsync();

            var vm = Services.GetRequiredService<MainWindowViewModel>();
            desktop.MainWindow = new MainWindow { DataContext = vm };
        }

        base.OnFrameworkInitializationCompleted();
        Common.Helpers.AppLogger.Memory("App.Init");
    }

    private async Task ApplySavedThemeAsync()
    {
        var settings = await Services.GetRequiredService<ISettingsRepository>().LoadAsync();
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
