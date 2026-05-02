using Tau.Agent;
using Tau.Agent.Runtime;
using Tau.Ai;
using Tau.Ai.Providers;
using Tau.Ai.Registry;
using Tau.CodingAgent.Tools;

namespace Tau.CodingAgent.Runtime;

public sealed class RuntimeCodingAgentRunner : ICodingAgentRunner
{
    private readonly AgentRuntime _runtime;
    private readonly AgentLoopConfig _config;

    public RuntimeCodingAgentRunner(AgentRuntime runtime, AgentLoopConfig config)
    {
        _runtime = runtime;
        _config = config;
    }

    public IReadOnlyList<ChatMessage> Messages => _runtime.State.Messages;
    public Model Model => _config.Model;

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

        return new RuntimeCodingAgentRunner(runtime, config);
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
}
