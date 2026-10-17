namespace ImageManager.Core.Models;

public sealed record VideoOriginalFrameResult(
    bool Success,
    bool AlreadyExisted,
    string? Path,
    string? Error);
