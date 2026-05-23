using Tau.Agent;
using Tau.Agent.Runtime;
using Tau.Ai;
using Tau.Ai.Auth;
using Tau.Ai.Auth.OAuth;
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
                Api = "openai-responses",
                ContextWindow = 128_000,
                Reasoning = true
            }
        ],
        ["google"] =
        [
            new Model
            {
                Provider = "google",
                Id = "gemini-2.5-pro",
                Name = "Gemini 2.5 Pro",
                Api = "google-gemini",
                ContextWindow = 1_048_576,
                Reasoning = true
            }
        ]
    };

    public FakeCodingAgentRunner(Func<string, CancellationToken, IAsyncEnumerable<AgentEvent>> run)
    {
        _run = run;
        Model = _models["openai"][0];
    }

    public List<string> Inputs { get; } = [];
    public List<IReadOnlyList<ContentBlock>> ContentInputs { get; } = [];
    public List<string> SteeringInputs { get; } = [];
    public List<string> FollowUpInputs { get; } = [];
    public List<ChatMessage> MutableMessages { get; } = [];
    public IReadOnlyList<ChatMessage> Messages => MutableMessages;
    public Model Model { get; private set; }
    public string? SessionName { get; set; }
    public ThinkingLevel? ThinkingLevel { get; set; }
    public AgentQueueMode SteeringMode { get; set; } = AgentQueueMode.OneAtATime;
    public AgentQueueMode FollowUpMode { get; set; } = AgentQueueMode.OneAtATime;
    public Func<string?, CancellationToken, Task<CodingAgentCompactionResult>>? CompactHandler { get; set; }
    public Func<IReadOnlyList<ChatMessage>, string?, CancellationToken, Task<CodingAgentBranchSummaryResult>>? BranchSummaryHandler { get; set; }
    public Action<string>? SteeringObserver { get; set; }
    public Action<string>? FollowUpObserver { get; set; }
    public string? LastCompactInstructions { get; private set; }
    public string? LastBranchSummaryInstructions { get; private set; }
    public IReadOnlyList<ChatMessage>? LastBranchSummaryMessages { get; private set; }
    public IReadOnlyList<CodingAgentSkill>? LastRefreshedSkills { get; private set; }
    public IReadOnlyList<CodingAgentContextFile>? LastRefreshedContextFiles { get; private set; }
    public bool RefreshSkillsResult { get; set; } = true;
    public List<string> LoggedOutProviders { get; } = [];
    public bool LogoutResult { get; set; } = true;
    public int ResetSessionCalls { get; private set; }

    public IReadOnlyList<string> GetProviders() => [.. _models.Keys.Order(StringComparer.OrdinalIgnoreCase)];

    public IReadOnlyList<Model> GetModels(string provider) =>
        _models.TryGetValue(provider, out var models) ? models : [];

    public void SetModelReasoning(string provider, string modelId, bool reasoning)
    {
        if (!_models.TryGetValue(provider, out var models))
        {
            throw new KeyNotFoundException($"Provider '{provider}' is not registered.");
        }

        var index = models.FindIndex(model => model.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            throw new KeyNotFoundException($"Model '{provider}/{modelId}' is not registered.");
        }

        var updated = models[index] with { Reasoning = reasoning };
        models[index] = updated;
        if (Model.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase) &&
            Model.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase))
        {
            Model = updated;
        }
    }

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
    public Dictionary<string, ProviderAuthStatus> AuthStatuses { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void ConfigureAuth(params string[] providers)
    {
        foreach (var provider in providers)
        {
            AuthStatuses[provider] = new ProviderAuthStatus(
                provider,
                true,
                "environment",
                false,
                false,
                "Credentials are available.");
        }
    }

    public ProviderAuthStatus GetAuthStatus(string? providerId = null)
    {
        var provider = string.IsNullOrWhiteSpace(providerId) ? Model.Provider : providerId;
        return AuthStatuses.TryGetValue(provider, out var status)
            ? status with { Provider = provider }
            : AuthStatus with { Provider = provider };
    }

    public IOAuthProvider? GetOAuthProvider(string providerId) =>
        OAuthProvider is not null && OAuthProvider.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase)
            ? OAuthProvider
            : null;

    public void SaveOAuthCredentials(string providerId, OAuthCredentials credentials)
    {
        SavedOAuthCredentials = (providerId, credentials);
    }

    public IOAuthProvider? OAuthProvider { get; set; }
    public (string ProviderId, OAuthCredentials Credentials)? SavedOAuthCredentials { get; private set; }

    public bool Logout(string providerId)
    {
        LoggedOutProviders.Add(providerId);
        return LogoutResult;
    }

    public bool RefreshSkills(IReadOnlyList<CodingAgentSkill> skills)
    {
        return RefreshSystemPromptResources(skills, LastRefreshedContextFiles ?? []);
    }

    public bool RefreshSystemPromptResources(
        IReadOnlyList<CodingAgentSkill> skills,
        IReadOnlyList<CodingAgentContextFile> contextFiles)
    {
        LastRefreshedSkills = skills;
        LastRefreshedContextFiles = contextFiles;
        return RefreshSkillsResult;
    }

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
            CodingAgentTokenEstimator.Estimate(Messages),
            Model.ContextWindow,
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

    public Task<CodingAgentBranchSummaryResult> SummarizeBranchAsync(
        IReadOnlyList<ChatMessage> messages,
        string? customInstructions = null,
        CancellationToken cancellationToken = default)
    {
        LastBranchSummaryMessages = messages;
        LastBranchSummaryInstructions = customInstructions;
        if (BranchSummaryHandler is not null)
        {
            return BranchSummaryHandler(messages, customInstructions, cancellationToken);
        }

        throw new InvalidOperationException("Branch summarization is not configured for this test runner.");
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

    public void Steer(string input)
    {
        SteeringInputs.Add(input);
        SteeringObserver?.Invoke(input);
    }

    public void FollowUp(string input)
    {
        FollowUpInputs.Add(input);
        FollowUpObserver?.Invoke(input);
    }

    public IAsyncEnumerable<AgentEvent> RunAsync(string input, CancellationToken cancellationToken = default)
    {
        Inputs.Add(input);
        return _run(input, cancellationToken);
    }

    public IAsyncEnumerable<AgentEvent> RunAsync(IReadOnlyList<ContentBlock> input, CancellationToken cancellationToken = default)
    {
        ContentInputs.Add(input);
        var text = string.Join(Environment.NewLine, input.OfType<TextContent>().Select(content => content.Text));
        return _run(text, cancellationToken);
    }
}
