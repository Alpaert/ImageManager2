using SkiaSharp;

namespace ImageManager.Infrastructure.Services;

public static class ImageSignatureService
{
    public const string AtmosphereModelKey = "atmosphere-signature";
    public const string ColorModelKey = "color-signature";
    public const string ModelVersion = "v1";
    public const int AtmosphereDimension = 51;
    public const int ColorDimension = 32;

    private const int Edge = 48;
    private const int AtmosphereHueBins = 12;
    private const int ColorHueBins = 24;

    public static float[] ComputeAtmosphere(string path)
    {
        using var resized = LoadResized(path);
        var total = Edge * Edge;
        var hueHistogram = new float[AtmosphereHueBins];
        var brightnessGrid = new float[16];
        var saturationGrid = new float[16];
        var cellCounts = new float[16];
        float saturationSum = 0, saturationSquareSum = 0;
        float valueSum = 0, valueSquareSum = 0;
        float dark = 0, highlight = 0, warm = 0, cool = 0;

        for (var y = 0; y < Edge; y++)
        {
            for (var x = 0; x < Edge; x++)
            {
                var (hue, saturation, value) = ToHsv(resized.GetPixel(x, y));
                hueHistogram[Math.Min(AtmosphereHueBins - 1, (int)(hue / 360f * AtmosphereHueBins))]++;
                saturationSum += saturation;
                saturationSquareSum += saturation * saturation;
                valueSum += value;
                valueSquareSum += value * value;
                if (value < 0.22f) dark++;
                if (value > 0.85f) highlight++;
                if ((hue <= 70 || hue >= 290) && saturation > 0.15f) warm++;
                else if (hue is >= 140 and <= 260 && saturation > 0.15f) cool++;
                var cell = Math.Min(3, y * 4 / Edge) * 4 + Math.Min(3, x * 4 / Edge);
                brightnessGrid[cell] += value;
                saturationGrid[cell] += saturation;
                cellCounts[cell]++;
            }
        }

        for (var i = 0; i < hueHistogram.Length; i++)
            hueHistogram[i] /= total;
        for (var i = 0; i < 16; i++)
        {
            var count = Math.Max(1, cellCounts[i]);
            brightnessGrid[i] /= count;
            saturationGrid[i] /= count;
        }

        var saturationMean = saturationSum / total;
        var valueMean = valueSum / total;
        var result = new List<float>(AtmosphereDimension);
        result.AddRange(hueHistogram);
        result.Add(saturationMean);
        result.Add(MathF.Sqrt(MathF.Max(0, saturationSquareSum / total - saturationMean * saturationMean)));
        result.Add(valueMean);
        result.Add(MathF.Sqrt(MathF.Max(0, valueSquareSum / total - valueMean * valueMean)));
        result.Add(dark / total);
        result.Add(highlight / total);
        result.Add(warm / (warm + cool + 1e-6f));
        result.AddRange(brightnessGrid);
        result.AddRange(saturationGrid);
        return result.ToArray();
    }

    public static float[] ComputeColor(string path)
    {
        using var resized = LoadResized(path);
        var total = Edge * Edge;
        var hueHistogram = new float[ColorHueBins];
        float saturationSum = 0, saturationSquareSum = 0;
        float valueSum = 0, valueSquareSum = 0;
        float dark = 0, highlight = 0, gray = 0, warm = 0;

        for (var y = 0; y < Edge; y++)
        {
            for (var x = 0; x < Edge; x++)
            {
                var (hue, saturation, value) = ToHsv(resized.GetPixel(x, y));
                var bin = Math.Min(ColorHueBins - 1, (int)(hue / 360f * ColorHueBins));
                hueHistogram[bin] += 0.35f + saturation * 0.65f;
                saturationSum += saturation;
                saturationSquareSum += saturation * saturation;
                valueSum += value;
                valueSquareSum += value * value;
                if (value < 0.22f) dark++;
                if (value > 0.85f) highlight++;
                if (saturation < 0.15f) gray++;
                if ((hue <= 70 || hue >= 290) && saturation > 0.15f) warm++;
            }
        }

        var histogramSum = Math.Max(1e-6f, hueHistogram.Sum());
        for (var i = 0; i < hueHistogram.Length; i++)
            hueHistogram[i] /= histogramSum;
        var saturationMean = saturationSum / total;
        var valueMean = valueSum / total;
        var result = new List<float>(ColorDimension);
        result.AddRange(hueHistogram);
        result.Add(saturationMean);
        result.Add(MathF.Sqrt(MathF.Max(0, saturationSquareSum / total - saturationMean * saturationMean)));
        result.Add(valueMean);
        result.Add(MathF.Sqrt(MathF.Max(0, valueSquareSum / total - valueMean * valueMean)));
        result.Add(dark / total);
        result.Add(highlight / total);
        result.Add(gray / total);
        result.Add(warm / total);
        return result.ToArray();
    }

    public static float AtmosphereScore(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        if (left.Length != AtmosphereDimension || right.Length != AtmosphereDimension)
            return float.NegativeInfinity;
        float sum = 0;
        for (var i = 0; i < AtmosphereDimension; i++)
        {
            var weight = i < AtmosphereHueBins ? 2f : i < AtmosphereHueBins + 7 ? 1.4f : i < AtmosphereHueBins + 23 ? 1.1f : 1f;
            var difference = left[i] - right[i];
            sum += weight * difference * difference;
        }
        return 1f / (1f + MathF.Sqrt(sum));
    }

    public static float ColorScore(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        if (left.Length != ColorDimension || right.Length != ColorDimension)
            return float.NegativeInfinity;
        float chiSquare = 0;
        for (var i = 0; i < ColorHueBins; i++)
        {
            var denominator = MathF.Max(1e-6f, left[i] + right[i]);
            var difference = left[i] - right[i];
            chiSquare += difference * difference / denominator;
        }
        var weights = new[] { 1.8f, 1.2f, 1.8f, 1.2f, 1.1f, 1.1f, 1.3f, 1f };
        float statistics = 0;
        for (var i = 0; i < weights.Length; i++)
        {
            var difference = left[ColorHueBins + i] - right[ColorHueBins + i];
            statistics += weights[i] * difference * difference;
        }
        return 1f / (1f + MathF.Sqrt(0.5f * chiSquare + statistics));
    }

    private static SKBitmap LoadResized(string path)
    {
        using var source = SKBitmap.Decode(path)
            ?? throw new InvalidOperationException($"无法解码图片: {path}");
        return source.Resize(
            new SKSizeI(Edge, Edge),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
            ?? throw new InvalidOperationException($"无法缩放图片: {path}");
    }

    private static (float Hue, float Saturation, float Value) ToHsv(SKColor color)
    {
        var red = color.Red / 255f;
        var green = color.Green / 255f;
        var blue = color.Blue / 255f;
        var maximum = MathF.Max(red, MathF.Max(green, blue));
        var minimum = MathF.Min(red, MathF.Min(green, blue));
        var delta = maximum - minimum;
        var saturation = maximum <= 1e-6f ? 0 : delta / maximum;
        float hue;
        if (delta <= 1e-6f) hue = 0;
        else if (maximum == red) hue = 60f * (((green - blue) / delta + 6f) % 6f);
        else if (maximum == green) hue = 60f * ((blue - red) / delta + 2f);
        else hue = 60f * ((red - green) / delta + 4f);
        return (hue, saturation, maximum);
    }
}
