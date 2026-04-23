namespace Tau.Agent;

/// <summary>
/// Read-only snapshot of agent conversation state.
/// </summary>
public sealed class AgentState
{
    private List<Ai.ChatMessage> _messages = [];
    private Ai.AssistantMessage? _streamingMessage;
    private List<Ai.ToolCallContent> _pendingToolCalls = [];
    private string? _errorMessage;
    private bool _isStreaming;

    public IReadOnlyList<Ai.ChatMessage> Messages => _messages.AsReadOnly();
    public Ai.AssistantMessage? StreamingMessage => _streamingMessage;
    public IReadOnlyList<Ai.ToolCallContent> PendingToolCalls => _pendingToolCalls.AsReadOnly();
    public string? ErrorMessage => _errorMessage;
    public bool IsStreaming => _isStreaming;

    internal void AddMessage(Ai.ChatMessage message) => _messages.Add(message);
    internal void SetMessages(List<Ai.ChatMessage> messages) => _messages = messages;
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
