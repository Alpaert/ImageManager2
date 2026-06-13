namespace ImageManager.Core.Services;

/// <summary>媒体处理器工厂接口</summary>
public interface IMediaProcessorFactory
{
    /// <summary>根据文件路径获取对应的处理器</summary>
    IMediaProcessor GetProcessor(string filePath);
}
