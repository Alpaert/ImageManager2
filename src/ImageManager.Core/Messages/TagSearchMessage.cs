using ImageManager.Core.Models;

namespace ImageManager.Core.Messages;

/// <summary>
/// Published when tag search suggestions change (e.g. auto-complete results).
/// </summary>
public sealed class TagSearchSuggestionsChangedMessage
{
    public IReadOnlyList<TagCount> Suggestions { get; }
    public TagSearchSuggestionsChangedMessage(IReadOnlyList<TagCount> suggestions)
        => Suggestions = suggestions;
}

/// <summary>
/// Published when the user cycles a co-occurring tag state.
/// </summary>
public sealed class CoTagCycledMessage;

/// <summary>
/// Published when co-tag mode is exited.
/// </summary>
public sealed class CoTagModeExitedMessage;
