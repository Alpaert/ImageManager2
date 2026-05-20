using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageManager.Infrastructure.Imaging;

namespace ImageManager.App.ViewModels;

public partial class TagImageViewerViewModel : ViewModelBase
{
    private List<string> _imagePaths = new();
    private int _currentIndex;

    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private byte[]? _imageData;
    [ObservableProperty] private string _infoText = string.Empty;
    [ObservableProperty] private bool _hasPrev = true;
    [ObservableProperty] private bool _hasNext = true;

    public void Initialize(string englishTag, List<string> imagePaths)
    {
        _imagePaths = imagePaths;
        _currentIndex = 0;
        Title = $"{englishTag}";
        LoadCurrent();
    }

    [RelayCommand]
    private void Prev()
    {
        _currentIndex = (_currentIndex - 1 + _imagePaths.Count) % _imagePaths.Count;
        LoadCurrent();
    }

    [RelayCommand]
    private void Next()
    {
        _currentIndex = (_currentIndex + 1) % _imagePaths.Count;
        LoadCurrent();
    }

    public void Navigate(int delta)
    {
        if (delta < 0) PrevCommand.Execute(null);
        else NextCommand.Execute(null);
    }

    private void LoadCurrent()
    {
        if (_imagePaths.Count == 0) return;
        try
        {
            var path = _imagePaths[_currentIndex];
            var data = ThumbnailGenerator.Generate(path, 200);
            ImageData = data;
            InfoText = $"第 {_currentIndex + 1}/{_imagePaths.Count} 张 — {System.IO.Path.GetFileName(path)}";
        }
        catch
        {
            ImageData = null;
            InfoText = "加载失败";
        }
    }
}
