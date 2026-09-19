using ImageManager.Common.Constants;
using ImageManager.Core.Models;

namespace ImageManager.App.Services;

public sealed record DisplayFilterResult(List<string> Files, int UnknownDimensionsCount);

/// <summary>Preserves source order; type-only filtering never reads metadata or opens files.</summary>
public static class DisplayFileFilter
{
    public static async Task<DisplayFilterResult> ApplyAsync(
        IReadOnlyList<string> source, DisplayFilterOptions options,
        Func<List<string>, CancellationToken, Task<Dictionary<string, (int Width, int Height)>>> loadDimensions,
        Func<List<string>, CancellationToken, Task<Dictionary<string, int>>> loadRatings,
        Func<string, (int Width, int Height)> readImageDimensions,
        CancellationToken cancellationToken = default)
    {
        var candidates = new List<string>();
        foreach (var path in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var type = FileTypeConstants.GetFileType(path);
            if (type != null && (options.TypeId == null || options.TypeId == type.Id)) candidates.Add(path);
        }
        if (!options.IncludesAllContentRatings && candidates.Count > 0)
        {
            var ratings = await loadRatings(candidates, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            candidates = candidates.Where(path => MatchesContentRating(
                ratings.TryGetValue(path, out var rating) ? rating : -1,
                options.ContentRatings)).ToList();
        }

        if (options.Orientation == MediaOrientation.All || candidates.Count == 0)
            return new(candidates, 0);

        var dimensions = await loadDimensions(candidates, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var filtered = new List<string>();
        var unknown = 0;
        foreach (var path in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var type = FileTypeConstants.GetFileType(path)!;
            if (!type.SupportsDimensions) continue;
            dimensions.TryGetValue(path, out var size);
            if ((size.Width <= 0 || size.Height <= 0) && type.Id == "image")
            {
                try { size = readImageDimensions(path); }
                catch (OperationCanceledException) { throw; }
                catch { size = default; }
            }
            if (size.Width <= 0 || size.Height <= 0)
            {
                unknown++;
                if (options.IncludeUnknownDimensions) filtered.Add(path);
                continue;
            }
            if (options.Orientation switch
            {
                MediaOrientation.Landscape => size.Width > size.Height,
                MediaOrientation.Portrait => size.Width < size.Height,
                MediaOrientation.Square => size.Width == size.Height,
                _ => true
            }) filtered.Add(path);
        }
        return new(filtered, unknown);
    }

    private static bool MatchesContentRating(int rating, ContentRatingFilter selected) => rating switch
    {
        0 => selected.HasFlag(ContentRatingFilter.General),
        1 => selected.HasFlag(ContentRatingFilter.Sensitive),
        2 => selected.HasFlag(ContentRatingFilter.Questionable),
        3 => selected.HasFlag(ContentRatingFilter.Explicit),
        _ => selected == ContentRatingFilter.All
    };
}
