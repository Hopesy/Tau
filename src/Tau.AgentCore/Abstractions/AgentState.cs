namespace Tau.AgentCore;

/// <summary>
/// Read-only snapshot of agent conversation state.
/// </summary>
public sealed class AgentState
{
    private string _systemPrompt = string.Empty;
    private Ai.Model? _model;
    private List<IAgentTool> _tools = [];
    private List<Ai.ChatMessage> _messages = [];
    private Ai.AssistantMessage? _streamingMessage;
    private List<Ai.ToolCallContent> _pendingToolCalls = [];
    private string? _errorMessage;
    private bool _isStreaming;

    /// <summary>从会话重放的当前提示；低层运行时的旧顶层提示作为最早基线。</summary>
    public string SystemPrompt => Ai.Transcript.GetCurrentSystemPrompt(
        Ai.Transcript.NormalizeContext(new(_systemPrompt, _messages, null)).Messages);
    public Ai.Model? Model => _model;
    public IReadOnlyList<IAgentTool> Tools => _tools.AsReadOnly();
    public IReadOnlyList<Ai.ChatMessage> Messages => _messages.AsReadOnly();
    public Ai.AssistantMessage? StreamingMessage => _streamingMessage;
    public IReadOnlyList<Ai.ToolCallContent> PendingToolCalls => _pendingToolCalls.AsReadOnly();
    public string? ErrorMessage => _errorMessage;
    public bool IsStreaming => _isStreaming;

    internal void Configure(string? systemPrompt, Ai.Model model, IReadOnlyList<IAgentTool> tools)
    {
        _systemPrompt = systemPrompt ?? string.Empty;
        _model = model;
        _tools = tools.ToList();
    }

    internal void AddMessage(Ai.ChatMessage message) => _messages.Add(message);
    internal void SetMessages(List<Ai.ChatMessage> messages) => _messages = messages;

    /// <summary>【AgentCore】【提示替换】兼容旧提示设置接口，替换全部提示文本并保留工具声明的位置。</summary>
    /// <param name="systemPrompt">替换后的完整提示；null 表示空提示。</param>
    internal void ReplaceSystemPrompt(string? systemPrompt)
    {
        var text = systemPrompt ?? "";
        if (text == SystemPrompt) return;
        _systemPrompt = "";
        // 1. 【AgentCore】【提示替换】已有开场声明保留工具与时间，后续声明只保留工具变化
        var hasInitial = _messages.FirstOrDefault() is Ai.SystemMessage;
        _messages = _messages.Select((message, index) => message is Ai.SystemMessage system
            ? system with { Content = index == 0 ? text : "", Sections = null }
            : message).ToList();
        if (!hasInitial && text.Length > 0) _messages.Insert(0, Ai.Transcript.CreateInitialSystemMessage(text, null)!);
    }
    internal void SetStreaming(bool streaming, Ai.AssistantMessage? partial = null)
    {
        _isStreaming = streaming;
        _streamingMessage = partial;
    }
    internal void SetPendingToolCalls(List<Ai.ToolCallContent> calls) => _pendingToolCalls = calls;
    internal void SetError(string? error) => _errorMessage = error;

    internal void Reset()
    {
        _messages.Clear();
        _streamingMessage = null;
        _pendingToolCalls.Clear();
        _errorMessage = null;
        _isStreaming = false;
    }
}
