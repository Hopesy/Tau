// 作者：xxx
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Tau.AgentCore;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime.Mcp;

/// <summary>【CodingAgent】【MCP 内容】将 MCP 内容转换为模型可见内容，同时保留完整的程序调用结果。</summary>
public static class CodingAgentMcpToolResult
{
    public const int MaxOutputBytes = 20 * 1024;
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>【CodingAgent】【MCP 结果】转换资源并限制模型文本，结构化结果保持完整且移除顶层 _meta。</summary>
    /// <param name="server">服务器名。</param><param name="tool">原始工具名。</param><param name="result">MCP 调用结果。</param>
    /// <param name="readableResources">是否可以提示调用资源读取工具。</param><param name="saveOutput">可选输出保存器。</param>
    /// <param name="token">取消信号。</param><returns>Agent 工具结果。</returns>
    public static async Task<ToolResult> ConvertAsync(string server, string tool, JsonElement result, bool readableResources = false,
        Func<ReadOnlyMemory<byte>, string, CancellationToken, Task<string>>? saveOutput = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var raw = JsonNode.Parse(result.GetRawText())!.AsObject();
        raw.Remove("_meta");
        var content = new List<ContentBlock>();
        var save = saveOutput ?? SaveToTempFileAsync;
        // 1. 【CodingAgent】【MCP 内容】保留内容顺序，将音频及二进制资源转换为模型可以理解的提示
        if (result.TryGetProperty("content", out var blocks))
            foreach (var block in blocks.EnumerateArray()) content.Add(await ConvertBlockAsync(server, block, readableResources, save, token).ConfigureAwait(false));
        if (content.Count == 0 && raw["structuredContent"] is { } structured) content.Add(new TextContent(structured.ToJsonString(Indented)));
        var isError = result.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True;
        var combined = string.Join('\n', content.OfType<TextContent>().Select(block => block.Text));
        if (isError && combined.Length == 0)
        {
            combined = $"MCP tool {server}/{tool} returned an error";
            content.Add(new TextContent(combined));
        }
        var details = new JsonObject { ["server"] = server, ["tool"] = tool };
        // 2. 【CodingAgent】【MCP 截断】按 UTF-8 字符边界保留首尾，完整输出写入仅当前用户可读的文件
        var bytes = Encoding.UTF8.GetBytes(combined);
        if (bytes.Length > MaxOutputBytes)
        {
            string where;
            try
            {
                var path = await save(bytes, ".txt", token).ConfigureAwait(false);
                details["fullOutputPath"] = path;
                where = $"[Full output: {path} (read it with offset/limit)]";
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { where = $"[Could not save the full output: {ex.Message}]"; }
            var headEnd = MaxOutputBytes / 2;
            while (headEnd > 0 && (bytes[headEnd] & 0xc0) == 0x80) headEnd--;
            var tailStart = bytes.Length - MaxOutputBytes / 2;
            while (tailStart < bytes.Length && (bytes[tailStart] & 0xc0) == 0x80) tailStart++;
            var removed = Encoding.UTF8.GetString(bytes.AsSpan(headEnd, tailStart - headEnd)).EnumerateRunes().Count();
            var preview = Encoding.UTF8.GetString(bytes.AsSpan(0, headEnd)) + $"…{removed} chars truncated…" + Encoding.UTF8.GetString(bytes.AsSpan(tailStart));
            var lines = Regex.Split(combined, "\\r\\n|\\r|\\n").Length;
            var text = $"Warning: truncated output (original token count: {(bytes.Length + 3L) / 4})\nTotal output lines: {lines}\n\n{preview}\n\n{where}";
            content = [new TextContent(text), .. content.OfType<ImageContent>()];
        }
        return new(content, isError, JsonSerializer.SerializeToElement(details)) { StructuredContent = JsonSerializer.SerializeToElement(raw) };
    }

    /// <summary>【CodingAgent】【MCP 内容块】转换单个文本、图像、音频、资源链接或内嵌资源。</summary>
    /// <param name="server">服务器名。</param><param name="block">内容块。</param><param name="readable">可读取资源。</param>
    /// <param name="save">保存器。</param><param name="token">取消信号。</param><returns>模型内容块。</returns>
    private static async Task<ContentBlock> ConvertBlockAsync(string server, JsonElement block, bool readable,
        Func<ReadOnlyMemory<byte>, string, CancellationToken, Task<string>> save, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var type = String(block, "type");
        switch (type)
        {
            case "text": return new TextContent(String(block, "text") ?? "");
            case "image": return new ImageContent(String(block, "data") ?? "", String(block, "mimeType") ?? "application/octet-stream");
            case "audio": return new TextContent($"[audio {String(block, "mimeType")} omitted]");
            case "resource_link":
                var details = new List<string>();
                if (String(block, "mimeType") is { Length: > 0 } mime) details.Add(mime);
                if (block.TryGetProperty("size", out var size) && size.TryGetDouble(out var length)) details.Add(FormatSize(length));
                var description = String(block, "description");
                return new TextContent($"[Resource {String(block, "uri")} \"{String(block, "title") ?? String(block, "name")}\"" +
                    (details.Count > 0 ? " (" + string.Join(", ", details) + ")" : "") + (string.IsNullOrEmpty(description) ? "" : ": " + description) +
                    (readable ? $". Read it with read_mcp_resource (server \"{server}\")" : "") + "]");
            case "resource" when block.TryGetProperty("resource", out var resource):
                if (String(resource, "text") is { } text) return new TextContent(text);
                var mimeType = String(resource, "mimeType"); var blob = String(resource, "blob") ?? "";
                if (mimeType?.StartsWith("image/", StringComparison.Ordinal) == true) return new ImageContent(blob, mimeType);
                var uri = String(resource, "uri") ?? "";
                var data = DecodeBase64(blob);
                var mediaType = mimeType?.Split(';')[0].Trim().ToLowerInvariant();
                if (mediaType is not null && (mediaType.StartsWith("text/", StringComparison.Ordinal) || mediaType == "application/json" ||
                    mediaType.EndsWith("+json", StringComparison.Ordinal) || mediaType.EndsWith("+xml", StringComparison.Ordinal))) return new TextContent(Encoding.UTF8.GetString(data));
                var kind = $"{mimeType ?? "unknown type"}, {FormatSize(data.Length)}";
                try
                {
                    var path = Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ? parsed.AbsolutePath : uri;
                    var match = Regex.Match(path, "\\.[A-Za-z0-9]{1,8}$", RegexOptions.CultureInvariant);
                    var saved = await save(data, match.Success ? match.Value : ".bin", token).ConfigureAwait(false);
                    return new TextContent($"[Binary resource {uri} ({kind}) saved to {saved}]");
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { return new TextContent($"[Binary resource {uri} ({kind}) could not be saved: {ex.Message}]"); }
            default: return new TextContent($"[unsupported MCP content {type}]");
        }
    }

    /// <summary>【CodingAgent】【MCP 二进制】兼容 Node 对无填充、URL 安全字符和空白的 Base64 解码。</summary>
    /// <param name="value">资源编码。</param><returns>解码字节。</returns>
    private static byte[] DecodeBase64(string value)
    {
        var normalized = Regex.Replace(value.Split('=')[0].Replace('-', '+').Replace('_', '/'), "[^A-Za-z0-9+/]", "");
        if (normalized.Length % 4 == 1) normalized = normalized[..^1];
        return Convert.FromBase64String(normalized.PadRight((normalized.Length + 3) / 4 * 4, '='));
    }

    /// <summary>【CodingAgent】【MCP 文件】使用随机名称和独占创建保存输出，Unix 权限在创建时设置为 0600。</summary>
    /// <param name="data">完整输出字节。</param><param name="extension">简单扩展名。</param><param name="token">取消信号。</param><returns>临时文件绝对路径。</returns>
    public static async Task<string> SaveToTempFileAsync(ReadOnlyMemory<byte> data, string extension, CancellationToken token = default)
    {
        if (!Regex.IsMatch(extension, "\\A\\.[A-Za-z0-9]{1,8}\\z", RegexOptions.CultureInvariant)) throw new ArgumentException("Invalid output extension", nameof(extension));
        var path = Path.Combine(Path.GetTempPath(), "tau-mcp-" + Guid.NewGuid().ToString("N") + extension);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            await using var stream = new FileStream(path, options);
            await stream.WriteAsync(data, token).ConfigureAwait(false);
            return path;
        }
        catch { if (File.Exists(path)) File.Delete(path); throw; }
    }

    /// <summary>【CodingAgent】【MCP 字段】读取可选字符串。</summary>
    /// <param name="value">对象。</param><param name="name">字段名。</param><returns>字符串或空值。</returns>
    internal static string? String(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;

    /// <summary>【CodingAgent】【MCP 大小】格式化与上游一致的字节、KB 和 MB 文本。</summary>
    /// <param name="bytes">字节数。</param><returns>大小文本。</returns>
    private static string FormatSize(double bytes) => bytes < 1024 ? bytes.ToString(CultureInfo.InvariantCulture) + "B" : bytes < 1024 * 1024
        ? (bytes / 1024).ToString("F1", CultureInfo.InvariantCulture) + "KB" : (bytes / (1024 * 1024)).ToString("F1", CultureInfo.InvariantCulture) + "MB";
}
