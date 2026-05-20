using System.Text;
using System.Text.Json;
using ImageManager.Core.Services;

namespace ImageManager.Infrastructure.Services;

public class DeepSeekTranslationService : ITranslationService
{
    private const string ApiEndpoint = "https://api.deepseek.com/chat/completions";
    private const string Model = "deepseek-chat";
    private const double Temperature = 0.1;
    private const int MaxRetries = 2;

    private readonly HttpClient _http = new();
    private string _apiKey = string.Empty;

    public bool IsAvailable => !string.IsNullOrEmpty(_apiKey);

    public void SetApiKey(string key) => _apiKey = key;

    public async Task<string?> TranslateSingleAsync(string englishTag)
    {
        var results = await TranslateBatchAsync(new List<string> { englishTag });
        return results.TryGetValue(englishTag, out var chinese) ? chinese : null;
    }

    public async Task<Dictionary<string, string>> TranslateBatchAsync(List<string> englishTags)
    {
        if (englishTags.Count == 0) return new Dictionary<string, string>();
        if (string.IsNullOrEmpty(_apiKey)) return FailedAll(englishTags);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var batchSize = 50;
        var delayMs = 500;

        for (int i = 0; i < englishTags.Count; i += batchSize)
        {
            var batch = englishTags.Skip(i).Take(batchSize).ToList();
            bool ok = false;
            for (int retry = 0; retry <= MaxRetries; retry++)
            {
                var translations = await TryTranslateBatch(batch);
                if (translations != null)
                {
                    foreach (var kv in translations) result[kv.Key] = kv.Value;
                    ok = true;
                    break;
                }
                if (retry < MaxRetries) await Task.Delay(1000 * (retry + 1));
            }
            if (!ok)
            {
                // Translation failed for this batch — leave English as-is (caller sees empty result)
                foreach (var tag in batch)
                    result.TryAdd(tag, string.Empty);
            }
            if (i + batchSize < englishTags.Count)
                await Task.Delay(delayMs);
        }

        return result;
    }

    private async Task<Dictionary<string, string>?> TryTranslateBatch(List<string> englishTags)
    {
        try
        {
            var prompt = BuildPrompt(englishTags);
            var requestBody = new
            {
                model = Model,
                messages = new[]
                {
                    new { role = "system", content = "你是一个专精于 AI 绘画 / 二次元插画标签的翻译器。" +
                        "输入标签来源于 Danbooru / Gelbooru 风格的插画标签系统，涵盖角色、画风、构图、服装、场景、动作、品质评级等。" +
                        "翻译规则：\n" +
                        "1. 日语罗马音词汇翻译为中文含义，不要音译。例：mizugi→泳装, yukata→浴衣, kimono→和服, seifuku→校服, " +
                        "megane→眼镜, zettai ryouiki→绝对领域, ahoge→呆毛, tsundere→傲娇, sukumizu→死库水/校园泳装\n" +
                        "2. 角色名称使用二次元圈内通用中文译名。例：hatsune miku→初音未来, frieren→芙莉莲, " +
                        "rem→蕾姆, flandre scarlet→芙兰朵露·斯卡雷特。无通用译名的保持原文\n" +
                        "3. 画风/技法标签用圈内通用中文。例：masterpiece→杰作, oil painting→油画, watercolor→水彩, " +
                        "sketch→草图, lineart→线稿, flat color→平涂, cel shading→赛璐珞上色, " +
                        "thick outline→粗线描, chromatic aberration→色差/色収差\n" +
                        "4. 构图/镜头语言。例：close-up→特写, full body→全身, cowboy shot→牛仔镜头, " +
                        "from above→俯视, from below→仰视, dynamic angle→动态角度, dutch angle→荷兰角\n" +
                        "5. 服装/配饰/部位特征准确翻译。例：school uniform→校服, maid→女仆装, " +
                        "cat ears→猫耳, twin tails→双马尾, thighhighs→过膝袜, barefoot→赤足, " +
                        "detached sleeves→分离袖, choker→项圈\n" +
                        "6. 品质/评分标签。例：best quality→最佳品质, high resolution→高分辨率, " +
                        "absurdres→超高分辨率, very aesthetic→极美, amazing quality→惊人品质, " +
                        "sharp focus→锐聚焦, detailed→细节丰富\n" +
                        "7. 背景/场景。例：outdoors→户外, indoors→室内, night→夜景, sunset→夕阳, " +
                        "cityscape→城景, nature→自然, underwater→水下, classroom→教室\n" +
                        "8. 保持简洁，每个标签一行，严格按输入顺序输出，不要编号，不要解释。" },
                    new { role = "user", content = prompt }
                },
                temperature = Temperature,
                max_tokens = 200 * Math.Max(1, englishTags.Count / 10),
                stream = false
            };

            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, ApiEndpoint) { Content = content };
            request.Headers.Add("Authorization", $"Bearer {_apiKey}");

            using var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;

            var responseBody = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseBody);
            var reply = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? string.Empty;

            return ParseResponse(reply, englishTags);
        }
        catch
        {
            return null;
        }
    }

    private static string BuildPrompt(List<string> tags)
    {
        return string.Join("\n", tags.Select((t, i) => $"  {t}"));
    }

    private static Dictionary<string, string> ParseResponse(string reply, List<string> englishTags)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = reply.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        for (int i = 0; i < Math.Min(lines.Count, englishTags.Count); i++)
        {
            var chinese = lines[i].Trim();
            // Strip any leading numbering like "1. " or "1、"
            var dotIdx = chinese.IndexOf('.');
            if (dotIdx > 0 && dotIdx <= 3 && char.IsDigit(chinese[0]))
                chinese = chinese[(dotIdx + 1)..].Trim();
            var bulletIdx = chinese.IndexOf('、');
            if (bulletIdx > 0 && bulletIdx <= 3 && char.IsDigit(chinese[0]))
                chinese = chinese[(bulletIdx + 1)..].Trim();

            if (chinese.Length > 0)
                result[englishTags[i]] = chinese;
        }

        // Ensure all input tags have an entry (empty string if not translated)
        foreach (var tag in englishTags)
            result.TryAdd(tag, string.Empty);

        return result;
    }

    private static Dictionary<string, string> FailedAll(List<string> tags)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tags) result[t] = string.Empty;
        return result;
    }
}
