using ImageManager.Core.Services;
using ImageManager.Infrastructure.Imaging;
using ImageManager.Infrastructure.Video;

namespace ImageManager.Infrastructure.Services;

/// <summary>媒体处理器工厂实现</summary>
public class MediaProcessorFactory : IMediaProcessorFactory
{
    private readonly IMediaProcessor[] _processors;

    public MediaProcessorFactory(VideoOriginalFrameCacheService originalFrames)
    {
        _processors = new IMediaProcessor[]
        {
            new ImageMediaProcessor(),
            new VideoMediaProcessor(originalFrames)
        };
    }

    public IMediaProcessor GetProcessor(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return _processors.FirstOrDefault(p => p.CanHandle(ext))
            ?? throw new NotSupportedException($"不支持的文件类型: {ext}");
    }
}
