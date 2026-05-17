namespace ImageManager.Core.Services;

public interface IHashService
{
    string ComputeFileHash(string filePath);
    string ComputePerceptualHash(byte[] imageData);
    int HammingDistance(string? a, string? b);
}
