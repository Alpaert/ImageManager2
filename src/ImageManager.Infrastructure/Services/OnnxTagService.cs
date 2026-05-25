using ImageManager.Core.Services;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace ImageManager.Infrastructure.Services;

public class OnnxTagService : IAutoTagService, IDisposable
{
    private const string ModelRepo = "SmilingWolf/wd-swinv2-tagger-v3";
    private const string ModelFile = "model.onnx";
    private const string TagsFile = "selected_tags.csv";
    private const int InputSize = 448;

    private InferenceSession? _session;
    private string _inputName = string.Empty;
    private string _outputName = string.Empty;
    private string _modelShapeInfo = string.Empty;
    private string[] _tagNames = Array.Empty<string>();
    private int _predictCount;
    private static readonly HttpClient _http = new();
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private string _modelDir = string.Empty;

    public event Action<AutoTagProgress>? ProgressChanged;
    public bool IsModelLoaded => _session != null;

    public async Task LoadModelAsync(string modelPath)
    {
        await _initLock.WaitAsync();
        try
        {
            if (_session != null) return;

            _modelDir = modelPath;
            Directory.CreateDirectory(_modelDir);

            var onnxPath = Path.Combine(_modelDir, ModelFile);
            var tagsPath = Path.Combine(_modelDir, TagsFile);

            if (!File.Exists(onnxPath))
                await DownloadFileAsync($"{ModelRepo}/resolve/main/{ModelFile}", onnxPath, "模型");
            if (!File.Exists(tagsPath))
                await DownloadFileAsync($"{ModelRepo}/resolve/main/{TagsFile}", tagsPath, "标签");

            _session = await Task.Run(() => new InferenceSession(onnxPath));
            _inputName = _session.InputNames[0];
            _outputName = _session.OutputNames[0];
            var inMeta = _session.InputMetadata[_inputName];
            _modelShapeInfo = $"[{string.Join(",", inMeta.Dimensions)}]";
            _tagNames = await Task.Run(() => ParseTagsCsv(tagsPath));

            ProgressChanged?.Invoke(new AutoTagProgress(0, 0,
                $"模型已加载 输入:{_inputName} 形状:{_modelShapeInfo} 标签:{_tagNames.Length}个"));
        }
        finally { _initLock.Release(); }
    }

    public async Task<List<TagPrediction>> PredictAsync(string imagePath)
    {
        if (_session == null)
            throw new InvalidOperationException("Model not loaded");

        return await Task.Run(() =>
        {
            var tensor = Preprocess(imagePath);
            if (tensor == null)
            {
                if (Interlocked.Increment(ref _predictCount) == 1)
                    ProgressChanged?.Invoke(new AutoTagProgress(0, 0, "预处理失败: 图片无法解码"));
                return new List<TagPrediction>();
            }

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(_inputName, tensor)
            };

            using var results = _session.Run(inputs);
            var output = results[0].AsTensor<float>();
            var probs = output.ToArray(); // model already has sigmoid built-in

            int aboveThreshold = 0;
            float maxProb = 0;
            var predictions = new List<TagPrediction>(probs.Length);
            for (int i = 0; i < probs.Length && i < _tagNames.Length; i++)
            {
                if (probs[i] > maxProb) maxProb = probs[i];
                if (probs[i] >= 0.01f)
                {
                    predictions.Add(new TagPrediction(_tagNames[i], probs[i]));
                    aboveThreshold++;
                }
            }

            // Report diagnostic for first image
            if (Interlocked.Increment(ref _predictCount) == 1)
            {
                var sample = predictions.Take(3).Select(p => $"{p.TagName}({p.Confidence:F2})");
                ProgressChanged?.Invoke(new AutoTagProgress(0, 0,
                    $"首图推理: maxProb={maxProb:F4} aboveThreshold={aboveThreshold} " +
                    $"top3=[{string.Join(", ", sample)}] shape={_modelShapeInfo}"));
            }

            predictions.Sort((a, b) => b.Confidence.CompareTo(a.Confidence));
            return predictions;
        });
    }

    // Preprocessing: square white-pad → 448 resize → float32 0-255 → BGR → NHWC [1,448,448,3]
    private static DenseTensor<float>? Preprocess(string imagePath)
    {
        try
        {
            using var original = SKBitmap.Decode(imagePath);
            if (original == null) return null;

            int w = original.Width;
            int h = original.Height;
            int maxDim = Math.Max(w, h);

            // Pad to square with white (255,255,255), centered
            using var padded = new SKBitmap(maxDim, maxDim, original.ColorType, SKAlphaType.Opaque);
            padded.Erase(SKColors.White);
            using var canvas = new SKCanvas(padded);
            int offX = (maxDim - w) / 2;
            int offY = (maxDim - h) / 2;
            canvas.DrawBitmap(original, offX, offY);

            // Resize to 448×448 (BICUBIC)
            using var resized = padded.Resize(new SKSizeI(InputSize, InputSize),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            if (resized == null) return null;

            // NHWC tensor: [1, 448, 448, 3], float32, 0-255 range, BGR order
            var tensor = new DenseTensor<float>(new[] { 1, InputSize, InputSize, 3 });

            unsafe
            {
                byte* ptr = (byte*)resized.GetPixels().ToPointer();
                int stride = resized.RowBytes;
                int bpp = resized.BytesPerPixel;

                for (int y = 0; y < InputSize; y++)
                {
                    for (int x = 0; x < InputSize; x++)
                    {
                        int offset = y * stride + x * bpp;
                        float b, g, r;
                        if (resized.ColorType == SKColorType.Bgra8888)
                        {
                            b = ptr[offset];
                            g = ptr[offset + 1];
                            r = ptr[offset + 2];
                        }
                        else
                        {
                            r = ptr[offset];
                            g = ptr[offset + 1];
                            b = ptr[offset + 2];
                        }

                        tensor[0, y, x, 0] = b; // B
                        tensor[0, y, x, 1] = g; // G
                        tensor[0, y, x, 2] = r; // R
                    }
                }
            }

            return tensor;
        }
        catch
        {
            return null;
        }
    }

    private static string[] ParseTagsCsv(string csvPath)
    {
        var tags = new List<string>();
        foreach (var line in File.ReadLines(csvPath))
        {
            var parts = line.Split(',', 3);
            if (parts.Length >= 2 && int.TryParse(parts[0], out _))
                tags.Add(parts[1].Trim('"'));
        }
        return tags.ToArray();
    }

    private async Task DownloadFileAsync(string repoFile, string destPath, string label)
    {
        var url = $"https://huggingface.co/{repoFile}";
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var totalBytes = response.Content.Headers.ContentLength ?? -1;

        using var stream = await response.Content.ReadAsStreamAsync();
        using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write);

        var buffer = new byte[8192];
        long totalRead = 0;
        int read;
        int reportStep = 0;

        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            await fs.WriteAsync(buffer.AsMemory(0, read));
            totalRead += read;
            if (++reportStep % 100 == 0 && totalBytes > 0)
            {
                ProgressChanged?.Invoke(new AutoTagProgress(
                    (int)(totalRead / 1024 / 1024), (int)(totalBytes / 1024 / 1024),
                    $"下载{label}... {totalRead / 1024 / 1024}/{totalBytes / 1024 / 1024} MB"));
            }
        }
    }

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
        _initLock.Dispose();
    }
}
