using System;
using System.Collections.Generic;
using System.Numerics;

namespace ImageManager.Infrastructure.Services.Inference;

/// <summary>
/// 提供基于 SIMD 硬件加速的向量数学运算与 Top-K 检索算法。
/// 专为 1024 维 PixAI / CLIP 等嵌入向量的余弦相似度比对与检索优化。
/// </summary>
public static class EmbeddingMath
{
    /// <summary>
    /// 对向量进行 L2 归一化（原地修改）。
    /// 若向量模长 <= 1e-10，则保持不变以避免除零或溢出。
    /// </summary>
    public static void Normalize(Span<float> vector)
    {
        if (vector.IsEmpty) return;

        int length = vector.Length;
        int count = Vector<float>.Count;
        int limit = length - count;
        int step = count * 2;
        int limit2 = length - step;

        var sum0 = Vector<float>.Zero;
        var sum1 = Vector<float>.Zero;
        int i = 0;

        while (i <= limit2)
        {
            var v0 = new Vector<float>(vector.Slice(i, count));
            var v1 = new Vector<float>(vector.Slice(i + count, count));
            sum0 += v0 * v0;
            sum1 += v1 * v1;
            i += step;
        }

        var sumVec = sum0 + sum1;

        while (i <= limit)
        {
            var v = new Vector<float>(vector.Slice(i, count));
            sumVec += v * v;
            i += count;
        }

        float sum = Vector.Dot(sumVec, Vector<float>.One);

        while (i < length)
        {
            sum += vector[i] * vector[i];
            i++;
        }

        float norm = MathF.Sqrt(sum);
        if (norm <= 1e-10f)
            return;

        float invNorm = 1.0f / norm;
        var invVec = new Vector<float>(invNorm);

        i = 0;
        while (i <= limit2)
        {
            var v0 = new Vector<float>(vector.Slice(i, count));
            var v1 = new Vector<float>(vector.Slice(i + count, count));
            (v0 * invVec).CopyTo(vector.Slice(i, count));
            (v1 * invVec).CopyTo(vector.Slice(i + count, count));
            i += step;
        }

        while (i <= limit)
        {
            var v = new Vector<float>(vector.Slice(i, count));
            (v * invVec).CopyTo(vector.Slice(i, count));
            i += count;
        }

        while (i < length)
        {
            vector[i] *= invNorm;
            i++;
        }
    }

    /// <summary>
    /// 使用 SIMD 硬件加速计算两个向量的点积。
    /// 自动处理向量尾部不足 SIMD 宽度的剩余元素。
    /// </summary>
    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
            throw new ArgumentException($"Vector length mismatch: a={a.Length}, b={b.Length}");

        int length = a.Length;
        int count = Vector<float>.Count;
        int limit = length - count;
        int step = count * 2;
        int limit2 = length - step;

        var sum0 = Vector<float>.Zero;
        var sum1 = Vector<float>.Zero;
        int i = 0;

        while (i <= limit2)
        {
            var va0 = new Vector<float>(a.Slice(i, count));
            var vb0 = new Vector<float>(b.Slice(i, count));
            sum0 += va0 * vb0;

            var va1 = new Vector<float>(a.Slice(i + count, count));
            var vb1 = new Vector<float>(b.Slice(i + count, count));
            sum1 += va1 * vb1;

            i += step;
        }

        var sumVec = sum0 + sum1;

        while (i <= limit)
        {
            var va = new Vector<float>(a.Slice(i, count));
            var vb = new Vector<float>(b.Slice(i, count));
            sumVec += va * vb;
            i += count;
        }

        float dot = Vector.Dot(sumVec, Vector<float>.One);

        while (i < length)
        {
            dot += a[i] * b[i];
            i++;
        }

        return dot;
    }

    /// <summary>
    /// 计算两个向量的余弦相似度。
    /// 若向量已经预归一化（isPreNormalized = true），直接调用 Dot(a, b) 加速；
    /// 默认支持未归一化向量，进行完整余弦相似度计算。
    /// </summary>
    public static double CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b, bool isPreNormalized = false)
    {
        if (a.Length != b.Length)
            throw new ArgumentException($"Vector length mismatch: a={a.Length}, b={b.Length}");

        if (isPreNormalized)
        {
            return Dot(a, b);
        }

        int length = a.Length;
        int count = Vector<float>.Count;
        int limit = length - count;

        var sumDot = Vector<float>.Zero;
        var sumNormA = Vector<float>.Zero;
        var sumNormB = Vector<float>.Zero;
        int i = 0;

        while (i <= limit)
        {
            var va = new Vector<float>(a.Slice(i, count));
            var vb = new Vector<float>(b.Slice(i, count));
            sumDot += va * vb;
            sumNormA += va * va;
            sumNormB += vb * vb;
            i += count;
        }

        double dot = Vector.Dot(sumDot, Vector<float>.One);
        double normA = Vector.Dot(sumNormA, Vector<float>.One);
        double normB = Vector.Dot(sumNormB, Vector<float>.One);

        while (i < length)
        {
            dot += (double)a[i] * b[i];
            normA += (double)a[i] * a[i];
            normB += (double)b[i] * b[i];
            i++;
        }

        if (normA < 1e-10 || normB < 1e-10)
            return 0.0;

        return dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }

    /// <summary>
    /// 计算两个预归一化向量的余弦相似度（等同于点积）。
    /// </summary>
    public static double CosineSimilarityNormalized(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
        => Dot(a, b);

    /// <summary>
    /// 针对字典的高性能 Top-K 检索算法（避免 LINQ 中间分配和闭包）。
    /// items 存储预归一化向量，queryNormalized 也需预先归一化。
    /// </summary>
    public static List<(string CharacterName, double Similarity)> FindTopK(
        IReadOnlyDictionary<string, float[]> items,
        ReadOnlySpan<float> queryNormalized,
        double minSimilarity,
        int maxResults)
    {
        if (items == null || items.Count == 0 || maxResults <= 0)
            return new List<(string CharacterName, double Similarity)>(0);

        if (maxResults == 1)
        {
            string? bestName = null;
            double bestSim = minSimilarity;
            bool found = false;

            foreach (var kv in items)
            {
                double sim = Dot(queryNormalized, kv.Value);
                if (sim >= minSimilarity)
                {
                    if (!found || sim > bestSim)
                    {
                        bestSim = sim;
                        bestName = kv.Key;
                        found = true;
                    }
                }
            }

            if (!found || bestName == null)
                return new List<(string CharacterName, double Similarity)>(0);

            return new List<(string CharacterName, double Similarity)>(1) { (bestName, bestSim) };
        }

        var topNames = new string[maxResults];
        var topSims = new double[maxResults];
        int count = 0;

        foreach (var kv in items)
        {
            double sim = Dot(queryNormalized, kv.Value);
            if (sim < minSimilarity)
                continue;

            if (count < maxResults)
            {
                int insertIdx = count;
                while (insertIdx > 0 && topSims[insertIdx - 1] < sim)
                {
                    topSims[insertIdx] = topSims[insertIdx - 1];
                    topNames[insertIdx] = topNames[insertIdx - 1];
                    insertIdx--;
                }
                topSims[insertIdx] = sim;
                topNames[insertIdx] = kv.Key;
                count++;
            }
            else
            {
                if (sim <= topSims[maxResults - 1])
                    continue;

                int insertIdx = maxResults - 1;
                while (insertIdx > 0 && topSims[insertIdx - 1] < sim)
                {
                    topSims[insertIdx] = topSims[insertIdx - 1];
                    topNames[insertIdx] = topNames[insertIdx - 1];
                    insertIdx--;
                }
                topSims[insertIdx] = sim;
                topNames[insertIdx] = kv.Key;
            }
        }

        var result = new List<(string CharacterName, double Similarity)>(count);
        for (int i = 0; i < count; i++)
        {
            result.Add((topNames[i], topSims[i]));
        }
        return result;
    }

    /// <summary>
    /// 泛型 Top-K 检索算法，基于固定大小有序数组维护 Top-K，避免 LINQ 分配。
    /// items 存储预归一化向量，queryNormalized 也需预先归一化。
    /// </summary>
    public static List<(TItem Item, double Similarity)> FindTopK<TItem>(
        IEnumerable<TItem> items,
        Func<TItem, float[]> getVector,
        ReadOnlySpan<float> queryNormalized,
        double minSimilarity,
        int maxResults)
    {
        if (items == null || maxResults <= 0)
            return new List<(TItem Item, double Similarity)>(0);

        if (maxResults == 1)
        {
            TItem? bestItem = default;
            double bestSim = minSimilarity;
            bool found = false;

            foreach (var item in items)
            {
                float[] vec = getVector(item);
                double sim = Dot(queryNormalized, vec);
                if (sim >= minSimilarity)
                {
                    if (!found || sim > bestSim)
                    {
                        bestSim = sim;
                        bestItem = item;
                        found = true;
                    }
                }
            }

            if (!found)
                return new List<(TItem Item, double Similarity)>(0);

            return new List<(TItem Item, double Similarity)>(1) { (bestItem!, bestSim) };
        }

        var topItems = new TItem[maxResults];
        var topSims = new double[maxResults];
        int count = 0;

        foreach (var item in items)
        {
            float[] vec = getVector(item);
            double sim = Dot(queryNormalized, vec);
            if (sim < minSimilarity)
                continue;

            if (count < maxResults)
            {
                int insertIdx = count;
                while (insertIdx > 0 && topSims[insertIdx - 1] < sim)
                {
                    topSims[insertIdx] = topSims[insertIdx - 1];
                    topItems[insertIdx] = topItems[insertIdx - 1];
                    insertIdx--;
                }
                topSims[insertIdx] = sim;
                topItems[insertIdx] = item;
                count++;
            }
            else
            {
                if (sim <= topSims[maxResults - 1])
                    continue;

                int insertIdx = maxResults - 1;
                while (insertIdx > 0 && topSims[insertIdx - 1] < sim)
                {
                    topSims[insertIdx] = topSims[insertIdx - 1];
                    topItems[insertIdx] = topItems[insertIdx - 1];
                    insertIdx--;
                }
                topSims[insertIdx] = sim;
                topItems[insertIdx] = item;
            }
        }

        var result = new List<(TItem Item, double Similarity)>(count);
        for (int i = 0; i < count; i++)
        {
            result.Add((topItems[i], topSims[i]));
        }
        return result;
    }
}
