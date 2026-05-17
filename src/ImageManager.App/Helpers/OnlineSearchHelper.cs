using System.Net;

namespace ImageManager.App.Helpers;

public static class OnlineSearchHelper
{
    private static readonly HttpClient _http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(10)
    })
    {
        Timeout = TimeSpan.FromSeconds(60)
    };

    private static readonly Dictionary<string, (string Url, string FileField)> _engines = new()
    {
        ["saucenao"] = ("https://saucenao.com/search.php", "file"),
        ["iqdb"] = ("https://iqdb.org/", "file"),
        ["ascii2d"] = ("https://ascii2d.net/search/file", "file"),
        ["tracemoe"] = ("https://api.trace.moe/search", "image"),
    };

    private static readonly Dictionary<string, string> _homeUrls = new()
    {
        ["tracemoe"] = "https://trace.moe/",
        ["saucenao"] = "https://saucenao.com/",
        ["yandex"] = "https://yandex.ru/images/search",
        ["google"] = "https://lens.google.com/",
        ["ascii2d"] = "https://ascii2d.net/",
        ["iqdb"] = "https://www.iqdb.org/",
        ["soutubot"] = "https://soutubot.moe/",
    };

    private static string _tempDir = Path.Combine(Path.GetTempPath(), "ImageManagerSearch");

    public static void SetTempDir(string dir)
    {
        _tempDir = dir;
    }

    public static void CleanupOldTempFiles()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                foreach (var f in Directory.GetFiles(_tempDir, "*.*"))
                {
                    try { File.Delete(f); } catch { }
                }
            }
        }
        catch { }
    }

    /// <summary>Try to auto-upload image to search engine. Returns true if fully automatic, false if fallback needed.</summary>
    public static async Task<bool> SearchAsync(string imagePath, string engine)
    {
        if (!_engines.TryGetValue(engine, out var cfg))
            return false;

        if (!File.Exists(imagePath)) return false;

        try
        {
            // Use stream for file upload to avoid loading entire file into memory
            using var form = new MultipartFormDataContent();
            var ext = Path.GetExtension(imagePath).ToLowerInvariant();
            var mime = ext switch
            {
                ".png" => "image/png",
                ".bmp" => "image/bmp",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                _ => "image/jpeg"
            };

            var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var fileContent = new StreamContent(stream);
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mime);
            form.Add(fileContent, cfg.FileField, Path.GetFileName(imagePath));

            if (engine == "tracemoe")
                form.Add(new StringContent(""), "anilistInfo");

            using var response = await _http.PostAsync(cfg.Url, form);

            if (IsRedirect(response.StatusCode) && response.Headers.Location != null)
            {
                var redirectUrl = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location.ToString()
                    : new Uri(new Uri(cfg.Url), response.Headers.Location).ToString();

                if (engine != "tracemoe")
                {
                    OpenUrl(redirectUrl);
                    return true;
                }
            }

            var html = await response.Content.ReadAsStringAsync();
            if (!string.IsNullOrWhiteSpace(html))
            {
                if (engine == "tracemoe")
                {
                    var resultUrl = ParseTraceMoeResult(html);
                    if (resultUrl != null)
                    {
                        OpenUrl(resultUrl);
                        return true;
                    }
                    return false;
                }

                var tempPath = Path.Combine(_tempDir, $"imgsearch_{Guid.NewGuid():N}.html");
                Directory.CreateDirectory(_tempDir);

                var baseUri = new Uri(cfg.Url);
                var baseTag = $"<base href=\"{baseUri.Scheme}://{baseUri.Host}/\">";
                if (html.Contains("<head>", StringComparison.OrdinalIgnoreCase))
                    html = html.Replace("<head>", $"<head>\n{baseTag}", StringComparison.OrdinalIgnoreCase);
                else if (html.Contains("<html>", StringComparison.OrdinalIgnoreCase))
                    html = html.Replace("<html>", $"<html>\n<head>{baseTag}</head>", StringComparison.OrdinalIgnoreCase);
                else
                    html = $"<!DOCTYPE html>\n<html>\n<head>{baseTag}</head>\n<body>{html}</body>\n</html>";

                await File.WriteAllTextAsync(tempPath, html);
                OpenUrl(tempPath);

                // Clean up temp file after browser has loaded it
                _ = Task.Run(async () =>
                {
                    await Task.Delay(5000);
                    try { File.Delete(tempPath); } catch { }
                });

                return true;
            }
        }
        catch { }

        return false;
    }

    private static bool IsRedirect(HttpStatusCode code) =>
        code == HttpStatusCode.Redirect ||
        code == HttpStatusCode.MovedPermanently ||
        code == HttpStatusCode.Found ||
        code == HttpStatusCode.SeeOther ||
        code == HttpStatusCode.TemporaryRedirect ||
        code == HttpStatusCode.PermanentRedirect;

    private static string? ParseTraceMoeResult(string json)
    {
        try
        {
            var doc = System.Text.Json.JsonDocument.Parse(json);
            var results = doc.RootElement.GetProperty("result");
            if (results.GetArrayLength() > 0)
            {
                var first = results[0];
                var id = first.GetProperty("anilist").GetProperty("id").GetInt32();
                var filename = first.GetProperty("filename").GetString() ?? "";
                return $"https://trace.moe/?id={id}&query={Uri.EscapeDataString(filename)}";
            }
        }
        catch { }
        return null;
    }

    private static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start("explorer.exe", $"\"{url}\""); }
        catch { }
    }

    public static void OpenHomePage(string engine)
    {
        if (_homeUrls.TryGetValue(engine, out var url))
            OpenUrl(url);
    }
}
