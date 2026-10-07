// 作者：xxx
using System.Text;
using Tau.AgentCore.Harness;

namespace Tau.CodingAgent.Tools;

/// <summary>【CodingAgent】【命令输出】内存只保留有界尾部，超过模型限制后把完整输出写入临时文件。</summary>
internal sealed class CodingAgentShellOutput(string prefix) : IDisposable
{
    internal const int StructuredMaxBytes = 1024 * 1024;
    private readonly StringBuilder _tail = new();
    private StreamWriter? _writer;
    private long _totalBytes;
    private long _newlines;
    private bool _endsWithNewline;
    private bool _finished;
    internal string? FullOutputPath { get; private set; }

    /// <summary>【CodingAgent】【输出追加】按接收顺序保存原始文本并统计真实长度，超过限制后启用完整文件。</summary>
    /// <param name="text">已经完成 UTF-8 解码的输出片段。</param>
    internal void Append(string text)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        if (text.Length == 0) return;
        _totalBytes += Encoding.UTF8.GetByteCount(text);
        _newlines += text.Count(character => character == '\n');
        _endsWithNewline = text.EndsWith('\n');
        if (_writer is null && (_totalBytes > ToolOutputTruncator.DefaultMaxBytes || TotalLines > ToolOutputTruncator.DefaultMaxLines))
        {
            FullOutputPath = Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N") + ".log");
            _writer = new StreamWriter(new FileStream(FullOutputPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
            _writer.Write(_tail.ToString());
        }
        _writer?.Write(text);
        // 1. 【CodingAgent】【内存边界】自定义后端可能一次提交巨量输出，只把其有界尾部复制到内存缓存
        if (text.Length > ToolOutputTruncator.DefaultMaxBytes * 2)
        {
            _tail.Clear();
            var start = text.Length - ToolOutputTruncator.DefaultMaxBytes * 2;
            if (char.IsLowSurrogate(text[start])) start++;
            _tail.Append(text.AsSpan(start));
        }
        else _tail.Append(text);
        var excess = _tail.Length - ToolOutputTruncator.DefaultMaxBytes * 2;
        if (excess > 0)
        {
            if (char.IsLowSurrogate(_tail[excess])) excess++;
            _tail.Remove(0, excess);
        }
    }

    private long TotalLines => Math.Max(1, _newlines + (_endsWithNewline ? 0 : 1));

    /// <summary>【CodingAgent】【输出快照】截取模型可见尾部，并恢复完整输出的行数与字节数统计。</summary>
    /// <returns>含截断信息的模型输出。</returns>
    internal ToolOutputTruncationResult Snapshot()
    {
        _writer?.Flush();
        var snapshot = ToolOutputTruncator.TruncateTail(_tail.ToString());
        return snapshot with
        {
            Truncated = FullOutputPath is not null || snapshot.Truncated,
            TotalBytes = (int)Math.Min(int.MaxValue, _totalBytes), TotalLines = (int)Math.Min(int.MaxValue, TotalLines)
        };
    }

    /// <summary>【CodingAgent】【输出完成】关闭完整输出文件，让嵌套程序调用可以安全读取其内容。</summary>
    public void Dispose()
    {
        _finished = true;
        _writer?.Dispose();
        _writer = null;
    }

    /// <summary>【CodingAgent】【结构化输出】读取最多一 MiB 的完整输出前缀，避免把大文件整体读入内存。</summary>
    /// <returns>程序可见文本以及是否超过程序输出上限。</returns>
    internal async Task<(string Output, bool Truncated)> ReadStructuredOutputAsync()
    {
        Dispose();
        if (FullOutputPath is null) return (_tail.ToString(), false);
        await using var stream = File.OpenRead(FullOutputPath);
        var bytes = new byte[(int)Math.Min(stream.Length, StructuredMaxBytes)];
        await stream.ReadExactlyAsync(bytes).ConfigureAwait(false);
        // 1. 【CodingAgent】【字符边界】截断位置落在多字节字符内部时丢弃不完整尾部，保留此前全部有效字符
        var length = bytes.Length;
        var encoding = new UTF8Encoding(false, true);
        while (true)
        {
            try { return (encoding.GetString(bytes, 0, length), stream.Length > StructuredMaxBytes); }
            catch (DecoderFallbackException) when (length > Math.Max(0, bytes.Length - 3)) { length--; }
        }
    }
}
