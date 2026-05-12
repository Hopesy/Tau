using Tau.Agent;
using Tau.Agent.Runtime;
using Tau.Ai;
using Tau.Ai.Auth;
using Tau.Ai.Providers;
using Tau.Ai.Registry;
using Tau.CodingAgent.Tools;

namespace Tau.CodingAgent.Runtime;

public sealed class RuntimeCodingAgentRunner : ICodingAgentRunner
{
    private readonly AgentRuntime _runtime;
    private readonly ModelCatalog _modelCatalog;
    private readonly ProviderAuthResolver _authResolver;
    private AgentLoopConfig _config;

    public RuntimeCodingAgentRunner(AgentRuntime runtime, AgentLoopConfig config, ModelCatalog modelCatalog, ProviderAuthResolver? authResolver = null)
    {
        _runtime = runtime;
        _config = config;
        _modelCatalog = modelCatalog;
        _authResolver = authResolver ?? new ProviderAuthResolver();
    }

    public IReadOnlyList<ChatMessage> Messages => _runtime.State.Messages;
    public Model Model => _config.Model;
    public string? SessionName { get; set; }

    public IReadOnlyList<string> GetProviders() => _modelCatalog.GetProviders();

    public IReadOnlyList<Model> GetModels(string provider) => _modelCatalog.GetModels(provider);

    public Model SelectModel(string? providerId, string? modelId)
    {
        var selection = _modelCatalog.ResolveSelection(providerId, modelId, defaultProvider: _config.Model.Provider);
        var model = _modelCatalog.GetModel(selection.Provider, selection.ModelId);
        _config = _config with { Model = model };
        return model;
    }

    public ProviderAuthStatus GetAuthStatus(string? providerId = null)
    {
        var provider = string.IsNullOrWhiteSpace(providerId) ? _config.Model.Provider : providerId;
        var model = provider.Equals(_config.Model.Provider, StringComparison.OrdinalIgnoreCase)
            ? _config.Model
            : _modelCatalog.GetModels(provider).FirstOrDefault();
        return _authResolver.GetStatus(provider, model);
    }

    public CodingAgentSessionStats GetSessionStats(string? sessionFile = null)
    {
        var messages = Messages;
        return new CodingAgentSessionStats(
            _config.Model.Provider,
            _config.Model.Id,
            messages.Count,
            messages.OfType<UserMessage>().Count(),
            messages.OfType<AssistantMessage>().Count(),
            messages.OfType<ToolResultMessage>().Count(),
            messages.OfType<AssistantMessage>().Sum(message => message.Content.OfType<ToolCallContent>().Count()),
            CodingAgentTokenEstimator.Estimate(messages),
            _config.Model.ContextWindow,
            SessionName,
            sessionFile);
    }
    public async Task<CodingAgentCompactionResult> CompactAsync(
        string? customInstructions = null,
        CancellationToken cancellationToken = default)
    {
        if (Messages.Count == 0)
        {
            throw new InvalidOperationException("Nothing to compact (session is empty)");
        }

        if (Messages.Count == 1 && IsCompactionSummaryMessage(Messages[0]))
        {
            throw new InvalidOperationException("Already compacted");
        }

        if (Messages.Count < 2)
        {
            throw new InvalidOperationException("Nothing to compact (session too small)");
        }

        var tokensBefore = CodingAgentTokenEstimator.Estimate(Messages);
        var compactionPrompt = string.IsNullOrWhiteSpace(customInstructions)
            ? CodingAgentCompactionMessages.Prompt
            : $"{CodingAgentCompactionMessages.Prompt}\n\nAdditional instructions:\n{customInstructions.Trim()}";
        var summaryContext = new LlmContext(
            CodingAgentCompactionMessages.SystemPrompt,
            [.. Messages, new UserMessage(compactionPrompt)],
            Tools: null);
        var summaryOptions = (_config.StreamOptions ?? new SimpleStreamOptions { MaxTokens = 16_384 }) with
        {
            MaxTokens = Math.Min(_config.StreamOptions?.MaxTokens ?? 16_384, 4_096),
            CacheRetention = CacheRetention.None,
            SessionId = null
        };

        var summaryMessage = await StreamFunctions
            .CompleteSimpleAsync(_config.ProviderRegistry, _config.Model, summaryContext, summaryOptions)
            .ConfigureAwait(false);
        var summary = ExtractCompactionSummary(summaryMessage);
        if (string.IsNullOrWhiteSpace(summary))
        {
            throw new InvalidOperationException("Compaction produced no text summary");
        }

        var messagesBefore = Messages.Count;
        _runtime.Reset();
        _runtime.AddMessage(CodingAgentCompactionMessages.CreateSummaryMessage(summary));

        return new CodingAgentCompactionResult(summary, messagesBefore, Messages.Count, tokensBefore);
    }

    public void ResetSession()
    {
        _runtime.Reset();
        SessionName = null;
    }

    public void RestoreSession(CodingAgentSessionSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.Provider) || !string.IsNullOrWhiteSpace(snapshot.Model))
        {
            SelectModel(snapshot.Provider, snapshot.Model);
        }

        _runtime.Reset();
        foreach (var message in snapshot.Messages)
        {
            _runtime.AddMessage(message);
        }

        SessionName = snapshot.Name;
    }

    public IAsyncEnumerable<AgentEvent> RunAsync(string input, CancellationToken cancellationToken = default)
    {
        _runtime.AddMessage(new UserMessage(input));
        return _runtime.RunAsync(_config, cancellationToken);
    }

    public static RuntimeCodingAgentRunner CreateDefault()
    {
        return Create();
    }

    public static RuntimeCodingAgentRunner Create(
        string? providerId = null,
        string? modelId = null,
        IReadOnlyList<ChatMessage>? initialMessages = null,
        IReadOnlyList<IAgentTool>? toolsOverride = null,
        string? systemPromptOverride = null,
        IReadOnlyList<CodingAgentSkill>? skills = null)
    {
        var registry = new ProviderRegistry();
        BuiltInProviders.RegisterAll(registry);

        var modelCatalog = new ModelCatalog();
        var selection = modelCatalog.ResolveSelection(
            providerId,
            modelId,
            defaultProvider: Environment.GetEnvironmentVariable("TAU_PROVIDER"));

        var model = modelCatalog.GetModel(selection.Provider, selection.ModelId);
        var tools = toolsOverride ?? CreateDefaultTools();
        var config = new AgentLoopConfig
        {
            Model = model,
            ProviderRegistry = registry,
            Tools = tools,
            SystemPrompt = string.IsNullOrWhiteSpace(systemPromptOverride) ? BuildSystemPrompt(tools, skills ?? []) : systemPromptOverride,
            StreamOptions = new SimpleStreamOptions { MaxTokens = 16_384 }
        };

        var runtime = new AgentRuntime();
        if (initialMessages is not null)
        {
            foreach (var message in initialMessages)
            {
                runtime.AddMessage(message);
            }
        }

        return new RuntimeCodingAgentRunner(runtime, config, modelCatalog);
    }

    public static string GetDefaultProviderId() => ModelCatalog.GetDefaultProviderId();

    public static string GetDefaultModelId(string providerId)
    {
        return ModelCatalog.GetDefaultModelId(providerId);
    }

    private static IAgentTool[] CreateDefaultTools()
    {
        return
        [
            new ReadFileTool(),
            new WriteFileTool(),
            new EditFileTool(),
            new ShellTool(),
            new GlobTool(),
            new GrepTool(),
            new ListDirectoryTool()
        ];
    }

    private static string BuildSystemPrompt(IReadOnlyList<IAgentTool> tools, IReadOnlyList<CodingAgentSkill> skills)
    {
        var prompt = $$"""
            You are Tau, a coding assistant. You help users with software engineering tasks.

            Working directory: {{Directory.GetCurrentDirectory()}}
            Platform: {{Environment.OSVersion}}

            Available tools: {{string.Join(", ", tools.Select(t => t.Name))}}

            Use tools to explore the codebase, read files, make edits, and run commands.
            Be concise. Think step by step.
            """;
        return prompt + CodingAgentSkillStore.FormatForSystemPrompt(skills);
    }

    private static string ExtractCompactionSummary(AssistantMessage message)
    {
        return string.Join(
            "\n\n",
            message.Content
                .OfType<TextContent>()
                .Select(content => content.Text.Trim())
                .Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    private static bool IsCompactionSummaryMessage(ChatMessage message)
    {
        return CodingAgentCompactionMessages.IsSummaryMessage(message);
    }
}
