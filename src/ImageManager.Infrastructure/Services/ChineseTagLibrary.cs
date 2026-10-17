using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ImageManager.Common.Helpers;
using ImageManager.Core.Services;

namespace ImageManager.Infrastructure.Services;

/// <summary>
/// 中文标签库 — 从用户预翻译的 CSV 文件直接加载 cnname 列。
/// 不调用任何翻译 API，纯本地查表。
/// 维护 英文→中文 和 中文→[英文列表] 双向索引，用于合并去重。
/// </summary>
public class ChineseTagLibrary : IChineseTagLibrary
{
    private readonly object _syncLock = new();
    private readonly Dictionary<string, string> _enToZh = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _zhToEns = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _registeredNames = new(StringComparer.OrdinalIgnoreCase);

    public int Count
    {
        get
        {
            lock (_syncLock) return _enToZh.Count;
        }
    }

    /// <summary>从模型 CSV 文件加载，读取最后一列作为中文翻译</summary>
    public void LoadFromModelCsv(string modelName, string csvPath)
    {
        if (!File.Exists(csvPath))
        {
            AppLogger.Warn($"中文标签 CSV 不存在: {csvPath}，将使用英文原名");
            return;
        }

        // pixai CSV: id,tag_id,name,category,... → name at index 2
        // WD CSV: tag_id,name,category,... → name at index 1
        int nameIdx = modelName == "wd14" ? 1 : 2;

        int loaded = 0;
        using var reader = new StreamReader(csvPath, System.Text.Encoding.GetEncoding(936));
        string? line;
        lock (_syncLock)
        {
            while ((line = reader.ReadLine()) != null)
            {
                var parts = line.Split(',');
                if (parts.Length <= nameIdx) continue;

                // skip header by checking if tag_id column parses as int
                int tagIdIdx = nameIdx - 1;
                if (!int.TryParse(parts[tagIdIdx], out _)) continue;

                var en = parts[nameIdx].Trim('"');
                var zh = parts[^1].Trim('"');  // cn_name / zh_name 在最后一列
                if (string.IsNullOrEmpty(zh) || zh == en) continue;  // 跳过未翻译的

                _enToZh[en] = zh;
                if (!_zhToEns.TryGetValue(zh, out var list))
                {
                    list = new List<string>();
                    _zhToEns[zh] = list;
                }
                if (!list.Contains(en, StringComparer.OrdinalIgnoreCase))
                    list.Add(en);
                loaded++;
            }
        }

        AppLogger.Info($"加载中文标签库 [{modelName}]: {csvPath} loaded={loaded} total={Count}");
    }

    // Rating 硬编码中文
    private static readonly Dictionary<string, string> RatingCnNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["general"] = "全年龄",
        ["sensitive"] = "敏感",
        ["questionable"] = "大尺度",
        ["explicit"] = "R18"
    };

    /// <summary>查英文标签对应的中文名，含 Rating 硬编码</summary>
    public string? Lookup(string englishTag)
    {
        if (string.IsNullOrEmpty(englishTag)) return null;
        lock (_syncLock)
        {
            if (_enToZh.TryGetValue(englishTag, out var zh)) return zh;
        }
        if (RatingCnNames.TryGetValue(englishTag, out var rzh)) return rzh;
        return null;
    }

    /// <summary>移除画师名映射</summary>
    public void RemoveArtistName(string name)
    {
        lock (_syncLock)
        {
            _enToZh.Remove(name);
            _registeredNames.Remove(name);
            _zhToEns.Remove(name);
        }
    }

    /// <summary>手动注册一条翻译（用于画师名等不在 CSV 中的条目）</summary>
    public void Register(string englishName, string chineseName)
    {
        lock (_syncLock)
        {
            RegisterCore(englishName, chineseName);
        }
    }

    private void RegisterCore(string englishName, string chineseName)
    {
        _enToZh[englishName] = chineseName;
        _registeredNames.Add(englishName);
        if (!_zhToEns.TryGetValue(chineseName, out var list))
        {
            list = new List<string>();
            _zhToEns[chineseName] = list;
        }
        if (!list.Contains(englishName, StringComparer.OrdinalIgnoreCase))
            list.Add(englishName);
    }

    /// <summary>原子替换全部已注册画师名</summary>
    public void ReplaceArtistNames(IReadOnlyDictionary<string, string> artistNames)
    {
        lock (_syncLock)
        {
            foreach (var name in _registeredNames.ToList())
            {
                _enToZh.Remove(name);
                if (_zhToEns.TryGetValue(name, out var list))
                {
                    list.Remove(name);
                    if (list.Count == 0) _zhToEns.Remove(name);
                }
            }
            _registeredNames.Clear();

            if (artistNames != null)
            {
                foreach (var (en, zh) in artistNames)
                {
                    RegisterCore(en, zh);
                }
            }
        }
    }

    /// <summary>保存画师名映射到文件（每行 english=chinese）</summary>
    public void SaveArtistNames(string path)
    {
        List<string> lines;
        lock (_syncLock)
        {
            if (_registeredNames.Count == 0) return;
            lines = _registeredNames
                .Where(n => _enToZh.ContainsKey(n))
                .Select(n => $"{n}={_enToZh[n]}")
                .ToList();
        }

        try
        {
            File.WriteAllLines(path, lines);
            AppLogger.Info($"画师名映射已保存: {path} count={lines.Count}");
        }
        catch (Exception ex) { AppLogger.Warn($"保存画师名映射失败: {ex.Message}"); }
    }

    /// <summary>从文件加载画师名映射</summary>
    public void LoadArtistNames(string path)
    {
        if (!File.Exists(path)) return;
        int loaded = 0;
        var lines = File.ReadAllLines(path);
        lock (_syncLock)
        {
            foreach (var line in lines)
            {
                var parts = line.Split('=', 2);
                if (parts.Length == 2)
                {
                    RegisterCore(parts[0], parts[1]);
                    loaded++;
                }
            }
        }
        if (loaded > 0)
            AppLogger.Info($"画师名映射已加载: {path} count={loaded}");
    }

    public void Clear()
    {
        lock (_syncLock)
        {
            _enToZh.Clear();
            _zhToEns.Clear();
            _registeredNames.Clear();
        }
    }

    /// <summary>获取某个中文标签对应的所有英文同义标签</summary>
    public IReadOnlyList<string> GetEnglishAliases(string chineseTag)
    {
        lock (_syncLock)
        {
            return _zhToEns.TryGetValue(chineseTag, out var list) ? list.ToList() : Array.Empty<string>();
        }
    }

    /// <summary>批量查找中文名</summary>
    public Dictionary<string, string?> LookupBatch(IEnumerable<string> englishTags)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in englishTags)
            result[tag] = Lookup(tag);
        return result;
    }
}
