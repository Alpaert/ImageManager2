using ImageManager.Core.Models;

namespace ImageManager.Core.Services;

/// <summary>
/// Service contract for batch character matching against character embeddings.
/// </summary>
public interface ICharacterMatchingService
{
    Task<CharacterMatchResult> MatchCharactersAsync(
        CharacterMatchRequest request,
        IProgress<CharacterMatchProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
