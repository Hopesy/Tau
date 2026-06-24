using System.Text;
using Tau.Ai;
using Tau.Ai.Providers;

namespace Tau.AgentCore.Harness;

public sealed class AgentSummaryException : Exception
{
    public AgentSummaryException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed record AgentSummaryGenerationOptions
{
    public required ProviderRegistry ProviderRegistry { get; init; }
    public required Model Model { get; init; }
    public string? CustomInstructions { get; init; }
    public bool ReplaceInstructions { get; init; }
    public int? ReserveTokens { get; init; }
    public ThinkingLevel? ThinkingLevel { get; init; }
    public CancellationToken CancellationToken { get; init; }
}

public sealed record AgentCompactionResult(
    string Summary,
    string FirstKeptEntryId,
    int TokensBefore,
    AgentCompactionDetails Details);

public sealed record AgentBranchSummaryResult(
    string Summary,
    IReadOnlyList<string> ReadFiles,
    IReadOnlyList<string> ModifiedFiles);

public static class AgentCompactionSummaries
{
    public const string SummarizationSystemPrompt = """
        You are a context summarization assistant. Your task is to read a conversation between a user and an AI assistant, then produce a structured summary following the exact format specified.

        Do NOT continue the conversation. Do NOT respond to any questions in the conversation. ONLY output the structured summary.
        """;

    public const string SummarizationPrompt = """
        The messages above are a conversation to summarize. Create a structured context checkpoint summary that another LLM will use to continue the work.

        Use this EXACT format:

        ## Goal
        [What is the user trying to accomplish? Can be multiple items if the session covers different tasks.]

        ## Constraints & Preferences
        - [Any constraints, preferences, or requirements mentioned by user]
        - [Or "(none)" if none were mentioned]

        ## Progress
        ### Done
        - [x] [Completed tasks/changes]

        ### In Progress
        - [ ] [Current work]

        ### Blocked
        - [Issues preventing progress, if any]

        ## Key Decisions
        - **[Decision]**: [Brief rationale]

        ## Next Steps
        1. [Ordered list of what should happen next]

        ## Critical Context
        - [Any data, examples, or references needed to continue]
        - [Or "(none)" if not applicable]

        Keep each section concise. Preserve exact file paths, function names, and error messages.
        """;

    public const string UpdateSummarizationPrompt = """
        The messages above are NEW conversation messages to incorporate into the existing summary provided in <previous-summary> tags.

        Update the existing structured summary with new information. RULES:
        - PRESERVE all existing information from the previous summary
        - ADD new progress, decisions, and context from the new messages
        - UPDATE the Progress section: move items from "In Progress" to "Done" when completed
        - UPDATE "Next Steps" based on what was accomplished
        - PRESERVE exact file paths, function names, and error messages
        - If something is no longer relevant, you may remove it

        Use this EXACT format:

        ## Goal
        [Preserve existing goals, add new ones if the task expanded]

        ## Constraints & Preferences
        - [Preserve existing, add new ones discovered]

        ## Progress
        ### Done
        - [x] [Include previously done items AND newly completed items]

        ### In Progress
        - [ ] [Current work - update based on progress]

        ### Blocked
        - [Current blockers - remove if resolved]

        ## Key Decisions
        - **[Decision]**: [Brief rationale] (preserve all previous, add new)

        ## Next Steps
        1. [Update based on current state]

        ## Critical Context
        - [Preserve important context, add new if needed]

        Keep each section concise. Preserve exact file paths, function names, and error messages.
        """;

    public const string TurnPrefixSummarizationPrompt = """
        This is the PREFIX of a turn that was too large to keep. The SUFFIX (recent work) is retained.

        Summarize the prefix to provide context for the retained suffix:

        ## Original Request
        [What did the user ask for in this turn?]

        ## Early Progress
        - [Key decisions and work done in the prefix]

        ## Context for Suffix
        - [Information needed to understand the kept suffix]

        Be concise. Focus on what's needed to understand the kept suffix.
        """;

    public static string BuildSummaryPrompt(
        IReadOnlyList<ChatMessage> currentMessages,
        string? customInstructions = null,
        string? previousSummary = null)
    {
        ArgumentNullException.ThrowIfNull(currentMessages);

        var llmMessages = AgentHarnessMessages.ConvertToLlm(currentMessages);
        var conversationText = AgentCompaction.SerializeConversation(llmMessages);
        var basePrompt = previousSummary is null ? SummarizationPrompt : UpdateSummarizationPrompt;
        if (!string.IsNullOrWhiteSpace(customInstructions))
            basePrompt = $"{basePrompt}\n\nAdditional focus: {customInstructions.Trim()}";

        var builder = new StringBuilder();
        builder.Append("<conversation>\n")
            .Append(conversationText)
            .Append("\n</conversation>\n\n");

        if (previousSummary is not null)
        {
            builder.Append("<previous-summary>\n")
                .Append(previousSummary)
                .Append("\n</previous-summary>\n\n");
        }

        builder.Append(basePrompt);
        return builder.ToString();
    }

    public static string BuildTurnPrefixSummaryPrompt(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var llmMessages = AgentHarnessMessages.ConvertToLlm(messages);
        var conversationText = AgentCompaction.SerializeConversation(llmMessages);
        return $"<conversation>\n{conversationText}\n</conversation>\n\n{TurnPrefixSummarizationPrompt}";
    }

    public static async Task<string> GenerateSummaryAsync(
        IReadOnlyList<ChatMessage> currentMessages,
        AgentSummaryGenerationOptions options)
    {
        ArgumentNullException.ThrowIfNull(currentMessages);
        ArgumentNullException.ThrowIfNull(options);

        var reserveTokens = options.ReserveTokens ?? AgentCompactionSettings.Default.ReserveTokens;
        var prompt = BuildSummaryPrompt(
            currentMessages,
            options.CustomInstructions,
            previousSummary: null);

        return await GeneratePromptSummaryAsync(
            prompt,
            options,
            maxTokens: ResolveMaxTokens(options.Model, reserveTokens, 0.8),
            abortedMessage: "Summarization aborted",
            failurePrefix: "Summarization failed").ConfigureAwait(false);
    }

    public static async Task<string> GenerateUpdatedSummaryAsync(
        IReadOnlyList<ChatMessage> currentMessages,
        string previousSummary,
        AgentSummaryGenerationOptions options)
    {
        ArgumentNullException.ThrowIfNull(currentMessages);
        ArgumentNullException.ThrowIfNull(previousSummary);
        ArgumentNullException.ThrowIfNull(options);

        var reserveTokens = options.ReserveTokens ?? AgentCompactionSettings.Default.ReserveTokens;
        var prompt = BuildSummaryPrompt(
            currentMessages,
            options.CustomInstructions,
            previousSummary);

        return await GeneratePromptSummaryAsync(
            prompt,
            options,
            maxTokens: ResolveMaxTokens(options.Model, reserveTokens, 0.8),
            abortedMessage: "Summarization aborted",
            failurePrefix: "Summarization failed").ConfigureAwait(false);
    }

    public static async Task<string> GenerateTurnPrefixSummaryAsync(
        IReadOnlyList<ChatMessage> messages,
        AgentSummaryGenerationOptions options)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(options);

        var reserveTokens = options.ReserveTokens ?? AgentCompactionSettings.Default.ReserveTokens;
        return await GeneratePromptSummaryAsync(
            BuildTurnPrefixSummaryPrompt(messages),
            options with { CustomInstructions = null },
            maxTokens: ResolveMaxTokens(options.Model, reserveTokens, 0.5),
            abortedMessage: "Turn prefix summarization aborted",
            failurePrefix: "Turn prefix summarization failed").ConfigureAwait(false);
    }

    public static async Task<AgentCompactionResult> CompactAsync(
        AgentCompactionPreparation preparation,
        AgentSummaryGenerationOptions options)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(preparation.FirstKeptEntryId))
        {
            throw new Session.SessionException(
                "invalid_session",
                "First kept entry has no UUID - session may need migration");
        }

        var effectiveOptions = options with
        {
            ReserveTokens = options.ReserveTokens ?? preparation.Settings.ReserveTokens
        };

        string summary;
        if (preparation.IsSplitTurn && preparation.TurnPrefixMessages.Count > 0)
        {
            var historyTask = preparation.MessagesToSummarize.Count > 0
                ? GenerateHistorySummaryAsync(preparation, effectiveOptions)
                : Task.FromResult("No prior history.");
            var turnPrefixTask = GenerateTurnPrefixSummaryAsync(preparation.TurnPrefixMessages, effectiveOptions);

            await Task.WhenAll(historyTask, turnPrefixTask).ConfigureAwait(false);
            summary = $"{historyTask.Result}\n\n---\n\n**Turn Context (split turn):**\n\n{turnPrefixTask.Result}";
        }
        else
        {
            summary = await GenerateHistorySummaryAsync(preparation, effectiveOptions).ConfigureAwait(false);
        }

        var details = AgentCompaction.ComputeFileLists(preparation.FileOperations);
        summary += AgentCompaction.FormatFileOperations(details.ReadFiles, details.ModifiedFiles);
        return new AgentCompactionResult(
            summary,
            preparation.FirstKeptEntryId,
            preparation.TokensBefore,
            details);
    }

    internal static async Task<string> GeneratePromptSummaryAsync(
        string prompt,
        AgentSummaryGenerationOptions options,
        int maxTokens,
        string abortedMessage,
        string failurePrefix)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(options);

        var streamOptions = new SimpleStreamOptions
        {
            MaxTokens = maxTokens,
            Signal = options.CancellationToken
        };
        if (options.Model.Reasoning && options.ThinkingLevel is { } thinkingLevel)
            streamOptions = streamOptions with { Reasoning = thinkingLevel };

        AssistantMessage response;
        try
        {
            response = await StreamFunctions.CompleteSimpleAsync(
                options.ProviderRegistry,
                options.Model,
                new LlmContext(
                    SummarizationSystemPrompt,
                    [new UserMessage([new TextContent(prompt)])],
                    Tools: null),
                streamOptions).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (options.CancellationToken.IsCancellationRequested)
        {
            throw new AgentSummaryException("aborted", abortedMessage);
        }
        catch (Exception ex) when (ex is not AgentSummaryException)
        {
            throw new AgentSummaryException("summarization_failed", $"{failurePrefix}: {ex.Message}", ex);
        }

        if (response.StopReason == StopReason.Aborted)
            throw new AgentSummaryException("aborted", response.ErrorMessage ?? abortedMessage);
        if (response.StopReason == StopReason.Error)
            throw new AgentSummaryException(
                "summarization_failed",
                $"{failurePrefix}: {response.ErrorMessage ?? "Unknown error"}");

        return string.Join("\n", response.Content.OfType<TextContent>().Select(static content => content.Text));
    }

    private static Task<string> GenerateHistorySummaryAsync(
        AgentCompactionPreparation preparation,
        AgentSummaryGenerationOptions options) =>
        preparation.PreviousSummary is { } previousSummary
            ? GenerateUpdatedSummaryAsync(preparation.MessagesToSummarize, previousSummary, options)
            : GenerateSummaryAsync(preparation.MessagesToSummarize, options);

    private static int ResolveMaxTokens(Model model, int reserveTokens, double reserveRatio)
    {
        var reserved = (int)Math.Floor(reserveTokens * reserveRatio);
        if (model.MaxOutputTokens is not > 0)
            return reserved;

        return Math.Min(reserved, model.MaxOutputTokens.Value);
    }
}
