using ImageManager.Core.Models;

namespace ImageManager.Core.Services;

public interface IAutoTagStateRepository
{
    // AutoTagState
    Task<AutoTagState?> GetStateAsync(long folderId);
    Task UpsertStateAsync(AutoTagState state);
    Task DeleteStateAsync(long folderId);

    // AutoTagTranslation
    Task<List<(string EnglishTag, string? ChineseTranslation, string? UserEditedText,
        bool IsConfirmed, bool IsExistingMapping)>> GetTranslationsAsync(long folderId);
    Task SaveTranslationAsync(long folderId, string englishTag,
        string? chineseTranslation, string? userEditedText,
        bool isConfirmed, bool isExistingMapping);
    Task DeleteTranslationsAsync(long folderId);
    Task DeleteTranslationAsync(long folderId, string englishTag);
}
