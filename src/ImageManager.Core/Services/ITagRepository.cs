using ImageManager.Core.Models;

namespace ImageManager.Core.Services;

public enum RenameResult { Success, Conflict, Cancelled }

public interface ITagRepository
{
    Task<long> GetOrCreateTagIdAsync(string name);
    Task<List<TagCount>> GetAllTagCountsAsync();
    Task AddFavoriteAsync(string name);
    Task RemoveFavoriteAsync(string name);
    Task<List<string>> GetFavoritesAsync();
    Task<RenameResult> RenameTagAsync(string oldName, string newName);
    Task MergeTagsAsync(string oldName, string newName);
    Task<List<string>> SearchTagsAsync(string keyword, int limit = 50);
}
