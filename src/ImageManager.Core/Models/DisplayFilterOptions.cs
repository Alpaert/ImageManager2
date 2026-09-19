namespace ImageManager.Core.Models;

public enum MediaOrientation { All, Landscape, Portrait, Square }

/// <summary>Content-rating values stored in <see cref="ImageMeta.SystemRating"/>.</summary>
[Flags]
public enum ContentRatingFilter
{
    None = 0,
    General = 1 << 0,
    Sensitive = 1 << 1,
    Questionable = 1 << 2,
    Explicit = 1 << 3,
    All = General | Sensitive | Questionable | Explicit
}

/// <summary>Session-only display criteria; null type means all supported file types.</summary>
public sealed record DisplayFilterOptions(
    string? TypeId = null,
    MediaOrientation Orientation = MediaOrientation.All,
    bool IncludeUnknownDimensions = true,
    ContentRatingFilter ContentRatings = ContentRatingFilter.All)
{
    public bool IncludesAllContentRatings => ContentRatings == ContentRatingFilter.All;
    public bool IsActive => TypeId != null || Orientation != MediaOrientation.All || !IncludesAllContentRatings;
}
