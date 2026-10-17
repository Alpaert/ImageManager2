using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageManager.Core.Models;
using ImageManager.Core.Services;

namespace ImageManager.App.ViewModels;

public partial class ArtistDbBuilderViewModel : ViewModelBase
{
    private readonly ILibraryBuilderService? _libraryBuilderService;
    private CancellationTokenSource? _buildCts;

    [ObservableProperty] private LibraryBuildTarget _target = LibraryBuildTarget.Artist;
    [ObservableProperty] private string _referenceDir = string.Empty;
    [ObservableProperty] private string _statusText = "就绪";
    [ObservableProperty] private string _referenceTitle = "参考图目录";
    [ObservableProperty] private string _referenceDescription = "每个子文件夹 = 一位画师，文件夹名 = 画师名，文件夹内为该画师的参考图";
    [ObservableProperty] private string _libraryTitle = "当前画师库";
    [ObservableProperty] private string _meanEmbeddingHint = "每位画师取所有参考图的嵌入均值";
    [ObservableProperty] private string _recommendedCountHint = "建议每位画师 5-20 张代表作";
    [ObservableProperty] private string _incrementalHint = "新画师可随时添加，自动与已有库合并";
    [ObservableProperty] private int _progress;
    [ObservableProperty] private int _total;
    [ObservableProperty] private bool _isRunning;

    public Action<string>? OnSelectFolder { get; set; }
    public Func<string, Task>? OnBuildAsync { get; set; }

    public ArtistDbBuilderViewModel(ILibraryBuilderService? libraryBuilderService = null)
    {
        _libraryBuilderService = libraryBuilderService;
    }

    partial void OnTargetChanged(LibraryBuildTarget value)
    {
        if (value == LibraryBuildTarget.Character)
        {
            ReferenceTitle = "参考图目录";
            ReferenceDescription = "每个子文件夹 = 一个角色，文件夹名 = 角色名，文件夹内为该角色的参考图";
            LibraryTitle = "当前角色库";
            MeanEmbeddingHint = "每个角色取所有参考图的嵌入均值";
            RecommendedCountHint = "建议每个角色 20-50 张代表图";
            IncrementalHint = "新角色可随时添加，自动与已有库合并";
        }
        else
        {
            ReferenceTitle = "参考图目录";
            ReferenceDescription = "每个子文件夹 = 一位画师，文件夹名 = 画师名，文件夹内为该画师的参考图";
            LibraryTitle = "当前画师库";
            MeanEmbeddingHint = "每位画师取所有参考图的嵌入均值";
            RecommendedCountHint = "建议每位画师 5-20 张代表作";
            IncrementalHint = "新画师可随时添加，自动与已有库合并";
        }
    }

    [RelayCommand]
    private void SelectFolder()
    {
        if (IsRunning) return;
        OnSelectFolder?.Invoke(ReferenceDir);
    }

    [RelayCommand]
    public void Cancel()
    {
        try
        {
            _buildCts?.Cancel();
        }
        catch { }
    }

    [RelayCommand]
    private async Task BuildAsync()
    {
        if (IsRunning || string.IsNullOrWhiteSpace(ReferenceDir)) return;

        var builderService = _libraryBuilderService;
        if (builderService == null)
        {
            if (OnBuildAsync != null)
            {
                IsRunning = true;
                try
                {
                    await OnBuildAsync(ReferenceDir);
                }
                catch (Exception ex)
                {
                    StatusText = $"错误: {ex.Message}";
                }
                finally { IsRunning = false; }
                return;
            }

            StatusText = "错误: 建库服务不可用";
            return;
        }

        _buildCts?.Cancel();
        _buildCts?.Dispose();
        _buildCts = new CancellationTokenSource();
        var ct = _buildCts.Token;

        IsRunning = true;
        Progress = 0;
        Total = 0;
        StatusText = "准备建库...";

        var progress = new Progress<LibraryBuildProgress>(p =>
        {
            Progress = p.CurrentIndex;
            Total = p.TotalCount;
            var itemDesc = string.IsNullOrEmpty(p.CurrentItemName) ? string.Empty : $": {p.CurrentItemName}";
            StatusText = $"{p.Stage} ({p.CurrentIndex}/{p.TotalCount}){itemDesc}";
        });

        try
        {
            var result = await builderService.BuildLibraryAsync(
                new LibraryBuildRequest(ReferenceDir, Target),
                progress,
                ct);

            if (result.Cancelled)
            {
                StatusText = "已取消构建";
            }
            else if (!string.IsNullOrEmpty(result.ErrorMessage))
            {
                StatusText = $"构建失败: {result.ErrorMessage}";
            }
            else
            {
                StatusText = $"完成！跳过 {result.SkippedCount} / 新增+更新 {result.BuiltCount}，共 {result.TotalInStore} 项";
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消构建";
        }
        catch (Exception ex)
        {
            StatusText = $"错误: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
            _buildCts?.Dispose();
            _buildCts = null;
        }
    }

    public void ReportProgress(int done, int total, string artist)
    {
        Progress = done;
        Total = total;
        StatusText = $"处理中 ({done}/{total}): {artist}";
    }
}
