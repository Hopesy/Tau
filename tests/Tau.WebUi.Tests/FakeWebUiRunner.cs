using Tau.Agent;
using Tau.Agent.Runtime;
using Tau.Ai;
using Tau.Ai.Auth;
using Tau.CodingAgent.Runtime;

namespace Tau.WebUi.Tests;

internal sealed class FakeWebUiRunner : ICodingAgentRunner
{
    private readonly Func<string, CancellationToken, IAsyncEnumerable<AgentEvent>> _run;

    public FakeWebUiRunner(Func<string, CancellationToken, IAsyncEnumerable<AgentEvent>> run)
    {
        _run = run;
        Model = new Model
        {
            Provider = "openai",
            Id = "gpt-5.4",
            Name = "GPT-5.4",
            Api = "openai-responses",
            ContextWindow = 128_000
        };
    }

    public List<ChatMessage> MutableMessages { get; } = [];
    public IReadOnlyList<ChatMessage> Messages => MutableMessages;
    public Model Model { get; private set; }
    public string? SessionName { get; set; }
    public ThinkingLevel? ThinkingLevel { get; set; }
    public AgentQueueMode SteeringMode { get; set; } = AgentQueueMode.OneAtATime;
    public AgentQueueMode FollowUpMode { get; set; } = AgentQueueMode.OneAtATime;

    public IReadOnlyList<string> GetProviders() => [Model.Provider];

    public IReadOnlyList<Model> GetModels(string provider) =>
        provider.Equals(Model.Provider, StringComparison.OrdinalIgnoreCase) ? [Model] : [];

    public Model SelectModel(string? providerId, string? modelId)
    {
        Model = new Model
        {
            Provider = string.IsNullOrWhiteSpace(providerId) ? Model.Provider : providerId,
            Id = string.IsNullOrWhiteSpace(modelId) ? Model.Id : modelId,
            Name = string.IsNullOrWhiteSpace(modelId) ? Model.Name : modelId,
            Api = Model.Api,
            ContextWindow = Model.ContextWindow
        };
        return Model;
    }

    public ProviderAuthStatus GetAuthStatus(string? providerId = null) =>
        new(providerId ?? Model.Provider, false, "none", false, false, "No credentials found.");

    public Tau.Ai.Auth.OAuth.IOAuthProvider? GetOAuthProvider(string providerId) => null;

    public void SaveOAuthCredentials(string providerId, Tau.Ai.Auth.OAuth.OAuthCredentials credentials) { }

    public bool Logout(string providerId) => false;

    public bool RefreshSkills(IReadOnlyList<CodingAgentSkill> skills) => false;

    public bool RefreshSystemPromptResources(
        IReadOnlyList<CodingAgentSkill> skills,
        IReadOnlyList<CodingAgentContextFile> contextFiles) => false;

    public CodingAgentSessionStats GetSessionStats(string? sessionFile = null) =>
        new(Model.Provider, Model.Id, Messages.Count, 0, 0, 0, 0, 0, Model.ContextWindow, SessionName, sessionFile);

    public Task<CodingAgentCompactionResult> CompactAsync(string? customInstructions = null, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Compaction is not configured for browser tests.");

    public Task<CodingAgentBranchSummaryResult> SummarizeBranchAsync(
        IReadOnlyList<ChatMessage> messages,
        string? customInstructions = null,
        bool replaceInstructions = false,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Branch summarization is not configured for browser tests.");

    public void Steer(string input) { }

    public void Steer(IReadOnlyList<ContentBlock> input) { }

    public void FollowUp(string input) { }

    public void FollowUp(IReadOnlyList<ContentBlock> input) { }

    public void ResetSession()
    {
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

    public IAsyncEnumerable<AgentEvent> RunAsync(string input, CancellationToken cancellationToken = default) =>
        _run(input, cancellationToken);

    public IAsyncEnumerable<AgentEvent> RunAsync(IReadOnlyList<ContentBlock> input, CancellationToken cancellationToken = default)
    {
        var text = string.Join(Environment.NewLine, input.OfType<TextContent>().Select(c => c.Text));
        return _run(text, cancellationToken);
    }
}
