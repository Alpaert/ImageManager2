using System;
using System.Threading;
using System.Threading.Tasks;

namespace ImageManager.Core.Services;

public interface IChineseClipService : IDisposable
{
    string ModelDirectory { get; }
    void ValidateImageModelFiles();
    void ValidateTextModelFiles();
    Task<float[]> GetImageEmbeddingAsync(string imagePath, CancellationToken ct = default);
    Task<float[]> GetTextEmbeddingAsync(string text, CancellationToken ct = default);
}
