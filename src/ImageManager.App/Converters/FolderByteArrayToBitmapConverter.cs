using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace ImageManager.App.Converters;

/// <summary>
/// Folder cards are virtualized, so they must not retain a global decoded-bitmap
/// cache after their containers leave the viewport.
/// </summary>
public sealed class FolderByteArrayToBitmapConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not byte[] data || data.Length == 0)
            return null;

        using var stream = new MemoryStream(data);
        return new Bitmap(stream);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
