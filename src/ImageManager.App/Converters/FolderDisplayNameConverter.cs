using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using ImageManager.App.ViewModels;
using ImageManager.Core.Models;

namespace ImageManager.App.Converters;

public class FolderDisplayNameConverter : IValueConverter
{
    public static readonly FolderDisplayNameConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path))
            return string.Empty;

        // Fallback: use last folder name
        var name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
        if (!string.IsNullOrEmpty(name))
            return name;

        return path;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
