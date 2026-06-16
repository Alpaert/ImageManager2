using ImageManager.Core.Models;

namespace ImageManager.Core.Services;

public interface IAutoTagStateRepository
{
    // AutoTagState
    Task<AutoTagState?> GetStateAsync(long folderId);
    Task UpsertStateAsync(AutoTagState state);
    Task DeleteStateAsync(long folderId);
}
