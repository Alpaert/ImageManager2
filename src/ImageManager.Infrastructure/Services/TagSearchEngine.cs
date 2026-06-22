using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Messaging;
using ImageManager.Common.Helpers;
using ImageManager.Core.Messages;
using ImageManager.Core.Models;
using ImageManager.Core.Services;

namespace ImageManager.Infrastructure.Services;

/// <summary>
/// Tag-based search/filter engine with DSL parsing.
/// Lives in Infrastructure with no dependency on Avalonia UI.
/// Communicates results via <see cref="IMessenger"/>.
/// </summary>
public class TagSearchEngine
{
    private readonly IImageMetaRepository _metaRepo;
    private readonly IMessenger _messenger;
    private readonly int _pageSize;

    private bool _coTagMode;
    private readonly Dictionary<string, int> _coTagStates = new(StringComparer.OrdinalIgnoreCase);
    private string _lastSearchText = string.Empty;
    private List<TagCount> _fullCoTags = new();

    public bool IsSuggestionCoTagMode => _coTagMode;
    public List<string> SearchResultFiles { get; set; } = new();
    public List<TagCount> AllTagCounts { get; set; } = new();

    public TagSearchEngine(IImageMetaRepository metaRepo, IMessenger messenger, int pageSize = 200)
    {
        _metaRepo = metaRepo;
        _messenger = messenger;
        _pageSize = pageSize;
    }

    // ==================== Public API ====================

    public void SetAllTagCounts(List<TagCount> counts) => AllTagCounts = counts;

    public void ClearSearchResults()
    {
        SearchResultFiles = new List<string>();
        AllTagCounts = new List<TagCount>();
        _coTagMode = false;
        _coTagStates.Clear();
        _fullCoTags.Clear();
    }

    public int GetCoTagState(string tagName)
    {
        _coTagStates.TryGetValue(tagName, out int state);
        return state;
    }

    public void UpdateSuggestions(string keyword, Action<TagCount> addSuggestion, Action<bool> setPopupOpen)
    {
        if (_coTagMode) return;

        if (string.IsNullOrWhiteSpace(keyword) || AllTagCounts.Count == 0)
        {
            setPopupOpen(false);
            return;
        }

        var activeToken = ExtractActiveToken(keyword);
        if (string.IsNullOrWhiteSpace(activeToken))
        {
            setPopupOpen(false);
            return;
        }

        var results = AllTagCounts
            .Where(t => t.Name.Contains(activeToken, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(t => t.Name.StartsWith(activeToken, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(t => t.Count)
            .Take(300)
            .ToList();

        foreach (var t in results)
            addSuggestion(t);

        setPopupOpen(results.Count > 0);
    }

    public void OnTextChanged(string value, string currentTagSearchText,
        Action onBorderColorChanged, Action<string> clearAndUpdateSuggestions)
    {
        if (_coTagMode)
        {
            if (value != _lastSearchText)
            {
                _coTagMode = false;
                _coTagStates.Clear();
                onBorderColorChanged();
                clearAndUpdateSuggestions(value);
                _messenger.Send(new CoTagModeExitedMessage());
            }
            return;
        }
        clearAndUpdateSuggestions(value);
    }

    public void OnGotFocus(string currentText,
        Action<bool> setPopupOpen,
        Func<List<TagCount>> getSuggestions)
    {
        if (_coTagMode)
        {
            setPopupOpen(getSuggestions().Count > 0);
        }
        else if (!string.IsNullOrWhiteSpace(currentText))
        {
            UpdateSuggestions(currentText,
                t => getSuggestions().Add(t),
                setPopupOpen);
        }
    }

    public async Task SearchByTagAsync(string raw, List<string> allFiles, bool alreadyShowingSearch,
        Action<List<TagCount>> setSuggestions)
    {
        // Special keyword: -every = find images with no tags
        if (string.Equals(raw, "-every", StringComparison.OrdinalIgnoreCase))
        {
            var untagged = await _metaRepo.GetFilePathsWithNoTagsAsync();
            var everyFileSet = new HashSet<string>(allFiles, StringComparer.OrdinalIgnoreCase);
            SearchResultFiles = untagged.Where(p => everyFileSet.Contains(p)).ToList();

            if (SearchResultFiles.Count == 0)
            {
                SendSearchCompleted(statusText: "", opName: "");
                return;
            }

            var everyTotalPages = (SearchResultFiles.Count + _pageSize - 1) / _pageSize;
            SendSearchCompleted(statusText: "", opName: "");
            return;
        }

        // Normalize leading "-" for pure-exclude: "-tag" → " - tag"
        if (raw.StartsWith('-') && !raw.Contains(" - ", StringComparison.OrdinalIgnoreCase))
        {
            raw = " - " + raw[1..].TrimStart();
        }

        // Parse " - " first: left side = include, right side = exclude
        List<string> excludeTags = new();
        bool excludeIsAnd = false;
        string includePart;
        if (raw.Contains(" - ", StringComparison.OrdinalIgnoreCase))
        {
            var parts = raw.Split(new[] { " - " }, 2, StringSplitOptions.None);
            includePart = parts[0].Trim();
            var excludePart = parts[1].Trim();
            if (excludePart.Contains(" a ", StringComparison.OrdinalIgnoreCase))
            {
                excludeTags = excludePart.Split(new[] { " a ", " - " }, StringSplitOptions.None)
                                       .Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
                excludeIsAnd = true;
            }
            else
            {
                excludeTags = excludePart.Split(new[] { " o ", " - " }, StringSplitOptions.None)
                                       .Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
            }
        }
        else
        {
            includePart = raw;
        }

        // Pure-exclude mode
        if (string.IsNullOrWhiteSpace(includePart) && excludeTags.Count > 0)
        {
            var excludedPaths = await _metaRepo.GetFilePathsExcludingTagsAsync(excludeTags, excludeIsAnd);
            var pureFileSet = new HashSet<string>(allFiles, StringComparer.OrdinalIgnoreCase);
            SearchResultFiles = excludedPaths.Where(p => pureFileSet.Contains(p)).ToList();
            SearchResultFiles.Sort(StringComparer.OrdinalIgnoreCase);

            if (SearchResultFiles.Count == 0)
            {
                SendSearchCompleted(statusText: "", opName: "");
                return;
            }

            _coTagMode = true;
            _lastSearchText = raw;

            SendSearchCompleted(statusText: "", opName: "");
            _ = RefreshCoTagSuggestionsAsync(setSuggestions);
            return;
        }

        // Parse " e " = AND-each, " a " = AND-all, " o " = OR
        List<string> tags;
        bool isAnd = false;
        bool isAndEach = false;
        bool baseIsAnd = true;
        List<string> eachTags = new();
        if (includePart.Contains(" e ", StringComparison.OrdinalIgnoreCase))
        {
            var parts = includePart.Split(new[] { " e " }, StringSplitOptions.None)
                                   .Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
            var basePart = parts[0];
            eachTags = parts.Skip(1).ToList();
            isAndEach = true;

            if (basePart.Contains(" a ", StringComparison.OrdinalIgnoreCase))
            {
                tags = basePart.Split(new[] { " a " }, StringSplitOptions.None)
                               .Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
                baseIsAnd = true;
            }
            else if (basePart.Contains(" o ", StringComparison.OrdinalIgnoreCase))
            {
                tags = basePart.Split(new[] { " o " }, StringSplitOptions.None)
                               .Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
                baseIsAnd = false;
            }
            else
            {
                tags = new List<string> { basePart };
            }
        }
        else if (includePart.Contains(" a ", StringComparison.OrdinalIgnoreCase))
        {
            tags = includePart.Split(new[] { " a " }, StringSplitOptions.None)
                      .Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
            isAnd = true;
        }
        else if (includePart.Contains(" o ", StringComparison.OrdinalIgnoreCase))
        {
            tags = includePart.Split(new[] { " o " }, StringSplitOptions.None)
                      .Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
        }
        else
        {
            tags = new List<string> { includePart };
        }

        if (tags.Count == 0)
        {
            SendSearchCompleted(statusText: "", opName: "");
            return;
        }

        List<string> taggedPaths;
        if (isAndEach)
        {
            taggedPaths = await _metaRepo.GetFilePathsByTagAndEachAsync(tags, baseIsAnd, eachTags,
                excludeTags.Count > 0 ? excludeTags : null);
        }
        else if (excludeTags.Count > 0)
        {
            taggedPaths = await _metaRepo.GetFilePathsByTagsExcludingAsync(tags, isAnd, excludeTags);
        }
        else if (tags.Count == 1)
        {
            taggedPaths = await _metaRepo.GetFilePathsByTagAsync(tags[0]);
        }
        else
        {
            taggedPaths = await _metaRepo.GetFilePathsByTagsAsync(tags, isAnd);
        }

        var fileSet = new HashSet<string>(allFiles, StringComparer.OrdinalIgnoreCase);
        SearchResultFiles = taggedPaths.Where(p => fileSet.Contains(p)).ToList();
        SearchResultFiles.Sort(StringComparer.OrdinalIgnoreCase);

        _coTagMode = true;
        _lastSearchText = raw;

        SendSearchCompleted(statusText: "", opName: "");
        _ = RefreshCoTagSuggestionsAsync(setSuggestions);
    }

    public void SelectSuggestion(TagCount tag, string currentText, Action<string> setTagSearchText, Action<bool> setPopupOpen,
        Func<Task> triggerSearch)
    {
        if (_coTagMode && !string.IsNullOrEmpty(_lastSearchText))
        {
            CycleCoTag(tag.Name, setTagSearchText);
            return;
        }

        setTagSearchText(ReplaceActiveToken(currentText, tag.Name));
        setPopupOpen(false);
        _ = triggerSearch();
    }

    public string SearchBoxBorderColor(string text)
    {
        var t = text ?? "";
        if (t.StartsWith('-') || t.Contains(" - ", StringComparison.OrdinalIgnoreCase))
            return "#E8A0A0";
        if (t.Contains(" e ", StringComparison.OrdinalIgnoreCase))
            return "#8CB8E8";
        if (t.Contains(" a ", StringComparison.OrdinalIgnoreCase))
            return "#86D9B0";
        return "#4A5568";
    }

    private void SendSearchCompleted(string statusText, string opName)
    {
        var totalPages = SearchResultFiles.Count > 0
            ? (SearchResultFiles.Count + _pageSize - 1) / _pageSize
            : 0;
        _messenger.Send(new TagSearchCompletedMessage(
            SearchResultFiles, totalPages, statusText,
            hasResults: SearchResultFiles.Count > 0, opName));
    }

    // ==================== Private Methods ====================

    private void CycleCoTag(string tagName, Action<string> setTagSearchText)
    {
        _coTagStates.TryGetValue(tagName, out int state);
        state = (state + 1) % 4;
        _coTagStates[tagName] = state;

        string includeSection, excludeSection;
        if (_lastSearchText.Contains(" - ", StringComparison.OrdinalIgnoreCase))
        {
            var parts = _lastSearchText.Split(new[] { " - " }, 2, StringSplitOptions.None);
            includeSection = parts[0].Trim();
            excludeSection = parts[1].Trim();
        }
        else if (_lastSearchText.TrimStart().StartsWith('-'))
        {
            includeSection = "";
            excludeSection = _lastSearchText.TrimStart()[1..].TrimStart();
        }
        else
        {
            includeSection = _lastSearchText;
            excludeSection = "";
        }

        foreach (var t in ParseTagNames(excludeSection))
        {
            if (!_coTagStates.ContainsKey(t))
                _coTagStates[t] = 3;
        }

        var allIncludeTags = ParseTagNames(includeSection);
        var cleaned = new List<string>();
        foreach (var t in allIncludeTags)
        {
            if (!_coTagStates.ContainsKey(t))
                cleaned.Add(t);
        }

        var andTags = new List<string>();
        var eachTags = new List<string>();
        var notTags = new List<string>();
        foreach (var kv in _coTagStates)
        {
            if (kv.Value == 0) continue;
            if (kv.Value == 1) andTags.Add(kv.Key);
            else if (kv.Value == 2) eachTags.Add(kv.Key);
            else if (kv.Value == 3) notTags.Add(kv.Key);
        }

        var sb = new System.Text.StringBuilder();
        sb.Append(string.Join(" a ", cleaned));
        foreach (var t in andTags) sb.Append($" a {t}");
        foreach (var t in eachTags) sb.Append($" e {t}");
        if (notTags.Count > 0) sb.Append(" - " + string.Join(" o ", notTags));

        var newText = sb.ToString();
        _lastSearchText = newText;
        setTagSearchText(newText);
        _messenger.Send(new CoTagCycledMessage());
    }

    private async Task RefreshCoTagSuggestionsAsync(Action<List<TagCount>> setSuggestions)
    {
        try
        {
            var usedTags = ParseTagNames(_lastSearchText);
            var samplePaths = SearchResultFiles.Count > 5000
                ? SearchResultFiles.Take(5000).ToList()
                : SearchResultFiles;
            var coTags = await _metaRepo.GetCoOccurringTagsAsync(samplePaths, usedTags);
            _fullCoTags = coTags.Take(300).ToList();
            _messenger.Send(new TagSearchSuggestionsChangedMessage(_fullCoTags));
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Co-tag query failed: {ex.Message}");
            _messenger.Send(new TagSearchSuggestionsChangedMessage(new List<TagCount>()));
        }
    }

    public async Task SearchCoTagsAsync(string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            _messenger.Send(new TagSearchSuggestionsChangedMessage(_fullCoTags));
            return;
        }

        try
        {
            var usedTags = ParseTagNames(_lastSearchText);
            var results = await _metaRepo.GetCoOccurringTagsAsync(SearchResultFiles, usedTags, keyword);
            _messenger.Send(new TagSearchSuggestionsChangedMessage(results));
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Co-tag query failed: {ex.Message}");
            _messenger.Send(new TagSearchSuggestionsChangedMessage(new List<TagCount>()));
        }
    }

    public Task RefreshCurrentCoTagSuggestionsAsync()
    {
        if (!_coTagMode)
            return Task.CompletedTask;

        return RefreshCoTagSuggestionsAsync(_ => { });
    }

    internal static string ExtractActiveToken(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var t = text.TrimStart();
        if (t.StartsWith('-') && !t.Contains(" - ", StringComparison.OrdinalIgnoreCase))
        {
            t = t[1..].TrimStart();
            var parts = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.LastOrDefault() ?? string.Empty;
        }

        var segments = text.Split(new[] { " a ", " o ", " e ", " - " }, StringSplitOptions.None);
        var lastSegment = segments.LastOrDefault()?.Trim() ?? string.Empty;

        var segmentParts = lastSegment.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return segmentParts.LastOrDefault() ?? string.Empty;
    }

    internal static string ReplaceActiveToken(string text, string tagName)
    {
        var trimmedStart = text.TrimStart();
        if (trimmedStart.StartsWith('-') && !text.Contains(" - ", StringComparison.OrdinalIgnoreCase))
        {
            var dashIdx = text.IndexOf('-');
            var afterDash = text[(dashIdx + 1)..];
            var lastSpaceIdx = afterDash.LastIndexOf(' ');
            if (lastSpaceIdx >= 0)
                return text[..(dashIdx + 1 + lastSpaceIdx + 1)] + tagName;
            return text[..(dashIdx + 1)] + tagName;
        }

        var operators = new[] { " a ", " o ", " e ", " - " };

        int lastSepIdx = -1;
        int lastSepLen = 0;
        foreach (var op in operators)
        {
            var pos = text.LastIndexOf(op, StringComparison.OrdinalIgnoreCase);
            if (pos > lastSepIdx)
            {
                lastSepIdx = pos;
                lastSepLen = op.Length;
            }
        }

        int startIdx = lastSepIdx >= 0 ? lastSepIdx + lastSepLen : 0;

        if (startIdx < text.Length)
        {
            var afterSep = text[startIdx..];
            var lastSpaceIdx2 = afterSep.LastIndexOf(' ');
            if (lastSpaceIdx2 >= 0)
                startIdx += lastSpaceIdx2 + 1;
        }

        return text[..startIdx] + tagName;
    }

    internal static List<string> ParseTagNames(string text)
    {
        return text.Split(new[] { " a ", " o ", " e ", " - " }, StringSplitOptions.None)
                   .Select(t => t.Trim())
                   .Where(t => t.Length > 0)
                   .Distinct(StringComparer.OrdinalIgnoreCase)
                   .ToList();
    }
}
