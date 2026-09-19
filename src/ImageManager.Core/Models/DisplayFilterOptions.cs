namespace ImageManager.Core.Models;

public enum MediaOrientation { All, Landscape, Portrait, Square }

/// <summary>Session-only display criteria; null type means all supported file types.</summary>
public sealed record DisplayFilterOptions(
    string? TypeId = null,
    MediaOrientation Orientation = MediaOrientation.All,
    bool IncludeUnknownDimensions = true)
{
    public bool IsActive => TypeId != null || Orientation != MediaOrientation.All;
}
