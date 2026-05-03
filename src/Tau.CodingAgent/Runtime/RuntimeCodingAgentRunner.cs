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
    private const string CompactionSummaryPrefix = """
        The conversation history before this point was compacted into the following summary:

        <summary>
        """;
    private const string CompactionSummarySuffix = """
        </summary>
        """;
    private const string CompactionSystemPrompt = """
        You are compacting an ongoing coding-agent session.
        Produce a concise markdown summary that preserves:
        - the user's current goal
        - implementation decisions and constraints
        - relevant files, commands, and observed outcomes
        - unresolved issues and immediate next steps
        Do not invent facts. Keep the summary dense and operational.
        """;
    private const string CompactionPrompt = """
        Compact this coding-agent session for future continuation.
        Summarize only facts already present in the conversation.
        Focus on task state, code changes, runtime findings, risks, and next steps.
        """;

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

        var compactionPrompt = string.IsNullOrWhiteSpace(customInstructions)
            ? CompactionPrompt
            : $"{CompactionPrompt}\n\nAdditional instructions:\n{customInstructions.Trim()}";
        var summaryContext = new LlmContext(
            CompactionSystemPrompt,
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
        _runtime.AddMessage(new UserMessage($"{CompactionSummaryPrefix}\n{summary}\n{CompactionSummarySuffix}"));

        return new CodingAgentCompactionResult(summary, messagesBefore, Messages.Count);
    }

    public void ResetSession()
    {
        _runtime.Reset();
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
        IReadOnlyList<ChatMessage>? initialMessages = null)
    {
        var registry = new ProviderRegistry();
        BuiltInProviders.RegisterAll(registry);

        var modelCatalog = new ModelCatalog();
        var selection = modelCatalog.ResolveSelection(
            providerId,
            modelId,
            defaultProvider: Environment.GetEnvironmentVariable("TAU_PROVIDER"));

        var model = modelCatalog.GetModel(selection.Provider, selection.ModelId);
        var tools = CreateDefaultTools();
        var config = new AgentLoopConfig
        {
            Model = model,
            ProviderRegistry = registry,
            Tools = tools,
            SystemPrompt = BuildSystemPrompt(tools),
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

    private static string BuildSystemPrompt(IReadOnlyList<IAgentTool> tools)
    {
        return $$"""
            You are Tau, a coding assistant. You help users with software engineering tasks.

            Working directory: {{Directory.GetCurrentDirectory()}}
            Platform: {{Environment.OSVersion}}

            Available tools: {{string.Join(", ", tools.Select(t => t.Name))}}

            Use tools to explore the codebase, read files, make edits, and run commands.
            Be concise. Think step by step.
            """;
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
        return message is UserMessage user &&
               user.Content.Count == 1 &&
               user.Content[0] is TextContent text &&
               text.Text.StartsWith(CompactionSummaryPrefix, StringComparison.Ordinal);
    }
}
