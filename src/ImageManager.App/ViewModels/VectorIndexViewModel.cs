using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageManager.Core.Models;
using ImageManager.Core.Services;
using ImageManager.Infrastructure.Services;

namespace ImageManager.App.ViewModels;

public partial class VectorIndexViewModel : ViewModelBase
{
    private readonly IVectorIndexService _indexService;

    [ObservableProperty] private string _semanticStatus = "正在读取...";
    [ObservableProperty] private string _atmosphereStatus = "正在读取...";
    [ObservableProperty] private string _colorStatus = "正在读取...";
    [ObservableProperty] private string _progressText = "空闲";
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private double _progressMaximum = 1;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private string? _selectedFolderPath;
    [ObservableProperty] private bool _includeSubfolders = true;

    public VectorIndexViewModel(IVectorIndexService indexService, ChineseClipService chineseClip)
    {
        _indexService = indexService;
        ModelDirectory = chineseClip.ModelDirectory;
    }

    public string ModelDirectory { get; }
    public string SelectedFolderDisplay => string.IsNullOrWhiteSpace(SelectedFolderPath)
        ? "全部图库"
        : SelectedFolderPath;
    public string ScopeDescription => string.IsNullOrWhiteSpace(SelectedFolderPath)
        ? "当前范围：全部图库"
        : $"当前范围：{SelectedFolderPath}{(IncludeSubfolders ? "（包含子文件夹）" : "（仅直属图片）")}";
    public Func<Task<string?>>? FolderPicker { get; set; }

    public async Task InitializeAsync() => await RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var statuses = await _indexService.GetStatusesAsync(CurrentScope);
        var semantic = statuses.First(status => status.Kind == VectorIndexKind.Semantic);
        var atmosphere = statuses.First(status => status.Kind == VectorIndexKind.Atmosphere);
        var color = statuses.First(status => status.Kind == VectorIndexKind.Color);
        SemanticStatus = FormatStatus(semantic);
        AtmosphereStatus = FormatStatus(atmosphere);
        ColorStatus = FormatStatus(color);
    }

    [RelayCommand] private Task BuildSemanticAsync() => RunAsync(VectorIndexKind.Semantic, false);
    [RelayCommand] private Task RebuildSemanticAsync() => RunAsync(VectorIndexKind.Semantic, true);
    [RelayCommand] private Task BuildAtmosphereAsync() => RunAsync(VectorIndexKind.Atmosphere, false);
    [RelayCommand] private Task RebuildAtmosphereAsync() => RunAsync(VectorIndexKind.Atmosphere, true);
    [RelayCommand] private Task BuildColorAsync() => RunAsync(VectorIndexKind.Color, false);
    [RelayCommand] private Task RebuildColorAsync() => RunAsync(VectorIndexKind.Color, true);

    [RelayCommand]
    private async Task SelectFolderAsync()
    {
        if (IsRunning || FolderPicker == null)
            return;
        var path = await FolderPicker();
        if (string.IsNullOrWhiteSpace(path))
            return;
        SelectedFolderPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task UseAllImagesAsync()
    {
        if (IsRunning)
            return;
        SelectedFolderPath = null;
        await RefreshAsync();
    }

    [RelayCommand]
    private void Pause()
    {
        _indexService.Pause();
        IsPaused = true;
        ProgressText = "已暂停";
    }

    [RelayCommand]
    private void Resume()
    {
        _indexService.Resume();
        IsPaused = false;
        ProgressText = "继续处理中...";
    }

    [RelayCommand]
    private void Cancel() => _indexService.Cancel();

    private async Task RunAsync(VectorIndexKind kind, bool rebuild)
    {
        if (IsRunning)
            return;
        IsRunning = true;
        IsPaused = false;
        ProgressValue = 0;
        ProgressMaximum = 1;
        ProgressText = rebuild ? $"正在重建 {KindName(kind)} 索引..." : $"正在增量生成 {KindName(kind)} 索引...";
        var progress = new Progress<VectorIndexProgress>(UpdateProgress);
        try
        {
            await _indexService.BuildAsync(kind, rebuild, CurrentScope, progress);
            ProgressText = $"{KindName(kind)} 索引处理完成";
        }
        catch (OperationCanceledException)
        {
            ProgressText = "索引任务已取消，已完成部分会保留";
        }
        catch (Exception ex)
        {
            ProgressText = $"索引任务失败: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
            IsPaused = false;
            await RefreshAsync();
        }
    }

    private void UpdateProgress(VectorIndexProgress progress)
    {
        ProgressMaximum = Math.Max(1, progress.Total);
        ProgressValue = progress.Processed + progress.Skipped;
        var file = string.IsNullOrWhiteSpace(progress.CurrentFile)
            ? string.Empty
            : $" | {Path.GetFileName(progress.CurrentFile)}";
        ProgressText = $"{KindName(progress.Kind)}: {progress.Processed + progress.Skipped}/{progress.Total} " +
                       $"新增 {progress.Generated}，跳过 {progress.Skipped}，失败 {progress.Failed}{file}";
    }

    private static string FormatStatus(VectorIndexStatus status) => status.TotalImages == 0
        ? "当前范围没有已收录图片"
        : $"有效 {status.IndexedImages:N0} / 图片 {status.TotalImages:N0}，缺失或过期 {status.MissingOrStaleImages:N0}";

    private static string KindName(VectorIndexKind kind) => kind switch
    {
        VectorIndexKind.Semantic => "Chinese-CLIP",
        VectorIndexKind.Atmosphere => "氛围",
        VectorIndexKind.Color => "颜色",
        _ => kind.ToString()
    };

    partial void OnSelectedFolderPathChanged(string? value)
    {
        OnPropertyChanged(nameof(SelectedFolderDisplay));
        OnPropertyChanged(nameof(ScopeDescription));
    }

    partial void OnIncludeSubfoldersChanged(bool value)
    {
        OnPropertyChanged(nameof(ScopeDescription));
        if (!IsRunning && !string.IsNullOrWhiteSpace(SelectedFolderPath))
            _ = RefreshAsync();
    }

    private VectorIndexScope CurrentScope => string.IsNullOrWhiteSpace(SelectedFolderPath)
        ? VectorIndexScope.All
        : new VectorIndexScope(SelectedFolderPath, IncludeSubfolders);
}
