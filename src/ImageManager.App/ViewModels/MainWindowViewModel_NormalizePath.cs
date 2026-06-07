namespace ImageManager.App.ViewModels;

public partial class MainWindowViewModel
{
    /// <summary>
    /// 标准化路径：移除尾部斜杠，统一使用反斜杠
    /// </summary>
    private static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        // 移除尾部的斜杠（/ 或 \）
        path = path.TrimEnd('\\', '/');

        // 统一使用反斜杠（Windows 标准）
        path = path.Replace('/', '\\');

        return path;
    }
}
