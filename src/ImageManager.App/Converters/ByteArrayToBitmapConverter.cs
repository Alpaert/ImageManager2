using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace ImageManager.App.Converters;

public class ByteArrayToBitmapConverter : IValueConverter
{
    // 缓存已解码的 Bitmap，避免 UI 线程重复 JPEG 解码
    // 使用引用相等：同一 byte[] 实例不解码两次；旧数组 GC 后缓存自动清除
    private static readonly ConditionalWeakTable<byte[], Bitmap> _cache = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not byte[] data || data.Length == 0)
            return null;

        if (_cache.TryGetValue(data, out var cached))
            return cached;

        using var ms = new MemoryStream(data);
        var bitmap = new Bitmap(ms);
        _cache.Add(data, bitmap);
        return bitmap;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
