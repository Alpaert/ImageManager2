using ImageManager.Core.Models;

namespace ImageManager.Core.Services;

public interface ITagRepository
{
    Task<long> GetOrCreateTagIdAsync(string name);
    Task<List<TagCount>> GetAllTagCountsAsync();
    Task AddFavoriteAsync(string name);
    Task RemoveFavoriteAsync(string name);
    Task<List<string>> GetFavoritesAsync();
    Task<List<string>> SearchTagsAsync(string keyword, int limit = 50);
}
