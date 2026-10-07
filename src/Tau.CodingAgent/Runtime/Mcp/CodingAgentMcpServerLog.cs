// 作者：xxx
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Tau.CodingAgent.Runtime.Mcp;

/// <summary>【MCP】【服务器日志】将服务器通知同步追加到共享文件，写入失败不影响工具调用。</summary>
public sealed class CodingAgentMcpServerLog
{
    public const long MaximumLogBytes = 5 * 1024 * 1024;
    private readonly string _mutexName;
    public string Path { get; }

    /// <summary>【MCP】【日志创建】保存日志位置；收到首条通知时才创建目录和文件。</summary>
    /// <param name="path">日志文件路径。</param>
    public CodingAgentMcpServerLog(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        var identity = OperatingSystem.IsWindows() ? Path.ToUpperInvariant() : Path;
        _mutexName = "Tau.Mcp.Log." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    /// <summary>【MCP】【通知格式化】保留结构化数据，统一 info/error 级别，多行数据缩进以保持记录边界。</summary>
    /// <param name="server">服务器名称。</param><param name="parameters">原始通知参数。</param><param name="now">可选记录时间。</param><returns>包含末尾换行的完整记录。</returns>
    public static string Format(string server, JsonElement? parameters, DateTimeOffset? now = null)
    {
        var record = parameters is { ValueKind: JsonValueKind.Object } value ? value : (JsonElement?)null;
        var remoteLevel = record is { } obj && obj.TryGetProperty("level", out var levelValue) && levelValue.ValueKind == JsonValueKind.String ? levelValue.GetString()! : "info";
        var level = remoteLevel is "error" or "critical" or "alert" or "emergency" ? "error" : "info";
        var sourceLevel = remoteLevel == level ? "" : " [remote-level=" + OneLine(remoteLevel) + "]";
        var logger = record is { } data && data.TryGetProperty("logger", out var loggerValue) && loggerValue.ValueKind == JsonValueKind.String ? loggerValue.GetString() : null;
        var dataValue = record is { } item ? item.TryGetProperty("data", out var content) ? content : (JsonElement?)null : parameters;
        var text = FormatData(dataValue).Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\n    ", StringComparison.Ordinal);
        return $"{(now ?? DateTimeOffset.UtcNow).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)} 【MCP】【服务器日志】 [{OneLine(server)}] {level}{sourceLevel}" +
            (string.IsNullOrEmpty(logger) ? "" : " " + OneLine(logger) + ":") + " " + text + "\n";
    }

    /// <summary>【MCP】【日志追加】使用跨进程互斥保护轮转和单条追加，超过 5 MiB 后将旧文件移动到 .1。</summary>
    /// <param name="server">服务器名称。</param><param name="parameters">通知原参数。</param>
    public void Write(string server, JsonElement? parameters)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(Format(server, parameters));
            using var mutex = new Mutex(false, _mutexName);
            var entered = false;
            try
            {
                // 1. 【MCP】【共享日志】同步等待设定上限，文件系统或其他进程故障不能无限阻塞报文接收
                try { entered = mutex.WaitOne(TimeSpan.FromSeconds(1)); }
                catch (AbandonedMutexException) { entered = true; }
                if (!entered) return;
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                // 2. 【MCP】【日志轮转】每次读取实际大小，避免其他进程轮转后仍使用过期计数
                if (File.Exists(Path) && new FileInfo(Path).Length > MaximumLogBytes) File.Move(Path, Path + ".1", true);
                using var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                stream.Write(bytes);
            }
            finally { if (entered) mutex.ReleaseMutex(); }
        }
        catch (Exception)
        {
            // 3. 【MCP】【日志容错】记录失败只丢弃本条日志，保持服务器工具链路可用
        }
    }

    /// <summary>【MCP】【日志字段】避免名称、级别及 logger 字段伪造下一条日志。</summary>
    /// <param name="text">字段文本。</param><returns>单行字段。</returns>
    private static string OneLine(string text) => text.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

    /// <summary>【MCP】【日志数据】字符串直接保留，其他 JSON 值紧凑序列化，缺失值采用上游 undefined 文本。</summary>
    /// <param name="data">原数据。</param><returns>日志正文。</returns>
    private static string FormatData(JsonElement? data)
    {
        if (data is null || data.Value.ValueKind == JsonValueKind.Undefined) return "undefined";
        if (data.Value.ValueKind == JsonValueKind.String) return data.Value.GetString()!;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })) data.Value.WriteTo(writer);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
