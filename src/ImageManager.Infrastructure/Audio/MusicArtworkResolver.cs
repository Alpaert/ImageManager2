namespace ImageManager.Infrastructure.Audio;

internal static class MusicArtworkResolver
{
    private static readonly string[] FileNames =
    [
        "Cover", "Folder", "Front", "AlbumArt", "Album", "Artwork"
    ];

    private static readonly string[] Extensions =
    [
        ".jpg", ".jpeg", ".png", ".webp", ".bmp"
    ];

    private static readonly string[] SharedArtworkKeywords =
    [
        "cover", "front", "folder", "album", "art", "artwork", "jacket", "booklet"
    ];

    public static string? FindSidecarArtwork(string audioFilePath)
    {
        var directory = Path.GetDirectoryName(audioFilePath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return null;

        var audioStem = Path.GetFileNameWithoutExtension(audioFilePath);
        if (!string.IsNullOrWhiteSpace(audioStem))
        {
            var sameStemArtwork = FindNamedArtwork(directory, audioStem);
            if (sameStemArtwork is not null)
                return sameStemArtwork;
        }

        var standardArtwork = FindStandardArtwork(directory);
        if (standardArtwork is not null)
            return standardArtwork;

        var scansArtwork = FindScansArtwork(directory);
        return scansArtwork ?? FindSharedArtwork(directory);
    }

    private static string? FindNamedArtwork(string directory, string fileName)
    {
        foreach (var extension in Extensions)
        {
            var candidate = Path.Combine(directory, fileName + extension);
            if (File.Exists(candidate))
                return candidate;
        }

        try
        {
            var acceptedExtensions = new HashSet<string>(Extensions, StringComparer.OrdinalIgnoreCase);
            return Directory.EnumerateFiles(directory)
                .Where(path => fileName.Equals(Path.GetFileNameWithoutExtension(path), StringComparison.OrdinalIgnoreCase) &&
                               acceptedExtensions.Contains(Path.GetExtension(path)))
                .OrderBy(path => Array.FindIndex(Extensions,
                    extension => extension.Equals(Path.GetExtension(path), StringComparison.OrdinalIgnoreCase)))
                .ThenBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .ThenBy(path => Path.GetFileName(path), StringComparer.Ordinal)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string? FindStandardArtwork(string directory)
    {
        foreach (var fileName in FileNames)
        {
            var artwork = FindNamedArtwork(directory, fileName);
            if (artwork is not null)
                return artwork;
        }

        return null;
    }

    private static string? FindScansArtwork(string directory)
    {
        var scansDirectory = Path.Combine(directory, "Scans");
        if (!Directory.Exists(scansDirectory))
            return null;

        try
        {
            var acceptedExtensions = new HashSet<string>(Extensions, StringComparer.OrdinalIgnoreCase);
            return Directory.EnumerateFiles(scansDirectory)
                .Where(path => acceptedExtensions.Contains(Path.GetExtension(path)))
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .ThenBy(path => Path.GetFileName(path), StringComparer.Ordinal)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string? FindSharedArtwork(string directory)
    {
        try
        {
            var acceptedExtensions = new HashSet<string>(Extensions, StringComparer.OrdinalIgnoreCase);
            return Directory.EnumerateFiles(directory)
                .Where(path => acceptedExtensions.Contains(Path.GetExtension(path)))
                .OrderBy(path => HasSharedArtworkKeyword(Path.GetFileNameWithoutExtension(path)) ? 0 : 1)
                .ThenBy(path => Array.FindIndex(Extensions,
                    extension => extension.Equals(Path.GetExtension(path), StringComparison.OrdinalIgnoreCase)))
                .ThenBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .ThenBy(path => Path.GetFileName(path), StringComparer.Ordinal)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static bool HasSharedArtworkKeyword(string fileName)
    {
        return SharedArtworkKeywords.Any(keyword =>
            fileName.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }
}
