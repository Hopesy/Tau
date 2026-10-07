// 作者：xxx
using System.Text;

namespace Tau.CodingAgent.Runtime.Mcp;

/// <summary>【CodingAgent】【MCP SSE】解析有界事件，保留用于断线恢复的 id 和 retry 控制字段。</summary>
public static class CodingAgentMcpSseParser
{
    /// <summary>【CodingAgent】【MCP SSE 接收】处理 UTF-8 分片、多行数据、控制事件以及末尾未换行事件。</summary>
    /// <param name="stream">SSE 字节流。</param><param name="onEvent">数据事件接收器。</param><param name="onId">所有有效 id 字段。</param>
    /// <param name="onRetry">服务端恢复间隔。</param><param name="maxEventBytes">单事件及单行字节上限。</param><param name="token">取消信号。</param><returns>消费任务。</returns>
    public static async Task ConsumeAsync(Stream stream, Action<string?, string> onEvent, Action<string>? onId = null,
        Action<long>? onRetry = null, int maxEventBytes = 16 * 1024 * 1024, CancellationToken token = default)
    {
        if (maxEventBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxEventBytes));
        using var line = new MemoryStream();
        var buffer = new byte[8192]; var data = new List<string>(); string? eventName = null; long dataBytes = 0; var firstLine = true;
        /// <summary>【CodingAgent】【MCP SSE 分发】空事件仅清理事件类型，数据事件使用换行连接。</summary>
        void Dispatch()
        {
            if (data.Count > 0) onEvent(string.IsNullOrEmpty(eventName) ? null : eventName, string.Join("\n", data));
            data.Clear(); eventName = null; dataBytes = 0;
        }
        /// <summary>【CodingAgent】【MCP SSE 字段】处理完整单行，控制字段即刻发布以支持无数据恢复事件。</summary>
        /// <param name="bytes">完整行字节。</param>
        void ProcessLine(byte[] bytes)
        {
            var text = Encoding.UTF8.GetString(bytes);
            if (text.EndsWith('\r')) text = text[..^1];
            if (firstLine) { firstLine = false; if (text.StartsWith('\uFEFF')) text = text[1..]; }
            if (text.Length == 0) { Dispatch(); return; }
            if (text[0] == ':') return;
            var colon = text.IndexOf(':'); var field = colon < 0 ? text : text[..colon];
            var value = colon < 0 ? "" : text[(colon + 1)..];
            if (value.StartsWith(' ')) value = value[1..];
            switch (field)
            {
                case "data":
                    dataBytes += Encoding.UTF8.GetByteCount(value) + (data.Count > 0 ? 1 : 0);
                    if (dataBytes > maxEventBytes) throw new IOException($"MCP SSE event exceeds {maxEventBytes} bytes");
                    data.Add(value); break;
                case "event": eventName = value; break;
                case "id": if (!value.Contains('\0')) onId?.Invoke(value); break;
                case "retry": if (value.Length > 0 && value.All(char.IsAsciiDigit) && long.TryParse(value, out var delay)) onRetry?.Invoke(delay); break;
            }
        }
        int length;
        while ((length = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            for (var index = 0; index < length; index++)
            {
                if (buffer[index] == '\n') { ProcessLine(line.ToArray()); line.SetLength(0); }
                else
                {
                    if (line.Length >= maxEventBytes) throw new IOException($"MCP SSE event exceeds {maxEventBytes} bytes");
                    line.WriteByte(buffer[index]);
                }
            }
        if (line.Length > 0) ProcessLine(line.ToArray());
        Dispatch();
    }
}
