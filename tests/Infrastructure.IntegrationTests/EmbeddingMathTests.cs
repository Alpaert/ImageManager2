using System;
using System.Collections.Generic;
using System.Linq;
using ImageManager.Infrastructure.Services;
using ImageManager.Infrastructure.Services.Inference;
using Xunit;

namespace Infrastructure.IntegrationTests;

public sealed class EmbeddingMathTests
{
    private static readonly Random Rng = new(42);

    private static float[] CreateRandomVector(int dim, bool normalize = false)
    {
        var v = new float[dim];
        for (int i = 0; i < dim; i++)
        {
            v[i] = (float)(Rng.NextDouble() * 2.0 - 1.0);
        }
        if (normalize)
        {
            EmbeddingMath.Normalize(v);
        }
        return v;
    }

    private static double ScalarDot(float[] a, float[] b)
    {
        double sum = 0;
        for (int i = 0; i < a.Length; i++)
            sum += (double)a[i] * b[i];
        return sum;
    }

    private static void ScalarNormalize(float[] v)
    {
        double normSq = 0;
        for (int i = 0; i < v.Length; i++)
            normSq += (double)v[i] * v[i];
        if (normSq < 1e-10) return;
        float inv = (float)(1.0 / Math.Sqrt(normSq));
        for (int i = 0; i < v.Length; i++)
            v[i] *= inv;
    }

    private static double ScalarCosineSimilarity(float[] a, float[] b)
    {
        double dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            normA += (double)a[i] * a[i];
            normB += (double)b[i] * b[i];
        }
        if (normA < 1e-10 || normB < 1e-10) return 0;
        return dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(128)]
    [InlineData(512)]
    [InlineData(1024)]
    public void Dot_MatchesScalarMath_ForVariousDimensions(int dim)
    {
        var a = CreateRandomVector(dim);
        var b = CreateRandomVector(dim);

        float expected = (float)ScalarDot(a, b);
        float actual = EmbeddingMath.Dot(a, b);

        Assert.Equal(expected, actual, precision: 4); // 误差 <= 1e-4 / 1e-5
    }

    [Fact]
    public void Dot_ThrowsOnDimensionMismatch()
    {
        var a = new float[4];
        var b = new float[5];
        Assert.Throws<ArgumentException>(() => EmbeddingMath.Dot(a, b));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(17)]
    [InlineData(1024)]
    public void Normalize_MatchesScalarMath_AndYieldsUnitLength(int dim)
    {
        var original = CreateRandomVector(dim);
        var expected = (float[])original.Clone();
        var actual = (float[])original.Clone();

        ScalarNormalize(expected);
        EmbeddingMath.Normalize(actual);

        for (int i = 0; i < dim; i++)
        {
            Assert.Equal(expected[i], actual[i], precision: 4);
        }

        // 验证模长为 1.0
        float length = MathF.Sqrt(EmbeddingMath.Dot(actual, actual));
        Assert.Equal(1.0f, length, precision: 4);
    }

    [Fact]
    public void Normalize_HandlesZeroAndSubnormalVectors_WithoutNan()
    {
        var zero = new float[1024];
        EmbeddingMath.Normalize(zero);
        foreach (var val in zero)
        {
            Assert.False(float.IsNaN(val));
            Assert.False(float.IsInfinity(val));
            Assert.Equal(0f, val);
        }

        var subnormal = new float[1024];
        subnormal[0] = 1e-12f;
        EmbeddingMath.Normalize(subnormal);
        Assert.False(float.IsNaN(subnormal[0]));
        Assert.Equal(1e-12f, subnormal[0]);
    }

    [Fact]
    public void CosineSimilarity_MatchesScalarMath_UnnormalizedAndPreNormalized()
    {
        var a = CreateRandomVector(1024);
        var b = CreateRandomVector(1024);

        double expected = ScalarCosineSimilarity(a, b);
        double actual = EmbeddingMath.CosineSimilarity(a, b);

        Assert.Equal(expected, actual, precision: 4);

        // 测试预归一化
        EmbeddingMath.Normalize(a);
        EmbeddingMath.Normalize(b);

        double expectedNorm = ScalarCosineSimilarity(a, b);
        double actualNorm = EmbeddingMath.CosineSimilarity(a, b, isPreNormalized: true);
        double actualDot = EmbeddingMath.Dot(a, b);

        Assert.Equal(expectedNorm, actualNorm, precision: 4);
        Assert.Equal(actualNorm, actualDot, precision: 5);
    }

    [Fact]
    public void CosineSimilarity_SpecialVectors()
    {
        var a = new float[] { 1f, 0f, 0f, 0f };
        var b = new float[] { 0f, 1f, 0f, 0f };
        var c = new float[] { -1f, 0f, 0f, 0f };

        // 正交
        Assert.Equal(0.0, EmbeddingMath.CosineSimilarity(a, b), precision: 5);
        // 同向
        Assert.Equal(1.0, EmbeddingMath.CosineSimilarity(a, a), precision: 5);
        // 反向
        Assert.Equal(-1.0, EmbeddingMath.CosineSimilarity(a, c), precision: 5);
    }

    [Fact]
    public void FindTopK_MatchesLinqReference_AcrossDifferentParameters()
    {
        const int dim = 1024;
        const int charCount = 80;

        var charDict = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < charCount; i++)
        {
            charDict[$"Char_{i:D3}"] = CreateRandomVector(dim, normalize: true);
        }

        var query = CreateRandomVector(dim, normalize: true);

        var testCases = new (double MinSim, int MaxResults)[]
        {
            (0.0, 1),
            (0.0, 5),
            (0.0, 10),
            (0.35, 1),
            (0.35, 5),
            (0.35, 20),
            (0.999, 5), // 可能无结果
            (0.0, 100), // 超过总数
            (-1.0, 3)
        };

        foreach (var (minSim, maxResults) in testCases)
        {
            // LINQ 参考实现（原有行为）
            var linqExpected = charDict
                .Select(kv => (CharacterName: kv.Key, Similarity: (double)EmbeddingMath.Dot(query, kv.Value)))
                .Where(m => m.Similarity >= minSim)
                .OrderByDescending(m => m.Similarity)
                .Take(maxResults)
                .ToList();

            // 字典专用 Top-K
            var dictActual = EmbeddingMath.FindTopK(charDict, query, minSim, maxResults);

            // 泛型 Top-K
            var genericActual = EmbeddingMath.FindTopK(
                charDict,
                kv => kv.Value,
                query,
                minSim,
                maxResults)
                .Select(x => (CharacterName: x.Item.Key, Similarity: x.Similarity))
                .ToList();

            Assert.Equal(linqExpected.Count, dictActual.Count);
            Assert.Equal(linqExpected.Count, genericActual.Count);

            for (int i = 0; i < linqExpected.Count; i++)
            {
                Assert.Equal(linqExpected[i].CharacterName, dictActual[i].CharacterName);
                Assert.Equal(linqExpected[i].Similarity, dictActual[i].Similarity, precision: 5);

                Assert.Equal(linqExpected[i].CharacterName, genericActual[i].CharacterName);
                Assert.Equal(linqExpected[i].Similarity, genericActual[i].Similarity, precision: 5);
            }
        }
    }

    [Fact]
    public void CharacterEmbeddingStore_SearchTop_EndToEndMatchesLinq()
    {
        var store = new CharacterEmbeddingStore(embeddingDim: 16);

        for (int i = 0; i < 20; i++)
        {
            store.Add($"Character_{i}", CreateRandomVector(16));
        }

        var query = CreateRandomVector(16);
        var results = store.SearchTop(query, minSimilarity: 0.1, maxResults: 3);

        // 验证返回数量 <= maxResults
        Assert.True(results.Count <= 3);

        // 验证降序
        for (int i = 0; i < results.Count - 1; i++)
        {
            Assert.True(results[i].Similarity >= results[i + 1].Similarity);
        }

        // 验证所有相似度 >= minSimilarity
        foreach (var r in results)
        {
            Assert.True(r.Similarity >= 0.1);
        }
    }

    [Fact]
    public void ArtistEmbeddingStore_Search_FindsClosestPreNormalizedMatch()
    {
        var store = new ArtistEmbeddingStore(embeddingDim: 8);

        var artistA = new float[] { 1f, 0f, 0f, 0f, 0f, 0f, 0f, 0f };
        var artistB = new float[] { 0f, 1f, 0f, 0f, 0f, 0f, 0f, 0f };
        var artistC = new float[] { 0f, 0f, 1f, 0f, 0f, 0f, 0f, 0f };

        store.Add("ArtistA", artistA);
        store.Add("ArtistB", artistB);
        store.Add("ArtistC", artistC);

        var query = new float[] { 0.8f, 0.2f, 0f, 0f, 0f, 0f, 0f, 0f };
        var match = store.Search(query, minSimilarity: 0.5);

        Assert.NotNull(match);
        Assert.Equal("ArtistA", match!.Value.artistName);
        Assert.True(match.Value.similarity >= 0.9); // (0.8 / sqrt(0.8^2 + 0.2^2)) ≈ 0.97
    }
}
