using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ImageManager.App.ViewModels;

public partial class ArtistDbBuilderViewModel : ViewModelBase
{
    [ObservableProperty] private string _referenceDir = string.Empty;
    [ObservableProperty] private string _statusText = "就绪";
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
