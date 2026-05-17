namespace ImageManager.Core.Models;

public class FolderInfo
{
    public long Id { get; set; }
    public string Path { get; set; } = string.Empty;
    public string? Alias { get; set; }
    public int SortOrder { get; set; }
    public int? LastPageIndex { get; set; }

    public string DisplayName => Alias ?? System.IO.Path.GetFileName(Path.TrimEnd('\\', '/'));
}
