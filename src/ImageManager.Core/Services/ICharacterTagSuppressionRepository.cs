namespace ImageManager.Core.Services;

public interface ICharacterTagSuppressionRepository
{
    Task SuppressAsync(long imageMetaId, IEnumerable<string> tagNames);
    Task SuppressBatchAsync(IReadOnlyList<(long ImageMetaId, string TagName)> suppressions);
    Task UnsuppressAsync(long imageMetaId, IEnumerable<string> tagNames);
    Task UnsuppressBatchAsync(IReadOnlyList<(long ImageMetaId, string TagName)> suppressions);
    Task<HashSet<string>> GetSuppressedTagsAsync(long imageMetaId);
    Task<Dictionary<long, HashSet<string>>> GetSuppressedTagsAsync(IEnumerable<long> imageMetaIds);
}
