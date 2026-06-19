using ImageManager.Common.Helpers;
using Microsoft.ML.OnnxRuntime;

namespace ImageManager.Infrastructure.Services;

internal static class OnnxSessionFactory
{
    private const long OneGb = 1024L * 1024 * 1024;
    private const long DefaultWdCudaMemoryLimitBytes = 1L * OneGb;
    private const long DefaultPixaiCudaMemoryLimitBytes = 2L * OneGb;
    private const long DefaultCudaMemoryLimitBytes = 2L * OneGb;

    public static SessionOptions CreateOptions()
    {
        return new SessionOptions
        {
            EnableMemoryPattern = false,
            EnableCpuMemArena = false
        };
    }

    public static InferenceSession Create(string onnxPath, string label)
    {
        var gpuMemLimitBytes = GetCudaMemoryLimitBytes(label);

        var optionsWithArena = new Dictionary<string, string>
        {
            ["device_id"] = "0",
            ["gpu_mem_limit"] = gpuMemLimitBytes.ToString(),
            ["arena_extend_strategy"] = "kSameAsRequested"
        };

        if (TryCreateCudaSession(onnxPath, label, gpuMemLimitBytes, optionsWithArena,
                "CUDA GPU enabled memLimit", out var session))
        {
            return session;
        }

        var optionsWithoutArena = new Dictionary<string, string>
        {
            ["device_id"] = "0",
            ["gpu_mem_limit"] = gpuMemLimitBytes.ToString()
        };

        if (TryCreateCudaSession(onnxPath, label, gpuMemLimitBytes, optionsWithoutArena,
                "CUDA GPU enabled memLimit without arena strategy", out session))
        {
            return session;
        }

        try
        {
            var opts = CreateOptions();
            opts.AppendExecutionProvider_CUDA(0);
            session = new InferenceSession(onnxPath, opts);
            AppLogger.Info($"[{label}] CUDA GPU enabled with default provider options");
            return session;
        }
        catch (Exception ex)
        {
            session?.Dispose();
            AppLogger.Warn($"[{label}] CUDA unavailable, falling back to CPU: {ex.Message}");
            using var opts = CreateOptions();
            return new InferenceSession(onnxPath, opts);
        }
    }

    private static bool TryCreateCudaSession(
        string onnxPath,
        string label,
        long gpuMemLimitBytes,
        Dictionary<string, string> cudaOptions,
        string successMessage,
        out InferenceSession session)
    {
        session = null!;
        InferenceSession? candidate = null;
        try
        {
            var opts = CreateOptions();
            using var cuda = new OrtCUDAProviderOptions();
            cuda.UpdateOptions(cudaOptions);
            opts.AppendExecutionProvider_CUDA(cuda);

            candidate = new InferenceSession(onnxPath, opts);
            AppLogger.Info($"[{label}] {successMessage}={gpuMemLimitBytes / 1024 / 1024}MB");
            session = candidate;
            return true;
        }
        catch (Exception ex)
        {
            candidate?.Dispose();
            AppLogger.Warn($"[{label}] CUDA provider options failed ({string.Join(",", cudaOptions.Keys)}): {ex.Message}");
            return false;
        }
    }

    private static long GetCudaMemoryLimitBytes(string label)
    {
        var normalized = label.Trim().ToUpperInvariant();
        var specificName = $"IMAGEMANAGER_ONNX_{normalized}_GPU_MEM_MB";
        if (TryReadLimitMb(specificName, out var specificMb))
            return specificMb * 1024L * 1024L;

        if (TryReadLimitMb("IMAGEMANAGER_ONNX_GPU_MEM_MB", out var globalMb))
            return globalMb * 1024L * 1024L;

        return normalized switch
        {
            "WD" or "WD14" => DefaultWdCudaMemoryLimitBytes,
            "PIXAI" => DefaultPixaiCudaMemoryLimitBytes,
            _ => DefaultCudaMemoryLimitBytes
        };
    }

    private static bool TryReadLimitMb(string name, out long mb)
    {
        mb = 0;
        var value = Environment.GetEnvironmentVariable(name);
        return long.TryParse(value, out mb) && mb > 0;
    }
}
