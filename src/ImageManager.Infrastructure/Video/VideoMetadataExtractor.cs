using System.Globalization;

namespace ImageManager.Infrastructure.Video;

public static class VideoMetadataExtractor
{
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

            // Parse resolution from stderr
            var resMatch = System.Text.RegularExpressions.Regex.Match(error, @"(\d{2,5})x(\d{2,5})");
            if (resMatch.Success)
            {
                width = int.Parse(resMatch.Groups[1].Value);
                height = int.Parse(resMatch.Groups[2].Value);
            }

            // Parse duration from stderr: "Duration: 00:24:00.09"
            var durMatch = System.Text.RegularExpressions.Regex.Match(error, @"Duration: (\d{2}):(\d{2}):(\d{2}\.\d{2})");
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
        catch (Exception ex)
        {
            return null;
        }
    }
}
