using System.Globalization;

namespace ImageManager.Infrastructure.Video;

public static class VideoMetadataExtractor
{
    /// <summary>
    /// Extract video width, height, and duration using ffprobe
    /// </summary>
    public static async Task<(int Width, int Height, double Duration)?> ExtractMetadataAsync(
        string filePath,
        CancellationToken ct = default)
    {
        try
        {
            // Use ffprobe to extract metadata (ffprobe comes with ffmpeg)
            string args = $"-v error -select_streams v:0 -show_entries stream=width,height,duration -of csv=p=0 \"{filePath}\"";
            var (exitCode, output, error) = await FFmpegManager.RunAsync(args, 10, ct);

            if (exitCode != 0 || string.IsNullOrWhiteSpace(output))
                return null;

            // Output format: "1920,1080,125.5"
            var parts = output.Trim().Split(',');
            if (parts.Length >= 2 &&
                int.TryParse(parts[0], out int w) &&
                int.TryParse(parts[1], out int h))
            {
                double duration = 0;
                if (parts.Length >= 3 && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                    duration = d;

                return (w, h, duration);
            }

            return null;
        }
        catch
        {
            return null;
        }
    }
}
