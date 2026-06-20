using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ImageManager.App.ViewModels;

public partial class ArtistDbBuilderViewModel : ViewModelBase
{
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

    [RelayCommand]
    private void SelectFolder()
    {
        if (IsRunning) return;
        OnSelectFolder?.Invoke(ReferenceDir);
    }

    [RelayCommand]
    private async Task BuildAsync()
    {
        if (IsRunning || string.IsNullOrWhiteSpace(ReferenceDir)) return;
        IsRunning = true;
        try
        {
            if (OnBuildAsync != null)
                await OnBuildAsync(ReferenceDir);
        }
        catch (Exception ex)
        {
            StatusText = $"错误: {ex.Message}";
        }
        finally { IsRunning = false; }
    }

    public void ReportProgress(int done, int total, string artist)
    {
        Progress = done;
        Total = total;
        StatusText = $"处理中 ({done}/{total}): {artist}";
    }
}
