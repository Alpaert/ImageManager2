using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageManager.Core.Models;
using ImageManager.Infrastructure.Services;

namespace ImageManager.App.ViewModels;

public partial class AutoTagReviewViewModel : ViewModelBase
{
    private readonly AutoTagOrchestrator _orchestrator;

    [ObservableProperty] private ObservableCollection<TagTranslationItem> _items = new();
    [ObservableProperty] private int _confirmedCount;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _isSingleImageMode;
    public string SingleImagePath { get; set; } = string.Empty;

    public AutoTagReviewViewModel(AutoTagOrchestrator orchestrator, List<TagTranslationDto> dtos,
        bool isSingleImageMode = false, string singleImagePath = "")
    {
        _orchestrator = orchestrator;
        IsSingleImageMode = isSingleImageMode;
        SingleImagePath = singleImagePath;
        Items = new ObservableCollection<TagTranslationItem>(
            dtos.Select(MapToViewModel));
        TotalCount = Items.Count;
        ConfirmedCount = Items.Count(i => i.IsConfirmed);
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
            var chineseName = item.UserEditedText ?? item.ChineseTranslation;
            await _orchestrator.ConfirmTagAsync(item.EnglishTag, chineseName);
            item.IsConfirmed = true;
        }
        ConfirmedCount++;
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
        var dtos = Items.Select(MapToDto).ToList();
        await _orchestrator.SaveMappingsAndTagsAsync(SingleImagePath, dtos);
        StatusText = "已保存到图片";
    }

    [RelayCommand]
    private async Task SaveMappings()
    {
        if (!IsSingleImageMode) return;
        var dtos = Items.Select(MapToDto).ToList();
        await _orchestrator.SaveMappingsOnlyAsync(dtos);
        StatusText = "映射已保存";
    }

    [RelayCommand]
    private async Task SaveDraft()
    {
        var dtos = Items.Select(MapToDto).ToList();
        await _orchestrator.SaveDraftAsync(dtos);
        StatusText = "翻译草稿已保存，下次打开此文件夹可继续编辑";
    }

    [RelayCommand]
    private async Task Delete(TagTranslationItem item)
    {
        if (!IsSingleImageMode)
            await _orchestrator.DeleteTagAsync(item.EnglishTag);
        Items.Remove(item);
        TotalCount = Items.Count;
        if (item.IsConfirmed) ConfirmedCount = Math.Max(0, ConfirmedCount - 1);
        UpdateStatus();
    }

    [RelayCommand]
    private void StartEdit(TagTranslationItem item)
    {
        item.UserEditedText ??= item.ChineseTranslation;
        item.IsEditing = true;
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
        return await _orchestrator.GetImagesWithTagAsync(englishTag);
    }

    public async Task MarkDoneAsync()
    {
        await _orchestrator.MarkFolderDoneAsync(0);
    }

    private void UpdateStatus()
    {
        StatusText = $"已确认 {ConfirmedCount}/{TotalCount}";
    }

    private static TagTranslationItem MapToViewModel(TagTranslationDto dto) => new()
    {
        EnglishTag = dto.EnglishTag,
        ChineseTranslation = dto.ChineseTranslation,
        UserEditedText = dto.UserEditedText,
        IsConfirmed = dto.IsConfirmed,
        IsExistingMapping = dto.IsExistingMapping,
        ImageCount = dto.ImageCount
    };

    private static TagTranslationDto MapToDto(TagTranslationItem item) => new()
    {
        EnglishTag = item.EnglishTag,
        ChineseTranslation = item.ChineseTranslation,
        UserEditedText = item.UserEditedText,
        IsConfirmed = item.IsConfirmed,
        IsExistingMapping = item.IsExistingMapping,
        ImageCount = item.ImageCount
    };
}
