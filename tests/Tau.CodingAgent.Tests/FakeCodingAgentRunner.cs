using Tau.Agent;
using Tau.Ai;
using Tau.Ai.Auth;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed class FakeCodingAgentRunner : ICodingAgentRunner
{
    private readonly Func<string, CancellationToken, IAsyncEnumerable<AgentEvent>> _run;
    private readonly Dictionary<string, List<Model>> _models = new(StringComparer.OrdinalIgnoreCase)
    {
        ["openai"] =
        [
            new Model
            {
                Provider = "openai",
                Id = "gpt-5.4",
                Name = "GPT-5.4",
                Api = "openai-responses"
            }
        ],
        ["google"] =
        [
            new Model
            {
                Provider = "google",
                Id = "gemini-2.5-pro",
                Name = "Gemini 2.5 Pro",
                Api = "google-gemini"
            }
        ]
    };

    public FakeCodingAgentRunner(Func<string, CancellationToken, IAsyncEnumerable<AgentEvent>> run)
    {
        _run = run;
        Model = _models["openai"][0];
    }

    public List<string> Inputs { get; } = [];
    public List<ChatMessage> MutableMessages { get; } = [];
    public IReadOnlyList<ChatMessage> Messages => MutableMessages;
    public Model Model { get; private set; }
    public string? SessionName { get; set; }
    public Func<string?, CancellationToken, Task<CodingAgentCompactionResult>>? CompactHandler { get; set; }
    public string? LastCompactInstructions { get; private set; }
    public int ResetSessionCalls { get; private set; }

    public IReadOnlyList<string> GetProviders() => [.. _models.Keys.Order(StringComparer.OrdinalIgnoreCase)];

    public IReadOnlyList<Model> GetModels(string provider) =>
        _models.TryGetValue(provider, out var models) ? models : [];

    public Model SelectModel(string? providerId, string? modelId)
    {
        var provider = string.IsNullOrWhiteSpace(providerId) ? Model.Provider : providerId;
        if (!string.IsNullOrWhiteSpace(modelId) && modelId.Contains('/'))
        {
            var parts = modelId.Split('/', 2);
            provider = parts[0];
            modelId = parts[1];
        }

        if (!_models.TryGetValue(provider, out var models))
        {
            throw new KeyNotFoundException($"Provider '{provider}' is not registered.");
        }

        Model = string.IsNullOrWhiteSpace(modelId)
            ? models[0]
            : models.Single(model => model.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase));
        return Model;
    }

    public ProviderAuthStatus AuthStatus { get; set; } = new("openai", false, "none", false, false, "No credentials found.");

    public ProviderAuthStatus GetAuthStatus(string? providerId = null) =>
        AuthStatus with { Provider = string.IsNullOrWhiteSpace(providerId) ? Model.Provider : providerId };

    public CodingAgentSessionStats GetSessionStats(string? sessionFile = null)
    {
        return new CodingAgentSessionStats(
            Model.Provider,
            Model.Id,
            Messages.Count,
            Messages.OfType<UserMessage>().Count(),
            Messages.OfType<AssistantMessage>().Count(),
            Messages.OfType<ToolResultMessage>().Count(),
            Messages.OfType<AssistantMessage>().Sum(message => message.Content.OfType<ToolCallContent>().Count()),
            SessionName,
            sessionFile);
    }
    public Task<CodingAgentCompactionResult> CompactAsync(string? customInstructions = null, CancellationToken cancellationToken = default)
    {
        LastCompactInstructions = customInstructions;
        if (CompactHandler is not null)
        {
            return CompactHandler(customInstructions, cancellationToken);
        }

        throw new InvalidOperationException("Compaction is not configured for this test runner.");
    }

    public void ResetSession()
    {
        ResetSessionCalls++;
        MutableMessages.Clear();
        SessionName = null;
    }

    public void RestoreSession(CodingAgentSessionSnapshot snapshot)
    {
        SelectModel(snapshot.Provider, snapshot.Model);
        MutableMessages.Clear();
        MutableMessages.AddRange(snapshot.Messages);
        SessionName = snapshot.Name;
    }

    public IAsyncEnumerable<AgentEvent> RunAsync(string input, CancellationToken cancellationToken = default)
    {
        Inputs.Add(input);
        return _run(input, cancellationToken);
    }
}
