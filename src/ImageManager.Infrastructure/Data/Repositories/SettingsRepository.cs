using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using Dapper;
using ImageManager.Core.Models;
using ImageManager.Core.Services;

namespace ImageManager.Infrastructure.Data.Repositories;

public class SettingsRepository : ISettingsRepository, IDisposable
{
    private readonly IDbContextFactory _dbFactory;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private static readonly PropertyInfo[] AppSettingProperties =
        typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance);

    public SettingsRepository(IDbContextFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<AppSettings> LoadAsync()
    {
        using var conn = _dbFactory.CreateConnection();
        var rows = await conn.QueryAsync<(string Key, string Value)>(
            "SELECT Key, Value FROM AppSetting");
        var dict = rows.ToDictionary(r => r.Key, r => r.Value, StringComparer.OrdinalIgnoreCase);
        if (!dict.ContainsKey(nameof(AppSettings.PerceptualSearchResultMode)) &&
            dict.TryGetValue("VectorSearchResultMode", out var legacySearchMode))
        {
            dict[nameof(AppSettings.PerceptualSearchResultMode)] = legacySearchMode;
        }

        var settings = new AppSettings();
        var props = AppSettingProperties;

        foreach (var prop in props)
        {
            if (!dict.TryGetValue(prop.Name, out var rawValue) || rawValue == null)
                continue;

            try
            {
                object? value = ConvertValue(rawValue, prop.PropertyType);
                prop.SetValue(settings, value);
            }
            catch { }
        }

        return settings;
    }

    public async Task SaveAsync(AppSettings settings)
    {
        await _saveLock.WaitAsync();
        try
        {
            using var conn = _dbFactory.CreateConnection();
            using var txn = conn.BeginTransaction();

            await conn.ExecuteAsync("DELETE FROM AppSetting", transaction: txn);

            var props = AppSettingProperties;
            foreach (var prop in props)
            {
                var value = prop.GetValue(settings);
                var str = value switch
                {
                    null => "",
                    bool b => b.ToString().ToLowerInvariant(),
                    double d => d.ToString(CultureInfo.InvariantCulture),
                    int i => i.ToString(),
                    string s => s,
                    List<string> list => string.Join("|", list),
                    Dictionary<string, string> dict => string.Join("||", dict.Select(kv => $"{kv.Key}={kv.Value}")),
                    _ => value.ToString()
                };

                await conn.ExecuteAsync(
                    "INSERT OR REPLACE INTO AppSetting (Key, Value) VALUES (@Key, @Value)",
                    new { Key = prop.Name, Value = str }, txn);
            }

            txn.Commit();
        }
        finally
        {
            _saveLock.Release();
        }
    }

    public void Dispose() => _saveLock.Dispose();

    private static object? ConvertValue(string raw, Type targetType)
    {
        if (targetType == typeof(string)) return raw;
        if (targetType == typeof(bool)) return bool.Parse(raw);
        if (targetType == typeof(double)) return double.Parse(raw, CultureInfo.InvariantCulture);
        if (targetType == typeof(int)) return int.Parse(raw);
        if (targetType == typeof(List<string>))
            return raw.Split('|', StringSplitOptions.RemoveEmptyEntries).ToList();

        if (targetType == typeof(Dictionary<string, string>))
        {
            var dict = new Dictionary<string, string>();
            foreach (var pair in raw.Split("||", StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                if (parts.Length == 2)
                    dict[parts[0]] = parts[1];
            }
            return dict;
        }

        var converter = TypeDescriptor.GetConverter(targetType);
        if (converter.CanConvertFrom(typeof(string)))
            return converter.ConvertFromString(raw);

        return null;
    }
}
