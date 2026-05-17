using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace ImageManager.App.Converters;

public class HexToBrushConverter : IValueConverter
{
    public static readonly HexToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is string hex && TryParse(hex, out var brush) ? brush : null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static bool TryParse(string hex, out IBrush? brush)
    {
        brush = null;
        hex = hex.Trim();
        if (hex.StartsWith("#")) hex = hex.Substring(1);

        try
        {
            byte a = 255, r = 0, g = 0, b = 0;
            if (hex.Length == 8)
            {
                a = byte.Parse(hex.Substring(0, 2), NumberStyles.HexNumber);
                r = byte.Parse(hex.Substring(2, 2), NumberStyles.HexNumber);
                g = byte.Parse(hex.Substring(4, 2), NumberStyles.HexNumber);
                b = byte.Parse(hex.Substring(6, 2), NumberStyles.HexNumber);
            }
            else if (hex.Length == 6)
            {
                r = byte.Parse(hex.Substring(0, 2), NumberStyles.HexNumber);
                g = byte.Parse(hex.Substring(2, 2), NumberStyles.HexNumber);
                b = byte.Parse(hex.Substring(4, 2), NumberStyles.HexNumber);
            }
            else return false;

            brush = new SolidColorBrush(new Color(a, r, g, b));
            return true;
        }
        catch
        {
            return false;
        }
    }
}
