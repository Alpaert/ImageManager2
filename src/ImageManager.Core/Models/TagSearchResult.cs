namespace ImageManager.Core.Models;

/// <summary>
/// Result of a tag-based image search.
/// </summary>
public class TagSearchResult
{
    public List<string> ResultFiles { get; init; } = new();
    public int TotalPages { get; init; }
    public string StatusText { get; init; } = string.Empty;
    public bool HasResults { get; init; }
    public string OpName { get; init; } = string.Empty;
}
