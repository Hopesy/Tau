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

    public IAsyncEnumerable<AgentEvent> RunAsync(string input, CancellationToken cancellationToken = default)
    {
        _runtime.AddMessage(new UserMessage(input));
        return _runtime.RunAsync(_config, cancellationToken);
    }

    public static RuntimeCodingAgentRunner CreateDefault()
    {
        var registry = new ProviderRegistry();
        BuiltInProviders.RegisterAll(registry);
        var modelCatalog = new ModelCatalog();
        var providerId = Environment.GetEnvironmentVariable("TAU_PROVIDER") ?? "openai";
        var modelId = Environment.GetEnvironmentVariable("TAU_MODEL") ?? GetDefaultModelId(providerId);
        var model = modelCatalog.GetModel(providerId, modelId);

        IAgentTool[] tools =
        [
            new ReadFileTool(),
            new WriteFileTool(),
            new EditFileTool(),
            new ShellTool(),
            new GlobTool(),
            new GrepTool(),
            new ListDirectoryTool()
        ];

        var systemPrompt = $"""
            You are Tau, a coding assistant. You help users with software engineering tasks.

            Working directory: {Directory.GetCurrentDirectory()}
            Platform: {Environment.OSVersion}

            Available tools: {string.Join(", ", tools.Select(t => t.Name))}

            Use tools to explore the codebase, read files, make edits, and run commands.
            Be concise. Think step by step.
            """;

        var config = new AgentLoopConfig
        {
            Model = model,
            ProviderRegistry = registry,
            Tools = tools,
            SystemPrompt = systemPrompt,
            StreamOptions = new SimpleStreamOptions { MaxTokens = 16_384 }
        };

        return new RuntimeCodingAgentRunner(new AgentRuntime(), config);
    }

    private static string GetDefaultModelId(string providerId)
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
}
