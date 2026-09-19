using System.Diagnostics;
using System.Runtime.CompilerServices;
using ImageManager.Common.Helpers;

namespace ImageManager.Infrastructure.Data;

/// <summary>Serializes metadata read/modify/write operations sharing the application's database factory.</summary>
internal static class MetadataWriteCoordinator
{
    private static readonly ConditionalWeakTable<IDbContextFactory, SemaphoreSlim> Gates = new();

    public static async Task<IDisposable> EnterAsync(IDbContextFactory factory, CancellationToken ct = default)
    {
        var gate = Gates.GetValue(factory, _ => new SemaphoreSlim(1, 1));
        var watch = Stopwatch.StartNew();
        await gate.WaitAsync(ct).ConfigureAwait(false);
        if (watch.ElapsedMilliseconds >= 100)
            AppLogger.Info($"MetadataWrite waitMs={watch.ElapsedMilliseconds}");
        return new Lease(gate);
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
