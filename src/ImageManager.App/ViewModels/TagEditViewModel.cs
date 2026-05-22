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
    [ObservableProperty] private string _currentTagsFilterText = string.Empty;

    public ObservableCollection<TagDisplayItem> AutoTagSuggestions { get; } = new();
    public ObservableCollection<string> CurrentTags { get; } = new();
    public ObservableCollection<string> FilteredCurrentTags { get; } = new();
    public ObservableCollection<string> FavoriteTagSuggestions { get; } = new();

    public string ResultText => string.Join(", ", CurrentTags);

    partial void OnCurrentTagsFilterTextChanged(string value) => RefreshFilteredTags();

    private void RefreshFilteredTags()
    {
        var keyword = CurrentTagsFilterText ?? string.Empty;
        FilteredCurrentTags.Clear();
        foreach (var tag in CurrentTags)
        {
            if (string.IsNullOrWhiteSpace(keyword) ||
                tag.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                FilteredCurrentTags.Add(tag);
        }
    }

    private static readonly HashSet<string> ReservedTagNames = new(StringComparer.OrdinalIgnoreCase) { "a", "o", "e" };

    private string _lastAutoSuggestKeyword = string.Empty;
    private int _autoSuggestVersion;
    private CancellationTokenSource? _autoSuggestCts;

    // --- 多图模式 ---
    public bool IsMultiImageMode { get; }
    private readonly Func<string, Task>? _onAddTagToAll;
    private readonly Func<string, Task>? _onRemoveTagFromAll;
    private readonly Func<Task>? _onClearAllTags;

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
        RefreshFilteredTags();
    }

    /// <summary>多图模式构造函数：中间栏显示交集，操作应用到所有图片</summary>
    public TagEditViewModel(
        List<string> commonTags,
        List<TagCount> allTagCounts,
        IList<string> favoriteTags,
        int maxSuggestionCount,
        Func<string, Task> onAddTagToAll,
        Func<string, Task> onRemoveTagFromAll,
        Func<Task> onClearAllTags)
    {
        IsMultiImageMode = true;
        _allTagCounts = allTagCounts;
        _favoriteTagsBacking = favoriteTags;
        _onAddTagToAll = onAddTagToAll;
        _onRemoveTagFromAll = onRemoveTagFromAll;
        _onClearAllTags = onClearAllTags;

        foreach (var t in commonTags)
            CurrentTags.Add(t);

        RebuildAutoSuggestions(string.Empty);
        RebuildFavoriteSuggestions(string.Empty);
        RefreshFilteredTags();
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
    private async Task AddTag()
    {
        ErrorMessage = string.Empty;
        var name = TagInputText.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;
        if (IsReservedName(name)) return;

        if (IsMultiImageMode && _onAddTagToAll != null)
        {
            await _onAddTagToAll(name);
            if (!CurrentTags.Any(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase)))
                CurrentTags.Add(name);
        }
        else
        {
            if (!CurrentTags.Any(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase)))
                CurrentTags.Add(name);
        }

        TagInputText = string.Empty;
        RebuildAutoSuggestions(string.Empty);
        RebuildFavoriteSuggestions(string.Empty);
        RefreshFilteredTags();
    }

    public async Task<RenameResult> HandleRenameAsync(string oldName, string newName)
    {
        if (_onRenameTag == null) return RenameResult.Cancelled;
        var result = await _onRenameTag(oldName, newName);
        if (result == RenameResult.Success)
        {
            ReplaceInCurrentTags(oldName, newName);
            RebuildAutoSuggestions(_lastAutoSuggestKeyword);
            RefreshFilteredTags();
        }
        return result;
    }

    public async Task HandleMergeAsync(string oldName, string newName)
    {
        if (_onMergeTags == null) return;
        await _onMergeTags(oldName, newName);
        ReplaceInCurrentTags(oldName, newName);
        RebuildAutoSuggestions(_lastAutoSuggestKeyword);
        RefreshFilteredTags();
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
    private async Task AddSuggestedTag(string name)
    {
        if (IsMultiImageMode && _onAddTagToAll != null)
        {
            await _onAddTagToAll(name);
        }
        if (!CurrentTags.Any(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase)))
        {
            CurrentTags.Add(name);
            RefreshFilteredTags();
        }
    }

    [RelayCommand]
    private async Task AddFavoriteTag(string name)
    {
        if (IsMultiImageMode && _onAddTagToAll != null)
        {
            await _onAddTagToAll(name);
        }
        if (!CurrentTags.Any(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase)))
        {
            CurrentTags.Add(name);
            RefreshFilteredTags();
        }
    }

    [RelayCommand]
    private async Task DeleteCurrentTag(string name)
    {
        if (IsMultiImageMode && _onRemoveTagFromAll != null)
        {
            await _onRemoveTagFromAll(name);
        }
        for (int i = CurrentTags.Count - 1; i >= 0; i--)
        {
            if (string.Equals(CurrentTags[i], name, StringComparison.OrdinalIgnoreCase))
                CurrentTags.RemoveAt(i);
        }
        RefreshFilteredTags();
    }

    [RelayCommand]
    private async Task ClearAllCurrentTags()
    {
        if (IsMultiImageMode && _onClearAllTags != null)
        {
            await _onClearAllTags();
        }
        CurrentTags.Clear();
        FilteredCurrentTags.Clear();
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
        RebuildFavoriteSuggestions(keyword);
        ScheduleAutoSuggestRebuild(keyword);
    }

    private void ScheduleAutoSuggestRebuild(string keyword)
    {
        _autoSuggestCts?.Cancel();
        _autoSuggestCts = new CancellationTokenSource();
        var token = _autoSuggestCts.Token;
        var captured = keyword;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(200, token);
                if (!token.IsCancellationRequested)
                    Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => RebuildAutoSuggestions(captured));
            }
            catch (OperationCanceledException) { }
        });
    }

    private void RebuildAutoSuggestions(string keyword)
    {
        int version = Interlocked.Increment(ref _autoSuggestVersion);
        var source = _allTagCounts.ToList();
        bool showCount = ShowTagCount;
        var captured = keyword;

        _ = Task.Run(() =>
        {
            if (source.Count == 0) return;

            IEnumerable<TagCount> query = source;

            if (!string.IsNullOrWhiteSpace(captured))
            {
                query = query
                    .Where(t => t.Name.Contains(captured, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(t => t.Name.StartsWith(captured, StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(t => t.Count)
                    .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                query = query
                    .OrderByDescending(t => t.Count)
                    .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase);
            }

            var results = query
                .Take(1000)  // 限制显示数量，防止大数据量卡顿；搜索时先匹配全库再截断
                .Select(t => new TagDisplayItem { Name = t.Name, Count = t.Count, Display = showCount ? $"{t.Name} ({t.Count})" : t.Name })
                .ToList();

            Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (version != _autoSuggestVersion) return;
                AutoTagSuggestions.Clear();
                foreach (var item in results)
                    AutoTagSuggestions.Add(item);
            });
        });
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
