namespace ImageManager.Core.Services;

public interface IDuplicateService
{
    /// <summary>
    /// 检测并移动重复图片，返回移动数量
    /// </summary>
    Task<(int exactCount, int fuzzyCount)> DetectAndMoveDuplicatesAsync(
        IEnumerable<string> filePaths,
        string targetDir,
        CancellationToken ct = default);
}
