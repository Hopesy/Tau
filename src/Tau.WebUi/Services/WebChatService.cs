using System.Collections.Concurrent;
using System.Text;
using Tau.Agent;
using Tau.Ai;
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
    private readonly WebChatStore _store;

    public WebChatService(WebChatStore store)
    {
        _store = store;
        _catalog = new ModelCatalog();
        _defaultProvider = Environment.GetEnvironmentVariable("TAU_PROVIDER") ?? "openai";
        _defaultModel = Environment.GetEnvironmentVariable("TAU_MODEL") ?? RuntimeCodingAgentRunner.GetDefaultModelId(_defaultProvider);

        foreach (var session in _store.Load())
        {
            _sessions[session.Id] = WebChatSession.FromDto(session);
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

    public WebChatSessionDto CreateSession(string? title = null, string? provider = null, string? model = null)
    {
        var resolvedProvider = ResolveProvider(provider);
        var resolvedModel = ResolveModel(resolvedProvider, model);
        var session = new WebChatSession(title, resolvedProvider, resolvedModel);
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

    public WebChatSessionDto? GetSession(string id)
    {
        return _sessions.TryGetValue(id, out var session) ? session.ToDto(persisted: true) : null;
    }

    public WebChatSessionDto? UpdateSessionSettings(string id, UpdateSessionSettingsRequest request)
    {
        if (!_sessions.TryGetValue(id, out var session))
        {
            return null;
        }

        var provider = string.IsNullOrWhiteSpace(request.Provider) ? session.Provider : ResolveProvider(request.Provider);
        var model = string.IsNullOrWhiteSpace(request.Model) ? session.Model : ResolveModel(provider, request.Model);
        session.UpdateSettings(request.Title, provider, model);
        Persist();
        return session.ToDto(persisted: true);
    }

    public async Task<WebChatSessionDto?> SendMessageAsync(string id, string text, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(id, out var session))
        {
            return null;
        }

        session.AddUserMessage(text);
        var dto = await session.SendAsync(cancellationToken).ConfigureAwait(false);
        Persist();
        return dto with { Persisted = true };
    }

    private void Persist()
    {
        _store.Save(_sessions.Values
            .OrderBy(session => session.CreatedAt)
            .Select(session => session.ToDto(persisted: true))
            .ToArray());
    }

    private string ResolveProvider(string? provider)
    {
        var resolved = string.IsNullOrWhiteSpace(provider) ? _defaultProvider : provider.Trim();
        if (!_catalog.GetProviders().Contains(resolved, StringComparer.OrdinalIgnoreCase))
        {
            throw new KeyNotFoundException($"Provider '{resolved}' is not registered.");
        }

        return resolved;
    }

    private string ResolveModel(string provider, string? model)
    {
        var resolved = string.IsNullOrWhiteSpace(model)
            ? RuntimeCodingAgentRunner.GetDefaultModelId(provider)
            : model.Trim();

        _ = _catalog.GetModel(provider, resolved);
        return resolved;
    }

    private sealed class WebChatSession
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly List<WebChatMessageDto> _messages = [];

        public WebChatSession(string? title, string provider, string model)
        {
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
            IReadOnlyList<WebChatMessageDto> messages)
        {
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

        public static WebChatSession FromDto(WebChatSessionDto dto)
        {
            return new WebChatSession(dto.Id, dto.Title, dto.Provider, dto.Model, dto.CreatedAt, dto.UpdatedAt, dto.Messages);
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

        public WebChatSessionDto ToDto(bool persisted)
        {
            return new WebChatSessionDto(Id, Title, Provider, Model, CreatedAt, UpdatedAt, persisted, _messages.ToArray());
        }

        public async Task<WebChatSessionDto> SendAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_messages.Count == 0 || _messages[^1].Role != "user" || string.IsNullOrWhiteSpace(_messages[^1].Text))
                {
                    throw new InvalidOperationException("Latest user message is missing.");
                }

                var prompt = _messages[^1].Text;
                var runner = WebUiRunnerFactory.Create(Provider, Model, BuildHistoryWithoutLatestUser());
                var assistant = new StringBuilder();
                var thinking = new StringBuilder();
                var toolEvents = new List<string>();
                string? error = null;

                await foreach (var evt in runner.RunAsync(prompt, cancellationToken).ConfigureAwait(false))
                {
                    switch (evt)
                    {
                        case MessageUpdateEvent { StreamEvent: TextDeltaEvent delta }:
                            assistant.Append(delta.Delta);
                            break;
                        case MessageUpdateEvent { StreamEvent: ThinkingDeltaEvent delta }:
                            thinking.Append(delta.Delta);
                            break;
                        case ToolExecutionStartEvent toolStart:
                            toolEvents.Add($"start:{toolStart.ToolName}");
                            break;
                        case ToolExecutionEndEvent toolEnd:
                            toolEvents.Add($"end:{toolEnd.ToolCallId}:{(toolEnd.Result.IsError ? "error" : "ok")}");
                            break;
                        case AgentEndEvent end when end.ErrorMessage is not null:
                            error = end.ErrorMessage;
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
                    error));

                return ToDto(persisted: false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public void AddUserMessage(string text)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(text);
            _messages.Add(new WebChatMessageDto("user", text.Trim(), DateTimeOffset.UtcNow));
            UpdatedAt = DateTimeOffset.UtcNow;
        }

        private IReadOnlyList<ChatMessage> BuildHistoryWithoutLatestUser()
        {
            var history = new List<ChatMessage>(Math.Max(0, _messages.Count - 1));
            for (var index = 0; index < _messages.Count - 1; index++)
            {
                var message = _messages[index];
                switch (message.Role)
                {
                    case "user":
                        history.Add(new UserMessage(message.Text));
                        break;
                    case "assistant":
                        var content = new List<ContentBlock>();
                        if (!string.IsNullOrWhiteSpace(message.Text))
                        {
                            content.Add(new TextContent(message.Text));
                        }
                        if (!string.IsNullOrWhiteSpace(message.Thinking))
                        {
                            content.Add(new ThinkingContent(message.Thinking));
                        }
                        history.Add(new AssistantMessage(content));
                        break;
                }
            }

            return history;
        }
    }
}
