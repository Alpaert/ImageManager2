namespace ImageManager.App.ViewModels;

public partial class MainWindowViewModel
{
    /// <summary>
    /// Normalize paths before comparing tree nodes.
    /// </summary>
    private static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        return path
            .TrimEnd('\\', '/')
            .Replace('/', '\\');
    }
}
