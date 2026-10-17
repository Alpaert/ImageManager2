using System;
using System.IO;
using System.Linq;
using System.Reflection;
using ImageManager.App;
using ImageManager.App.Services;
using ImageManager.Core.Models;
using ImageManager.Core.Services;
using ImageManager.Infrastructure.Data.Repositories;
using ImageManager.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ContinuousDisplay.UnitTests;

public sealed class ArchitectureBoundaryTests
{
    [Fact]
    public void CoreAssembly_HasNoForbiddenDependencies()
    {
        var coreAssembly = typeof(ImageMeta).Assembly;
        var referencedAssemblies = coreAssembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToList();

        Assert.DoesNotContain(referencedAssemblies, name => name.StartsWith("ImageManager.App", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(referencedAssemblies, name => name.StartsWith("ImageManager.Infrastructure", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(referencedAssemblies, name => name.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(referencedAssemblies, name => name.StartsWith("Microsoft.Data.Sqlite", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void InfrastructureAssembly_HasNoForbiddenDependencies()
    {
        var infraAssembly = typeof(SimilarImageService).Assembly;
        var referencedAssemblies = infraAssembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToList();

        Assert.DoesNotContain(referencedAssemblies, name => name.StartsWith("ImageManager.App", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(referencedAssemblies, name => name.StartsWith("Avalonia.Controls", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ViewCodeBehind_HasNoServiceLocatorOrDirectSql()
    {
        var baseDir = AppContext.BaseDirectory;
        // Search upwards to find MainWindow.axaml.cs
        var current = new DirectoryInfo(baseDir);
        string? viewFile = null;
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, "src", "ImageManager.App", "Views", "MainWindow.axaml.cs");
            if (File.Exists(candidate))
            {
                viewFile = candidate;
                break;
            }
            current = current.Parent;
        }

        Assert.NotNull(viewFile);
        var content = File.ReadAllText(viewFile!);

        Assert.DoesNotContain("GetRequiredService", content);
        Assert.DoesNotContain("GetService", content);
        Assert.DoesNotContain("Microsoft.Data.Sqlite", content);
        Assert.DoesNotContain("SqliteConnection", content);
    }

    [Fact]
    public void CompositeRepository_SubInterfaces_AreSegregatedAndImplemented()
    {
        var compositeType = typeof(IImageMetaRepository);
        var implType = typeof(ImageMetaRepository);

        Assert.True(typeof(IImageMetaReadRepository).IsAssignableFrom(compositeType), "IImageMetaRepository must inherit IImageMetaReadRepository");
        Assert.True(typeof(IImageMetaWriteRepository).IsAssignableFrom(compositeType), "IImageMetaRepository must inherit IImageMetaWriteRepository");
        Assert.True(typeof(IImageTagRepository).IsAssignableFrom(compositeType), "IImageMetaRepository must inherit IImageTagRepository");
        Assert.True(typeof(IImageHashRepository).IsAssignableFrom(compositeType), "IImageMetaRepository must inherit IImageHashRepository");

        Assert.True(typeof(IImageMetaReadRepository).IsAssignableFrom(implType), "ImageMetaRepository must implement IImageMetaReadRepository");
        Assert.True(typeof(IImageMetaWriteRepository).IsAssignableFrom(implType), "ImageMetaRepository must implement IImageMetaWriteRepository");
        Assert.True(typeof(IImageTagRepository).IsAssignableFrom(implType), "ImageMetaRepository must implement IImageTagRepository");
        Assert.True(typeof(IImageHashRepository).IsAssignableFrom(implType), "ImageMetaRepository must implement IImageHashRepository");
    }

    [Fact]
    public void DecomposedClasses_ExistAndAreProperlyStructured()
    {
        // 5.2 SimilarImageService decomposition
        var infraAssembly = typeof(SimilarImageService).Assembly;
        var scorerType = infraAssembly.GetType("ImageManager.Infrastructure.Services.SimilarImageScorer");
        var snapshotManagerType = infraAssembly.GetType("ImageManager.Infrastructure.Services.SimilaritySnapshotManager");
        Assert.NotNull(scorerType);
        Assert.NotNull(snapshotManagerType);

        // 5.3 PageManager decomposition
        var pageCacheType = typeof(PageCache);
        var schedulerType = typeof(ThumbnailScheduler);
        Assert.NotNull(pageCacheType);
        Assert.NotNull(schedulerType);
    }

    [Fact]
    public void DiContainer_ResolvesRepositorySubInterfacesToSameInstance()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ImageManager_ArchTest_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var provider = App.ConfigureServices(tempDir);

            var metaRepo = provider.GetRequiredService<IImageMetaRepository>();
            var readRepo = provider.GetRequiredService<IImageMetaReadRepository>();
            var writeRepo = provider.GetRequiredService<IImageMetaWriteRepository>();
            var tagRepo = provider.GetRequiredService<IImageTagRepository>();
            var hashRepo = provider.GetRequiredService<IImageHashRepository>();

            Assert.NotNull(metaRepo);
            Assert.NotNull(readRepo);
            Assert.NotNull(writeRepo);
            Assert.NotNull(tagRepo);
            Assert.NotNull(hashRepo);

            Assert.Same(metaRepo, readRepo);
            Assert.Same(metaRepo, writeRepo);
            Assert.Same(metaRepo, tagRepo);
            Assert.Same(metaRepo, hashRepo);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Ignore transient test temp directory cleanup
            }
        }
    }

    [Fact]
    public void ViewModels_HaveNoServiceLocatorCalls()
    {
        var baseDir = AppContext.BaseDirectory;
        var current = new DirectoryInfo(baseDir);
        string? vmDir = null;
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, "src", "ImageManager.App", "ViewModels");
            if (Directory.Exists(candidate))
            {
                vmDir = candidate;
                break;
            }
            current = current.Parent;
        }

        Assert.NotNull(vmDir);
        var csFiles = Directory.GetFiles(vmDir!, "*.cs", SearchOption.AllDirectories);
        Assert.NotEmpty(csFiles);

        foreach (var file in csFiles)
        {
            var content = File.ReadAllText(file);
            Assert.False(content.Contains("App.Services.") || content.Contains("App.Services?"),
                $"ViewModel {Path.GetFileName(file)} must not use App.Services locator!");
            Assert.False(content.Contains("GetService<"), $"ViewModel {Path.GetFileName(file)} must not call GetService<T>!");
            Assert.False(content.Contains("GetRequiredService<"), $"ViewModel {Path.GetFileName(file)} must not call GetRequiredService<T>!");
        }
    }

    [Fact]
    public void MainWindowViewModel_HasNoInfrastructureTypeReferences()
    {
        var baseDir = AppContext.BaseDirectory;
        var current = new DirectoryInfo(baseDir);
        string? vmDir = null;
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, "src", "ImageManager.App", "ViewModels");
            if (Directory.Exists(candidate))
            {
                vmDir = candidate;
                break;
            }
            current = current.Parent;
        }

        Assert.NotNull(vmDir);
        var mwFiles = Directory.GetFiles(vmDir!, "MainWindowViewModel*.cs", SearchOption.TopDirectoryOnly);
        Assert.NotEmpty(mwFiles);

        foreach (var file in mwFiles)
        {
            var content = File.ReadAllText(file);
            Assert.False(content.Contains("using ImageManager.Infrastructure"),
                $"{Path.GetFileName(file)} must not have using ImageManager.Infrastructure!");
            Assert.False(content.Contains("ImageManager.Infrastructure."),
                $"{Path.GetFileName(file)} must not reference ImageManager.Infrastructure types directly!");
        }
    }

    [Fact]
    public void DiContainer_ResolvesAllNewCoreInterfacesAndMainWindowViewModel()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ImageManager_ArchDiTest_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var provider = App.ConfigureServices(tempDir);

            // Verify Core interfaces segregated from Infrastructure are resolvable
            Assert.NotNull(provider.GetRequiredService<IArtistEmbeddingStore>());
            Assert.NotNull(provider.GetRequiredService<ICharacterEmbeddingStore>());
            Assert.NotNull(provider.GetRequiredService<IChineseTagLibrary>());
            Assert.NotNull(provider.GetRequiredService<ITagSearchEngine>());
            Assert.NotNull(provider.GetRequiredService<IAutoTagOrchestrator>());
            Assert.NotNull(provider.GetRequiredService<IVideoService>());
            Assert.NotNull(provider.GetRequiredService<IVideoOriginalFrameCacheService>());
            Assert.NotNull(provider.GetRequiredService<IChineseClipService>());
            Assert.NotNull(provider.GetRequiredService<IHashService>());

            // Verify MainWindowViewModel resolves cleanly with all its dependencies
            var mainVm = provider.GetRequiredService<ImageManager.App.ViewModels.MainWindowViewModel>();
            Assert.NotNull(mainVm);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch { }
        }
    }
}
