using System.Globalization;
using System.Text;
using ImageManager.Common.Helpers;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace ImageManager.Infrastructure.Services;

public sealed class ChineseClipService : IDisposable
{
    public const string ModelKey = "chinese-clip-vit-base-patch16";
    public const string ModelVersion = "v2";

    private const int ImageSize = 224;
    private const int MaxTextLength = 52;
    private static readonly TimeSpan SessionIdleTimeout = TimeSpan.FromMinutes(1);
    private static readonly float[] Mean = [0.48145466f, 0.4578275f, 0.40821073f];
    private static readonly float[] Std = [0.26862954f, 0.26130258f, 0.27577711f];

    private readonly string _modelDirectory;
    private readonly SemaphoreSlim _imageLock = new(1, 1);
    private readonly SemaphoreSlim _textLock = new(1, 1);
    private readonly object _sessionLock = new();
    private readonly object _idleLock = new();
    private InferenceSession? _imageSession;
    private InferenceSession? _textSession;
    private CancellationTokenSource? _imageIdleCts;
    private CancellationTokenSource? _textIdleCts;
    private Dictionary<string, int>? _vocabulary;
    private bool _disposed;

    public ChineseClipService(string cacheDirectory)
    {
        _modelDirectory = Path.Combine(cacheDirectory, "models", ModelKey);
    }

    public string ModelDirectory => _modelDirectory;

    public void ValidateImageModelFiles()
    {
        EnsureFile(Path.Combine(_modelDirectory, "onnx", "chinese_clip_image_encoder.onnx"));
        EnsureFile(Path.Combine(_modelDirectory, "preprocessor_config.json"));
    }

    public void ValidateTextModelFiles()
    {
        EnsureFile(Path.Combine(_modelDirectory, "onnx", "chinese_clip_text_encoder.onnx"));
        EnsureFile(Path.Combine(_modelDirectory, "vocab.txt"));
    }

    public async Task<float[]> GetImageEmbeddingAsync(string imagePath, CancellationToken ct = default)
    {
        CancelIdleRelease(isImage: true);
        try
        {
            await _imageLock.WaitAsync(ct);
            try
            {
                var session = EnsureImageSession();
                var tensor = await Task.Run(() => PreprocessImage(imagePath), ct);
                var inputName = session.InputMetadata.ContainsKey("pixel_values")
                    ? "pixel_values"
                    : session.InputMetadata.Keys.First();
                var input = NamedOnnxValue.CreateFromTensor(inputName, tensor);
                using var results = session.Run([input]);
                return Normalize(results.First().AsTensor<float>().ToArray());
            }
            finally
            {
                _imageLock.Release();
            }
        }
        finally
        {
            ResetIdleRelease(isImage: true);
        }
    }

    public async Task<float[]> GetTextEmbeddingAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        CancelIdleRelease(isImage: false);
        try
        {
            await _textLock.WaitAsync(ct);
            try
            {
                var session = EnsureTextSession();
                var (inputIds, attentionMask) = EncodeText(text, EnsureVocabulary());
                var idsName = session.InputMetadata.ContainsKey("input_ids")
                    ? "input_ids"
                    : session.InputMetadata.Keys.First();
                var maskName = session.InputMetadata.ContainsKey("attention_mask")
                    ? "attention_mask"
                    : session.InputMetadata.Keys.Skip(1).First();
                var ids = NamedOnnxValue.CreateFromTensor(idsName, inputIds);
                var mask = NamedOnnxValue.CreateFromTensor(maskName, attentionMask);
                using var results = session.Run([ids, mask]);
                return Normalize(results.First().AsTensor<float>().ToArray());
            }
            finally
            {
                _textLock.Release();
            }
        }
        finally
        {
            ResetIdleRelease(isImage: false);
        }
    }

    public void ReleaseImageSession()
    {
        CancelIdleRelease(isImage: true);
        _imageLock.Wait();
        try
        {
            lock (_sessionLock)
            {
                _imageSession?.Dispose();
                _imageSession = null;
            }
        }
        finally { _imageLock.Release(); }
    }

    public void ReleaseAllSessions()
    {
        CancelIdleRelease(isImage: true);
        CancelIdleRelease(isImage: false);
        _imageLock.Wait();
        _textLock.Wait();
        try
        {
            lock (_sessionLock)
            {
                _imageSession?.Dispose();
                _textSession?.Dispose();
                _imageSession = null;
                _textSession = null;
            }
        }
        finally
        {
            _textLock.Release();
            _imageLock.Release();
        }
    }

    private void CancelIdleRelease(bool isImage)
    {
        lock (_idleLock)
        {
            var current = isImage ? _imageIdleCts : _textIdleCts;
            current?.Cancel();
            current?.Dispose();
            if (isImage)
                _imageIdleCts = null;
            else
                _textIdleCts = null;
        }
    }

    private void ResetIdleRelease(bool isImage)
    {
        lock (_idleLock)
        {
            if (_disposed)
                return;

            var current = isImage ? _imageIdleCts : _textIdleCts;
            current?.Cancel();
            current?.Dispose();
            var next = new CancellationTokenSource();
            if (isImage)
                _imageIdleCts = next;
            else
                _textIdleCts = next;
            _ = ReleaseSessionAfterIdleAsync(isImage, next, next.Token);
        }
    }

    private async Task ReleaseSessionAfterIdleAsync(
        bool isImage,
        CancellationTokenSource owner,
        CancellationToken ct)
    {
        try
        {
            await Task.Delay(SessionIdleTimeout, ct);
            var inferenceLock = isImage ? _imageLock : _textLock;
            await inferenceLock.WaitAsync(ct);
            try
            {
                lock (_idleLock)
                {
                    var current = isImage ? _imageIdleCts : _textIdleCts;
                    if (!ReferenceEquals(current, owner) || ct.IsCancellationRequested)
                        return;
                    if (isImage)
                        _imageIdleCts = null;
                    else
                        _textIdleCts = null;
                }

                var released = false;
                lock (_sessionLock)
                {
                    if (isImage && _imageSession != null)
                    {
                        _imageSession.Dispose();
                        _imageSession = null;
                        released = true;
                    }
                    else if (!isImage && _textSession != null)
                    {
                        _textSession.Dispose();
                        _textSession = null;
                        released = true;
                    }
                }

                if (released)
                {
                    var label = isImage ? "image" : "text";
                    AppLogger.Info($"[chinese-clip-{label}] 1 minute idle, released ONNX session");
                    AppLogger.Memory($"ChineseClip.{label}.AfterIdleDispose");
                }
            }
            finally
            {
                inferenceLock.Release();
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            owner.Dispose();
        }
    }

    private InferenceSession EnsureImageSession()
    {
        lock (_sessionLock)
        {
            if (_imageSession != null)
                return _imageSession;
            ValidateImageModelFiles();
            var path = Path.Combine(_modelDirectory, "onnx", "chinese_clip_image_encoder.onnx");
            _imageSession = OnnxSessionFactory.Create(path, "chinese-clip-image");
            return _imageSession;
        }
    }

    private InferenceSession EnsureTextSession()
    {
        lock (_sessionLock)
        {
            if (_textSession != null)
                return _textSession;
            ValidateTextModelFiles();
            var path = Path.Combine(_modelDirectory, "onnx", "chinese_clip_text_encoder.onnx");
            _textSession = OnnxSessionFactory.Create(path, "chinese-clip-text");
            return _textSession;
        }
    }

    private Dictionary<string, int> EnsureVocabulary()
    {
        if (_vocabulary != null)
            return _vocabulary;

        var path = Path.Combine(_modelDirectory, "vocab.txt");
        EnsureFile(path);
        _vocabulary = File.ReadLines(path, Encoding.UTF8)
            .Select((token, index) => (Token: token.TrimEnd('\r', '\n'), Index: index))
            .Where(item => item.Token.Length > 0)
            .ToDictionary(item => item.Token, item => item.Index, StringComparer.Ordinal);
        return _vocabulary;
    }

    private static DenseTensor<float> PreprocessImage(string imagePath)
    {
        using var source = SKBitmap.Decode(imagePath)
            ?? throw new InvalidOperationException($"无法解码图片: {imagePath}");
        using var resized = source.Resize(
            new SKSizeI(ImageSize, ImageSize),
            new SKSamplingOptions(SKCubicResampler.CatmullRom))
            ?? throw new InvalidOperationException($"无法缩放图片: {imagePath}");

        var tensor = new DenseTensor<float>([1, 3, ImageSize, ImageSize]);
        for (var y = 0; y < ImageSize; y++)
        {
            for (var x = 0; x < ImageSize; x++)
            {
                var color = resized.GetPixel(x, y);
                tensor[0, 0, y, x] = (color.Red / 255f - Mean[0]) / Std[0];
                tensor[0, 1, y, x] = (color.Green / 255f - Mean[1]) / Std[1];
                tensor[0, 2, y, x] = (color.Blue / 255f - Mean[2]) / Std[2];
            }
        }
        return tensor;
    }

    private static (DenseTensor<long> InputIds, DenseTensor<long> AttentionMask) EncodeText(
        string text,
        IReadOnlyDictionary<string, int> vocabulary)
    {
        foreach (var required in new[] { "[CLS]", "[SEP]", "[PAD]", "[UNK]" })
        {
            if (!vocabulary.ContainsKey(required))
                throw new InvalidOperationException($"vocab.txt 缺少 {required}");
        }

        var tokens = new List<string>();
        foreach (var token in BasicTokenize(text))
            tokens.AddRange(WordPieceTokenize(token, vocabulary));
        if (tokens.Count > MaxTextLength - 2)
            tokens.RemoveRange(MaxTextLength - 2, tokens.Count - (MaxTextLength - 2));

        var inputIds = new DenseTensor<long>([1, MaxTextLength]);
        var attentionMask = new DenseTensor<long>([1, MaxTextLength]);
        var padId = vocabulary["[PAD]"];
        for (var i = 0; i < MaxTextLength; i++)
            inputIds[0, i] = padId;

        var ids = new List<int> { vocabulary["[CLS]"] };
        ids.AddRange(tokens.Select(token => vocabulary.TryGetValue(token, out var id) ? id : vocabulary["[UNK]"]));
        ids.Add(vocabulary["[SEP]"]);
        for (var i = 0; i < ids.Count; i++)
        {
            inputIds[0, i] = ids[i];
            attentionMask[0, i] = 1;
        }
        return (inputIds, attentionMask);
    }

    private static IEnumerable<string> BasicTokenize(string text)
    {
        var normalized = RemoveAccents(text.ToLowerInvariant());
        var current = new StringBuilder();
        foreach (var character in normalized)
        {
            if (char.IsControl(character) && character is not '\t' and not '\n' and not '\r')
                continue;
            if (char.IsWhiteSpace(character))
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }
                continue;
            }
            if (IsChinese(character) || char.IsPunctuation(character) || char.IsSymbol(character))
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }
                yield return character.ToString();
                continue;
            }
            current.Append(character);
        }
        if (current.Length > 0)
            yield return current.ToString();
    }

    private static IEnumerable<string> WordPieceTokenize(
        string token,
        IReadOnlyDictionary<string, int> vocabulary)
    {
        if (token.Length > 100)
            return ["[UNK]"];

        var pieces = new List<string>();
        var start = 0;
        while (start < token.Length)
        {
            string? match = null;
            var end = token.Length;
            while (start < end)
            {
                var part = token[start..end];
                if (start > 0)
                    part = "##" + part;
                if (vocabulary.ContainsKey(part))
                {
                    match = part;
                    break;
                }
                end--;
            }
            if (match == null)
                return ["[UNK]"];
            pieces.Add(match);
            start = end;
        }
        return pieces;
    }

    private static string RemoveAccents(string text)
    {
        var builder = new StringBuilder();
        foreach (var character in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                builder.Append(character);
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static bool IsChinese(char character) =>
        character is >= '\u3400' and <= '\u9fff' or >= '\uf900' and <= '\ufaff';

    private static float[] Normalize(float[] values)
    {
        double sum = 0;
        foreach (var value in values)
            sum += value * value;
        if (sum <= 1e-18)
            return values;
        var inverse = 1.0 / Math.Sqrt(sum);
        for (var i = 0; i < values.Length; i++)
            values[i] = (float)(values[i] * inverse);
        return values;
    }

    private static void EnsureFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Chinese-CLIP 模型文件不存在: {path}", path);
    }

    public void Dispose()
    {
        lock (_idleLock)
            _disposed = true;
        ReleaseAllSessions();
        _imageLock.Dispose();
        _textLock.Dispose();
    }
}
