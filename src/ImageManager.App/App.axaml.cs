using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Markup.Xaml;
using ImageManager.App.ViewModels;
using ImageManager.App.Views;
using ImageManager.Core.Services;
using ImageManager.Infrastructure.Caching;
using ImageManager.Infrastructure.Data;
using ImageManager.Infrastructure.Data.Repositories;
using ImageManager.Infrastructure.Hashing;
using ImageManager.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ImageManager.App;

public partial class App : Application
{
    public static ServiceProvider Services { get; private set; } = null!;

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        var dbDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ImageManager");
        System.IO.Directory.CreateDirectory(dbDir);

        var dbPath = System.IO.Path.Combine(dbDir, "data.db");
        services.AddSingleton(new AppDbContext(dbPath));

        services.AddSingleton<IImageMetaRepository, ImageMetaRepository>();
        services.AddSingleton<ITagRepository, TagRepository>();
        services.AddSingleton<IFolderRepository, FolderRepository>();
        services.AddSingleton<ISettingsRepository, SettingsRepository>();

        services.AddSingleton<IHashService, HashService>();
        services.AddSingleton<ISimilarImageService, SimilarImageService>();
        services.AddSingleton<IDuplicateService, DuplicateService>();

        services.AddSingleton<ThumbnailCacheService>();
        services.AddSingleton<IThumbnailCacheService>(sp => sp.GetRequiredService<ThumbnailCacheService>());

        services.AddTransient<MainWindowViewModel>();

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
