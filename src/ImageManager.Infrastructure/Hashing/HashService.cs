using System.Security.Cryptography;
using ImageManager.Core.Services;
using SkiaSharp;

namespace ImageManager.Infrastructure.Hashing;

public class HashService : IHashService
{
    // ==================== MD5 ====================

    public string ComputeFileHash(string filePath)
    {
        using var md5 = MD5.Create();
        using var stream = File.OpenRead(filePath);
        var hash = md5.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string ComputeMD5FromBytes(byte[] data)
    {
        var hash = MD5.HashData(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // ==================== Perceptual Hash ====================

    public string ComputePerceptualHash(byte[] imageData)
    {
        return ComputeCombinedPerceptualHashFromBytes(imageData);
    }

    public int HammingDistance(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            return int.MaxValue;
        if (a.Length != b.Length)
            return int.MaxValue;
        int d = 0;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) d++;
        return d;
    }

    // ==================== Combined Hash (single decode → 4 hashes) ====================

    /// <summary>
    /// Compute "aHash|dHash|pHash|histogram" from in-memory bytes.
    /// Decodes the image once, reuses the bitmap for all four hashes.
    /// </summary>
    public static string ComputeCombinedPerceptualHashFromBytes(byte[] imageData)
    {
        try
        {
            using var stream = new MemoryStream(imageData);
            using var codec = SKCodec.Create(stream);
            if (codec == null) return string.Empty;

            float scale = Math.Min(1f, 256f / Math.Max(codec.Info.Width, codec.Info.Height));
            var decodeSize = codec.GetScaledDimensions(scale);
            var decodeInfo = new SKImageInfo(decodeSize.Width, decodeSize.Height);
            using var original = SKBitmap.Decode(codec, decodeInfo);
            if (original == null) return string.Empty;

            var aHash = ComputeAHash(original, 8);
            var dHash = ComputeDHash(original, 8);
            var wHash = ComputeWHash(original, 32, 8);
            var hist = ComputeColorHistogram(original);
            return $"{aHash}|{dHash}|{wHash}|{hist}";
        }
        catch
        {
            return string.Empty;
        }
    }

    public static string ComputeCombinedPerceptualHashFromFile(string filePath)
    {
        try
        {
            var data = File.ReadAllBytes(filePath);
            return ComputeCombinedPerceptualHashFromBytes(data);
        }
        catch
        {
            return string.Empty;
        }
    }

    // ==================== aHash (Average Hash) ====================

    private static string ComputeAHash(SKBitmap original, int size)
    {
        using var resized = original.Resize(new SKSizeI(size, size),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        if (resized == null) return string.Empty;

        int total = size * size;
        long sum = 0;
        var gray = new byte[total];

        unsafe
        {
            byte* ptr = (byte*)resized.GetPixels().ToPointer();
            int stride = resized.RowBytes;
            int bpp = resized.BytesPerPixel;

            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int offset = y * stride + x * bpp;
                    byte g = (byte)(0.299 * ptr[offset] + 0.587 * ptr[offset + 1] + 0.114 * ptr[offset + 2]);
                    gray[y * size + x] = g;
                    sum += g;
                }
        }

        double avg = (double)sum / total;
        var sb = new System.Text.StringBuilder(total);
        for (int i = 0; i < total; i++)
            sb.Append(gray[i] >= avg ? '1' : '0');
        return sb.ToString();
    }

    // ==================== dHash (Difference Hash) ====================

    private static string ComputeDHash(SKBitmap original, int size)
    {
        using var resized = original.Resize(new SKSizeI(size + 1, size),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        if (resized == null) return string.Empty;

        int total = size * size;
        var sb = new System.Text.StringBuilder(total);

        unsafe
        {
            byte* ptr = (byte*)resized.GetPixels().ToPointer();
            int stride = resized.RowBytes;
            int bpp = resized.BytesPerPixel;

            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int offset = y * stride + x * bpp;
                    int nextOffset = y * stride + (x + 1) * bpp;
                    byte g1 = (byte)(0.299 * ptr[offset] + 0.587 * ptr[offset + 1] + 0.114 * ptr[offset + 2]);
                    byte g2 = (byte)(0.299 * ptr[nextOffset] + 0.587 * ptr[nextOffset + 1] + 0.114 * ptr[nextOffset + 2]);
                    sb.Append(g1 < g2 ? '1' : '0');
                }
        }

        return sb.ToString();
    }

    // ==================== pHash (DCT Hash) ====================

    private static string ComputePHash(SKBitmap original, int size, int hashSize)
    {
        using var resized = original.Resize(new SKSizeI(size, size),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        if (resized == null) return string.Empty;

        int total = size * size;
        var gray = new double[total];

        unsafe
        {
            byte* ptr = (byte*)resized.GetPixels().ToPointer();
            int stride = resized.RowBytes;
            int bpp = resized.BytesPerPixel;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    gray[y * size + x] = 0.299 * ptr[y * stride + x * bpp]
                                        + 0.587 * ptr[y * stride + x * bpp + 1]
                                        + 0.114 * ptr[y * stride + x * bpp + 2];
        }

        var dct = Compute2DDct(gray, size);
        if (dct == null) return string.Empty;

        int hc = hashSize;
        var lowFreq = new double[hc * hc];
        int idx = 0;
        for (int y = 0; y < hc; y++)
            for (int x = 0; x < hc; x++)
                lowFreq[idx++] = dct[y * size + x];

        var sorted = lowFreq.OrderBy(v => v).ToArray();
        double median = sorted[sorted.Length / 2];

        var sb = new System.Text.StringBuilder(hc * hc);
        for (int i = 0; i < lowFreq.Length; i++)
            sb.Append(lowFreq[i] > median ? '1' : '0');
        return sb.ToString();
    }

    // Separable 2D DCT-II: 1D on rows → 1D on columns. O(2N³) with precomputed cos table.
    private static double[] Compute2DDct(double[] pixels, int size)
    {
        int total = size * size;

        // Precompute cos(n,k) = cos((2n+1)*k*π/(2N))
        var cosTable = new double[size * size];
        double piOver2N = Math.PI / (2.0 * size);
        for (int n = 0; n < size; n++)
        {
            double nFactor = (2.0 * n + 1) * piOver2N;
            int nOffset = n * size;
            for (int k = 0; k < size; k++)
                cosTable[nOffset + k] = Math.Cos(nFactor * k);
        }

        // 1D DCT-II on each row
        var rowTransformed = new double[total];
        for (int y = 0; y < size; y++)
        {
            int rowOffset = y * size;
            for (int k = 0; k < size; k++)
            {
                double sum = 0;
                for (int n = 0; n < size; n++)
                    sum += pixels[rowOffset + n] * cosTable[n * size + k];
                rowTransformed[rowOffset + k] = sum;
            }
        }

        // 1D DCT-II on each column
        var result = new double[total];
        for (int x = 0; x < size; x++)
        {
            for (int k = 0; k < size; k++)
            {
                double sum = 0;
                for (int n = 0; n < size; n++)
                    sum += rowTransformed[n * size + x] * cosTable[n * size + k];
                result[k * size + x] = sum;
            }
        }
        return result;
    }

    // ==================== wHash (Walsh-Hadamard Transform) ====================

    /// <summary>
    /// wHash: Hadamard-based hash. Only uses addition/subtraction — 5-10× faster than DCT.
    /// Returns 64-bit string (8×8 low-freq coefficients, median threshold).
    /// </summary>
    private static string ComputeWHash(SKBitmap original, int size, int hashSize)
    {
        using var resized = original.Resize(new SKSizeI(size, size),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        if (resized == null) return string.Empty;

        // Convert to grayscale
        int total = size * size;
        var gray = new double[total];
        unsafe
        {
            byte* ptr = (byte*)resized.GetPixels().ToPointer();
            int stride = resized.RowBytes;
            int bpp = resized.BytesPerPixel;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int offset = y * stride + x * bpp;
                    gray[y * size + x] = 0.299 * ptr[offset]
                                        + 0.587 * ptr[offset + 1]
                                        + 0.114 * ptr[offset + 2];
                }
        }

        // 2D Hadamard transform (separable: rows then columns)
        Hadamard2D(gray, size);

        // Extract low-frequency coefficients (exclude DC at [0,0])
        int hc = hashSize;
        var lowFreq = new double[hc * hc];
        int idx = 0;
        for (int y = 0; y < hc; y++)
            for (int x = 0; x < hc; x++)
                lowFreq[idx++] = gray[y * size + x];

        double median = lowFreq.OrderBy(v => v).ToArray()[lowFreq.Length / 2];

        var sb = new System.Text.StringBuilder(hc * hc);
        for (int i = 0; i < lowFreq.Length; i++)
            sb.Append(lowFreq[i] > median ? '1' : '0');
        return sb.ToString();
    }

    // Fast Walsh-Hadamard Transform: in-place, O(N log N), only +/- operations.
    private static void Hadamard1D(double[] data, int n)
    {
        for (int step = 1; step < n; step <<= 1)
        {
            for (int i = 0; i < n; i += step * 2)
            {
                for (int j = 0; j < step; j++)
                {
                    double a = data[i + j];
                    double b = data[i + j + step];
                    data[i + j] = a + b;
                    data[i + j + step] = a - b;
                }
            }
        }
    }

    private static void Hadamard2D(double[] data, int size)
    {
        // 1D on each row
        for (int y = 0; y < size; y++)
        {
            var row = new double[size];
            int rowOffset = y * size;
            Array.Copy(data, rowOffset, row, 0, size);
            Hadamard1D(row, size);
            Array.Copy(row, 0, data, rowOffset, size);
        }
        // 1D on each column
        for (int x = 0; x < size; x++)
        {
            var col = new double[size];
            for (int y = 0; y < size; y++)
                col[y] = data[y * size + x];
            Hadamard1D(col, size);
            for (int y = 0; y < size; y++)
                data[y * size + x] = col[y];
        }
    }

    // ==================== Color Histogram ====================

    private static string ComputeColorHistogram(SKBitmap original)
    {
        int maxDim = 100;
        float scale = Math.Min(1.0f, (float)maxDim / Math.Max(original.Width, original.Height));
        int w = Math.Max(1, (int)(original.Width * scale));
        int h = Math.Max(1, (int)(original.Height * scale));

        using var resized = original.Resize(new SKSizeI(w, h),
            new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
        if (resized == null) return string.Empty;

        int bins = 4;
        int totalBins = bins * bins * bins;
        int[] hist = new int[totalBins];

        unsafe
        {
            byte* ptr = (byte*)resized.GetPixels().ToPointer();
            int stride = resized.RowBytes;
            int bpp = resized.BytesPerPixel;

            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int offset = y * stride + x * bpp;
                    int r = ptr[offset] * bins / 256;
                    int g = ptr[offset + 1] * bins / 256;
                    int b = ptr[offset + 2] * bins / 256;
                    if (r >= bins) r = bins - 1;
                    if (g >= bins) g = bins - 1;
                    if (b >= bins) b = bins - 1;
                    hist[(r * bins + g) * bins + b]++;
                }
        }

        int totalPixels = w * h;
        var parts = new string[totalBins];
        for (int i = 0; i < totalBins; i++)
            parts[i] = (hist[i] / (float)totalPixels).ToString("F4",
                System.Globalization.CultureInfo.InvariantCulture);
        return string.Join(",", parts);
    }

    // ==================== Similarity Comparison ====================

    /// <summary>Histogram intersection: 0.0–1.0</summary>
    public static double CompareHistograms(string? histA, string? histB)
    {
        if (string.IsNullOrEmpty(histA) || string.IsNullOrEmpty(histB)) return 0;
        try
        {
            var partsA = histA.Split(',');
            var partsB = histB.Split(',');
            if (partsA.Length != partsB.Length) return 0;

            double intersection = 0;
            for (int i = 0; i < partsA.Length; i++)
            {
                double a = double.Parse(partsA[i], System.Globalization.CultureInfo.InvariantCulture);
                double b = double.Parse(partsB[i], System.Globalization.CultureInfo.InvariantCulture);
                intersection += Math.Min(a, b);
            }
            return intersection;
        }
        catch { return 0; }
    }

    /// <summary>
    /// Multi-hash voting: 2 of 3 must pass.
    /// aHash ≤ aThreshold (8), dHash ≤ dThreshold (8), pHash ≤ pThreshold (10).
    /// </summary>
    public static bool AreSimilarByMultiHash(string? combinedA, string? combinedB,
        int aThreshold = 8, int dThreshold = 8, int pThreshold = 10)
    {
        if (string.IsNullOrEmpty(combinedA) || string.IsNullOrEmpty(combinedB))
            return false;

        var partsA = combinedA.Split('|');
        var partsB = combinedB.Split('|');

        if (partsA.Length < 3 || partsB.Length < 3)
            return HammingDistanceStatic(combinedA, combinedB) <= aThreshold;

        int aDist = HammingDistanceStatic(partsA[0], partsB[0]);
        int dDist = HammingDistanceStatic(partsA[1], partsB[1]);
        int pDist = HammingDistanceStatic(partsA[2], partsB[2]);

        int votes = 0;
        if (aDist <= aThreshold) votes++;
        if (dDist <= dThreshold) votes++;
        if (pDist <= pThreshold) votes++;

        return votes >= 2;
    }

    private static int HammingDistanceStatic(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return int.MaxValue;
        if (a.Length != b.Length) return int.MaxValue;
        int d = 0;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) d++;
        return d;
    }
}
