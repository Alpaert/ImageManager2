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
}
