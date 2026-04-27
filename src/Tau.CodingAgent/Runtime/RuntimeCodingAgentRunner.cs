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
        var resolvedProvider = string.IsNullOrWhiteSpace(providerId)
            ? (Environment.GetEnvironmentVariable("TAU_PROVIDER") ?? "openai")
            : providerId.Trim();
        var resolvedModel = string.IsNullOrWhiteSpace(modelId)
            ? (Environment.GetEnvironmentVariable("TAU_MODEL") ?? GetDefaultModelId(resolvedProvider))
            : modelId.Trim();

        var model = modelCatalog.GetModel(resolvedProvider, resolvedModel);
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

    public static string GetDefaultModelId(string providerId)
    {
        return providerId.ToLowerInvariant() switch
        {
            "anthropic" => "claude-opus-4-6",
            "openai" => "gpt-5.4",
            "google" => "gemini-2.5-pro",
            "azure-openai-responses" => "gpt-5.2",
            "openai-codex" => "gpt-5.4",
            "github-copilot" => "gpt-4o",
            "mistral" => "devstral-medium-latest",
            "google-vertex" => "gemini-3-pro-preview",
            "google-gemini-cli" => "gemini-2.5-pro",
            "google-antigravity" => "gemini-3.1-pro-high",
            "amazon-bedrock" => "us.anthropic.claude-opus-4-6-v1",
            _ => "gpt-5.4"
        };
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
        return $"""
            You are Tau, a coding assistant. You help users with software engineering tasks.

            Working directory: {Directory.GetCurrentDirectory()}
            Platform: {Environment.OSVersion}

            Available tools: {string.Join(", ", tools.Select(t => t.Name))}

            Use tools to explore the codebase, read files, make edits, and run commands.
            Be concise. Think step by step.
            """;
    }
}
