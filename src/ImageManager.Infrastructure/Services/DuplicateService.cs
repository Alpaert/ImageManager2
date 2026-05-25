using System.Security.Cryptography;
using ImageManager.Core.Services;
using ImageManager.Infrastructure.Imaging;

namespace ImageManager.Infrastructure.Services;

public class DuplicateService : IDuplicateService
{
    private readonly IImageMetaRepository _metaRepo;

    public DuplicateService(IImageMetaRepository metaRepo)
    {
        _metaRepo = metaRepo;
    }

    public async Task<(int exactCount, int fuzzyCount)> DetectAndMoveDuplicatesAsync(
        IEnumerable<string> filePaths,
        string targetDir,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(targetDir);

        int exactCount = 0;
        var moved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var infos = new List<DuplicateImageInfo>();
        foreach (var file in filePaths)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var meta = await _metaRepo.GetByPathAsync(file);
                var (width, height) = ThumbnailGenerator.GetDimensions(file);

                var fileHash = meta?.FileHash;
                if (string.IsNullOrEmpty(fileHash))
                {
                    using var stream = File.OpenRead(file);
                    var hash = MD5.HashData(stream);
                    fileHash = Convert.ToHexString(hash).ToLowerInvariant();
                }

                long fileSize = meta?.FileSize ?? new FileInfo(file).Length;

                infos.Add(new DuplicateImageInfo
                {
                    FilePath = file,
                    FileHash = fileHash,
                    Width = width,
                    Height = height,
                    FileSize = fileSize
                });
            }
            catch { }
        }

        // Exact duplicates by MD5
        var exactGroups = infos
            .Where(i => !string.IsNullOrEmpty(i.FileHash))
            .GroupBy(i => i.FileHash)
            .Where(g => g.Count() > 1);

        foreach (var g in exactGroups)
        {
            if (ct.IsCancellationRequested) break;
            var list = g.ToList();
            var keep = list.OrderByDescending(x => (long)x.Width * x.Height)
                           .ThenByDescending(x => x.FileSize)
                           .First();

            foreach (var img in list)
            {
                if (string.Equals(img.FilePath, keep.FilePath, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (moved.Contains(img.FilePath)) continue;

                await MoveFileAsync(img.FilePath, targetDir);
                moved.Add(img.FilePath);
                exactCount++;
            }
        }

        return (exactCount, 0);
    }

    private async Task MoveFileAsync(string oldPath, string targetDir)
    {
        try
        {
            var fileName = Path.GetFileName(oldPath);
            var destPath = Common.Helpers.PathHelper.GetNonConflictingPath(
                Path.Combine(targetDir, fileName));
            File.Move(oldPath, destPath);

            var meta = await _metaRepo.GetByPathAsync(oldPath);
            if (meta != null)
            {
                meta.FilePath = destPath;
                await _metaRepo.UpsertAsync(meta);
            }
        }
        catch { }
    }

    private class DuplicateImageInfo
    {
        public string FilePath { get; set; } = string.Empty;
        public string? FileHash { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public long FileSize { get; set; }
    }
}
