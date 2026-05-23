using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using Tau.Agent;
using Tau.Ai;
using Tau.Ai.Auth;
using Tau.Ai.Registry;
using Tau.CodingAgent.Runtime;
using Tau.WebUi.Contracts;

namespace Tau.WebUi.Services;

public sealed class WebChatService
{
    private readonly ConcurrentDictionary<string, WebChatSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _defaultProvider;
    private readonly string _defaultModel;
    private readonly ModelCatalog _catalog;
    private readonly ProviderAuthResolver _authResolver = new();
    private readonly WebChatStore _store;
    private readonly Func<string, string, IReadOnlyList<ChatMessage>?, ICodingAgentRunner> _runnerFactory;

    public WebChatService(WebChatStore store)
        : this(store, WebUiRunnerFactory.Create)
    {
    }

    public WebChatService(
        WebChatStore store,
        Func<string, string, IReadOnlyList<ChatMessage>?, ICodingAgentRunner> runnerFactory)
    {
        _store = store;
        _runnerFactory = runnerFactory;
        _catalog = new ModelCatalog();
        var selection = _catalog.ResolveSelection(
            Environment.GetEnvironmentVariable("TAU_PROVIDER"),
            Environment.GetEnvironmentVariable("TAU_MODEL"));
        _defaultProvider = selection.Provider;
        _defaultModel = selection.ModelId;

        foreach (var session in _store.Load())
        {
            _sessions[session.Id] = WebChatSession.FromDto(session, _runnerFactory);
        }
    }

    public WebUiStatusDto GetStatus()
    {
        return new WebUiStatusDto(
            "Tau.WebUi",
            _defaultProvider,
            _defaultModel,
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY")),
            _sessions.Count,
            "runtime-coding-agent",
            true,
            System.IO.Path.GetFullPath(_store.Path));
    }

    public WebUiCatalogDto GetCatalog()
    {
        var providers = _catalog.GetProviders()
            .Select(provider => new WebUiProviderOptionDto(
                provider,
                _catalog.GetModels(provider)
                    .OrderBy(model => model.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(model => new WebUiModelOptionDto(
                        model.Id,
                        model.Name,
                        model.Reasoning,
                        model.ContextWindow ?? 0,
                        model.MaxOutputTokens ?? 0,
                        model.BaseUrl == "<authenticated>" || string.IsNullOrWhiteSpace(model.BaseUrl),
                        model.Api,
                        model.BaseUrl))
                    .ToArray()))
            .ToArray();

        return new WebUiCatalogDto(providers);
    }

    public WebUiAuthStatusDto GetAuthStatus(string? provider = null, string? model = null)
    {
        var selection = _catalog.ResolveSelection(provider, model, _defaultProvider);
        var resolvedModel = _catalog.GetModel(selection.Provider, selection.ModelId);
        var status = _authResolver.GetStatus(selection.Provider, resolvedModel);
        return new WebUiAuthStatusDto(
            status.Provider,
            status.IsConfigured,
            status.Source,
            status.UsesOAuth,
            status.CanLogin,
            status.Message);
    }

    public WebChatSessionDto CreateSession(string? title = null, string? provider = null, string? model = null)
    {
        var selection = _catalog.ResolveSelection(provider, model, _defaultProvider);
        var session = new WebChatSession(title, selection.Provider, selection.ModelId, _runnerFactory);
        _sessions[session.Id] = session;
        Persist();
        return session.ToDto(persisted: true);
    }

    public IReadOnlyList<WebChatSessionDto> ListSessions()
    {
        return _sessions.Values
            .OrderByDescending(s => s.UpdatedAt)
            .Select(s => s.ToDto(persisted: true))
            .ToArray();
    }

    public IReadOnlyList<WebChatSessionDto> SearchSessions(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<WebChatSessionDto>();
        }

        var trimmed = query.Trim();
        return _sessions.Values
            .Where(s => s.Title.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.UpdatedAt)
            .Select(s => s.ToDto(persisted: true))
            .ToArray();
    }

    public WebChatSessionDto? GetSession(string id)
    {
        return _sessions.TryGetValue(id, out var session) ? session.ToDto(persisted: true) : null;
    }

    public bool HasSession(string id) => _sessions.ContainsKey(id);

    public bool DeleteSession(string id)
    {
        if (!_sessions.TryRemove(id, out _))
        {
            return false;
        }

        Persist();
        return true;
    }

    public WebChatSessionDto ImportSession(WebChatSessionDto dto)
    {
        var selection = _catalog.ResolveSelection(dto.Provider, dto.Model, _defaultProvider);
        var session = WebChatSession.FromImportedDto(dto, selection.Provider, selection.ModelId, _runnerFactory);
        _sessions[session.Id] = session;
        Persist();
        return session.ToDto(persisted: true);
    }

    public CodingAgentJsonlSessionPreviewDto PreviewCodingAgentJsonlSession(
        string jsonl,
        string? filePath = null) =>
        CodingAgentJsonlSessionPreviewer.Parse(jsonl, filePath);

    public WebChatSessionDto? CloneSession(string id)
    {
        if (!_sessions.TryGetValue(id, out var existing))
        {
            return null;
        }

        var sourceDto = existing.ToDto(persisted: true);
        var cloneDto = sourceDto with
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = $"Copy of {sourceDto.Title}",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Persisted = false
        };
        var clone = WebChatSession.FromImportedDto(cloneDto, sourceDto.Provider, sourceDto.Model, _runnerFactory);
        _sessions[clone.Id] = clone;
        Persist();
        return clone.ToDto(persisted: true);
    }

    public WebChatSessionDto? UpdateSessionSettings(string id, UpdateSessionSettingsRequest request)
    {
        if (!_sessions.TryGetValue(id, out var session))
        {
            return null;
        }

        var selection = _catalog.ResolveSelection(
            request.Provider ?? session.Provider,
            request.Model ?? session.Model,
            _defaultProvider);
        session.UpdateSettings(request.Title, selection.Provider, selection.ModelId);
        Persist();
        return session.ToDto(persisted: true);
    }

    public WebChatSessionDto? ClearSessionMessages(string id)
    {
        if (!_sessions.TryGetValue(id, out var session))
        {
            return null;
        }

        session.ClearMessages();
        Persist();
        return session.ToDto(persisted: true);
    }

    public async Task<WebChatSessionDto?> SendMessageAsync(
        string id,
        string? text,
        IReadOnlyList<WebChatAttachmentDto>? attachments = null,
        CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(id, out var session))
        {
            return null;
        }

        session.AddUserMessage(text, attachments);
        var dto = await session.SendAsync(cancellationToken).ConfigureAwait(false);
        Persist();
        return dto with { Persisted = true };
    }

    public async IAsyncEnumerable<WebChatStreamEventDto> SendMessageStreamAsync(
        string id,
        string? text,
        IReadOnlyList<WebChatAttachmentDto>? attachments = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(id, out var session))
        {
            yield break;
        }

        session.AddUserMessage(text, attachments);
        await foreach (var streamEvent in session.SendStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(streamEvent.Type, "done", StringComparison.OrdinalIgnoreCase) &&
                streamEvent.Session is not null)
            {
                Persist();
                yield return streamEvent with { Session = streamEvent.Session with { Persisted = true } };
                continue;
            }

            yield return streamEvent;
        }
    }

    private void Persist()
    {
        _store.Save(_sessions.Values
            .OrderBy(session => session.CreatedAt)
            .Select(session => session.ToDto(persisted: true))
            .ToArray());
    }

    private sealed class WebChatSession
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly List<WebChatMessageDto> _messages = [];
        private readonly Func<string, string, IReadOnlyList<ChatMessage>?, ICodingAgentRunner> _runnerFactory;

        public WebChatSession(
            string? title,
            string provider,
            string model,
            Func<string, string, IReadOnlyList<ChatMessage>?, ICodingAgentRunner> runnerFactory)
        {
            _runnerFactory = runnerFactory;
            Id = Guid.NewGuid().ToString("N");
            Title = string.IsNullOrWhiteSpace(title) ? $"Session {DateTimeOffset.Now:HH:mm:ss}" : title.Trim();
            Provider = provider;
            Model = model;
            CreatedAt = DateTimeOffset.UtcNow;
            UpdatedAt = CreatedAt;
        }

        private WebChatSession(
            string id,
            string title,
            string provider,
            string model,
            DateTimeOffset createdAt,
            DateTimeOffset updatedAt,
            IReadOnlyList<WebChatMessageDto> messages,
            Func<string, string, IReadOnlyList<ChatMessage>?, ICodingAgentRunner> runnerFactory)
        {
            _runnerFactory = runnerFactory;
            Id = id;
            Title = title;
            Provider = provider;
            Model = model;
            CreatedAt = createdAt;
            UpdatedAt = updatedAt;
            _messages.AddRange(messages);
        }

        public string Id { get; }
        public string Title { get; private set; }
        public string Provider { get; private set; }
        public string Model { get; private set; }
        public DateTimeOffset CreatedAt { get; }
        public DateTimeOffset UpdatedAt { get; private set; }

        public static WebChatSession FromDto(
            WebChatSessionDto dto,
            Func<string, string, IReadOnlyList<ChatMessage>?, ICodingAgentRunner> runnerFactory)
        {
            return new WebChatSession(dto.Id, dto.Title, dto.Provider, dto.Model, dto.CreatedAt, dto.UpdatedAt, dto.Messages, runnerFactory);
        }

        public static WebChatSession FromImportedDto(
            WebChatSessionDto dto,
            string provider,
            string model,
            Func<string, string, IReadOnlyList<ChatMessage>?, ICodingAgentRunner> runnerFactory)
        {
            var now = DateTimeOffset.UtcNow;
            var title = string.IsNullOrWhiteSpace(dto.Title) ? $"Imported {now:HH:mm:ss}" : dto.Title.Trim();
            return new WebChatSession(Guid.NewGuid().ToString("N"), title, provider, model, now, now, dto.Messages, runnerFactory);
        }

        public void UpdateSettings(string? title, string provider, string model)
        {
            if (!string.IsNullOrWhiteSpace(title))
            {
                Title = title.Trim();
            }

            Provider = provider;
            Model = model;
            UpdatedAt = DateTimeOffset.UtcNow;
        }

        public void ClearMessages()
        {
            _messages.Clear();
            UpdatedAt = DateTimeOffset.UtcNow;
        }

        public WebChatSessionDto ToDto(bool persisted)
        {
            return new WebChatSessionDto(Id, Title, Provider, Model, CreatedAt, UpdatedAt, persisted, _messages.ToArray());
        }

        public async Task<WebChatSessionDto> SendAsync(CancellationToken cancellationToken)
        {
            WebChatSessionDto? finalSession = null;
            await foreach (var streamEvent in SendStreamAsync(cancellationToken).ConfigureAwait(false))
            {
                if (streamEvent.Session is not null)
                {
                    finalSession = streamEvent.Session;
                }
            }

            return finalSession ?? ToDto(persisted: false);
        }

        public async IAsyncEnumerable<WebChatStreamEventDto> SendStreamAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_messages.Count == 0 || _messages[^1].Role != "user" || !HasUsableInput(_messages[^1]))
                {
                    throw new InvalidOperationException("Latest user message is missing.");
                }

                var latestUserMessage = _messages[^1];
                var prompt = BuildUserContent(latestUserMessage);
                yield return new WebChatStreamEventDto(
                    "user",
                    Role: "user",
                    Text: latestUserMessage.Text,
                    Timestamp: latestUserMessage.Timestamp,
                    Attachments: latestUserMessage.Attachments);

                var runner = _runnerFactory(Provider, Model, BuildHistoryWithoutLatestUser());
                var assistant = new StringBuilder();
                var thinking = new StringBuilder();
                var toolEvents = new List<string>();
                var toolCalls = new List<WebChatToolCallState>();
                var toolCallIndex = new Dictionary<string, WebChatToolCallState>(StringComparer.Ordinal);
                string? error = null;

                await using var eventEnumerator = runner.RunAsync(prompt, cancellationToken).GetAsyncEnumerator(cancellationToken);
                while (true)
                {
                    AgentEvent? evt = null;
                    WebChatStreamEventDto? pendingErrorEvent = null;
                    try
                    {
                        if (!await eventEnumerator.MoveNextAsync().ConfigureAwait(false))
                        {
                            break;
                        }

                        evt = eventEnumerator.Current;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        error = ex.Message;
                        pendingErrorEvent = new WebChatStreamEventDto(
                            "error",
                            Role: "assistant",
                            Error: error,
                            Timestamp: DateTimeOffset.UtcNow);
                    }

                    if (pendingErrorEvent is not null)
                    {
                        yield return pendingErrorEvent;
                        break;
                    }

                    switch (evt)
                    {
                        case MessageUpdateEvent { StreamEvent: TextDeltaEvent delta }:
                            assistant.Append(delta.Delta);
                            yield return new WebChatStreamEventDto(
                                "text_delta",
                                Role: "assistant",
                                Text: delta.Delta,
                                Timestamp: DateTimeOffset.UtcNow);
                            break;
                        case MessageUpdateEvent { StreamEvent: ThinkingDeltaEvent delta }:
                            thinking.Append(delta.Delta);
                            yield return new WebChatStreamEventDto(
                                "thinking_delta",
                                Role: "assistant",
                                Thinking: delta.Delta,
                                Timestamp: DateTimeOffset.UtcNow);
                            break;
                        case MessageUpdateEvent { StreamEvent: ToolCallStartEvent toolCallStart }:
                            {
                                var toolCall = GetToolCall(toolCallStart.Partial, toolCallStart.ContentIndex);
                                var state = UpsertToolCall(toolCalls, toolCallIndex, toolCall, toolCallStart.ContentIndex);
                                state.Status = "preparing";
                                yield return new WebChatStreamEventDto(
                                    "tool_call",
                                    Role: "assistant",
                                    ToolCall: state.ToDto(),
                                    Timestamp: DateTimeOffset.UtcNow);
                                break;
                            }
                        case MessageUpdateEvent { StreamEvent: ToolCallDeltaEvent toolCallDelta }:
                            {
                                var toolCall = GetToolCall(toolCallDelta.Partial, toolCallDelta.ContentIndex);
                                var state = UpsertToolCall(toolCalls, toolCallIndex, toolCall, toolCallDelta.ContentIndex);
                                state.Status = "preparing";
                                state.Arguments = toolCall?.Arguments ?? state.Arguments + toolCallDelta.Delta;
                                yield return new WebChatStreamEventDto(
                                    "tool_call",
                                    Role: "assistant",
                                    ToolCall: state.ToDto(),
                                    Timestamp: DateTimeOffset.UtcNow);
                                break;
                            }
                        case MessageUpdateEvent { StreamEvent: ToolCallEndEvent toolCallEnd }:
                            {
                                var toolCall = GetToolCall(toolCallEnd.Partial, toolCallEnd.ContentIndex);
                                var state = UpsertToolCall(toolCalls, toolCallIndex, toolCall, toolCallEnd.ContentIndex);
                                state.Status = "ready";
                                yield return new WebChatStreamEventDto(
                                    "tool_call",
                                    Role: "assistant",
                                    ToolCall: state.ToDto(),
                                    Timestamp: DateTimeOffset.UtcNow);
                                break;
                            }
                        case ToolExecutionStartEvent toolStart:
                            var startEvent = $"start:{toolStart.ToolName}";
                            toolEvents.Add(startEvent);
                            var started = UpsertToolCall(toolCalls, toolCallIndex, toolStart.ToolCallId, toolStart.ToolName);
                            started.Status = "running";
                            started.StartedAt ??= DateTimeOffset.UtcNow;
                            yield return new WebChatStreamEventDto(
                                "tool_start",
                                Role: "assistant",
                                ToolEvent: startEvent,
                                ToolCall: started.ToDto(),
                                Timestamp: DateTimeOffset.UtcNow);
                            break;
                        case ToolExecutionUpdateEvent toolUpdate:
                            {
                                var updated = UpsertToolCall(toolCalls, toolCallIndex, toolUpdate.ToolCallId, "tool");
                                updated.Status = "running";
                                updated.Updates.Add(toolUpdate.Update.Text);
                                yield return new WebChatStreamEventDto(
                                    "tool_update",
                                    Role: "assistant",
                                    ToolCall: updated.ToDto(),
                                    Timestamp: DateTimeOffset.UtcNow);
                                break;
                            }
                        case ToolExecutionEndEvent toolEnd:
                            var endEvent = $"end:{toolEnd.ToolCallId}:{(toolEnd.Result.IsError ? "error" : "ok")}";
                            toolEvents.Add(endEvent);
                            var ended = UpsertToolCall(toolCalls, toolCallIndex, toolEnd.ToolCallId, "tool");
                            ended.Status = toolEnd.Result.IsError ? "error" : "completed";
                            ended.IsError = toolEnd.Result.IsError;
                            ended.Output = FormatToolResultContent(toolEnd.Result.Content);
                            ended.CompletedAt ??= DateTimeOffset.UtcNow;
                            yield return new WebChatStreamEventDto(
                                "tool_end",
                                Role: "assistant",
                                ToolEvent: endEvent,
                                ToolCall: ended.ToDto(),
                                Timestamp: DateTimeOffset.UtcNow);
                            break;
                        case AgentEndEvent end when end.ErrorMessage is not null:
                            error = end.ErrorMessage;
                            yield return new WebChatStreamEventDto(
                                "error",
                                Role: "assistant",
                                Error: error,
                                Timestamp: DateTimeOffset.UtcNow);
                            break;
                    }
                }

                UpdatedAt = DateTimeOffset.UtcNow;
                _messages.Add(new WebChatMessageDto(
                    "assistant",
                    assistant.Length > 0 ? assistant.ToString() : (error is null ? "(assistant produced no textual output)" : string.Empty),
                    UpdatedAt,
                    thinking.Length > 0 ? thinking.ToString() : null,
                    toolEvents.Count > 0 ? toolEvents.ToArray() : null,
                    error,
                    ToolCalls: toolCalls.Count > 0 ? toolCalls.Select(call => call.ToDto()).ToArray() : null));

                yield return new WebChatStreamEventDto(
                    "done",
                    Timestamp: UpdatedAt,
                    Session: ToDto(persisted: false));
            }
            finally
            {
                _gate.Release();
            }
        }

        public void AddUserMessage(string? text, IReadOnlyList<WebChatAttachmentDto>? attachments)
        {
            var normalizedText = string.IsNullOrWhiteSpace(text) ? string.Empty : text.Trim();
            var normalizedAttachments = NormalizeAttachments(attachments);
            if (string.IsNullOrWhiteSpace(normalizedText) && normalizedAttachments is not { Count: > 0 })
            {
                throw new ArgumentException("Message text or at least one attachment is required.", nameof(text));
            }

            _messages.Add(new WebChatMessageDto(
                "user",
                normalizedText,
                DateTimeOffset.UtcNow,
                Attachments: normalizedAttachments));
            UpdatedAt = DateTimeOffset.UtcNow;
        }

        private static bool HasUsableInput(WebChatMessageDto message) =>
            !string.IsNullOrWhiteSpace(message.Text) || message.Attachments is { Count: > 0 };

        private static IReadOnlyList<ContentBlock> BuildUserContent(WebChatMessageDto message)
        {
            var attachments = message.Attachments is { Count: > 0 } ? message.Attachments : null;
            var text = BuildPromptText(message.Text, attachments);
            var content = new List<ContentBlock> { new TextContent(text) };

            if (attachments is not null)
            {
                foreach (var attachment in attachments)
                {
                    if (IsImageAttachment(attachment) && !string.IsNullOrWhiteSpace(attachment.Content))
                    {
                        content.Add(new ImageContent(attachment.Content, attachment.MimeType));
                    }
                }
            }

            return content;
        }

        private static string BuildPromptText(string? text, IReadOnlyList<WebChatAttachmentDto>? attachments)
        {
            var builder = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(text))
            {
                builder.Append(text.Trim());
            }
            else if (attachments is { Count: > 0 })
            {
                builder.Append("Please review the attached file(s).");
            }

            if (attachments is not { Count: > 0 })
            {
                return builder.ToString();
            }

            if (builder.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLine();
            }

            foreach (var attachment in attachments)
            {
                builder
                    .Append("<file name=\"")
                    .Append(EscapePromptAttribute(attachment.FileName))
                    .Append("\" mimeType=\"")
                    .Append(EscapePromptAttribute(attachment.MimeType))
                    .Append("\" size=\"")
                    .Append(attachment.Size)
                    .AppendLine("\">");

                if (!string.IsNullOrWhiteSpace(attachment.ExtractedText))
                {
                    builder.AppendLine(attachment.ExtractedText.Trim());
                }
                else if (IsImageAttachment(attachment))
                {
                    builder.AppendLine("[Image attachment included as image content.]");
                }
                else
                {
                    builder.AppendLine("[Binary attachment persisted in the Web UI session; no extracted text is available.]");
                }

                builder.AppendLine("</file>");
            }

            return builder.ToString().TrimEnd();
        }

        private static IReadOnlyList<WebChatAttachmentDto>? NormalizeAttachments(IReadOnlyList<WebChatAttachmentDto>? attachments)
        {
            if (attachments is not { Count: > 0 })
            {
                return null;
            }

            var normalized = new List<WebChatAttachmentDto>(attachments.Count);
            foreach (var attachment in attachments)
            {
                var content = NormalizeBase64(attachment.Content);
                var preview = NormalizeBase64(attachment.Preview);
                var mimeType = string.IsNullOrWhiteSpace(attachment.MimeType)
                    ? "application/octet-stream"
                    : attachment.MimeType.Trim();
                var type = NormalizeAttachmentType(attachment.Type, mimeType);
                if (string.IsNullOrWhiteSpace(content) && string.IsNullOrWhiteSpace(attachment.ExtractedText))
                {
                    continue;
                }

                normalized.Add(attachment with
                {
                    Id = string.IsNullOrWhiteSpace(attachment.Id) ? Guid.NewGuid().ToString("N") : attachment.Id.Trim(),
                    Type = type,
                    FileName = string.IsNullOrWhiteSpace(attachment.FileName) ? "attachment" : attachment.FileName.Trim(),
                    MimeType = mimeType,
                    Size = attachment.Size > 0 ? attachment.Size : EstimateBase64Size(content),
                    Content = content,
                    Preview = string.IsNullOrWhiteSpace(preview) && type == "image" ? content : preview,
                    ExtractedText = string.IsNullOrWhiteSpace(attachment.ExtractedText) ? null : attachment.ExtractedText.Trim()
                });
            }

            return normalized.Count == 0 ? null : normalized.ToArray();
        }

        private static string NormalizeBase64(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var content = value.Trim();
            var comma = content.IndexOf(',', StringComparison.Ordinal);
            if (content.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0)
            {
                content = content[(comma + 1)..];
            }

            return content
                .Replace("\r", string.Empty, StringComparison.Ordinal)
                .Replace("\n", string.Empty, StringComparison.Ordinal);
        }

        private static string NormalizeAttachmentType(string? type, string mimeType)
        {
            if (string.Equals(type, "image", StringComparison.OrdinalIgnoreCase) ||
                mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                return "image";
            }

            return "document";
        }

        private static long EstimateBase64Size(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return 0;
            }

            var padding = content.EndsWith("==", StringComparison.Ordinal) ? 2 :
                content.EndsWith("=", StringComparison.Ordinal) ? 1 : 0;
            return Math.Max(0, (content.Length * 3L / 4L) - padding);
        }

        private static bool IsImageAttachment(WebChatAttachmentDto attachment) =>
            attachment.Type.Equals("image", StringComparison.OrdinalIgnoreCase) ||
            attachment.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

        private static ToolCallContent? GetToolCall(AssistantMessage partial, int contentIndex)
        {
            return contentIndex >= 0 &&
                contentIndex < partial.Content.Count &&
                partial.Content[contentIndex] is ToolCallContent toolCall
                    ? toolCall
                    : null;
        }

        private static WebChatToolCallState UpsertToolCall(
            List<WebChatToolCallState> toolCalls,
            Dictionary<string, WebChatToolCallState> toolCallIndex,
            ToolCallContent? toolCall,
            int contentIndex)
        {
            var id = string.IsNullOrWhiteSpace(toolCall?.Id)
                ? $"content-{contentIndex}"
                : toolCall.Id.Trim();
            var name = string.IsNullOrWhiteSpace(toolCall?.Name) ? "tool" : toolCall.Name.Trim();
            var state = UpsertToolCall(toolCalls, toolCallIndex, id, name);
            state.Arguments = toolCall?.Arguments ?? state.Arguments;
            return state;
        }

        private static WebChatToolCallState UpsertToolCall(
            List<WebChatToolCallState> toolCalls,
            Dictionary<string, WebChatToolCallState> toolCallIndex,
            string id,
            string toolName)
        {
            var normalizedId = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id.Trim();
            if (toolCallIndex.TryGetValue(normalizedId, out var existing))
            {
                if (!string.Equals(toolName, "tool", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(toolName))
                {
                    existing.ToolName = toolName.Trim();
                }

                return existing;
            }

            var state = new WebChatToolCallState(normalizedId, string.IsNullOrWhiteSpace(toolName) ? "tool" : toolName.Trim());
            toolCallIndex[normalizedId] = state;
            toolCalls.Add(state);
            return state;
        }

        private static string FormatToolResultContent(IReadOnlyList<ContentBlock> content)
        {
            var builder = new StringBuilder();
            foreach (var block in content)
            {
                switch (block)
                {
                    case TextContent text:
                        AppendToolOutputLine(builder, text.Text);
                        break;
                    case ImageContent image:
                        AppendToolOutputLine(builder, $"[image:{image.MimeType}; {EstimateBase64Size(image.Data)} bytes]");
                        break;
                    case ThinkingContent thinking when !string.IsNullOrWhiteSpace(thinking.Thinking):
                        AppendToolOutputLine(builder, thinking.Thinking);
                        break;
                    case ToolCallContent toolCall:
                        AppendToolOutputLine(builder, $"[tool-call:{toolCall.Name} {toolCall.Arguments}]");
                        break;
                }
            }

            return builder.ToString().TrimEnd();
        }

        private static void AppendToolOutputLine(StringBuilder builder, string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.Append(text.TrimEnd());
        }

        private static string EscapePromptAttribute(string value) =>
            value
                .Replace("&", "&amp;", StringComparison.Ordinal)
                .Replace("\"", "&quot;", StringComparison.Ordinal)
                .Replace("<", "&lt;", StringComparison.Ordinal)
                .Replace(">", "&gt;", StringComparison.Ordinal);

        private IReadOnlyList<ChatMessage> BuildHistoryWithoutLatestUser()
        {
            var history = new List<ChatMessage>(Math.Max(0, _messages.Count - 1));
            for (var index = 0; index < _messages.Count - 1; index++)
            {
                var message = _messages[index];
                switch (message.Role)
                {
                    case "user":
                        history.Add(new UserMessage(BuildUserContent(message)));
                        break;
                    case "assistant":
                        if (message.ToolCalls is { Count: > 0 })
                        {
                            var toolCallContent = new List<ContentBlock>();
                            if (!string.IsNullOrWhiteSpace(message.Thinking))
                            {
                                toolCallContent.Add(new ThinkingContent(message.Thinking));
                            }

                            foreach (var toolCall in message.ToolCalls)
                            {
                                toolCallContent.Add(new ToolCallContent(
                                    toolCall.Id,
                                    toolCall.ToolName,
                                    toolCall.Arguments ?? string.Empty));
                            }

                            if (toolCallContent.Count > 0)
                            {
                                history.Add(new AssistantMessage(toolCallContent));
                            }

                            foreach (var toolCall in message.ToolCalls)
                            {
                                if (!string.IsNullOrWhiteSpace(toolCall.Output))
                                {
                                    history.Add(new ToolResultMessage(
                                        toolCall.Id,
                                        [new TextContent(toolCall.Output)],
                                        toolCall.IsError));
                                }
                            }

                            if (!string.IsNullOrWhiteSpace(message.Text))
                            {
                                history.Add(new AssistantMessage([new TextContent(message.Text)]));
                            }

                            break;
                        }

                        var content = new List<ContentBlock>();
                        if (!string.IsNullOrWhiteSpace(message.Text))
                        {
                            content.Add(new TextContent(message.Text));
                        }
                        if (!string.IsNullOrWhiteSpace(message.Thinking))
                        {
                            content.Add(new ThinkingContent(message.Thinking));
                        }
                        if (content.Count > 0)
                        {
                            history.Add(new AssistantMessage(content));
                        }
                        break;
                }
            }

            return history;
        }

        private sealed class WebChatToolCallState
        {
            public WebChatToolCallState(string id, string toolName)
            {
                Id = id;
                ToolName = toolName;
                CreatedAt = DateTimeOffset.UtcNow;
            }

            public string Id { get; }
            public string ToolName { get; set; }
            public string Status { get; set; } = "preparing";
            public string Arguments { get; set; } = string.Empty;
            public string? Output { get; set; }
            public bool IsError { get; set; }
            public DateTimeOffset CreatedAt { get; }
            public DateTimeOffset? StartedAt { get; set; }
            public DateTimeOffset? CompletedAt { get; set; }
            public List<string> Updates { get; } = [];

            public WebChatToolCallDto ToDto()
            {
                return new WebChatToolCallDto(
                    Id,
                    ToolName,
                    Status,
                    string.IsNullOrWhiteSpace(Arguments) ? null : Arguments,
                    string.IsNullOrWhiteSpace(Output) ? null : Output,
                    IsError,
                    CreatedAt,
                    StartedAt,
                    CompletedAt,
                    Updates.Count > 0 ? Updates.ToArray() : null);
            }
        }
    }
}
