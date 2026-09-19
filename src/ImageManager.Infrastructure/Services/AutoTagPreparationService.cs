using System.Diagnostics;
using System.Security.Cryptography;
using ImageManager.Common.Constants;
using ImageManager.Common.Helpers;
using ImageManager.Core.Models;
using ImageManager.Core.Services;

namespace ImageManager.Infrastructure.Services;

/// <summary>Shared, model-free preparation for folder and selected-image tagging.</summary>
public sealed class AutoTagPreparationService(IImageMetaRepository repository)
{
    public async Task<AutoTagPreparationResult> PrepareAsync(long folderId, IEnumerable<string> paths,
        Action<AutoTagPipelineProgress>? progress = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var files = paths.Where(FileTypeConstants.IsImageFile).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var watch = Stopwatch.StartNew();
        void Report(string phase, int done, string message) =>
            progress?.Invoke(new AutoTagPipelineProgress(phase, done, files.Count, message));

        Report("Filter", 0, $"正在检查打标状态：0/{files.Count}");
        var statuses = await repository.GetStatusMapByPathsAsync(files, ct,
            done => Report("Filter", done, $"正在检查打标状态：{done}/{files.Count}"));
        var skipped = files.Count(p => statuses.TryGetValue(p, out var s) && s.Status == 1);
        var missing = files.Where(p => !statuses.ContainsKey(p)).ToList();
        var filterMs = watch.ElapsedMilliseconds;
        AppLogger.Info($"AutoTag.Filter total={files.Count} skipped={skipped} unregistered={missing.Count} elapsedMs={filterMs}");
        Report("Prepare", files.Count, $"已跳过 {skipped} 张，待处理 {files.Count - skipped} 张，其中新文件 {missing.Count} 张");

        var failed = 0;
        var prepared = 0;
        long hashMs = 0, registerMs = 0;
        foreach (var chunk in missing.Chunk(100))
        {
            ct.ThrowIfCancellationRequested();
            var registrations = new List<AutoTagFileRegistration>(chunk.Length);
            foreach (var path in chunk)
            {
                ct.ThrowIfCancellationRequested();
                var fileWatch = Stopwatch.StartNew();
                try
                {
                    var info = new FileInfo(path);
                    var size = info.Length;
                    var ticks = info.LastWriteTimeUtc.Ticks;
                    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                        64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    var md5 = Convert.ToHexString(await MD5.HashDataAsync(stream, ct)).ToLowerInvariant();
                    info.Refresh();
                    if (!info.Exists || info.Length != size || info.LastWriteTimeUtc.Ticks != ticks)
                        throw new IOException("文件在读取期间发生变化，请重试");
                    ct.ThrowIfCancellationRequested();
                    var match = await repository.GetByFileHashAsync(md5);
                    var isMove = match != null && !File.Exists(match.FilePath);
                    registrations.Add(new AutoTagFileRegistration(new ImageMeta
                    {
                        FilePath = path, FileHash = md5, FolderId = folderId > 0 ? folderId : null,
                        FileSize = size, LastWriteTicks = ticks
                    }, isMove ? match!.Id : null, isMove ? match!.FilePath : null));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    failed++;
                    AppLogger.Warn($"AutoTag.Prepare failed file={path}: {ex.Message}");
                }
                hashMs += fileWatch.ElapsedMilliseconds;
                prepared++;
                if (prepared % 10 == 0 || prepared == missing.Count)
                    Report("Prepare", prepared, $"已跳过 {skipped} 张，正在读取新文件：{prepared}/{missing.Count}，失败 {failed}");
            }

            ct.ThrowIfCancellationRequested();
            if (registrations.Count == 0) continue;
            Report("Register", prepared, $"正在登记新文件：{prepared}/{missing.Count}");
            var registrationWatch = Stopwatch.StartNew();
            // A database failure aborts the run rather than sending invalid image IDs into inference.
            var registered = await repository.RegisterAutoTagFilesAsync(registrations, ct);
            registerMs += registrationWatch.ElapsedMilliseconds;
            foreach (var entry in registered)
                statuses[entry.Key] = entry.Value;
        }

        ct.ThrowIfCancellationRequested();
        var images = new List<(long Id, string FilePath)>();
        skipped = 0;
        foreach (var path in files)
        {
            if (!statuses.TryGetValue(path, out var status) || status.Id <= 0) continue;
            if (status.Status == 1) skipped++;
            else images.Add((status.Id, path));
        }
        AppLogger.Info($"AutoTag.Prepare total={files.Count} skipped={skipped} pending={images.Count} failed={failed} " +
            $"filterMs={filterMs} hashMs={hashMs} registerMs={registerMs} elapsedMs={watch.ElapsedMilliseconds}");
        return new AutoTagPreparationResult(images, files.Count, skipped, failed);
    }
}
