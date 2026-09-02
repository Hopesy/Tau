namespace Tau.Ai;

/// <summary>
/// pi-messages 服务端消息重写的影响摘要。
/// </summary>
public sealed record PiMessagesRewriteImpact(
    string PolicyId,
    int PolicyVersion,
    bool Changed,
    int TokenCountChange,
    int MessageCountChange,
    bool SystemPromptChanged);

/// <summary>
/// pi-messages 流式事件的终止原因。
/// </summary>
public enum PiMessagesStopReason
{
    Stop,
    Length,
    ToolUse,
    Aborted,
    Error
}

/// <summary>
/// pi-messages provider 返回的结构化错误。
/// </summary>
public sealed class PiMessagesResponseException : Exception
{
    /// <summary>服务端错误代码。</summary>
    public string? Code { get; }

    /// <summary>服务端诊断字段。</summary>
    public IReadOnlyDictionary<string, object?> DiagnosticDetails { get; }

    /// <summary>
    /// 创建 pi-messages 错误。
    /// </summary>
    /// <param name="message">错误消息。</param>
    /// <param name="code">服务端错误代码。</param>
    /// <param name="diagnosticDetails">诊断字段。</param>
    public PiMessagesResponseException(string message, string? code = null, IReadOnlyDictionary<string, object?>? diagnosticDetails = null)
        : base(message)
    {
        Code = code;
        DiagnosticDetails = diagnosticDetails ?? new Dictionary<string, object?>();
    }
}
