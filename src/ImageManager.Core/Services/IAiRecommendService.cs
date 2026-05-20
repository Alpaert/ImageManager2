using ImageManager.Core.Models;

namespace ImageManager.Core.Services;

public interface IAiRecommendService
{
    Task<string> RecommendAsync(string userInput, List<TagMapping> tagMappings);
}
