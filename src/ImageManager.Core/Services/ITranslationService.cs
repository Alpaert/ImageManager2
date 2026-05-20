namespace ImageManager.Core.Services;

public interface ITranslationService
{
    bool IsAvailable { get; }
    Task<string?> TranslateSingleAsync(string englishTag);
    Task<Dictionary<string, string>> TranslateBatchAsync(List<string> englishTags);
}
