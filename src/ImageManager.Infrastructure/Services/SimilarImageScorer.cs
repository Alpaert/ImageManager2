using System.Globalization;
using System.Numerics;

namespace ImageManager.Infrastructure.Services;

internal readonly record struct ParsedPerceptualHash(
    ulong Average,
    ulong Difference,
    ulong Perceptual,
    double[] Histogram);

internal static class SimilarImageScorer
{
    public const double HistogramThreshold = 0.35;
    public const int AThreshold = 8;
    public const int DThreshold = 8;
    public const int PThreshold = 10;
    public const float PerceptualFallbackThreshold = 0.55f;

    public static float DotProduct(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        var vectorSize = Vector<float>.Count;
        var index = 0;
        var sum = Vector<float>.Zero;
        for (; index <= left.Length - vectorSize; index += vectorSize)
            sum += new Vector<float>(left.Slice(index, vectorSize)) * new Vector<float>(right.Slice(index, vectorSize));
        var result = Vector.Dot(sum, Vector<float>.One);
        for (; index < left.Length; index++)
            result += left[index] * right[index];
        return result;
    }

    public static (float Score, bool IsStrict) ScorePerceptualMatch(
        in ParsedPerceptualHash baseHash,
        in ParsedPerceptualHash candidateHash)
    {
        var averageDistance = BitOperations.PopCount(baseHash.Average ^ candidateHash.Average);
        var differenceDistance = BitOperations.PopCount(baseHash.Difference ^ candidateHash.Difference);
        var perceptualDistance = BitOperations.PopCount(baseHash.Perceptual ^ candidateHash.Perceptual);
        var histogramSimilarity = CompareHistograms(baseHash.Histogram, candidateHash.Histogram);
        var votes = 0;
        if (averageDistance <= AThreshold) votes++;
        if (differenceDistance <= DThreshold) votes++;
        if (perceptualDistance <= PThreshold) votes++;
        var isStrict = histogramSimilarity >= HistogramThreshold && votes >= 2;

        var averageSimilarity = 1f - Math.Clamp(averageDistance / 64f, 0f, 1f);
        var differenceSimilarity = 1f - Math.Clamp(differenceDistance / 64f, 0f, 1f);
        var perceptualSimilarity = 1f - Math.Clamp(perceptualDistance / 64f, 0f, 1f);
        var score = 0.15f * averageSimilarity +
                    0.30f * differenceSimilarity +
                    0.40f * perceptualSimilarity +
                    0.15f * Math.Clamp(histogramSimilarity, 0f, 1f);
        return (score, isStrict);
    }

    public static bool TryParsePerceptualHash(string? value, out ParsedPerceptualHash hash)
    {
        hash = default;
        if (string.IsNullOrEmpty(value))
            return false;

        var parts = value.Split('|');
        if (parts.Length < 4 ||
            !TryParseBinaryHash(parts[0], out var average) ||
            !TryParseBinaryHash(parts[1], out var difference) ||
            !TryParseBinaryHash(parts[2], out var perceptual))
            return false;

        var histogramParts = parts[3].Split(',');
        if (histogramParts.Length == 0)
            return false;

        var histogram = new double[histogramParts.Length];
        for (var index = 0; index < histogramParts.Length; index++)
        {
            if (!double.TryParse(histogramParts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out histogram[index]))
                return false;
        }

        hash = new ParsedPerceptualHash(average, difference, perceptual, histogram);
        return true;
    }

    public static bool TryParseBinaryHash(string value, out ulong hash)
    {
        hash = 0;
        if (value.Length != 64)
            return false;

        foreach (var bit in value)
        {
            if (bit is not ('0' or '1'))
                return false;
            hash = (hash << 1) | (uint)(bit - '0');
        }
        return true;
    }

    public static float CompareHistograms(ReadOnlySpan<double> left, ReadOnlySpan<double> right)
    {
        if (left.Length != right.Length)
            return 0;

        double intersection = 0;
        for (var index = 0; index < left.Length; index++)
            intersection += Math.Min(left[index], right[index]);
        return (float)intersection;
    }
}
