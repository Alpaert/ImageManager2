namespace ImageManager.Core.Models;

public static class ImagePaging
{
    public const int Min = 100;
    public const int Max = 500;
    public const int Default = 200;

    public static int Clamp(int value) =>
        value <= 0 ? Default : Math.Clamp(value, Min, Max);
}
