using System.Threading;

namespace ImageManager.Core.Services;

public interface ITranslationService
{
    bool IsAvailable { get; }
    Task<string?> TranslateSingleAsync(string englishTag, CancellationToken ct = default);
    Task<Dictionary<string, string>> TranslateBatchAsync(List<string> englishTags, CancellationToken ct = default);
}
