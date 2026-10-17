namespace ImageManager.Infrastructure.Services;

public interface IFileSystemOperations
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    void CreateDirectory(string path);
    void MoveFile(string source, string destination);
    void MoveDirectory(string source, string destination);
    void DeleteFile(string path);
}

public sealed class DefaultFileSystemOperations : IFileSystemOperations
{
    public static DefaultFileSystemOperations Instance { get; } = new();

    public bool FileExists(string path) => File.Exists(path);
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public void MoveFile(string source, string destination) => File.Move(source, destination);
    public void MoveDirectory(string source, string destination) => Directory.Move(source, destination);
    public void DeleteFile(string path) => File.Delete(path);
}
