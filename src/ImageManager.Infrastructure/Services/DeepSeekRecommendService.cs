using System.Text;
using System.Text.Json;
using ImageManager.Core.Models;
using ImageManager.Core.Services;

namespace ImageManager.Infrastructure.Services;

public class DeepSeekRecommendService : IAiRecommendService
{
    private const string ApiEndpoint = "https://api.deepseek.com/chat/completions";
    private const string Model = "deepseek-chat";
    private const double Temperature = 0.4;

    private static readonly HttpClient _http = new();
    private string _apiKey = string.Empty;

    public bool IsAvailable => !string.IsNullOrEmpty(_apiKey);

    public void SetApiKey(string key) => _apiKey = key;

    public async Task<string> RecommendAsync(string userInput, List<TagMapping> tagMappings)
    {
        if (string.IsNullOrEmpty(_apiKey)) return "错误：未设置 API Key，请在内存与缓存设置中配置 DeepSeek API Key。";
        if (tagMappings.Count == 0) return "错误：本地标签库为空。";

        try
        {
            var systemPrompt = BuildSystemPrompt();
            var userPrompt = BuildUserPrompt(userInput, tagMappings);

            var requestBody = new
            {
                model = Model,
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                },
                temperature = Temperature,
                max_tokens = 1000,
                stream = false
            };

            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, ApiEndpoint) { Content = content };
            request.Headers.Add("Authorization", $"Bearer {_apiKey}");

            using var response = await _http.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync();
                return $"API 请求失败 (HTTP {(int)response.StatusCode}): {errBody.Truncate(300)}";
            }

            var responseBody = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseBody);
            var reply = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "（AI 返回为空）";

            return reply.Trim();
        }
        catch (Exception ex)
        {
            return $"请求异常: {ex.Message}";
        }
    }

    private static string BuildSystemPrompt()
    {
        return """
            你是一个 AI 绘画标签搜索专家。用户会描述想要的图片类型，你需要从可用标签库中挑选最匹配的标签，组合成搜索字符串。

            搜索语法规则：
            - 空格表示 AND（同时满足）：girl outdoor → 搜索同时包含 girl 和 outdoor 的图片
            - 小写字母 o 表示 OR（满足其一）：smile o laugh → 搜索包含 smile 或 laugh 的图片
            - 减号 - 表示 NOT（排除）：-sketch → 排除包含 sketch 标签的图片
            - 可以组合使用：girl outdoor -sketch o smile → 同时满足 girl + outdoor，且不包含 sketch，或包含 smile

            输出要求：
            - 每行输出一个推荐组合，不要编号
            - 每行格式：先用英文标签 + 搜索符号构成可搜索的字符串，然后用 " → " 分隔，后面跟对应的中文标签名
            - 示例格式：girl outdoor → 女孩 户外
            - 示例格式（带排除）：girl outdoor -sketch → 女孩 户外 排除草图
            - 推荐的组合应该多样化，覆盖不同的构图角度、画风方向、场景搭配
            - 最多输出 10 个推荐组合
            - 每个组合中标签数量建议 2-6 个

            重要：你必须严格使用可用标签列表中的英文标签名来构成搜索字符串，中文标签名必须与列表中给出的中文名一致，不要自行编造或翻译。
            """;
    }

    private static string BuildUserPrompt(string userInput, List<TagMapping> tagMappings)
    {
        var sb = new StringBuilder();
        sb.AppendLine("用户需求：");
        sb.AppendLine(userInput.Trim());
        sb.AppendLine();
        sb.AppendLine("可用标签列表（格式：英文名 → 中文名）：");

        foreach (var m in tagMappings)
        {
            if (string.IsNullOrEmpty(m.ChineseName))
                sb.AppendLine($"  {m.EnglishName}");
            else
                sb.AppendLine($"  {m.EnglishName} → {m.ChineseName}");
        }

        sb.AppendLine();
        sb.AppendLine("请根据用户需求，从上述标签中挑选并组合出搜索字符串。");

        return sb.ToString();
    }
}

internal static class StringExtensions
{
    public static string Truncate(this string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "...";
}
