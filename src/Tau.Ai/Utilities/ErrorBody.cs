using System.Text.Json;

namespace Tau.Ai.Utilities;

/// <summary>
/// 统一提取 provider 错误响应中的可读错误信息，避免重复拼接 response body。
/// </summary>
public static class ErrorBody
{
    /// <summary>
    /// 从异常或响应正文中解析错误消息，并限制输出长度。
    /// </summary>
    /// <param name="body">响应正文，可以为空。</param>
    /// <param name="fallback">正文没有可读错误时使用的默认文本。</param>
    /// <param name="maximumLength">返回消息的最大字符数。</param>
    /// <returns>规范化后的错误文本。</returns>
    public static string Format(string? body, string fallback = "Provider request failed", int maximumLength = 4000)
    {
        var text = body?.Trim() ?? string.Empty;
        if (text.Length == 0) return fallback;
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            var candidates = new[] { "message", "error", "detail", "details" };
            foreach (var name in candidates)
            {
                if (!root.TryGetProperty(name, out var value)) continue;
                var message = value.ValueKind == JsonValueKind.Object && value.TryGetProperty("message", out var nested)
                    ? nested.ToString()
                    : value.ToString();
                if (!string.IsNullOrWhiteSpace(message)) return Truncate(message, maximumLength);
            }
        }
        catch (JsonException)
        {
            // 非 JSON provider 仍应保留原始正文
        }

        return Truncate(text, maximumLength);
    }

    /// <summary>
    /// 截断错误文本，避免 provider 返回的大正文污染日志和 UI。
    /// </summary>
    /// <param name="text">原始文本。</param>
    /// <param name="maximumLength">最大长度。</param>
    /// <returns>截断后的文本。</returns>
    public static string Truncate(string text, int maximumLength = 4000)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (maximumLength < 1) return string.Empty;
        return text.Length <= maximumLength ? text : $"{text[..maximumLength]}... [truncated {text.Length - maximumLength} chars]";
    }
}
