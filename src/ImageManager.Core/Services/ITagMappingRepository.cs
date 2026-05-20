using ImageManager.Core.Models;

namespace ImageManager.Core.Services;

public interface ITagMappingRepository
{
    Task<TagMapping?> GetByEnglishNameAsync(string englishName);
    Task<List<TagMapping>> GetAllAsync();
    Task UpsertAsync(string englishName, string chineseName);
    Task DeleteAsync(string englishName);
}
