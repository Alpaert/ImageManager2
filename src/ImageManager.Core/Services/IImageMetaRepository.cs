using ImageManager.Core.Models;

namespace ImageManager.Core.Services;

/// <summary>
/// Composite repository interface providing metadata, tagging, hash, and lifecycle access for images.
/// Decomposed into smaller focused interfaces: <see cref="IImageMetaReadRepository"/>,
/// <see cref="IImageMetaWriteRepository"/>, <see cref="IImageTagRepository"/>, and <see cref="IImageHashRepository"/>.
/// </summary>
public interface IImageMetaRepository :
    IImageMetaReadRepository,
    IImageMetaWriteRepository,
    IImageTagRepository,
    IImageHashRepository
{
}
