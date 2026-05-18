using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageManager.Core.Models;
using ImageManager.Core.Services;

namespace ImageManager.App.ViewModels;

public class TagDisplayItem
{
    public string Name { get; init; } = string.Empty;
    public int Count { get; init; }
    public string Display { get; set; } = string.Empty;
}

public partial class TagEditViewModel : ViewModelBase
{
    private readonly List<TagCount> _allTagCounts;
    private readonly IList<string> _favoriteTagsBacking;
    private readonly Func<string, string, Task<RenameResult>>? _onRenameTag;
    private readonly Func<string, string, Task>? _onMergeTags;

    [ObservableProperty] private string _tagInputText = string.Empty;
    [ObservableProperty] private string _newFavoriteText = string.Empty;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private bool _showTagCount = true;

    public ObservableCollection<TagDisplayItem> AutoTagSuggestions { get; } = new();
    public ObservableCollection<string> CurrentTags { get; } = new();
    public ObservableCollection<string> FavoriteTagSuggestions { get; } = new();

    public string ResultText => string.Join(", ", CurrentTags);

    private static readonly HashSet<string> ReservedTagNames = new(StringComparer.OrdinalIgnoreCase) { "a", "o", "e" };

    private string _lastAutoSuggestKeyword = string.Empty;

    public TagEditViewModel(
        string currentTagsText,
        List<TagCount> allTagCounts,
        IList<string> favoriteTags,
        int maxSuggestionCount,
        Func<string, string, Task<RenameResult>>? onRenameTag = null,
        Func<string, string, Task>? onMergeTags = null)
    {
        _allTagCounts = allTagCounts;
        _favoriteTagsBacking = favoriteTags;
        _onRenameTag = onRenameTag;
        _onMergeTags = onMergeTags;

        if (!string.IsNullOrWhiteSpace(currentTagsText))
        {
            var parts = currentTagsText
                .Split(',')
                .Select(p => p.Trim())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var p in parts)
                CurrentTags.Add(p);
        }

        RebuildAutoSuggestions(string.Empty);
        RebuildFavoriteSuggestions(string.Empty);
    }

    private bool IsReservedName(string name)
    {
        if (ReservedTagNames.Contains(name))
        {
            ErrorMessage = $"Tag 名称 \"{name}\" 是搜索关键字，不能用作 Tag 名，请改用其他名称";
            return true;
        }
        return false;
    }

    [RelayCommand]
    private void AddTag()
    {
        ErrorMessage = string.Empty;
        var name = TagInputText.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;

        if (IsReservedName(name)) return;

        if (!CurrentTags.Any(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase)))
            CurrentTags.Add(name);

        TagInputText = string.Empty;
        RebuildAutoSuggestions(string.Empty);
        RebuildFavoriteSuggestions(string.Empty);
    }

    public async Task<RenameResult> HandleRenameAsync(string oldName, string newName)
    {
        if (_onRenameTag == null) return RenameResult.Cancelled;
        var result = await _onRenameTag(oldName, newName);
        if (result == RenameResult.Success)
        {
            ReplaceInCurrentTags(oldName, newName);
            RebuildAutoSuggestions(_lastAutoSuggestKeyword);
        }
        return result;
    }

    public async Task HandleMergeAsync(string oldName, string newName)
    {
        if (_onMergeTags == null) return;
        await _onMergeTags(oldName, newName);
        ReplaceInCurrentTags(oldName, newName);
        RebuildAutoSuggestions(_lastAutoSuggestKeyword);
    }

    private void ReplaceInCurrentTags(string oldName, string newName)
    {
        for (int i = CurrentTags.Count - 1; i >= 0; i--)
        {
            if (!string.Equals(CurrentTags[i], oldName, StringComparison.OrdinalIgnoreCase)) continue;
            if (!CurrentTags.Contains(newName, StringComparer.OrdinalIgnoreCase))
                CurrentTags[i] = newName;
            else
                CurrentTags.RemoveAt(i);
        }
    }

    [RelayCommand]
    private void AddSuggestedTag(string name)
    {
        if (!CurrentTags.Any(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase)))
            CurrentTags.Add(name);
    }

    [RelayCommand]
    private void AddFavoriteTag(string name)
    {
        if (!CurrentTags.Any(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase)))
            CurrentTags.Add(name);
    }

    [RelayCommand]
    private void DeleteCurrentTag(string name)
    {
        for (int i = CurrentTags.Count - 1; i >= 0; i--)
        {
            if (string.Equals(CurrentTags[i], name, StringComparison.OrdinalIgnoreCase))
                CurrentTags.RemoveAt(i);
        }
    }

    [RelayCommand]
    private void AddFavorite()
    {
        ErrorMessage = string.Empty;
        var name = NewFavoriteText.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;

        if (IsReservedName(name)) return;

        if (!_favoriteTagsBacking.Any(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase)))
            _favoriteTagsBacking.Add(name);

        NewFavoriteText = string.Empty;
        RebuildFavoriteSuggestions(string.Empty);
    }

    [RelayCommand]
    private void DeleteFavorite(string name)
    {
        for (int i = _favoriteTagsBacking.Count - 1; i >= 0; i--)
        {
            if (string.Equals(_favoriteTagsBacking[i], name, StringComparison.OrdinalIgnoreCase))
                _favoriteTagsBacking.RemoveAt(i);
        }
        RebuildFavoriteSuggestions(string.Empty);
    }

    partial void OnShowTagCountChanged(bool value)
    {
        RebuildAutoSuggestions(_lastAutoSuggestKeyword);
    }

    partial void OnTagInputTextChanged(string value)
    {
        var keyword = value.Trim();
        _lastAutoSuggestKeyword = keyword;
        RebuildAutoSuggestions(keyword);
        RebuildFavoriteSuggestions(keyword);
    }

    private void RebuildAutoSuggestions(string keyword)
    {
        AutoTagSuggestions.Clear();
        if (_allTagCounts.Count == 0) return;

        IEnumerable<TagCount> query = _allTagCounts;

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            query = query
                .Where(t => t.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(t => t.Name.StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(t => t.Count)
                .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            query = query
                .OrderByDescending(t => t.Count)
                .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase);
        }

        foreach (var t in query)
            AutoTagSuggestions.Add(new TagDisplayItem { Name = t.Name, Count = t.Count, Display = FormatTagDisplay(t.Name, t.Count) });
    }

    private string FormatTagDisplay(string name, int count)
        => ShowTagCount ? $"{name} ({count})" : name;

    private void RebuildFavoriteSuggestions(string keyword)
    {
        FavoriteTagSuggestions.Clear();
        if (_favoriteTagsBacking == null) return;

        IEnumerable<string> query = _favoriteTagsBacking
            .Select(t => t?.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            query = query
                .Where(t => t!.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(t => t!.StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
                .ThenBy(t => t, StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            query = query.OrderBy(t => t, StringComparer.OrdinalIgnoreCase);
        }

        foreach (var t in query)
            FavoriteTagSuggestions.Add(t!);
    }
}
