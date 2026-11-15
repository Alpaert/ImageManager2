using System.Security.Cryptography;
using ImageManager.Infrastructure.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace ImageManager.Infrastructure.Services;

public sealed class SscdEmbeddingService(string modelsRoot) : IDisposable
{
    public const string ModelKey = "sscd";
    public const string ModelVersion = "disc-mixup-512-v1";
    public const int Dimension = 512;
    public const string ModelFile = "sscd-disc-mixup-512-v1.onnx";
    private const string ModelSha256 = "2298fb558fae30b8464a15d3cab5d2878bf7b86e05abbbe39acba1d49dc80f6e";
    private const int InputSize = 320;
    private readonly string _modelPath = Path.Combine(modelsRoot, "sscd", ModelFile);
    private readonly object _sessionLock = new();
    private InferenceSession? _session;
    private bool _disposed;

    public bool IsAvailable => File.Exists(_modelPath);

    public Task ValidateModelAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        lock (_sessionLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ct.ThrowIfCancellationRequested();
            _session ??= CreateSession();
        }
    }, ct);

    public Task<float[]> EncodeAsync(SKBitmap bitmap, CancellationToken ct = default) =>
        Task.Run(() => Encode(bitmap, ct), ct);

    private float[] Encode(SKBitmap bitmap, CancellationToken ct)
    {
        var tensor = Prepare(bitmap);
        lock (_sessionLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ct.ThrowIfCancellationRequested();
            _session ??= CreateSession();
            var input = NamedOnnxValue.CreateFromTensor("pixels", tensor);
            using var results = _session.Run([input]);
            ct.ThrowIfCancellationRequested();
            var vector = results.Single().AsTensor<float>().ToArray();
            if (vector.Length != Dimension)
                throw new InvalidDataException("SSCD 输出维度无效。");

            double squareSum = 0;
            foreach (var value in vector)
                squareSum += (double)value * value;
            if (!double.IsFinite(squareSum) || squareSum == 0)
                throw new InvalidDataException("SSCD 输出向量无效。");
            var norm = Math.Sqrt(squareSum);
            for (var i = 0; i < vector.Length; i++)
                vector[i] = (float)(vector[i] / norm);
            return vector;
        }
    }

    private InferenceSession CreateSession()
    {
        if (!IsAvailable)
            throw new FileNotFoundException("请先安装 SSCD 模型。", _modelPath);
        using (var file = File.OpenRead(_modelPath))
        {
            var hash = Convert.ToHexString(SHA256.HashData(file));
            if (!hash.Equals(ModelSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("SSCD 模型校验失败，请重新安装经过验证的模型。");
            return OnnxSessionFactory.Create(_modelPath, ModelKey);
        }
    }

    private static DenseTensor<float> Prepare(SKBitmap source)
    {
        if (source.Width <= 0 || source.Height <= 0)
            throw new InvalidDataException("SSCD 输入图片为空。");

        var limit = InferenceImageLoader.DefaultDecodeMaxSize;
        var scale = Math.Min(1d, (double)limit / Math.Max(source.Width, source.Height));
        var width = Math.Max(1, (int)(source.Width * scale));
        var height = Math.Max(1, (int)(source.Height * scale));
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
        using var opaque = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(opaque))
        using (var image = SKImage.FromBitmap(source))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawImage(image, new SKRect(0, 0, width, height), sampling);
        }

        using var resized = opaque.Resize(new SKSizeI(InputSize, InputSize), sampling)
            ?? throw new InvalidDataException("无法缩放 SSCD 输入图片。");
        var tensor = new DenseTensor<float>(new[] { 1, 3, InputSize, InputSize });
        WritePixels(resized, tensor.Buffer.Span);
        return tensor;
    }

    private static unsafe void WritePixels(SKBitmap bitmap, Span<float> tensor)
    {
        const int plane = InputSize * InputSize;
        var pixels = (byte*)bitmap.GetPixels();
        for (var y = 0; y < InputSize; y++)
        {
            var row = pixels + y * bitmap.RowBytes;
            for (var x = 0; x < InputSize; x++)
            {
                var i = y * InputSize + x;
                var pixel = row + 4 * x;
                tensor[i] = (pixel[0] / 255f - .485f) / .229f;
                tensor[i + plane] = (pixel[1] / 255f - .456f) / .224f;
                tensor[i + 2 * plane] = (pixel[2] / 255f - .406f) / .225f;
            }
        }
    }

    public void ReleaseSession()
    {
        lock (_sessionLock)
        {
            _session?.Dispose();
            _session = null;
        }
    }

    public void Dispose()
    {
        lock (_sessionLock)
        {
            _disposed = true;
            ReleaseSession();
        }
    }
}
