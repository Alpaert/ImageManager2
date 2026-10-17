using System.Text;
using Avalonia;
using Avalonia.Controls;
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
using ImageManager.Core.Models;
using ImageManager.Core.Services;
using ImageManager.Infrastructure.Caching;
using ImageManager.Infrastructure.Data;
using ImageManager.Infrastructure.Data.Repositories;
using ImageManager.Infrastructure.Hashing;
using ImageManager.Infrastructure.Services;
using ImageManager.Infrastructure.Video;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace ImageManager.App;

public partial class App : Application
{
    public static ServiceProvider Services { get; private set; } = null!;
    /// <summary>UI-thread dispatcher for safe cross-thread UI access. Set after DI container is built.</summary>
    public static IDispatcher UI { get; private set; } = null!;
    public static string CacheDirectoryPath { get; private set; } = StartupCacheConfig.DefaultCacheDirectory;
    private static StartupCacheConfig _startupCacheConfig = new();

    public static void ReleaseInferenceSessions()
    {
        if (Services == null)
            return;

        try { Services.GetService<ChineseClipService>()?.ReleaseAllSessions(); }
        catch (Exception ex) { AppLogger.Warn($"Release Chinese-CLIP session failed: {ex.Message}"); }
        try { Services.GetService<OnnxTagService>()?.ReleaseSession(); }
        catch (Exception ex) { AppLogger.Warn($"Release WD session failed: {ex.Message}"); }
        try { Services.GetService<PixaiTagService>()?.ReleaseSession(); }
        catch (Exception ex) { AppLogger.Warn($"Release PixAI session failed: {ex.Message}"); }
        AppLogger.Memory("App.ReleaseInferenceSessions");
    }

    internal static ServiceProvider ConfigureServices(string cacheDir)
    {
        var services = new ServiceCollection();

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
            MigrateDatabaseIfAvailable(dbPath);
        }

        // Create connection factory
        var dbFactory = new DbContextFactory(dbPath);

        // Initialize schema and validate a second connection without destructive recovery.
        // A lock, permission error, or malformed database must never turn into silent data loss.
        try
        {
            using (var initConn = dbFactory.CreateConnection())
            {
                DatabaseInitializer.Initialize(initConn);
            }

            using var testConn = dbFactory.CreateConnection();
        }
        catch (Exception ex)
        {
            SqliteConnection.ClearAllPools();
            AppLogger.Error($"Database startup failed; existing database was preserved: path={dbPath} error={ex}");
            throw new InvalidOperationException(
                $"无法打开或初始化数据库，原有数据未删除：{dbPath}", ex);
        }

        services.AddSingleton<IDbContextFactory>(dbFactory);

        // UI dispatcher abstraction (allows Infrastructure services to marshal to UI thread)
        services.AddSingleton<IDispatcher, Helpers.AvaloniaDispatcher>();

        // Event aggregator for cross-component communication
        services.AddSingleton<CommunityToolkit.Mvvm.Messaging.IMessenger>(
            CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default);

        services.AddSingleton<IHashDataVersionTracker, HashDataVersionTracker>();
        services.AddSingleton<ImageMetaRepository>();
        services.AddSingleton<IImageMetaRepository>(sp => sp.GetRequiredService<ImageMetaRepository>());
        services.AddSingleton<IImageMetaReadRepository>(sp => sp.GetRequiredService<ImageMetaRepository>());
        services.AddSingleton<IImageMetaWriteRepository>(sp => sp.GetRequiredService<ImageMetaRepository>());
        services.AddSingleton<IImageTagRepository>(sp => sp.GetRequiredService<ImageMetaRepository>());
        services.AddSingleton<IImageHashRepository>(sp => sp.GetRequiredService<ImageMetaRepository>());
        services.AddSingleton<ITagRepository, TagRepository>();
        services.AddSingleton<ITagMappingRepository, TagMappingRepository>();
        services.AddSingleton<IFolderRepository, FolderRepository>();
        services.AddSingleton<ISettingsRepository, SettingsRepository>();
        services.AddSingleton<ICharacterTagSuppressionRepository, CharacterTagSuppressionRepository>();
        services.AddSingleton<IImageEmbeddingRepository, ImageEmbeddingRepository>();

        services.AddSingleton<IHashService, HashService>();
        services.AddSingleton(new ChineseClipService(cacheDir));
        services.AddSingleton<IChineseClipService>(sp => sp.GetRequiredService<ChineseClipService>());
        services.AddSingleton<ISimilarImageService, SimilarImageService>();
        services.AddSingleton<IVectorIndexService, VectorIndexService>();
        services.AddSingleton<IDuplicateService, DuplicateService>();
        services.AddSingleton<IVideoService, VideoService>();

        // ==================== Media Processor Factory ====================
        services.AddSingleton(new VideoOriginalFrameCacheService(cacheDir));
        services.AddSingleton<IVideoOriginalFrameCacheService>(sp => sp.GetRequiredService<VideoOriginalFrameCacheService>());
        services.AddSingleton<IMediaProcessorFactory>(sp =>
            new MediaProcessorFactory(sp.GetRequiredService<VideoOriginalFrameCacheService>()));

        services.AddSingleton<ThumbnailCacheService>(sp =>
            new ThumbnailCacheService(
                sp.GetRequiredService<IMediaProcessorFactory>(),
                cacheDir,
                200));
        services.AddSingleton<IThumbnailCacheService>(sp => sp.GetRequiredService<ThumbnailCacheService>());
        services.AddSingleton<IMediaMoveService, MediaMoveService>();
        services.AddSingleton<IMediaThumbnailPrewarmService, MediaThumbnailPrewarmService>();

        // Existing WD service
        services.AddSingleton<OnnxTagService>();
        services.AddSingleton<IAutoTagService>(sp => sp.GetRequiredService<OnnxTagService>());

        // ==================== Ensemble Tag Services ====================
        services.AddSingleton<ChineseTagLibrary>();
        services.AddSingleton<IChineseTagLibrary>(sp => sp.GetRequiredService<ChineseTagLibrary>());
        services.AddSingleton<ArtistEmbeddingStore>();
        services.AddSingleton<IArtistEmbeddingStore>(sp => sp.GetRequiredService<ArtistEmbeddingStore>());
        services.AddSingleton<CharacterEmbeddingStore>();
        services.AddSingleton<ICharacterEmbeddingStore>(sp => sp.GetRequiredService<CharacterEmbeddingStore>());
        services.AddSingleton<PixaiTagService>();
        services.AddSingleton<IImageEmbeddingExtractor>(sp => sp.GetRequiredService<PixaiTagService>());
        services.AddSingleton<ICharacterMatchingService, CharacterMatchingService>();
        services.AddSingleton<ILibraryBuilderService, LibraryBuilderService>();
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
        services.AddSingleton<IAutoTagOrchestrator>(sp => sp.GetRequiredService<AutoTagOrchestrator>());
        services.AddSingleton<DeepSeekRecommendService>();
        services.AddSingleton<IAiRecommendService>(sp => sp.GetRequiredService<DeepSeekRecommendService>());


        services.AddSingleton<PageManager>();
        services.AddSingleton<TagSearchEngine>();
        services.AddSingleton<ITagSearchEngine>(sp => sp.GetRequiredService<TagSearchEngine>());
        services.AddSingleton<Services.ImagePreloader>();
        services.AddSingleton<MainWindowViewModel>();

        return services.BuildServiceProvider();
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override async void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktopLifetime)
        {
            desktopLifetime.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            ApplyColors(true);
            try
            {
                if (!await EnsureStartupCacheDirectoryAsync())
                {
                    desktopLifetime.Shutdown();
                    return;
                }
                Services = ConfigureServices(CacheDirectoryPath);
            }
            catch (Exception ex)
            {
                await ShowStartupFailureAsync(ex);
                desktopLifetime.Shutdown();
                return;
            }
        }
        else
        {
            Services = ConfigureServices(CacheDirectoryPath);
        }

        ConfigureReadOnlyCacheFallback();

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
            string? startupRecoveryNotice = null;
            try
            {
                var moveService = Services.GetRequiredService<IMediaMoveService>();
                var recovered = await moveService.RecoverPendingMovesAsync();
                if (recovered.Count > 0)
                {
                    int successCount = recovered.Count(r => r.Status == MediaMoveItemStatus.Success);
                    int compFailedCount = recovered.Count(r => r.Status == MediaMoveItemStatus.CompensationFailed);
                    int failedCount = recovered.Count(r => r.Status == MediaMoveItemStatus.Failed);

                    if (compFailedCount > 0)
                    {
                        startupRecoveryNotice = $"启动恢复检查：存在 {compFailedCount} 项移动未完成需人工核查（已恢复 {successCount} 项）";
                        AppLogger.Warn($"Startup recovery completed with items needing manual attention: success={successCount}, compFailed={compFailedCount}, failed={failedCount}");
                    }
                    else
                    {
                        startupRecoveryNotice = $"启动恢复完成：已恢复 {successCount} 项未完成的移动操作";
                        AppLogger.Info($"Startup recovery succeeded: {successCount} operation(s) recovered.");
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error($"Startup recovery for pending media moves failed: {ex.Message}");
            }

            await ApplySavedThemeAsync();

            var vm = Services.GetRequiredService<MainWindowViewModel>();
            if (!string.IsNullOrEmpty(startupRecoveryNotice))
            {
                vm.StatusText = startupRecoveryNotice;
            }
            desktop.MainWindow = new MainWindow { DataContext = vm };
            desktop.MainWindow.Closed += (_, _) => desktop.Shutdown();
            desktop.Exit += (_, _) => ReleaseInferenceSessions();
            desktop.MainWindow.Show();
        }

        // ========== 全局未处理异常捕获 ==========
        // 捕获 fire-and-forget Task.Run 中被遗弃的异常（如 _ = Task.Run(...) 无 await）
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            var ex = args.Exception; // AggregateException，展开所有 inner
            var sb = new StringBuilder();
            sb.AppendLine("===== UnobservedTaskException =====");
            foreach (var inner in ex.InnerExceptions)
                sb.AppendLine(inner.ToString()); // 含完整类型、消息、堆栈
            AppLogger.Error(sb.ToString());
            args.SetObserved(); // 标记已处理，防止进程崩溃
        };

        // 捕获 async void / UI 线程 / 线程池等未处理异常
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                AppLogger.Error($"===== UnhandledException =====\n{ex}");
            else
                AppLogger.Error($"===== UnhandledException (non-Exception) =====\n{args.ExceptionObject}");
        };

        base.OnFrameworkInitializationCompleted();
        Common.Helpers.AppLogger.Memory("App.Init");
    }

    private static void MigrateDatabaseIfAvailable(string dbPath)
    {
        CacheDatabaseMigration.MigrateIfMissing(dbPath,
            _startupCacheConfig.PreviousCacheDirectory,
            Path.GetDirectoryName(StartupCacheConfig.ConfigPath) ?? "");
    }

    public static string GetConfiguredCacheDirectory() => _startupCacheConfig.CacheDirectory;

    public static string RequestCacheDirectoryChange(string path)
    {
        _startupCacheConfig.RequestDirectoryChange(path, CacheDirectoryPath);
        return _startupCacheConfig.CacheDirectory;
    }

    private static async Task<bool> EnsureStartupCacheDirectoryAsync()
    {
        _startupCacheConfig = StartupCacheConfig.Load();
        // First launch uses LocalAppData directly. Explicit paths are never replaced
        // with an exe-relative Cache or CacheRecovery directory.
        while (!StartupCacheConfig.TryPrepareDirectory(_startupCacheConfig.CacheDirectory, out var error))
        {
            var repairWindow = new Views.Settings.StartupCacheWindow(
                _startupCacheConfig.CacheDirectory, isRepairRequired: true, repairReason: error);
            if (!await ShowStartupWindowAsync(repairWindow)) return false;

            _startupCacheConfig.SetCacheDirectory(repairWindow.SelectedPath, promptShown: true);
            _startupCacheConfig.Save();
        }

        CacheDirectoryPath = StartupCacheConfig.NormalizeDirectory(_startupCacheConfig.CacheDirectory);
        _startupCacheConfig.CacheDirectory = CacheDirectoryPath;
        if (!File.Exists(StartupCacheConfig.ConfigPath))
        {
            _startupCacheConfig.CachePromptShown = true;
            _startupCacheConfig.Save();
        }
        return true;
    }

    private static async Task ShowStartupFailureAsync(Exception error)
    {
        var window = new Window
        {
            Title = "无法打开缓存位置",
            Width = 620, Height = 300,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        var close = new Button { Content = "关闭", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        close.Click += (_, _) => window.Close();
        var panel = new StackPanel { Margin = new Avalonia.Thickness(20), Spacing = 16 };
        panel.Children.Add(new TextBlock
        {
            Text = $"无法使用指定的缓存位置：{_startupCacheConfig.CacheDirectory}\n"
                + "原有数据和目录选择已保留。请根据下面的实际错误检查后重新启动。\n\n"
                + error.GetBaseException().Message,
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(close);
        window.Content = panel;
        await ShowStartupWindowAsync(window);
    }

    private static void ConfigureReadOnlyCacheFallback()
    {
        var previous = _startupCacheConfig.PreviousCacheDirectory;
        if (string.IsNullOrWhiteSpace(previous)
            || string.Equals(previous.TrimEnd('\\', '/'),
                CacheDirectoryPath.TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase)
            || !Directory.Exists(previous))
        {
            return;
        }

        var thumbnails = Services.GetRequiredService<ThumbnailCacheService>();
        thumbnails.ReadOnlyFallbackCacheDirectory = previous;
        var videoFrames = Services.GetRequiredService<VideoOriginalFrameCacheService>();
        videoFrames.ReadOnlyFallbackCacheDirectory = previous;
        AppLogger.Info($"Using read-only legacy cache fallback: {previous}");
    }

    private static Task<bool> ShowStartupWindowAsync(Window window)
    {
        var tcs = new TaskCompletionSource<bool>();
        window.Closed += (_, _) =>
        {
            tcs.TrySetResult(window is Views.Settings.StartupCacheWindow startup
                && startup.IsAccepted);
        };
        window.Show();
        return tcs.Task;
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
            resources["ToolbarDropBgBrush"]  = new SolidColorBrush(Color.Parse("#206366F1"));
            resources["ToolbarDropFgBrush"]  = new SolidColorBrush(Color.Parse("#AAB0FF"));
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
            resources["ToolbarDropBgBrush"]  = new SolidColorBrush(Color.Parse("#246366F1"));
            resources["ToolbarDropFgBrush"]  = new SolidColorBrush(Color.Parse("#4F46E5"));
        }
    }
}
