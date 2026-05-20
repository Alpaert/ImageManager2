using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageManager.App.Services;

namespace ImageManager.App.ViewModels;

public partial class AutoTagReviewViewModel : ViewModelBase
{
    private readonly AutoTagController _controller;

    [ObservableProperty] private ObservableCollection<TagTranslationItem> _items = new();
    [ObservableProperty] private int _confirmedCount;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _isSingleImageMode;
    public string SingleImagePath { get; set; } = string.Empty;

    public AutoTagReviewViewModel(AutoTagController controller, List<TagTranslationItem> items,
        bool isSingleImageMode = false, string singleImagePath = "")
    {
        _controller = controller;
        IsSingleImageMode = isSingleImageMode;
        SingleImagePath = singleImagePath;
        Items = new ObservableCollection<TagTranslationItem>(items);
        TotalCount = items.Count;
        ConfirmedCount = items.Count(i => i.IsConfirmed);
        UpdateStatus();
    }

    [RelayCommand]
    private async Task Confirm(TagTranslationItem item)
    {
        if (item.IsConfirmed) return;
        if (IsSingleImageMode)
        {
            item.IsConfirmed = true;
        }
        else
        {
            await _controller.ConfirmTagAsync(item);
        }
        ConfirmedCount = Items.Count(i => i.IsConfirmed);
        UpdateStatus();
    }

    [RelayCommand]
    private async Task ConfirmAll()
    {
        var unconfirmed = Items.Where(i => !i.IsConfirmed).ToList();
        foreach (var item in unconfirmed)
        {
            if (string.IsNullOrWhiteSpace(item.UserEditedText))
                item.UserEditedText = item.ChineseTranslation;
            await Confirm(item);
        }
    }

    [RelayCommand]
    private async Task SaveToImage()
    {
        if (!IsSingleImageMode || string.IsNullOrEmpty(SingleImagePath)) return;
        // Save mappings for confirmed items, then write to image
        var items = Items.ToList();
        await _controller.SaveMappingsAndTagsAsync(SingleImagePath, items);
        StatusText = "已保存到图片";
    }

    [RelayCommand]
    private async Task SaveMappings()
    {
        if (!IsSingleImageMode) return;
        // Save ALL edited translations to TagMapping (for future reuse)
        var items = Items.ToList();
        await _controller.SaveMappingsOnlyAsync(items);
        StatusText = "映射已保存";
    }

    [RelayCommand]
    private async Task SaveDraft()
    {
        await _controller.SaveDraftAsync(Items.ToList());
        StatusText = "翻译草稿已保存，下次打开此文件夹可继续编辑";
    }

    [RelayCommand]
    private async Task Delete(TagTranslationItem item)
    {
        if (!IsSingleImageMode)
            await _controller.DeleteTagAsync(item);
        Items.Remove(item);
        TotalCount = Items.Count;
        ConfirmedCount = Items.Count(i => i.IsConfirmed);
        UpdateStatus();
    }

    [RelayCommand]
    private void StartEdit(TagTranslationItem item)
    {
        item.UserEditedText ??= item.ChineseTranslation;
        item.IsEditing = true;
        // Editing resets confirmed status — new translation needs re-confirmation
        item.IsConfirmed = false;
        ConfirmedCount = Items.Count(i => i.IsConfirmed);
        UpdateStatus();
    }

    [RelayCommand]
    private void CommitEdit(TagTranslationItem item)
    {
        item.IsEditing = false;
    }

    public async Task<List<string>> GetImagesForTag(string englishTag)
    {
        if (IsSingleImageMode)
            return string.IsNullOrEmpty(SingleImagePath) ? new List<string>() : new List<string> { SingleImagePath };
        return await _controller.GetImagesWithTagAsync(englishTag);
    }

    public async Task MarkDoneAsync()
    {
        await _controller.MarkFolderDoneAsync(0);
    }

    private void UpdateStatus()
    {
        StatusText = $"已确认 {ConfirmedCount}/{TotalCount}";
    }
}
