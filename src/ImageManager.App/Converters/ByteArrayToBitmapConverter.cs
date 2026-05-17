using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace ImageManager.App.Converters;

public class ByteArrayToBitmapConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is byte[] data && data.Length > 0)
        {
            using var ms = new MemoryStream(data);
            return new Bitmap(ms);
        }
        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
