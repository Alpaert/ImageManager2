namespace ImageManager.Core.Models;

public class TagMapping
{
    public long Id { get; set; }
    public string EnglishName { get; set; } = string.Empty;
    public string ChineseName { get; set; } = string.Empty;
    public DateTime ConfirmedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
