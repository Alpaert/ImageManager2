using System.Collections.Generic;

namespace ImageManager.Core.Services;

public interface IChineseTagLibrary
{
    int Count { get; }
    void LoadFromModelCsv(string modelName, string csvPath);
    string? Lookup(string englishTag);
    void RemoveArtistName(string name);
    void Register(string englishName, string chineseName);
    void ReplaceArtistNames(IReadOnlyDictionary<string, string> artistNames);
    void SaveArtistNames(string path);
    void LoadArtistNames(string path);
    void Clear();
    IReadOnlyList<string> GetEnglishAliases(string chineseTag);
    Dictionary<string, string?> LookupBatch(IEnumerable<string> englishTags);
}
