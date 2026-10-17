using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ImageManager.Core.Models;

namespace ImageManager.Core.Services;

public interface ITagSearchEngine : IDisposable
{
    bool IsSuggestionCoTagMode { get; }
    List<string> SearchResultFiles { get; set; }
    List<TagCount> AllTagCounts { get; set; }

    void SetAllTagCounts(List<TagCount> counts);
    IReadOnlyList<TagCount> GetTypedSuggestions(string keyword);
    Task<IReadOnlyList<TagCount>> GetCoTagSuggestionsAsync(
        IReadOnlyCollection<string> filePaths, string expression, string? nameFilter = null);
    void ClearSearchResults();
    int GetCoTagState(string tagName);
    void NotifySearchResultFilesChanged();
    void UpdateSuggestions(string keyword, Action<TagCount> addSuggestion, Action<bool> setPopupOpen);
    void OnTextChanged(string value, string currentTagSearchText,
        Action onBorderColorChanged, Action<string> clearAndUpdateSuggestions);
    void OnGotFocus(string currentText,
        Action<bool> setPopupOpen,
        Func<List<TagCount>> getSuggestions);
    Task SearchByTagAsync(string raw, List<string> allFiles, bool alreadyShowingSearch,
        Action<List<TagCount>> setSuggestions);
    void SelectSuggestion(TagCount tag, string currentText, Action<string> setTagSearchText, Action<bool> setPopupOpen,
        Func<Task> triggerSearch);
    string SearchBoxBorderColor(string text);
    Task SearchCoTagsAsync(string keyword);
    Task RefreshCurrentCoTagSuggestionsAsync();
    Task<IReadOnlyList<TagCount>> QueryCurrentCoTagSuggestionsAsync(
        string keyword,
        CancellationToken cancellationToken = default);
}
