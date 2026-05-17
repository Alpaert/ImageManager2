using ImageManager.Core.Models;

namespace ImageManager.Core.Services;

public interface ISettingsRepository
{
    Task<AppSettings> LoadAsync();
    Task SaveAsync(AppSettings settings);
}
