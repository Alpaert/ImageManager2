using CommunityToolkit.Mvvm.ComponentModel;

namespace ImageManager.App.ViewModels;

public partial class TagTranslationItem : ObservableObject
{
    public string EnglishTag { get; set; } = string.Empty;
    public string ChineseTranslation { get; set; } = string.Empty;

    [ObservableProperty] private string? _userEditedText;
    [ObservableProperty] private bool _isConfirmed;
    [ObservableProperty] private bool _isExistingMapping;
    [ObservableProperty] private bool _isEditing;

    public int ImageCount { get; set; }

    public string DisplayText => UserEditedText ?? ChineseTranslation;
    public string StatusText => IsExistingMapping ? "已映射" : IsConfirmed ? "已确认" : "待确认";

    partial void OnUserEditedTextChanged(string? value)
    {
        OnPropertyChanged(nameof(DisplayText));
    }
}
