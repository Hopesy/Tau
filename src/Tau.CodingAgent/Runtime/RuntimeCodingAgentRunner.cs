using System.Text;
using Tau.Agent;
using Tau.Agent.Runtime;
using Tau.Ai;
using Tau.Ai.Auth;
using Tau.Ai.Auth.OAuth;
using Tau.Ai.Observability;
using Tau.Ai.Providers;
using Tau.Ai.Registry;
using Tau.CodingAgent.Tools;

namespace Tau.CodingAgent.Runtime;

public sealed class RuntimeCodingAgentRunner : ICodingAgentRunner
{
    private readonly AgentRuntime _runtime;
    private readonly ModelCatalog _modelCatalog;
    private readonly ProviderAuthResolver _authResolver;
    private readonly ITauLogSink _logSink;
    private AgentLoopConfig _config;

    public RuntimeCodingAgentRunner(
        AgentRuntime runtime,
        AgentLoopConfig config,
        ModelCatalog modelCatalog,
        ProviderAuthResolver? authResolver = null,
        ITauLogSink? logSink = null)
    {
        _runtime = runtime;
        _config = config;
        _modelCatalog = modelCatalog;
        _authResolver = authResolver ?? new ProviderAuthResolver();
        _logSink = logSink ?? NullTauLogSink.Instance;
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

    public IOAuthProvider? GetOAuthProvider(string providerId) =>
        _authResolver.GetOAuthProvider(providerId);

    public void SaveOAuthCredentials(string providerId, OAuthCredentials credentials) =>
        _authResolver.SaveOAuthCredentials(providerId, credentials);

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
        var compactionPrompt = BuildCompactionPrompt(customInstructions, Messages);
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

    private static string BuildCompactionPrompt(string? customInstructions, IReadOnlyList<ChatMessage> messages)
    {
        var previousSummary = ExtractPreviousSummary(messages);
        var basePrompt = previousSummary is not null
            ? CodingAgentCompactionMessages.UpdatePrompt
            : CodingAgentCompactionMessages.Prompt;

        var builder = new StringBuilder(basePrompt);

        if (previousSummary is not null)
        {
            builder.AppendLine();
            builder.AppendLine();
            builder.AppendLine("<previous-summary>");
            builder.Append(previousSummary.Trim());
            builder.AppendLine();
            builder.AppendLine("</previous-summary>");
        }

        var fileOperations = CodingAgentFileOperationTracker.FormatForCompaction(messages);
        if (!string.IsNullOrWhiteSpace(fileOperations))
        {
            builder.AppendLine();
            builder.AppendLine();
            builder.AppendLine("File operations observed in this session:");
            builder.Append(fileOperations);
        }

        if (!string.IsNullOrWhiteSpace(customInstructions))
        {
            builder.AppendLine();
            builder.AppendLine();
            builder.AppendLine("Additional focus:");
            builder.Append(customInstructions.Trim());
        }

        return builder.ToString();
    }

    private static string? ExtractPreviousSummary(IReadOnlyList<ChatMessage> messages)
    {
        if (messages.Count == 0) return null;
        var first = messages[0];
        if (!CodingAgentCompactionMessages.IsSummaryMessage(first)) return null;

        var text = ((TextContent)((UserMessage)first).Content[0]).Text;
        var startIndex = text.IndexOf(CodingAgentCompactionMessages.SummaryPrefix, StringComparison.Ordinal);
        if (startIndex < 0) return null;

        var contentStart = startIndex + CodingAgentCompactionMessages.SummaryPrefix.Length;
        var endIndex = text.IndexOf(CodingAgentCompactionMessages.SummarySuffix, contentStart, StringComparison.Ordinal);
        if (endIndex < 0) return text[contentStart..].Trim();

        return text[contentStart..endIndex].Trim();
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
        return InstrumentRun(_runtime.RunAsync(_config, cancellationToken), inputBytes: input?.Length ?? 0);
    }

    public IAsyncEnumerable<AgentEvent> RunAsync(IReadOnlyList<ContentBlock> input, CancellationToken cancellationToken = default)
    {
        if (input.Count == 0)
        {
            throw new ArgumentException("Input content must not be empty.", nameof(input));
        }

        _runtime.AddMessage(new UserMessage(input));
        var sizeHint = input.OfType<TextContent>().Sum(static block => block.Text.Length);
        return InstrumentRun(_runtime.RunAsync(_config, cancellationToken), inputBytes: sizeHint);
    }

    private async IAsyncEnumerable<AgentEvent> InstrumentRun(
        IAsyncEnumerable<AgentEvent> inner,
        int inputBytes)
    {
        var startedAt = DateTimeOffset.UtcNow;
        _logSink.Log(new TauLogEvent(
            "agent",
            "run.start",
            startedAt,
            new Dictionary<string, string?>
            {
                ["provider"] = _config.Model.Provider,
                ["model"] = _config.Model.Id,
                ["inputBytes"] = inputBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }));

        var hadError = false;
        var hadCancel = false;
        await using var enumerator = inner.GetAsyncEnumerator();
        try
        {
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    hadCancel = true;
                    throw;
                }
                catch (Exception ex)
                {
                    hadError = true;
                    _logSink.Log(new TauLogEvent(
                        "agent",
                        "run.error",
                        DateTimeOffset.UtcNow,
                        new Dictionary<string, string?>
                        {
                            ["provider"] = _config.Model.Provider,
                            ["model"] = _config.Model.Id,
                            ["error"] = ex.GetType().Name,
                            ["message"] = ex.Message
                        }));
                    throw;
                }

                if (!hasNext) break;
                yield return enumerator.Current;
            }
        }
        finally
        {
            if (!hadError)
            {
                var endedAt = DateTimeOffset.UtcNow;
                _logSink.Log(new TauLogEvent(
                    "agent",
                    hadCancel ? "run.cancel" : "run.end",
                    endedAt,
                    new Dictionary<string, string?>
                    {
                        ["provider"] = _config.Model.Provider,
                        ["model"] = _config.Model.Id,
                        ["elapsedMs"] = ((long)(endedAt - startedAt).TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    }));
            }
        }
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
        IReadOnlyList<CodingAgentSkill>? skills = null,
        ITauLogSink? logSink = null)
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

        return new RuntimeCodingAgentRunner(runtime, config, modelCatalog, logSink: logSink);
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
        var cwd = Directory.GetCurrentDirectory().Replace('\\', '/');
        var date = DateTimeOffset.Now.ToString("yyyy-MM-dd");
        var toolsList = string.Join(", ", tools.Select(t => t.Name));

        var prompt = $"""
            You are Tau, an expert coding assistant. You help users by reading files, executing commands, editing code, and writing new files.

            Available tools: {toolsList}

            Guidelines:
            - Prefer grep/find/ls tools over bash for file exploration when available
            - Be concise in your responses
            - Show file paths clearly when working with files
            - Read relevant code before making changes
            - Run tests after making changes to verify correctness
            - Use tools to explore the codebase rather than guessing

            Current date: {date}
            Current working directory: {cwd}
            Platform: {Environment.OSVersion}
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
