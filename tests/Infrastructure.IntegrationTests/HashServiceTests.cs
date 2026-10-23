using System.Text;
using ImageManager.Infrastructure.Hashing;
using Xunit;

namespace Infrastructure.IntegrationTests;

public sealed class HashServiceTests : IDisposable
{
    private readonly string _testRoot;
    private readonly HashService _hashService;

    public HashServiceTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "IM2-HashTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
        _hashService = new HashService();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testRoot))
                Directory.Delete(_testRoot, true);
        }
        catch { }
    }

    [Fact]
    public void ComputeMD5FromBytes_And_ComputeFileHash_MatchesKnownMD5()
    {
        // Standard RFC 1321 test vector: "The quick brown fox jumps over the lazy dog"
        const string standardText = "The quick brown fox jumps over the lazy dog";
        const string expectedMd5 = "9e107d9d372bb6826bd81d3542a419d6";

        var bytes = Encoding.UTF8.GetBytes(standardText);
        var filePath = Path.Combine(_testRoot, "fox.txt");
        File.WriteAllBytes(filePath, bytes);

        var bytesHash = HashService.ComputeMD5FromBytes(bytes);
        var fileHash = _hashService.ComputeFileHash(filePath);

        // Independent known value assertion
        Assert.Equal(expectedMd5, bytesHash);
        Assert.Equal(expectedMd5, fileHash);
        Assert.Equal(32, bytesHash.Length);

        // Boundary: empty array standard MD5
        var emptyHash = HashService.ComputeMD5FromBytes(Array.Empty<byte>());
        Assert.Equal("d41d8cd98f00b204e9800998ecf8427e", emptyHash);
    }

    [Fact]
    public void HammingDistance_CalculatesAccurateDistance_AndHandlesBoundaryCases()
    {
        // Identical
        Assert.Equal(0, _hashService.HammingDistance("101010", "101010"));

        // 2 differences
        Assert.Equal(2, _hashService.HammingDistance("101010", "100011"));

        // Length mismatch
        Assert.Equal(int.MaxValue, _hashService.HammingDistance("101", "1010"));

        // Null or empty
        Assert.Equal(int.MaxValue, _hashService.HammingDistance(null, "101"));
        Assert.Equal(int.MaxValue, _hashService.HammingDistance("101", ""));
    }

    [Fact]
    public void ComputeCombinedPerceptualHash_PatternedImage_ProducesConsistentFormatAndValuesAcrossEntryPoints()
    {
        // Create non-solid patterned image (checkerboard contrast pattern)
        using var bitmap = new SkiaSharp.SKBitmap(64, 64);
        using var canvas = new SkiaSharp.SKCanvas(bitmap);
        canvas.Clear(SkiaSharp.SKColors.White);

        using var blackPaint = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.Black };
        canvas.DrawRect(0, 0, 32, 32, blackPaint);
        canvas.DrawRect(32, 32, 32, 32, blackPaint);

        using var encoded = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        var bytes = encoded.ToArray();
        var filePath = Path.Combine(_testRoot, "pattern.png");
        File.WriteAllBytes(filePath, bytes);

        // Compute via byte entry, file entry, and instance method
        var hashFromBytes = HashService.ComputeCombinedPerceptualHashFromBytes(bytes);
        var hashFromFile = HashService.ComputeCombinedPerceptualHashFromFile(filePath);
        var hashFromInstance = _hashService.ComputePerceptualHash(bytes);

        // Byte entry and instance entry must match exactly
        Assert.False(string.IsNullOrEmpty(hashFromBytes));
        Assert.Equal(hashFromBytes, hashFromInstance);

        // Deterministic stability across repeated calls
        Assert.Equal(hashFromBytes, HashService.ComputeCombinedPerceptualHashFromBytes(bytes));
        Assert.Equal(hashFromFile, HashService.ComputeCombinedPerceptualHashFromFile(filePath));

        // File-based decode uses ThumbnailGenerator.DecodeForHashInput pipeline;
        // verify that ComputeCombinedPerceptualHashFromFile matches ComputeCombinedPerceptualHashFromBytes on decoded input:
        var hashInput = ImageManager.Infrastructure.Imaging.ThumbnailGenerator.DecodeForHashInput(filePath, 256);
        Assert.NotNull(hashInput);
        Assert.Equal(hashFromFile, HashService.ComputeCombinedPerceptualHashFromBytes(hashInput!));

        var partsBytes = hashFromBytes.Split('|');
        var partsFile = hashFromFile.Split('|');

        // Format validation: 4 segments (aHash | dHash | wHash | histogram) for both byte and file pipelines
        foreach (var parts in new[] { partsBytes, partsFile })
        {
            Assert.Equal(4, parts.Length);
            for (int i = 0; i < 3; i++)
            {
                var binarySegment = parts[i];
                Assert.Equal(64, binarySegment.Length);
                Assert.All(binarySegment, c => Assert.True(c == '0' || c == '1'));

                // Must NOT be trivial constant output like all zeros or all ones
                var onesCount = binarySegment.Count(c => c == '1');
                Assert.True(onesCount > 0 && onesCount < 64,
                    $"Segment {i} has abnormal bit count {onesCount}, indicating non-diverse or constant hash output.");
            }
            Assert.NotEmpty(parts[3]);
        }
    }

    [Fact]
    public void ComputeCombinedPerceptualHashFromBytes_CorruptedBytes_ReturnsEmptyString()
    {
        var corruptedBytes = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };
        var hash = HashService.ComputeCombinedPerceptualHashFromBytes(corruptedBytes);

        Assert.Equal(string.Empty, hash);
    }

    [Fact]
    public void ComputeCombinedPerceptualHashFromFile_NonExistentOrBadFile_ReturnsEmptyString()
    {
        var missingPath = Path.Combine(_testRoot, "missing.png");
        var hash1 = HashService.ComputeCombinedPerceptualHashFromFile(missingPath);
        Assert.Equal(string.Empty, hash1);

        var badFile = Path.Combine(_testRoot, "bad.png");
        File.WriteAllText(badFile, "This is not an image");
        var hash2 = HashService.ComputeCombinedPerceptualHashFromFile(badFile);
        Assert.Equal(string.Empty, hash2);
    }
}
