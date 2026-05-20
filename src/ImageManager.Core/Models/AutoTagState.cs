namespace ImageManager.Core.Models;

public class AutoTagState
{
    public long FolderId { get; set; }
    public string Status { get; set; } = "Pending";
    public int TotalFiles { get; set; }
    public int Processed { get; set; }
    public int LastFileCount { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ErrorMsg { get; set; }
}
