using System.Globalization;
using System.Text.RegularExpressions;

namespace ImageManager.Infrastructure.Video;

public static class VideoMetadataExtractor
{
    private static readonly Regex VideoResolutionPattern = new(
        @"^\s*Stream\s+#\d+(?::\d+)?(?:\[[^\]]+\])?(?:\([^\)]*\))?:\s+Video:.*?(?<width>\d{2,5})x(?<height>\d{2,5})",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly Regex DisplayMatrixRotationPattern = new(
        @"displaymatrix:\s*rotation\s+of\s+(?<degrees>[+-]?\d+(?:\.\d+)?)\s+degrees",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex RotateTagPattern = new(
        @"(?:^|\r?\n)\s*rotate\s*:\s*(?<degrees>[+-]?\d+(?:\.\d+)?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Extract video width, height, and duration using ffmpeg
    /// </summary>
    public static async Task<(int Width, int Height, double Duration)?> ExtractMetadataAsync(
        string filePath,
        CancellationToken ct = default)
    {
        try
        {
            // Use ffmpeg to extract metadata (faster than ffprobe for our use case)
            // -i: input file, output shows metadata in stderr
            string args = $"-i \"{filePath}\"";
            var (exitCode, output, error) = await FFmpegManager.RunAsync(args, 10, ct);

            // ffmpeg prints metadata to stderr
            // Look for: "Stream #0:0: Video: ..., 1920x1080"
            // Look for: "Duration: 00:24:00.09"

            int width = 0, height = 0;
            double duration = 0;

            // Restrict resolution parsing to the video stream. ffmpeg output can contain
            // unrelated dimensions from attached artwork or other streams.
            var resMatch = VideoResolutionPattern.Match(error);
            if (resMatch.Success)
            {
                width = int.Parse(resMatch.Groups["width"].Value, CultureInfo.InvariantCulture);
                height = int.Parse(resMatch.Groups["height"].Value, CultureInfo.InvariantCulture);

                // The encoded frame is commonly landscape even when the display matrix
                // declares a portrait presentation. Persist display dimensions so every
                // consumer, including continuous layout, receives the visible aspect ratio.
                if (HasQuarterTurnRotation(error))
                {
                    (width, height) = (height, width);
                }
            }

            // Parse duration from stderr: "Duration: 00:24:00.09"
            var durMatch = Regex.Match(error, @"Duration: (\d{2}):(\d{2}):(\d{2}\.\d{2})", RegexOptions.CultureInvariant);
            if (durMatch.Success)
            {
                int hours = int.Parse(durMatch.Groups[1].Value);
                int minutes = int.Parse(durMatch.Groups[2].Value);
                double seconds = double.Parse(durMatch.Groups[3].Value, CultureInfo.InvariantCulture);
                duration = hours * 3600 + minutes * 60 + seconds;
            }

            if (width > 0 && height > 0)
            {
                return (width, height, duration);
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool HasQuarterTurnRotation(string ffmpegOutput)
    {
        var rotationMatch = DisplayMatrixRotationPattern.Match(ffmpegOutput);
        if (!rotationMatch.Success)
        {
            rotationMatch = RotateTagPattern.Match(ffmpegOutput);
        }

        if (!rotationMatch.Success ||
            !double.TryParse(rotationMatch.Groups["degrees"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var degrees))
        {
            return false;
        }

        var normalized = ((degrees % 360) + 360) % 360;
        return Math.Abs(normalized - 90) < 1 || Math.Abs(normalized - 270) < 1;
    }
}
