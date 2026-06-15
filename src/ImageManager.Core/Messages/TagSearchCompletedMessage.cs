namespace ImageManager.Core.Messages;

/// <summary>
/// Published when a tag-based search/filter completes.
/// Carries the result file list and computed metadata.
/// </summary>
public sealed class TagSearchCompletedMessage
{
    public IReadOnlyList<string> ResultFiles { get; }
    public int TotalPages { get; }
    public string StatusText { get; }
    public bool HasResults { get; }
    public string OpName { get; }

    public TagSearchCompletedMessage(IReadOnlyList<string> resultFiles, int totalPages, string statusText, bool hasResults, string opName = "")
    {
        ResultFiles = resultFiles;
        TotalPages = totalPages;
        StatusText = statusText;
        HasResults = hasResults;
        OpName = opName;
    }
}
