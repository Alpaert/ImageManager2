namespace ImageManager.Core.Models;

/// <summary>
/// DTO for auto-tag translation review data.
/// Decouples the orchestrator from UI-layer ViewModel types.
/// </summary>
public class TagTranslationDto
{
    public string EnglishTag { get; set; } = string.Empty;
    public string ChineseTranslation { get; set; } = string.Empty;
    public string? UserEditedText { get; set; }
    public bool IsConfirmed { get; set; }
    public bool IsExistingMapping { get; set; }
    public int ImageCount { get; set; }
}
