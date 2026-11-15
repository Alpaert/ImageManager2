using CommunityToolkit.Mvvm.ComponentModel;

namespace ImageManager.App.ViewModels;

public partial class MainWindowViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSelectWholeImageSearchMode))]
    [NotifyPropertyChangedFor(nameof(ImageDropSearchHint))]
    private bool _useRegionSearchOnImageDrop;

    public bool CanSelectWholeImageSearchMode => !UseRegionSearchOnImageDrop;
    public string ImageDropSearchHint => UseRegionSearchOnImageDrop
        ? "拖入图片后框选搜索" : "拖拽图片按所选模式搜索";

    partial void OnUseRegionSearchOnImageDropChanged(bool value) =>
        AppSettings.UseRegionSearchOnImageDrop = value;

    public async Task SaveImageDropPreferenceAsync()
    {
        try { await SaveSettingsAsync(); }
        catch (Exception ex) { StatusText = $"拖入搜索设置本次已生效，但未保存：{ex.Message}"; }
    }
}
