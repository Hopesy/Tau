using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

public static class CodingAgentHtmlSessionExporter
{
    private const int FoldLongToolResultTextLength = 4000;
    private const int FoldLongToolCallArgumentsLength = 4000;
    private const int ToolSummaryPreviewLength = 800;

    private static readonly JsonSerializerOptions ToolArgumentsJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    private static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
    {
        "abstract",
        "as",
        "base",
        "bool",
        "break",
        "byte",
        "case",
        "catch",
        "char",
        "checked",
        "class",
        "const",
        "continue",
        "decimal",
        "default",
        "delegate",
        "do",
        "double",
        "else",
        "enum",
        "event",
        "explicit",
        "extern",
        "false",
        "finally",
        "fixed",
        "float",
        "for",
        "foreach",
        "goto",
        "if",
        "implicit",
        "in",
        "int",
        "interface",
        "internal",
        "is",
        "lock",
        "long",
        "namespace",
        "new",
        "null",
        "object",
        "operator",
        "out",
        "override",
        "params",
        "private",
        "protected",
        "public",
        "readonly",
        "record",
        "ref",
        "return",
        "sbyte",
        "sealed",
        "short",
        "sizeof",
        "stackalloc",
        "static",
        "string",
        "struct",
        "switch",
        "this",
        "throw",
        "true",
        "try",
        "typeof",
        "uint",
        "ulong",
        "unchecked",
        "unsafe",
        "ushort",
        "using",
        "var",
        "virtual",
        "void",
        "volatile",
        "while",
        "with",
        "yield"
    };

    private static readonly HashSet<string> ShellKeywords = new(StringComparer.Ordinal)
    {
        "case",
        "do",
        "done",
        "elif",
        "else",
        "esac",
        "export",
        "fi",
        "for",
        "function",
        "if",
        "in",
        "local",
        "return",
        "select",
        "set",
        "then",
        "until",
        "while"
    };

    private static readonly HashSet<string> PowerShellKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "begin",
        "break",
        "catch",
        "class",
        "continue",
        "data",
        "do",
        "dynamicparam",
        "else",
        "elseif",
        "end",
        "enum",
        "exit",
        "filter",
        "finally",
        "for",
        "foreach",
        "from",
        "function",
        "if",
        "in",
        "param",
        "process",
        "return",
        "switch",
        "throw",
        "trap",
        "try",
        "until",
        "using",
        "var",
        "while"
    };

    private static readonly HashSet<string> JavaScriptKeywords = new(StringComparer.Ordinal)
    {
        "async",
        "await",
        "break",
        "case",
        "catch",
        "class",
        "const",
        "continue",
        "debugger",
        "default",
        "delete",
        "do",
        "else",
        "enum",
        "export",
        "extends",
        "false",
        "finally",
        "for",
        "from",
        "function",
        "if",
        "implements",
        "import",
        "in",
        "instanceof",
        "interface",
        "let",
        "new",
        "null",
        "of",
        "package",
        "private",
        "protected",
        "public",
        "return",
        "static",
        "super",
        "switch",
        "this",
        "throw",
        "true",
        "try",
        "type",
        "typeof",
        "undefined",
        "var",
        "void",
        "while",
        "with",
        "yield"
    };

    private static readonly HashSet<string> PythonKeywords = new(StringComparer.Ordinal)
    {
        "and",
        "as",
        "assert",
        "async",
        "await",
        "break",
        "class",
        "continue",
        "def",
        "del",
        "elif",
        "else",
        "except",
        "False",
        "finally",
        "for",
        "from",
        "global",
        "if",
        "import",
        "in",
        "is",
        "lambda",
        "None",
        "nonlocal",
        "not",
        "or",
        "pass",
        "raise",
        "return",
        "True",
        "try",
        "while",
        "with",
        "yield"
    };

    public static bool IsHtmlPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var extension = Path.GetExtension(path);
        return extension.Equals(".html", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".htm", StringComparison.OrdinalIgnoreCase);
    }

    public static string Export(
        string path,
        IReadOnlyList<ChatMessage> messages,
        string? provider,
        string? model,
        string? sessionName,
        CodingAgentTreeSessionSummary? treeSummary = null,
        string? sessionJsonl = null,
        CodingAgentSecretRedactor? redactor = null)
    {
        var exportPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(exportPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var effectiveRedactor = redactor ?? CodingAgentSecretRedactor.Default;
        var redactedMessages = effectiveRedactor.Enabled
            ? RedactMessages(messages, effectiveRedactor)
            : messages;
        var redactedJsonl = effectiveRedactor.Enabled && !string.IsNullOrEmpty(sessionJsonl)
            ? effectiveRedactor.Redact(sessionJsonl)
            : sessionJsonl;

        var html = Render(redactedMessages, provider, model, sessionName, treeSummary, redactedJsonl);
        File.WriteAllText(exportPath, html, Encoding.UTF8);
        return exportPath;
    }

    private static IReadOnlyList<ChatMessage> RedactMessages(
        IReadOnlyList<ChatMessage> messages,
        CodingAgentSecretRedactor redactor)
    {
        var result = new ChatMessage[messages.Count];
        for (var i = 0; i < messages.Count; i++)
        {
            result[i] = RedactMessage(messages[i], redactor);
        }

        return result;
    }

    private static ChatMessage RedactMessage(ChatMessage message, CodingAgentSecretRedactor redactor)
    {
        return message switch
        {
            UserMessage user => user with { Content = RedactContent(user.Content, redactor) },
            AssistantMessage assistant => assistant with
            {
                Content = RedactContent(assistant.Content, redactor),
                ErrorMessage = redactor.Redact(assistant.ErrorMessage)
            },
            ToolResultMessage toolResult => new ToolResultMessage(
                toolResult.ToolCallId,
                RedactContent(toolResult.Content, redactor),
                toolResult.IsError),
            _ => message
        };
    }

    private static IReadOnlyList<ContentBlock> RedactContent(
        IReadOnlyList<ContentBlock> content,
        CodingAgentSecretRedactor redactor)
    {
        var result = new ContentBlock[content.Count];
        for (var i = 0; i < content.Count; i++)
        {
            result[i] = content[i] switch
            {
                TextContent text => new TextContent(redactor.Redact(text.Text))
                {
                    TextSignature = text.TextSignature
                },
                ThinkingContent thinking => new ThinkingContent(redactor.Redact(thinking.Thinking))
                {
                    ThinkingSignature = thinking.ThinkingSignature,
                    Redacted = thinking.Redacted
                },
                ToolCallContent toolCall => new ToolCallContent(
                    toolCall.Id,
                    toolCall.Name,
                    redactor.Redact(toolCall.Arguments))
                {
                    ThoughtSignature = toolCall.ThoughtSignature
                },
                _ => content[i]
            };
        }

        return result;
    }

    private static string Render(
        IReadOnlyList<ChatMessage> messages,
        string? provider,
        string? model,
        string? sessionName,
        CodingAgentTreeSessionSummary? treeSummary,
        string? sessionJsonl)
    {
        var stats = SessionStats.FromMessages(messages);
        var title = string.IsNullOrWhiteSpace(sessionName) ? "Tau Coding Agent Session" : sessionName.Trim();
        var generatedAt = DateTimeOffset.Now;
        var jsonl = string.IsNullOrWhiteSpace(sessionJsonl)
            ? BuildStandaloneJsonl(messages, provider, model, sessionName)
            : EnsureTrailingNewline(sessionJsonl);
        var timeline = SessionTimeline.FromJsonl(jsonl, messages);
        var downloadName = $"tau-session-{SanitizeFileName(title)}.jsonl";
        var builder = new StringBuilder();
        builder.AppendLine("<!doctype html>");
        builder.AppendLine("<html lang=\"en\">");
        builder.AppendLine("<head>");
        builder.AppendLine("<meta charset=\"utf-8\">");
        builder.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        builder.Append("<title>").Append(Html(title)).AppendLine("</title>");
        builder.AppendLine("<style>");
        builder.AppendLine(Css);
        builder.AppendLine("</style>");
        builder.AppendLine("</head>");
        builder.AppendLine("<body>");
        builder.AppendLine("<div class=\"layout\">");
        builder.AppendLine("<aside class=\"session-sidebar\" aria-label=\"Session branch outline\">");
        builder.AppendLine("<div class=\"sidebar-title\">Branch Outline</div>");
        builder.AppendLine("<div class=\"tree-controls\">");
        builder.AppendLine("<input id=\"tree-search\" class=\"tree-search\" type=\"search\" placeholder=\"Search entries\" aria-label=\"Search session entries\">");
        builder.AppendLine("<div class=\"tree-filter-row\" aria-label=\"Filter session entries\">");
        builder.AppendLine("<button type=\"button\" class=\"tree-filter-button active\" data-filter=\"default\">Default</button>");
        builder.AppendLine("<button type=\"button\" class=\"tree-filter-button\" data-filter=\"no-tools\">No tools</button>");
        builder.AppendLine("<button type=\"button\" class=\"tree-filter-button\" data-filter=\"user-only\">User</button>");
        builder.AppendLine("<button type=\"button\" class=\"tree-filter-button\" data-filter=\"labeled-only\">Labeled</button>");
        builder.AppendLine("<button type=\"button\" class=\"tree-filter-button\" data-filter=\"all\">All</button>");
        builder.AppendLine("</div>");
        builder.AppendLine("</div>");
        builder.AppendLine("<div id=\"tree-list\" class=\"tree-list\">Loading...</div>");
        builder.AppendLine("<div id=\"tree-status\" class=\"tree-status\"></div>");
        builder.AppendLine("</aside>");
        builder.AppendLine("<main class=\"shell\">");
        builder.AppendLine("<header class=\"page-header\">");
        builder.Append("<p class=\"eyebrow\">Tau Coding Agent</p>");
        builder.Append("<h1>").Append(Html(title)).AppendLine("</h1>");
        builder.AppendLine("<div class=\"header-actions\">");
        builder.Append("<button type=\"button\" class=\"download-button\" onclick=\"downloadSessionJsonl()\" data-download-name=\"")
            .Append(HtmlAttribute(downloadName))
            .AppendLine("\">Download JSONL</button>");
        builder.AppendLine("</div>");
        builder.AppendLine("<dl class=\"meta-grid\">");
        AppendMeta(builder, "Generated", generatedAt.ToString("yyyy-MM-dd HH:mm:ss zzz"));
        AppendMeta(builder, "Model", FormatModel(provider, model));
        AppendMeta(builder, "Messages", $"{messages.Count} total, {stats.UserMessages} user, {stats.AssistantMessages} assistant, {stats.ToolResults} tool results");
        AppendMeta(builder, "Tool calls", stats.ToolCalls.ToString());
        if (timeline.EntryCount > 0)
        {
            AppendMeta(
                builder,
                "Entries",
                $"{timeline.EntryCount} branch entries, {timeline.CompactionCount} compactions, {timeline.RetryEventCount} retry events, {timeline.LabelChangeCount} label changes");
        }

        if (treeSummary is not null)
        {
            AppendMeta(builder, "Tree", $"{ShortId(treeSummary.LeafId)} leaf, {treeSummary.BranchMessageCount} branch messages, {treeSummary.LabelCount} labels");
            if (!string.IsNullOrWhiteSpace(treeSummary.Cwd))
            {
                AppendMeta(builder, "Cwd", treeSummary.Cwd);
            }

            if (!string.IsNullOrWhiteSpace(treeSummary.ParentSession))
            {
                AppendMeta(builder, "Parent session", treeSummary.ParentSession);
            }
        }

        builder.AppendLine("</dl>");
        builder.AppendLine("</header>");

        if (timeline.Items.Count == 0)
        {
            builder.AppendLine("<section class=\"empty-state\">No messages in this session.</section>");
        }
        else
        {
            builder.AppendLine("<section class=\"timeline\" aria-label=\"Session transcript\">");
            var messageIndex = 0;
            var toolCalls = new Dictionary<string, ToolCallRenderContext>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in timeline.Items)
            {
                if (item.Message is { } message)
                {
                    messageIndex++;
                    RenderMessage(builder, message, messageIndex, item.Id, item.ResolvedLabel, item.Timestamp, toolCalls);
                    RegisterToolCalls(message, toolCalls);
                    continue;
                }

                RenderSessionEntry(builder, item);
            }

            builder.AppendLine("</section>");
        }

        builder.Append("<textarea id=\"session-jsonl\" hidden>")
            .Append(Html(jsonl))
            .AppendLine("</textarea>");
        builder.AppendLine("<script>");
        builder.AppendLine(Script);
        builder.AppendLine("</script>");
        builder.AppendLine("</main>");
        builder.AppendLine("</div>");
        builder.AppendLine("</body>");
        builder.AppendLine("</html>");
        return builder.ToString();
    }

    private static void RenderMessage(
        StringBuilder builder,
        ChatMessage message,
        int index,
        string? entryId,
        string? label,
        DateTimeOffset? timestamp,
        IReadOnlyDictionary<string, ToolCallRenderContext> toolCalls)
    {
        switch (message)
        {
            case UserMessage user:
                AppendArticleStart(builder, index, entryId, "message user");
                AppendMessageHeader(builder, index, "user", entryId: entryId, label: label, timestamp: timestamp);
                RenderContentBlocks(builder, user.Content);
                builder.AppendLine("</article>");
                break;

            case AssistantMessage assistant:
                AppendArticleStart(builder, index, entryId, "message assistant");
                AppendMessageHeader(builder, index, "assistant", FormatAssistantMeta(assistant), entryId, label, timestamp);
                RenderContentBlocks(builder, assistant.Content);
                if (!string.IsNullOrWhiteSpace(assistant.ErrorMessage))
                {
                    builder.Append("<pre class=\"error-text\">")
                        .Append(Html(assistant.ErrorMessage))
                        .AppendLine("</pre>");
                }

                builder.AppendLine("</article>");
                break;

            case ToolResultMessage toolResult:
                AppendArticleStart(builder, index, entryId, toolResult.IsError ? "message tool-result error" : "message tool-result");
                AppendMessageHeader(
                    builder,
                    index,
                    "tool result",
                    string.IsNullOrWhiteSpace(toolResult.ToolCallId) ? null : $"call {toolResult.ToolCallId}",
                    entryId,
                    label,
                    timestamp);
                var toolContext = !string.IsNullOrWhiteSpace(toolResult.ToolCallId) &&
                    toolCalls.TryGetValue(toolResult.ToolCallId, out var context)
                        ? context
                        : null;
                RenderToolResultContent(builder, toolResult, toolContext);
                builder.AppendLine("</article>");
                break;

            default:
                AppendArticleStart(builder, index, entryId, "message unknown");
                AppendMessageHeader(builder, index, message.Role, entryId: entryId, label: label, timestamp: timestamp);
                builder.Append("<pre class=\"content-text\">")
                    .Append(Html(message.ToString() ?? message.Role))
                    .AppendLine("</pre>");
                builder.AppendLine("</article>");
                break;
        }
    }

    private static void RegisterToolCalls(ChatMessage message, IDictionary<string, ToolCallRenderContext> toolCalls)
    {
        if (message is not AssistantMessage assistant)
        {
            return;
        }

        foreach (var toolCall in assistant.Content.OfType<ToolCallContent>())
        {
            if (string.IsNullOrWhiteSpace(toolCall.Id))
            {
                continue;
            }

            toolCalls[toolCall.Id] = CreateToolCallRenderContext(toolCall);
        }
    }

    private static ToolCallRenderContext CreateToolCallRenderContext(ToolCallContent toolCall)
    {
        string? path = null;
        string? command = null;
        try
        {
            using var document = JsonDocument.Parse(toolCall.Arguments);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                path = GetStringProperty(document.RootElement, "path") ??
                    GetStringProperty(document.RootElement, "file_path") ??
                    GetStringProperty(document.RootElement, "working_directory");
                command = GetStringProperty(document.RootElement, "command");
            }
        }
        catch (JsonException)
        {
        }

        return new ToolCallRenderContext(
            toolCall.Id,
            toolCall.Name,
            toolCall.Arguments,
            path,
            GuessLanguageFromPath(path),
            command);
    }

    private static string? NormalizeKnownToolName(string toolName) =>
        toolName switch
        {
            "shell" or "bash" => "shell",
            "read_file" or "read" => "read_file",
            "write_file" or "write" => "write_file",
            "edit_file" or "edit" => "edit_file",
            "grep" => "grep",
            "glob" or "find" => "glob",
            "ls" => "ls",
            _ => null
        };

    private static void AppendArticleStart(StringBuilder builder, int index, string? entryId, string cssClass)
    {
        builder.Append("<article id=\"message-")
            .Append(index)
            .Append("\" data-message-index=\"")
            .Append(index)
            .Append("\"");
        if (!string.IsNullOrWhiteSpace(entryId))
        {
            builder.Append(" data-entry-id=\"")
                .Append(HtmlAttribute(entryId))
                .Append("\"");
        }

        builder.Append(" class=\"")
            .Append(HtmlAttribute(cssClass))
            .AppendLine("\">");
    }

    private static void RenderContentBlocks(
        StringBuilder builder,
        IReadOnlyList<ContentBlock> blocks,
        bool foldLongText = false)
    {
        if (blocks.Count == 0)
        {
            builder.AppendLine("<p class=\"empty-content\">No content.</p>");
            return;
        }

        foreach (var block in blocks)
        {
            switch (block)
            {
                case TextContent text when !string.IsNullOrWhiteSpace(text.Text):
                    if (foldLongText && text.Text.Length > FoldLongToolResultTextLength)
                    {
                        RenderFoldedToolResultText(builder, text.Text);
                    }
                    else
                    {
                        RenderTextContent(builder, text.Text);
                    }

                    break;

                case ThinkingContent thinking when !string.IsNullOrWhiteSpace(thinking.Thinking):
                    builder.AppendLine("<details class=\"thinking\" open>");
                    builder.AppendLine("<summary>Thinking</summary>");
                    builder.Append("<pre>")
                        .Append(Html(thinking.Thinking))
                        .AppendLine("</pre>");
                    builder.AppendLine("</details>");
                    break;

                case ToolCallContent toolCall:
                    builder.AppendLine("<details class=\"tool-call\" open>");
                    builder.Append("<summary>")
                        .Append(Html(toolCall.Name))
                        .Append(" <span>")
                        .Append(Html(toolCall.Id))
                        .AppendLine("</span></summary>");
                    AppendToolCallSummary(builder, toolCall);
                    AppendToolCallArguments(builder, toolCall.Arguments);
                    builder.AppendLine("</details>");
                    break;

                case ImageContent image:
                    AppendImageBlock(builder, image);
                    break;

                default:
                    builder.Append("<pre class=\"content-text muted\">")
                        .Append(Html($"[{block.Type}]"))
                        .AppendLine("</pre>");
                    break;
            }
        }
    }

    private static void RenderToolResultContent(
        StringBuilder builder,
        ToolResultMessage toolResult,
        ToolCallRenderContext? toolContext)
    {
        if (toolContext is not null &&
            toolResult.Content.Count == 1 &&
            toolResult.Content[0] is TextContent text &&
            !string.IsNullOrWhiteSpace(text.Text) &&
            TryRenderCustomToolResult(builder, text.Text, toolResult.IsError, toolContext))
        {
            return;
        }

        RenderContentBlocks(builder, toolResult.Content, foldLongText: true);
    }

    private static bool TryRenderCustomToolResult(
        StringBuilder builder,
        string text,
        bool isError,
        ToolCallRenderContext toolContext)
    {
        var knownTool = NormalizeKnownToolName(toolContext.Name);
        if (knownTool is null)
        {
            return false;
        }

        if (text.Length > FoldLongToolResultTextLength)
        {
            builder.AppendLine("<details class=\"tool-result-fold\">");
            builder.Append("<summary>")
                .Append(Html(toolContext.Name))
                .Append(" output, ")
                .Append(Html(text.Length.ToString("N0", CultureInfo.InvariantCulture)))
                .AppendLine(" characters</summary>");
            AppendCustomToolResultBody(builder, text, isError, toolContext, knownTool);
            builder.AppendLine("</details>");
            return true;
        }

        AppendCustomToolResultBody(builder, text, isError, toolContext, knownTool);
        return true;
    }

    private static void AppendCustomToolResultBody(
        StringBuilder builder,
        string text,
        bool isError,
        ToolCallRenderContext toolContext,
        string knownTool)
    {
        builder.Append("<div class=\"tool-result-render tool-result-")
            .Append(HtmlAttribute(toolContext.Name.Replace('_', '-')))
            .AppendLine("\">");

        switch (knownTool)
        {
            case "shell":
                AppendShellToolResult(builder, text, isError);
                break;

            case "read_file":
                AppendToolResultCode(builder, "file content", text, toolContext.Language);
                break;

            case "write_file":
            case "edit_file":
                AppendToolResultStatus(builder, text, isError ? "error" : "success");
                break;

            case "grep":
                AppendSearchToolResult(builder, text, isError);
                break;

            case "glob":
            case "ls":
                AppendListToolResult(builder, text, isError, knownTool == "ls");
                break;
        }

        builder.AppendLine("</div>");
    }

    private static void AppendShellToolResult(StringBuilder builder, string text, bool isError)
    {
        var parsed = ParseShellToolOutput(text);
        var statusClass = isError || parsed.ExitCode.GetValueOrDefault() != 0 ? "error" : "success";
        var status = parsed.ExitCode is null
            ? "shell completed"
            : $"exit code {parsed.ExitCode.Value.ToString(CultureInfo.InvariantCulture)}";
        AppendToolResultStatus(builder, status, statusClass);

        if (!string.IsNullOrWhiteSpace(parsed.Stdout))
        {
            AppendToolResultCode(builder, "stdout", parsed.Stdout, null);
        }

        if (!string.IsNullOrWhiteSpace(parsed.Stderr))
        {
            AppendToolResultCode(builder, "stderr", parsed.Stderr, null, "tool-result-stderr");
        }

        if (string.IsNullOrWhiteSpace(parsed.Stdout) && string.IsNullOrWhiteSpace(parsed.Stderr))
        {
            AppendToolResultStatus(builder, "no output", "muted");
        }
    }

    private static ShellToolOutput ParseShellToolOutput(string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n').ToList();
        int? exitCode = null;
        if (lines.Count > 0 && TryParseExitCodeLine(lines[^1], out var parsedExitCode))
        {
            exitCode = parsedExitCode;
            lines.RemoveAt(lines.Count - 1);
        }

        var body = string.Join('\n', lines).TrimEnd('\n');
        const string stderrMarker = "\n[stderr]\n";
        var stderrIndex = body.IndexOf(stderrMarker, StringComparison.Ordinal);
        if (stderrIndex >= 0)
        {
            return new ShellToolOutput(
                body[..stderrIndex].TrimEnd('\n'),
                body[(stderrIndex + stderrMarker.Length)..].TrimEnd('\n'),
                exitCode);
        }

        if (body.StartsWith("[stderr]\n", StringComparison.Ordinal))
        {
            return new ShellToolOutput(
                string.Empty,
                body["[stderr]\n".Length..].TrimEnd('\n'),
                exitCode);
        }

        return new ShellToolOutput(body, string.Empty, exitCode);
    }

    private static bool TryParseExitCodeLine(string line, out int exitCode)
    {
        exitCode = 0;
        var trimmed = line.Trim();
        if (!trimmed.StartsWith("[exit code: ", StringComparison.Ordinal) ||
            !trimmed.EndsWith(']'))
        {
            return false;
        }

        return int.TryParse(
            trimmed["[exit code: ".Length..^1],
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out exitCode);
    }

    private static void AppendSearchToolResult(StringBuilder builder, string text, bool isError)
    {
        if (isError || IsNoResultText(text))
        {
            AppendToolResultStatus(builder, text, isError ? "error" : "muted");
            return;
        }

        AppendToolResultCode(builder, "matches", text, null);
    }

    private static void AppendListToolResult(StringBuilder builder, string text, bool isError, bool markDirectories)
    {
        if (isError || IsNoResultText(text))
        {
            AppendToolResultStatus(builder, text, isError ? "error" : "muted");
            return;
        }

        var lines = ReadLines(text)
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .Take(500)
            .ToList();
        if (lines.Count == 0)
        {
            AppendToolResultStatus(builder, "no output", "muted");
            return;
        }

        builder.AppendLine("<ul class=\"tool-result-list\">");
        foreach (var line in lines)
        {
            var itemClass = markDirectories && line.TrimStart().StartsWith("[DIR]", StringComparison.Ordinal)
                ? " class=\"tool-result-directory\""
                : string.Empty;
            builder.Append("<li")
                .Append(itemClass)
                .Append(">")
                .Append(Html(line))
                .AppendLine("</li>");
        }

        builder.AppendLine("</ul>");
    }

    private static bool IsNoResultText(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Equals("No matches found.", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("No files matched the pattern.", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("Directory is empty.", StringComparison.OrdinalIgnoreCase);
    }

    private static void AppendToolResultStatus(StringBuilder builder, string text, string statusClass)
    {
        builder.Append("<div class=\"tool-result-status ")
            .Append(HtmlAttribute(statusClass))
            .Append("\">")
            .Append(Html(text))
            .AppendLine("</div>");
    }

    private static void AppendToolResultCode(
        StringBuilder builder,
        string label,
        string code,
        string? language,
        string? extraClass = null)
    {
        builder.Append("<div class=\"tool-result-section");
        if (!string.IsNullOrWhiteSpace(extraClass))
        {
            builder.Append(' ')
                .Append(HtmlAttribute(extraClass));
        }

        builder.AppendLine("\">");
        builder.Append("<div class=\"tool-result-label\">")
            .Append(Html(label))
            .AppendLine("</div>");
        builder.Append("<pre><code");
        if (!string.IsNullOrWhiteSpace(language))
        {
            builder.Append(" data-language=\"")
                .Append(HtmlAttribute(language))
                .Append("\"");
        }

        builder.Append(">")
            .AppendHighlightedCode(code, language)
            .AppendLine("</code></pre>");
        builder.AppendLine("</div>");
    }

    private static void RenderTextContent(StringBuilder builder, string text)
    {
        var segments = SplitCodeFenceSegments(text);
        foreach (var segment in segments)
        {
            if (segment.IsCode)
            {
                AppendCodeBlock(builder, segment.Text, segment.Language);
                continue;
            }

            if (string.IsNullOrWhiteSpace(segment.Text))
            {
                continue;
            }

            if (ContainsMarkdownBlockMarkup(segment.Text))
            {
                RenderMarkdownBlockContent(builder, segment.Text);
                continue;
            }

            builder.Append("<pre class=\"content-text\">")
                .AppendPlainTextMarkup(segment.Text)
                .AppendLine("</pre>");
        }
    }

    private static bool ContainsMarkdownBlockMarkup(string text)
    {
        var lines = ReadLines(text);
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            if (TryParseMarkdownTable(lines, index, out _, out _) ||
                TryParseHeadingLine(line, out _, out _) ||
                IsHorizontalRuleLine(line) ||
                TryParseUnorderedListItem(line, out _) ||
                TryParseOrderedListItem(line, out _) ||
                TryParseBlockQuoteLine(line, out _))
            {
                return true;
            }
        }

        return false;
    }

    private static void RenderMarkdownBlockContent(StringBuilder builder, string text)
    {
        var paragraph = new StringBuilder();
        var listStack = new List<(MarkdownListKind Kind, int Indent)>();
        var inQuote = false;

        builder.AppendLine("<div class=\"content-text rich-text\">");
        var lines = ReadLines(text);
        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph();
                CloseList();
                CloseQuote();
                continue;
            }

            if (TryParseMarkdownTable(lines, lineIndex, out var table, out var consumedLines))
            {
                FlushParagraph();
                CloseList();
                CloseQuote();
                AppendMarkdownTable(builder, table);
                lineIndex += consumedLines - 1;
                continue;
            }

            if (TryParseHeadingLine(line, out var headingLevel, out var headingText))
            {
                FlushParagraph();
                CloseList();
                CloseQuote();
                builder.Append("<h")
                    .Append(headingLevel.ToString(CultureInfo.InvariantCulture))
                    .Append(">");
                builder.AppendPlainTextMarkup(headingText);
                builder.Append("</h")
                    .Append(headingLevel.ToString(CultureInfo.InvariantCulture))
                    .AppendLine(">");
                continue;
            }

            if (IsHorizontalRuleLine(line))
            {
                FlushParagraph();
                CloseList();
                CloseQuote();
                builder.AppendLine("<hr>");
                continue;
            }

            if (TryParseUnorderedListItem(line, out var unorderedItem, out var unorderedIndent))
            {
                FlushParagraph();
                CloseQuote();
                AdjustListStack(MarkdownListKind.Unordered, unorderedIndent);
                AppendMarkdownListItem(builder, unorderedItem);
                continue;
            }

            if (TryParseOrderedListItem(line, out var orderedItem, out var orderedIndent))
            {
                FlushParagraph();
                CloseQuote();
                AdjustListStack(MarkdownListKind.Ordered, orderedIndent);
                AppendMarkdownListItem(builder, orderedItem);
                continue;
            }

            if (TryParseBlockQuoteLine(line, out var quoteText))
            {
                FlushParagraph();
                CloseList();
                EnsureQuote();
                builder.Append("<p>");
                builder.AppendPlainTextMarkup(quoteText);
                builder.AppendLine("</p>");
                continue;
            }

            CloseList();
            CloseQuote();
            if (paragraph.Length > 0)
            {
                paragraph.AppendLine();
            }

            paragraph.Append(line.Trim());
        }

        FlushParagraph();
        CloseList();
        CloseQuote();
        builder.AppendLine("</div>");

        void FlushParagraph()
        {
            if (paragraph.Length == 0)
            {
                return;
            }

            builder.Append("<p>");
            builder.AppendPlainTextMarkup(paragraph.ToString());
            builder.AppendLine("</p>");
            paragraph.Clear();
        }

        void AdjustListStack(MarkdownListKind kind, int indent)
        {
            // Pop deeper-or-equal indents that no longer fit the new line.
            while (listStack.Count > 0 && listStack[^1].Indent > indent)
            {
                var popped = listStack[^1];
                listStack.RemoveAt(listStack.Count - 1);
                builder.AppendLine(popped.Kind == MarkdownListKind.Ordered ? "</ol>" : "</ul>");
            }

            // If the deepest open list has the same indent but a different kind,
            // close it before opening the new one.
            if (listStack.Count > 0 && listStack[^1].Indent == indent && listStack[^1].Kind != kind)
            {
                var popped = listStack[^1];
                listStack.RemoveAt(listStack.Count - 1);
                builder.AppendLine(popped.Kind == MarkdownListKind.Ordered ? "</ol>" : "</ul>");
            }

            if (listStack.Count == 0 || listStack[^1].Indent < indent)
            {
                listStack.Add((kind, indent));
                builder.AppendLine(kind == MarkdownListKind.Ordered ? "<ol>" : "<ul>");
            }
        }

        void CloseList()
        {
            while (listStack.Count > 0)
            {
                var popped = listStack[^1];
                listStack.RemoveAt(listStack.Count - 1);
                builder.AppendLine(popped.Kind == MarkdownListKind.Ordered ? "</ol>" : "</ul>");
            }
        }

        void EnsureQuote()
        {
            if (inQuote)
            {
                return;
            }

            builder.AppendLine("<blockquote>");
            inQuote = true;
        }

        void CloseQuote()
        {
            if (!inQuote)
            {
                return;
            }

            builder.AppendLine("</blockquote>");
            inQuote = false;
        }
    }

    private static IReadOnlyList<string> ReadLines(string text)
    {
        var lines = new List<string>();
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return lines;
    }

    private static bool TryParseMarkdownTable(
        IReadOnlyList<string> lines,
        int start,
        out MarkdownTable table,
        out int consumedLines)
    {
        table = MarkdownTable.Empty;
        consumedLines = 0;

        if (start + 1 >= lines.Count ||
            !TrySplitMarkdownTableRow(lines[start], out var headers) ||
            !TrySplitMarkdownTableRow(lines[start + 1], out var separators) ||
            separators.Count != headers.Count ||
            !separators.All(IsMarkdownTableSeparatorCell))
        {
            return false;
        }

        var rows = new List<IReadOnlyList<string>>();
        consumedLines = 2;
        for (var index = start + 2; index < lines.Count; index++)
        {
            if (string.IsNullOrWhiteSpace(lines[index]) ||
                !TrySplitMarkdownTableRow(lines[index], out var cells) ||
                cells.Count != headers.Count)
            {
                break;
            }

            rows.Add(cells);
            consumedLines++;
        }

        table = new MarkdownTable(headers, rows);
        return true;
    }

    private static bool TrySplitMarkdownTableRow(string line, out IReadOnlyList<string> cells)
    {
        cells = [];
        var trimmed = line.Trim();
        if (!trimmed.Contains('|', StringComparison.Ordinal))
        {
            return false;
        }

        if (trimmed.StartsWith('|'))
        {
            trimmed = trimmed[1..];
        }

        if (trimmed.EndsWith('|'))
        {
            trimmed = trimmed[..^1];
        }

        var parts = trimmed
            .Split('|')
            .Select(static part => part.Trim())
            .ToList();
        if (parts.Count < 2 || parts.All(static part => part.Length == 0))
        {
            return false;
        }

        cells = parts;
        return true;
    }

    private static bool IsMarkdownTableSeparatorCell(string cell)
    {
        var trimmed = cell.Trim();
        if (trimmed.StartsWith(":", StringComparison.Ordinal))
        {
            trimmed = trimmed[1..];
        }

        if (trimmed.EndsWith(":", StringComparison.Ordinal))
        {
            trimmed = trimmed[..^1];
        }

        return trimmed.Length >= 3 && trimmed.All(static character => character == '-');
    }

    private static void AppendMarkdownTable(StringBuilder builder, MarkdownTable table)
    {
        builder.AppendLine("<div class=\"table-scroll\"><table>");
        builder.AppendLine("<thead><tr>");
        foreach (var header in table.Headers)
        {
            builder.Append("<th>");
            builder.AppendPlainTextMarkup(header);
            builder.AppendLine("</th>");
        }

        builder.AppendLine("</tr></thead>");
        builder.AppendLine("<tbody>");
        foreach (var row in table.Rows)
        {
            builder.AppendLine("<tr>");
            foreach (var cell in row)
            {
                builder.Append("<td>");
                builder.AppendPlainTextMarkup(cell);
                builder.AppendLine("</td>");
            }

            builder.AppendLine("</tr>");
        }

        builder.AppendLine("</tbody>");
        builder.AppendLine("</table></div>");
    }

    private static void AppendMarkdownListItem(StringBuilder builder, string itemText)
    {
        if (TryParseTaskListItem(itemText, out var isChecked, out var taskText))
        {
            builder.Append("<li class=\"task-list-item\"><input type=\"checkbox\" disabled");
            if (isChecked)
            {
                builder.Append(" checked");
            }

            builder.Append("> <span>");
            builder.AppendPlainTextMarkup(taskText);
            builder.AppendLine("</span></li>");
            return;
        }

        builder.Append("<li>");
        builder.AppendPlainTextMarkup(itemText);
        builder.AppendLine("</li>");
    }

    private static bool TryParseTaskListItem(
        string itemText,
        out bool isChecked,
        out string taskText)
    {
        isChecked = false;
        taskText = string.Empty;
        var trimmed = itemText.TrimStart();
        if (trimmed.Length < 3 ||
            trimmed[0] != '[' ||
            trimmed[2] != ']' ||
            trimmed[1] is not (' ' or 'x' or 'X'))
        {
            return false;
        }

        if (trimmed.Length > 3 && !char.IsWhiteSpace(trimmed[3]))
        {
            return false;
        }

        isChecked = trimmed[1] is 'x' or 'X';
        taskText = trimmed.Length > 3 ? trimmed[4..].Trim() : string.Empty;
        return true;
    }

    private static StringBuilder AppendPlainTextMarkup(this StringBuilder builder, string text)
    {
        var index = 0;
        var plainStart = 0;
        while (index < text.Length)
        {
            if (TryParseMarkdownLink(text, index, out var label, out var markdownUrl, out var markdownEnd))
            {
                builder.Append(Html(text[plainStart..index]));
                AppendAnchor(builder, markdownUrl, label);
                index = markdownEnd;
                plainStart = index;
                continue;
            }

            if (TryParseAutolink(text, index, out var autolinkUrl, out var autolinkEnd))
            {
                builder.Append(Html(text[plainStart..index]));
                AppendAnchor(builder, autolinkUrl, autolinkUrl);
                index = autolinkEnd;
                plainStart = index;
                continue;
            }

            if (TryParseInlineCodeSpan(text, index, out var code, out var codeEnd))
            {
                builder.Append(Html(text[plainStart..index]));
                AppendInlineCode(builder, code);
                index = codeEnd;
                plainStart = index;
                continue;
            }

            if (TryParseEmphasisSpan(text, index, out var emphasisText, out var isStrong, out var emphasisEnd))
            {
                builder.Append(Html(text[plainStart..index]));
                AppendEmphasis(builder, emphasisText, isStrong);
                index = emphasisEnd;
                plainStart = index;
                continue;
            }

            if (TryParseStrikethroughSpan(text, index, out var strikeText, out var strikeEnd))
            {
                builder.Append(Html(text[plainStart..index]));
                builder.Append("<del>");
                builder.AppendPlainTextMarkup(strikeText);
                builder.Append("</del>");
                index = strikeEnd;
                plainStart = index;
                continue;
            }

            if (TryParseBareUrl(text, index, out var bareUrl, out var bareEnd))
            {
                builder.Append(Html(text[plainStart..index]));
                AppendAnchor(builder, bareUrl, bareUrl);
                index = bareEnd;
                plainStart = index;
                continue;
            }

            index++;
        }

        if (plainStart < text.Length)
        {
            builder.Append(Html(text[plainStart..]));
        }

        return builder;
    }

    private static bool TryParseHeadingLine(string line, out int level, out string headingText)
    {
        level = 0;
        headingText = string.Empty;
        var trimmed = line.TrimStart();
        while (level < trimmed.Length && level < 6 && trimmed[level] == '#')
        {
            level++;
        }

        if (level == 0 ||
            level >= trimmed.Length ||
            trimmed[level] != ' ')
        {
            level = 0;
            return false;
        }

        headingText = trimmed[(level + 1)..].Trim();
        return headingText.Length > 0;
    }

    internal static bool IsHorizontalRuleLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var trimmed = line.Trim();
        if (trimmed.Length < 3)
        {
            return false;
        }

        var marker = trimmed[0];
        if (marker != '-' && marker != '*' && marker != '_')
        {
            return false;
        }

        var count = 0;
        foreach (var ch in trimmed)
        {
            if (ch == marker)
            {
                count++;
                continue;
            }

            if (ch == ' ' || ch == '\t')
            {
                continue;
            }

            return false;
        }

        return count >= 3;
    }

    private static bool TryParseUnorderedListItem(string line, out string itemText)
    {
        return TryParseUnorderedListItem(line, out itemText, out _);
    }

    private static bool TryParseUnorderedListItem(string line, out string itemText, out int indent)
    {
        itemText = string.Empty;
        indent = 0;
        var trimmed = line.TrimStart();
        if (trimmed.Length < 3 ||
            trimmed[0] is not ('-' or '*' or '+') ||
            !char.IsWhiteSpace(trimmed[1]))
        {
            return false;
        }

        indent = line.Length - trimmed.Length;
        itemText = trimmed[2..].Trim();
        return itemText.Length > 0;
    }

    private static bool TryParseOrderedListItem(string line, out string itemText)
    {
        return TryParseOrderedListItem(line, out itemText, out _);
    }

    private static bool TryParseOrderedListItem(string line, out string itemText, out int indent)
    {
        itemText = string.Empty;
        indent = 0;
        var trimmed = line.TrimStart();
        var index = 0;
        while (index < trimmed.Length && char.IsDigit(trimmed[index]))
        {
            index++;
        }

        if (index == 0 ||
            index >= trimmed.Length - 1 ||
            trimmed[index] is not ('.' or ')') ||
            !char.IsWhiteSpace(trimmed[index + 1]))
        {
            return false;
        }

        indent = line.Length - trimmed.Length;
        itemText = trimmed[(index + 2)..].Trim();
        return itemText.Length > 0;
    }

    private static bool TryParseBlockQuoteLine(string line, out string quoteText)
    {
        quoteText = string.Empty;
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '>')
        {
            return false;
        }

        quoteText = trimmed.Length > 1 && trimmed[1] == ' '
            ? trimmed[2..].Trim()
            : trimmed[1..].Trim();
        return true;
    }

    private static void AppendAnchor(StringBuilder builder, string href, string label)
    {
        builder.Append("<a href=\"")
            .Append(HtmlAttribute(href))
            .Append("\" target=\"_blank\" rel=\"noreferrer noopener\">")
            .Append(Html(label))
            .Append("</a>");
    }

    private static void AppendInlineCode(StringBuilder builder, string code)
    {
        builder.Append("<code class=\"inline-code\">")
            .Append(Html(code))
            .Append("</code>");
    }

    private static void AppendEmphasis(StringBuilder builder, string text, bool isStrong)
    {
        builder.Append(isStrong ? "<strong>" : "<em>");
        builder.AppendPlainTextMarkup(text);
        builder.Append(isStrong ? "</strong>" : "</em>");
    }

    private static bool TryParseMarkdownLink(
        string text,
        int start,
        out string label,
        out string url,
        out int end)
    {
        label = string.Empty;
        url = string.Empty;
        end = start;

        if (text[start] != '[')
        {
            return false;
        }

        var closeLabel = text.IndexOf(']', start + 1);
        if (closeLabel <= start + 1 ||
            closeLabel + 1 >= text.Length ||
            text[closeLabel + 1] != '(')
        {
            return false;
        }

        var urlStart = closeLabel + 2;
        var closeUrl = text.IndexOf(')', urlStart);
        if (closeUrl <= urlStart)
        {
            return false;
        }

        var candidate = text[urlStart..closeUrl];
        if (!IsAllowedHttpLink(candidate))
        {
            return false;
        }

        label = text[(start + 1)..closeLabel];
        url = candidate;
        end = closeUrl + 1;
        return true;
    }

    private static bool TryParseInlineCodeSpan(
        string text,
        int start,
        out string code,
        out int end)
    {
        code = string.Empty;
        end = start;

        if (text[start] != '`')
        {
            return false;
        }

        var tickCount = CountBackticks(text, start);
        var codeStart = start + tickCount;
        var close = FindBacktickRun(text, codeStart, tickCount);
        if (close <= codeStart)
        {
            return false;
        }

        code = text[codeStart..close];
        end = close + tickCount;
        return true;
    }

    private static int CountBackticks(string text, int start)
    {
        var count = 0;
        while (start + count < text.Length && text[start + count] == '`')
        {
            count++;
        }

        return count;
    }

    private static int FindBacktickRun(string text, int start, int tickCount)
    {
        for (var index = start; index < text.Length; index++)
        {
            if (text[index] != '`')
            {
                continue;
            }

            var current = CountBackticks(text, index);
            if (current == tickCount)
            {
                return index;
            }

            index += current - 1;
        }

        return -1;
    }

    private static bool TryParseStrikethroughSpan(
        string text,
        int start,
        out string strikeText,
        out int end)
    {
        strikeText = string.Empty;
        end = start;

        if (start + 2 > text.Length || text[start] != '~' || text[start + 1] != '~')
        {
            return false;
        }

        if (!HasEmphasisBoundaryBefore(text, start))
        {
            return false;
        }

        var contentStart = start + 2;
        if (contentStart >= text.Length || char.IsWhiteSpace(text[contentStart]))
        {
            return false;
        }

        var search = contentStart;
        while (search < text.Length - 1)
        {
            var current = text[search];
            if (current == '\n' || current == '\r')
            {
                return false;
            }

            if (current == '~' && text[search + 1] == '~')
            {
                if (search > contentStart && !char.IsWhiteSpace(text[search - 1]))
                {
                    var content = text[contentStart..search];
                    var after = search + 2;
                    if (after < text.Length && !HasEmphasisBoundaryAfter(text, after))
                    {
                        search++;
                        continue;
                    }

                    if (content.Length == 0)
                    {
                        search++;
                        continue;
                    }

                    strikeText = content;
                    end = after;
                    return true;
                }

                search += 2;
                continue;
            }

            search++;
        }

        return false;
    }

    private static bool TryParseEmphasisSpan(
        string text,
        int start,
        out string emphasisText,
        out bool isStrong,
        out int end)
    {
        emphasisText = string.Empty;
        isStrong = false;
        end = start;

        if (text[start] is not ('*' or '_'))
        {
            return false;
        }

        var marker = text[start];
        var markerRun = CountRepeated(text, start, marker);
        var markerLength = markerRun >= 2 ? 2 : 1;
        isStrong = markerLength == 2;
        var contentStart = start + markerLength;
        if (contentStart >= text.Length ||
            char.IsWhiteSpace(text[contentStart]) ||
            !HasEmphasisBoundaryBefore(text, start))
        {
            return false;
        }

        var close = FindEmphasisClose(text, contentStart, marker, markerLength);
        if (close <= contentStart ||
            char.IsWhiteSpace(text[close - 1]) ||
            !HasEmphasisBoundaryAfter(text, close + markerLength))
        {
            return false;
        }

        emphasisText = text[contentStart..close];
        end = close + markerLength;
        return true;
    }

    private static int CountRepeated(string text, int start, char marker)
    {
        var count = 0;
        while (start + count < text.Length && text[start + count] == marker)
        {
            count++;
        }

        return count;
    }

    private static int FindEmphasisClose(string text, int start, char marker, int markerLength)
    {
        for (var index = start; index < text.Length; index++)
        {
            if (text[index] != marker)
            {
                continue;
            }

            var current = CountRepeated(text, index, marker);
            if (current >= markerLength)
            {
                return index;
            }

            index += current - 1;
        }

        return -1;
    }

    private static bool HasEmphasisBoundaryBefore(string text, int start)
    {
        if (start == 0)
        {
            return true;
        }

        return !char.IsLetterOrDigit(text[start - 1]);
    }

    private static bool HasEmphasisBoundaryAfter(string text, int end)
    {
        if (end >= text.Length)
        {
            return true;
        }

        return !char.IsLetterOrDigit(text[end]);
    }

    private static bool TryParseAutolink(string text, int start, out string url, out int end)
    {
        url = string.Empty;
        end = start;

        if (start >= text.Length || text[start] != '<')
        {
            return false;
        }

        if (!StartsWithHttpScheme(text, start + 1))
        {
            return false;
        }

        var scan = start + 1;
        while (scan < text.Length)
        {
            var ch = text[scan];
            if (ch == '>')
            {
                if (scan == start + 1)
                {
                    return false;
                }

                url = text[(start + 1)..scan];
                end = scan + 1;
                return !string.IsNullOrWhiteSpace(url);
            }

            if (char.IsWhiteSpace(ch) || ch == '<')
            {
                return false;
            }

            scan++;
        }

        return false;
    }

    private static bool TryParseBareUrl(
        string text,
        int start,
        out string url,
        out int end)
    {
        url = string.Empty;
        end = start;

        if (!StartsWithHttpScheme(text, start) || !HasUrlBoundary(text, start))
        {
            return false;
        }

        var scan = start;
        while (scan < text.Length && !IsBareUrlTerminator(text[scan]))
        {
            scan++;
        }

        var trimmedEnd = scan;
        while (trimmedEnd > start && IsBareUrlTrailingPunctuation(text[trimmedEnd - 1]))
        {
            trimmedEnd--;
        }

        if (trimmedEnd <= start)
        {
            return false;
        }

        var candidate = text[start..trimmedEnd];
        if (!IsAllowedHttpLink(candidate))
        {
            return false;
        }

        url = candidate;
        end = trimmedEnd;
        return true;
    }

    private static bool StartsWithHttpScheme(string text, int start) =>
        text.AsSpan(start).StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
        text.AsSpan(start).StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    private static bool IsAllowedHttpLink(string url) =>
        StartsWithHttpScheme(url, 0) &&
        !url.Any(static character => char.IsWhiteSpace(character) || char.IsControl(character));

    private static bool HasUrlBoundary(string text, int start)
    {
        if (start == 0)
        {
            return true;
        }

        var previous = text[start - 1];
        return char.IsWhiteSpace(previous) || previous is '(' or '[' or '{' or '"' or '\'';
    }

    private static bool IsBareUrlTerminator(char character) =>
        char.IsWhiteSpace(character) ||
        char.IsControl(character) ||
        character is '<' or '"' or '\'';

    private static bool IsBareUrlTrailingPunctuation(char character) =>
        character is '.' or ',' or ';' or ':' or '!' or '?' or ')' or ']' or '}';

    private static void AppendImageBlock(StringBuilder builder, ImageContent image)
    {
        builder.Append("<figure class=\"image-block\"><img alt=\"session image\" src=\"data:")
            .Append(HtmlAttribute(image.MimeType))
            .Append(";base64,")
            .Append(HtmlAttribute(image.Data))
            .AppendLine("\">");
        builder.Append("<figcaption>")
            .Append(Html(FormatImageDescription(image)))
            .AppendLine("</figcaption>");
        builder.AppendLine("</figure>");
    }

    private static string FormatImageDescription(ImageContent image)
    {
        var size = TryGetBase64ByteLength(image.Data, out var byteCount)
            ? FormatByteCount(byteCount)
            : "unknown size";
        return $"{image.MimeType}, {size}";
    }

    private static bool TryGetBase64ByteLength(string data, out int byteCount)
    {
        byteCount = 0;
        if (string.IsNullOrWhiteSpace(data))
        {
            return true;
        }

        var normalized = new string(data.Where(static character => !char.IsWhiteSpace(character)).ToArray());
        if (normalized.Length == 0)
        {
            return true;
        }

        if (normalized.Length % 4 != 0)
        {
            return false;
        }

        var padding = normalized.EndsWith("==", StringComparison.Ordinal)
            ? 2
            : normalized.EndsWith('=') ? 1 : 0;
        byteCount = normalized.Length / 4 * 3 - padding;
        return byteCount >= 0;
    }

    private static string FormatByteCount(int byteCount)
    {
        if (byteCount == 1)
        {
            return "1 byte";
        }

        if (byteCount < 1024)
        {
            return $"{byteCount.ToString("N0", CultureInfo.InvariantCulture)} bytes";
        }

        var kib = byteCount / 1024d;
        if (kib < 1024)
        {
            return $"{kib.ToString("0.#", CultureInfo.InvariantCulture)} KiB";
        }

        var mib = kib / 1024d;
        return $"{mib.ToString("0.#", CultureInfo.InvariantCulture)} MiB";
    }

    private static void RenderFoldedToolResultText(StringBuilder builder, string text)
    {
        builder.AppendLine("<details class=\"tool-result-fold\">");
        builder.Append("<summary>Tool output, ")
            .Append(Html(text.Length.ToString("N0", CultureInfo.InvariantCulture)))
            .AppendLine(" characters</summary>");
        RenderTextContent(builder, text);
        builder.AppendLine("</details>");
    }

    private static void AppendToolCallSummary(StringBuilder builder, ToolCallContent toolCall)
    {
        try
        {
            using var document = JsonDocument.Parse(toolCall.Arguments);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var root = document.RootElement;
            builder.AppendLine("<div class=\"tool-call-summary\">");
            switch (NormalizeKnownToolName(toolCall.Name))
            {
                case "shell":
                    AppendToolSummaryCode(builder, "command", GetStringProperty(root, "command"), "shell");
                    AppendToolSummaryField(builder, "working directory", GetDisplayProperty(root, "working_directory"));
                    AppendToolSummaryField(
                        builder,
                        "timeout",
                        FormatMilliseconds(GetIntProperty(root, "timeout_ms")) ??
                        FormatSeconds(GetIntProperty(root, "timeout")));
                    break;

                case "read_file":
                    AppendToolSummaryField(builder, "path", GetDisplayPropertyAny(root, "path", "file_path"));
                    AppendToolSummaryField(builder, "offset", GetDisplayProperty(root, "offset"));
                    AppendToolSummaryField(builder, "limit", GetDisplayProperty(root, "limit"));
                    break;

                case "write_file":
                    var writePath = GetStringPropertyAny(root, "path", "file_path");
                    AppendToolSummaryField(builder, "path", writePath);
                    AppendContentSummary(builder, "content", writePath, GetStringProperty(root, "content"));
                    break;

                case "edit_file":
                    AppendEditToolSummary(builder, root);
                    break;

                case "grep":
                    AppendToolSummaryField(builder, "pattern", GetDisplayProperty(root, "pattern"));
                    AppendToolSummaryField(builder, "path", GetDisplayProperty(root, "path"));
                    AppendToolSummaryField(builder, "glob", GetDisplayProperty(root, "glob"));
                    AppendToolSummaryField(builder, "include content", GetDisplayProperty(root, "include_content"));
                    AppendToolSummaryField(builder, "ignore case", GetDisplayProperty(root, "ignoreCase"));
                    AppendToolSummaryField(builder, "literal", GetDisplayProperty(root, "literal"));
                    AppendToolSummaryField(builder, "context", GetDisplayProperty(root, "context"));
                    AppendToolSummaryField(builder, "limit", GetDisplayProperty(root, "limit"));
                    break;

                case "glob":
                    AppendToolSummaryField(builder, "pattern", GetDisplayPropertyAny(root, "pattern", "glob"));
                    AppendToolSummaryField(builder, "path", GetDisplayProperty(root, "path"));
                    AppendToolSummaryField(builder, "limit", GetDisplayProperty(root, "limit"));
                    break;

                case "ls":
                    AppendToolSummaryField(builder, "path", GetDisplayProperty(root, "path"));
                    break;

                default:
                    AppendGenericToolSummary(builder, root);
                    break;
            }

            builder.AppendLine("</div>");
        }
        catch (JsonException)
        {
        }
    }

    private static void AppendGenericToolSummary(StringBuilder builder, JsonElement root)
    {
        foreach (var property in root.EnumerateObject())
        {
            AppendToolSummaryField(builder, property.Name, GetJsonElementDisplay(property.Value));
        }
    }

    private static void AppendEditToolSummary(StringBuilder builder, JsonElement root)
    {
        var editPath = GetStringPropertyAny(root, "path", "file_path");
        var language = GuessLanguageFromPath(editPath);
        AppendToolSummaryField(builder, "path", editPath);

        var oldText = GetStringPropertyAny(root, "old_string", "oldText");
        var newText = GetStringPropertyAny(root, "new_string", "newText");
        if (oldText is not null || newText is not null)
        {
            AppendToolSummaryCode(builder, "old string", oldText, language);
            AppendToolSummaryCode(builder, "new string", newText, language);
            return;
        }

        if (!root.TryGetProperty("edits", out var edits) || edits.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var editIndex = 0;
        foreach (var edit in edits.EnumerateArray())
        {
            if (edit.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            editIndex++;
            AppendToolSummaryCode(builder, $"edit {editIndex} old", GetStringPropertyAny(edit, "oldText", "old_string"), language);
            AppendToolSummaryCode(builder, $"edit {editIndex} new", GetStringPropertyAny(edit, "newText", "new_string"), language);
        }
    }

    private static void AppendContentSummary(StringBuilder builder, string label, string? path, string? content)
    {
        if (content is null)
        {
            return;
        }

        var description = $"{content.Length.ToString("N0", CultureInfo.InvariantCulture)} characters";
        AppendToolSummaryField(builder, $"{label} size", description);
        AppendToolSummaryCode(builder, label, content, GuessLanguageFromPath(path));
    }

    private static void AppendToolSummaryField(StringBuilder builder, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        builder.AppendLine("<div class=\"tool-summary-field\">");
        builder.Append("<span class=\"tool-summary-key\">")
            .Append(Html(label))
            .AppendLine("</span>");
        builder.Append("<span class=\"tool-summary-value\">")
            .Append(Html(value))
            .AppendLine("</span>");
        builder.AppendLine("</div>");
    }

    private static void AppendToolSummaryCode(StringBuilder builder, string label, string? code, string? language)
    {
        if (string.IsNullOrEmpty(code))
        {
            return;
        }

        var truncated = TruncateToolSummaryValue(code);
        builder.AppendLine("<div class=\"tool-summary-code-block\">");
        builder.Append("<div class=\"tool-summary-key\">")
            .Append(Html(label))
            .AppendLine("</div>");
        builder.Append("<pre><code");
        if (!string.IsNullOrWhiteSpace(language))
        {
            builder.Append(" data-language=\"")
                .Append(HtmlAttribute(language))
                .Append("\"");
        }

        builder.Append(">")
            .AppendHighlightedCode(truncated, language)
            .AppendLine("</code></pre>");
        if (truncated.Length != code.Length)
        {
            builder.Append("<div class=\"tool-summary-more\">")
                .Append(Html($"{code.Length - truncated.Length:N0} more characters in raw arguments"))
                .AppendLine("</div>");
        }

        builder.AppendLine("</div>");
    }

    private static string TruncateToolSummaryValue(string value)
    {
        if (value.Length <= ToolSummaryPreviewLength)
        {
            return value;
        }

        return value[..ToolSummaryPreviewLength];
    }

    private static string? GetDisplayProperty(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var property)
            ? GetJsonElementDisplay(property)
            : null;
    }

    private static string? GetStringProperty(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static string? GetStringPropertyAny(JsonElement root, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var value = GetStringProperty(root, propertyName);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static int? GetIntProperty(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value)
            ? value
            : null;
    }

    private static string? GetDisplayPropertyAny(JsonElement root, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var value = GetDisplayProperty(root, propertyName);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string? GetJsonElementDisplay(JsonElement property)
    {
        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            JsonValueKind.Array => $"array, {property.GetArrayLength().ToString("N0", CultureInfo.InvariantCulture)} items",
            JsonValueKind.Object => "object",
            _ => null
        };
    }

    private static string? FormatMilliseconds(int? milliseconds)
    {
        return milliseconds is null
            ? null
            : $"{milliseconds.Value.ToString("N0", CultureInfo.InvariantCulture)} ms";
    }

    private static string? FormatSeconds(int? seconds)
    {
        return seconds is null
            ? null
            : $"{seconds.Value.ToString("N0", CultureInfo.InvariantCulture)} s";
    }

    private static string? GuessLanguageFromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".cs" => "csharp",
            ".js" or ".jsx" or ".mjs" or ".cjs" or ".ts" or ".tsx" => "javascript",
            ".py" => "python",
            ".csproj" or ".props" or ".targets" or ".xml" or ".xaml" or ".html" or ".htm" => "xml",
            ".json" or ".jsonl" => "json",
            ".ps1" or ".psm1" or ".psd1" => "powershell",
            ".sh" or ".bash" or ".zsh" => "shell",
            ".diff" or ".patch" => "diff",
            _ => null
        };
    }

    private static void AppendToolCallArguments(StringBuilder builder, string arguments)
    {
        if (arguments.Length <= FoldLongToolCallArgumentsLength)
        {
            AppendToolCallArgumentsContent(builder, arguments);
            return;
        }

        builder.AppendLine("<details class=\"tool-call-arguments-fold\">");
        builder.Append("<summary>Tool arguments, ")
            .Append(Html(arguments.Length.ToString("N0", CultureInfo.InvariantCulture)))
            .AppendLine(" characters</summary>");
        AppendToolCallArgumentsContent(builder, arguments);
        builder.AppendLine("</details>");
    }

    private static void AppendToolCallArgumentsContent(StringBuilder builder, string arguments)
    {
        if (TryFormatJson(arguments, out var formatted))
        {
            AppendCodeBlock(builder, formatted, "json");
            return;
        }

        builder.Append("<pre>")
            .Append(Html(arguments))
            .AppendLine("</pre>");
    }

    private static bool TryFormatJson(string value, out string formatted)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            formatted = string.Empty;
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            formatted = JsonSerializer.Serialize(document.RootElement, ToolArgumentsJsonOptions);
            return true;
        }
        catch (JsonException)
        {
            formatted = string.Empty;
            return false;
        }
    }

    private static IReadOnlyList<TextRenderSegment> SplitCodeFenceSegments(string text)
    {
        if (!text.Contains("```", StringComparison.Ordinal))
        {
            return [new TextRenderSegment(text, null, false)];
        }

        var segments = new List<TextRenderSegment>();
        using var reader = new StringReader(text);
        var plain = new StringBuilder();
        var code = new StringBuilder();
        string? codeLanguage = null;
        var inCodeFence = false;

        while (reader.ReadLine() is { } line)
        {
            if (TryParseCodeFence(line, out var language))
            {
                if (inCodeFence)
                {
                    AddSegment(segments, code, codeLanguage, isCode: true);
                    codeLanguage = null;
                }
                else
                {
                    AddSegment(segments, plain, null, isCode: false);
                    codeLanguage = language;
                }

                inCodeFence = !inCodeFence;
                continue;
            }

            if (inCodeFence)
            {
                code.AppendLine(line);
            }
            else
            {
                plain.AppendLine(line);
            }
        }

        AddSegment(segments, inCodeFence ? code : plain, codeLanguage, inCodeFence);
        return segments.Count == 0 ? [new TextRenderSegment(text, null, false)] : segments;
    }

    private static void AddSegment(
        ICollection<TextRenderSegment> segments,
        StringBuilder buffer,
        string? language,
        bool isCode)
    {
        var text = TrimSingleTrailingNewline(buffer.ToString());
        buffer.Clear();
        if (text.Length == 0 && !isCode)
        {
            return;
        }

        segments.Add(new TextRenderSegment(text, language, isCode));
    }

    private static bool TryParseCodeFence(string line, out string? language)
    {
        var trimmed = line.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            language = null;
            return false;
        }

        var fenceLength = 0;
        while (fenceLength < trimmed.Length && trimmed[fenceLength] == '`')
        {
            fenceLength++;
        }

        if (fenceLength < 3)
        {
            language = null;
            return false;
        }

        var info = trimmed[fenceLength..].Trim();
        language = string.IsNullOrWhiteSpace(info) ? null : info;
        return true;
    }

    private static string TrimSingleTrailingNewline(string value)
    {
        if (value.EndsWith("\r\n", StringComparison.Ordinal))
        {
            return value[..^2];
        }

        return value.EndsWith('\n') ? value[..^1] : value;
    }

    private static void AppendCodeBlock(StringBuilder builder, string code, string? language)
    {
        builder.AppendLine("<figure class=\"code-block\">");
        if (!string.IsNullOrWhiteSpace(language))
        {
            builder.Append("<figcaption>")
                .Append(Html(language.Trim()))
                .AppendLine("</figcaption>");
        }

        builder.Append("<pre><code");
        if (!string.IsNullOrWhiteSpace(language))
        {
            builder.Append(" data-language=\"")
                .Append(HtmlAttribute(language.Trim()))
                .Append("\"");
        }

        builder.Append(">")
            .AppendHighlightedCode(code, language)
            .AppendLine("</code></pre>");
        builder.AppendLine("</figure>");
    }

    private static StringBuilder AppendHighlightedCode(this StringBuilder builder, string code, string? language)
    {
        return NormalizeCodeLanguage(language) switch
        {
            "json" or "jsonc" => AppendJsonHighlight(builder, code),
            "csharp" => AppendCStyleHighlight(builder, code, CSharpKeywords, highlightDollarVariables: false),
            "javascript" => AppendCStyleHighlight(builder, code, JavaScriptKeywords, highlightDollarVariables: true),
            "python" => AppendShellHighlight(builder, code, PythonKeywords, highlightDollarVariables: false),
            "powershell" => AppendShellHighlight(builder, code, PowerShellKeywords, highlightDollarVariables: true),
            "shell" => AppendShellHighlight(builder, code, ShellKeywords, highlightDollarVariables: true),
            "xml" => AppendXmlHighlight(builder, code),
            "diff" => AppendDiffHighlight(builder, code),
            _ => builder.Append(Html(code))
        };
    }

    private static string NormalizeCodeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return string.Empty;
        }

        var trimmed = language.Trim();
        var separator = trimmed.IndexOfAny([' ', '\t', ':', ',']);
        var token = separator < 0 ? trimmed : trimmed[..separator];
        return token.TrimStart('.').ToLowerInvariant() switch
        {
            "cs" or "c#" or "csharp" => "csharp",
            "js" or "jsx" or "mjs" or "cjs" or "javascript" => "javascript",
            "ts" or "tsx" or "typescript" => "javascript",
            "py" or "python" or "python3" => "python",
            "ps1" or "psm1" or "psd1" or "pwsh" or "powershell" => "powershell",
            "bash" or "sh" or "shell" or "zsh" => "shell",
            "html" or "htm" or "xml" or "xaml" or "csproj" or "props" or "targets" => "xml",
            "patch" or "diff" => "diff",
            "json" or "jsonc" => "json",
            _ => token.ToLowerInvariant()
        };
    }

    private static StringBuilder AppendJsonHighlight(StringBuilder builder, string code)
    {
        var index = 0;
        while (index < code.Length)
        {
            var current = code[index];
            if (current == '"')
            {
                var end = FindStringEnd(code, index, '"', '\\');
                var cssClass = IsJsonPropertyName(code, end) ? "syntax-property" : "syntax-string";
                AppendSyntaxSpan(builder, cssClass, code[index..end]);
                index = end;
                continue;
            }

            if (current == '-' || char.IsDigit(current))
            {
                var end = ReadJsonNumber(code, index);
                if (end > index)
                {
                    AppendSyntaxSpan(builder, "syntax-number", code[index..end]);
                    index = end;
                    continue;
                }
            }

            if (IsIdentifierStart(current))
            {
                var end = ReadIdentifier(code, index);
                var token = code[index..end];
                if (token is "true" or "false" or "null")
                {
                    AppendSyntaxSpan(builder, "syntax-keyword", token);
                }
                else
                {
                    builder.Append(Html(token));
                }

                index = end;
                continue;
            }

            if (IsJsonOperator(current))
            {
                AppendSyntaxSpan(builder, "syntax-operator", current.ToString());
                index++;
                continue;
            }

            builder.Append(Html(current.ToString()));
            index++;
        }

        return builder;
    }

    private static StringBuilder AppendCStyleHighlight(
        StringBuilder builder,
        string code,
        ISet<string> keywords,
        bool highlightDollarVariables)
    {
        var index = 0;
        while (index < code.Length)
        {
            if (code[index] == '/' && index + 1 < code.Length && code[index + 1] == '/')
            {
                var end = ReadToLineEnd(code, index);
                AppendSyntaxSpan(builder, "syntax-comment", code[index..end]);
                index = end;
                continue;
            }

            if (code[index] == '/' && index + 1 < code.Length && code[index + 1] == '*')
            {
                var end = code.IndexOf("*/", index + 2, StringComparison.Ordinal);
                end = end < 0 ? code.Length : end + 2;
                AppendSyntaxSpan(builder, "syntax-comment", code[index..end]);
                index = end;
                continue;
            }

            if (TryReadCSharpStringPrefix(code, index, out var prefixedStringEnd))
            {
                AppendSyntaxSpan(builder, "syntax-string", code[index..prefixedStringEnd]);
                index = prefixedStringEnd;
                continue;
            }

            if (code[index] is '"' or '\'')
            {
                var quote = code[index];
                var end = FindStringEnd(code, index, quote, '\\');
                AppendSyntaxSpan(builder, "syntax-string", code[index..end]);
                index = end;
                continue;
            }

            if (highlightDollarVariables && code[index] == '$')
            {
                var end = ReadShellVariable(code, index);
                if (end > index + 1)
                {
                    AppendSyntaxSpan(builder, "syntax-property", code[index..end]);
                    index = end;
                    continue;
                }
            }

            if (char.IsDigit(code[index]))
            {
                var end = ReadNumber(code, index);
                AppendSyntaxSpan(builder, "syntax-number", code[index..end]);
                index = end;
                continue;
            }

            if (IsIdentifierStart(code[index]) || code[index] == '@')
            {
                var start = code[index] == '@' && index + 1 < code.Length && IsIdentifierStart(code[index + 1])
                    ? index + 1
                    : index;
                var end = ReadIdentifier(code, start);
                var token = code[start..end];
                if (keywords.Contains(token))
                {
                    if (start > index)
                    {
                        builder.Append(Html(code[index..start]));
                    }

                    AppendSyntaxSpan(builder, "syntax-keyword", token);
                }
                else
                {
                    builder.Append(Html(code[index..end]));
                }

                index = end;
                continue;
            }

            if (IsCodeOperator(code[index]))
            {
                AppendSyntaxSpan(builder, "syntax-operator", code[index].ToString());
                index++;
                continue;
            }

            builder.Append(Html(code[index].ToString()));
            index++;
        }

        return builder;
    }

    private static StringBuilder AppendShellHighlight(
        StringBuilder builder,
        string code,
        ISet<string> keywords,
        bool highlightDollarVariables)
    {
        var index = 0;
        while (index < code.Length)
        {
            if (code[index] == '#')
            {
                var end = ReadToLineEnd(code, index);
                AppendSyntaxSpan(builder, "syntax-comment", code[index..end]);
                index = end;
                continue;
            }

            if (code[index] is '"' or '\'')
            {
                var quote = code[index];
                var escape = quote == '"' ? '\\' : '\0';
                var end = FindStringEnd(code, index, quote, escape);
                AppendSyntaxSpan(builder, "syntax-string", code[index..end]);
                index = end;
                continue;
            }

            if (highlightDollarVariables && code[index] == '$')
            {
                var end = ReadShellVariable(code, index);
                if (end > index + 1)
                {
                    AppendSyntaxSpan(builder, "syntax-property", code[index..end]);
                    index = end;
                    continue;
                }
            }

            if (char.IsDigit(code[index]))
            {
                var end = ReadNumber(code, index);
                AppendSyntaxSpan(builder, "syntax-number", code[index..end]);
                index = end;
                continue;
            }

            if (IsIdentifierStart(code[index]))
            {
                var end = ReadIdentifier(code, index);
                var token = code[index..end];
                if (keywords.Contains(token))
                {
                    AppendSyntaxSpan(builder, "syntax-keyword", token);
                }
                else
                {
                    builder.Append(Html(token));
                }

                index = end;
                continue;
            }

            if (IsCodeOperator(code[index]))
            {
                AppendSyntaxSpan(builder, "syntax-operator", code[index].ToString());
                index++;
                continue;
            }

            builder.Append(Html(code[index].ToString()));
            index++;
        }

        return builder;
    }

    private static StringBuilder AppendXmlHighlight(StringBuilder builder, string code)
    {
        var index = 0;
        while (index < code.Length)
        {
            if (code.IndexOf("<!--", index, StringComparison.Ordinal) == index)
            {
                var end = code.IndexOf("-->", index + 4, StringComparison.Ordinal);
                end = end < 0 ? code.Length : end + 3;
                AppendSyntaxSpan(builder, "syntax-comment", code[index..end]);
                index = end;
                continue;
            }

            if (code[index] != '<')
            {
                builder.Append(Html(code[index].ToString()));
                index++;
                continue;
            }

            var tagEnd = code.IndexOf('>', index + 1);
            if (tagEnd < 0)
            {
                builder.Append(Html(code[index..]));
                break;
            }

            AppendXmlTagHighlight(builder, code[index..(tagEnd + 1)]);
            index = tagEnd + 1;
        }

        return builder;
    }

    private static void AppendXmlTagHighlight(StringBuilder builder, string tag)
    {
        var index = 0;
        while (index < tag.Length)
        {
            var current = tag[index];
            if (current is '<' or '>' or '/' or '?' or '!' or '=')
            {
                AppendSyntaxSpan(builder, "syntax-operator", current.ToString());
                index++;
                continue;
            }

            if (current is '"' or '\'')
            {
                var end = FindStringEnd(tag, index, current, '\0');
                AppendSyntaxSpan(builder, "syntax-string", tag[index..end]);
                index = end;
                continue;
            }

            if (IsIdentifierStart(current) || current is ':' or '-')
            {
                var end = index + 1;
                while (end < tag.Length && (IsIdentifierPart(tag[end]) || tag[end] is ':' or '-' or '.'))
                {
                    end++;
                }

                var token = tag[index..end];
                var cssClass = IsXmlTagName(tag, index) ? "syntax-keyword" : "syntax-property";
                AppendSyntaxSpan(builder, cssClass, token);
                index = end;
                continue;
            }

            builder.Append(Html(current.ToString()));
            index++;
        }
    }

    private static StringBuilder AppendDiffHighlight(StringBuilder builder, string code)
    {
        var index = 0;
        while (index < code.Length)
        {
            var end = code.IndexOf('\n', index);
            end = end < 0 ? code.Length : end + 1;
            var line = code[index..end];
            var lineText = line.TrimEnd('\r', '\n');
            var cssClass = lineText.StartsWith("@@", StringComparison.Ordinal) ||
                lineText.StartsWith("diff ", StringComparison.Ordinal) ||
                lineText.StartsWith("index ", StringComparison.Ordinal) ||
                lineText.StartsWith("+++", StringComparison.Ordinal) ||
                lineText.StartsWith("---", StringComparison.Ordinal)
                    ? "syntax-hunk"
                    : lineText.StartsWith('+') ? "syntax-added"
                    : lineText.StartsWith('-') ? "syntax-removed"
                    : null;

            if (cssClass is null)
            {
                builder.Append(Html(line));
            }
            else
            {
                AppendSyntaxSpan(builder, cssClass, line);
            }

            index = end;
        }

        return builder;
    }

    private static bool TryReadCSharpStringPrefix(string code, int index, out int end)
    {
        end = index;
        if (index >= code.Length)
        {
            return false;
        }

        var prefixEnd = index;
        if (code[index] == '$')
        {
            prefixEnd++;
        }

        if (prefixEnd < code.Length && code[prefixEnd] == '@')
        {
            prefixEnd++;
        }

        if (prefixEnd == index && code[index] == '@')
        {
            prefixEnd++;
        }

        if (prefixEnd <= index || prefixEnd >= code.Length || code[prefixEnd] != '"')
        {
            return false;
        }

        end = code[prefixEnd - 1] == '@'
            ? FindVerbatimStringEnd(code, prefixEnd)
            : FindStringEnd(code, prefixEnd, '"', '\\');
        return true;
    }

    private static int FindStringEnd(string code, int start, char quote, char escape)
    {
        var index = start + 1;
        while (index < code.Length)
        {
            if (escape != '\0' && code[index] == escape)
            {
                index = Math.Min(index + 2, code.Length);
                continue;
            }

            if (code[index] == quote)
            {
                return index + 1;
            }

            index++;
        }

        return code.Length;
    }

    private static int FindVerbatimStringEnd(string code, int start)
    {
        var index = start + 1;
        while (index < code.Length)
        {
            if (code[index] == '"')
            {
                if (index + 1 < code.Length && code[index + 1] == '"')
                {
                    index += 2;
                    continue;
                }

                return index + 1;
            }

            index++;
        }

        return code.Length;
    }

    private static bool IsJsonPropertyName(string code, int stringEnd)
    {
        var index = stringEnd;
        while (index < code.Length && char.IsWhiteSpace(code[index]))
        {
            index++;
        }

        return index < code.Length && code[index] == ':';
    }

    private static int ReadJsonNumber(string code, int start)
    {
        var index = start;
        if (index < code.Length && code[index] == '-')
        {
            index++;
        }

        var hasDigit = false;
        while (index < code.Length && char.IsDigit(code[index]))
        {
            hasDigit = true;
            index++;
        }

        if (!hasDigit)
        {
            return start;
        }

        if (index < code.Length && code[index] == '.')
        {
            index++;
            while (index < code.Length && char.IsDigit(code[index]))
            {
                index++;
            }
        }

        if (index < code.Length && code[index] is 'e' or 'E')
        {
            index++;
            if (index < code.Length && code[index] is '+' or '-')
            {
                index++;
            }

            while (index < code.Length && char.IsDigit(code[index]))
            {
                index++;
            }
        }

        return index;
    }

    private static int ReadNumber(string code, int start)
    {
        var index = start;
        while (index < code.Length && (char.IsLetterOrDigit(code[index]) || code[index] is '_' or '.'))
        {
            index++;
        }

        return index;
    }

    private static int ReadIdentifier(string code, int start)
    {
        var index = start;
        while (index < code.Length && IsIdentifierPart(code[index]))
        {
            index++;
        }

        return index;
    }

    private static int ReadShellVariable(string code, int start)
    {
        if (start + 1 >= code.Length)
        {
            return start + 1;
        }

        if (code[start + 1] == '{')
        {
            var end = code.IndexOf('}', start + 2);
            return end < 0 ? start + 1 : end + 1;
        }

        if (code[start + 1] is '?' or '!' or '$' or '#')
        {
            return start + 2;
        }

        var index = start + 1;
        while (index < code.Length && (IsIdentifierPart(code[index]) || code[index] == ':'))
        {
            index++;
        }

        return index;
    }

    private static int ReadToLineEnd(string code, int start)
    {
        var end = code.IndexOf('\n', start);
        return end < 0 ? code.Length : end;
    }

    private static bool IsIdentifierStart(char value) => char.IsLetter(value) || value == '_';

    private static bool IsIdentifierPart(char value) => char.IsLetterOrDigit(value) || value == '_';

    private static bool IsJsonOperator(char value) => value is '{' or '}' or '[' or ']' or ':' or ',';

    private static bool IsCodeOperator(char value) =>
        value is '{' or '}' or '[' or ']' or '(' or ')' or ';' or ',' or '.' or '<' or '>' or '+' or '-' or '*' or '/' or '=' or '!' or '?' or ':' or '&' or '|' or '%' or '^' or '~';

    private static bool IsXmlTagName(string tag, int index)
    {
        var previous = index - 1;
        while (previous >= 0 && char.IsWhiteSpace(tag[previous]))
        {
            previous--;
        }

        return previous >= 0 && tag[previous] is '<' or '/' or '?' or '!';
    }

    private static void AppendSyntaxSpan(StringBuilder builder, string cssClass, string text)
    {
        builder.Append("<span class=\"")
            .Append(cssClass)
            .Append("\">")
            .Append(Html(text))
            .Append("</span>");
    }

    private static void RenderSessionEntry(StringBuilder builder, SessionTimelineItem item)
    {
        var cssClass = item.Type switch
        {
            "model_change" => "timeline-event model-change-event",
            "session_info" => "timeline-event session-info-event",
            "compaction" => "timeline-event compaction-event",
            "branch_summary" => "timeline-event branch-summary-event",
            "auto_retry_start" or "auto_retry_end" => "timeline-event retry-event",
            "label" => "timeline-event label-event",
            _ => "timeline-event"
        };

        AppendSessionEntryStart(builder, item, cssClass);
        switch (item.Type)
        {
            case "model_change":
                AppendEventHeader(builder, "model change", item.Timestamp);
                builder.Append("<p class=\"event-summary\">Switched to <strong>")
                    .Append(Html(FormatModel(item.Provider, item.Model)))
                    .AppendLine("</strong>.</p>");
                break;

            case "session_info":
                AppendEventHeader(builder, FormatSessionInfoTitle(item), item.Timestamp);
                builder.Append("<p class=\"event-summary\">")
                    .Append(Html(FormatSessionInfoSummary(item)))
                    .AppendLine("</p>");
                break;

            case "compaction":
                AppendEventHeader(builder, item.FromHook == true ? "auto compaction" : "compaction", item.Timestamp);
                builder.Append("<details class=\"compaction-detail\" open><summary>")
                    .Append(Html(FormatCompactionSummary(item)))
                    .AppendLine("</summary>");
                if (!string.IsNullOrWhiteSpace(item.Summary))
                {
                    builder.Append("<pre>")
                        .Append(Html(item.Summary))
                        .AppendLine("</pre>");
                }

                if (!string.IsNullOrWhiteSpace(item.TurnPrefixSummary))
                {
                    builder.AppendLine("<h4>Turn Context (split turn)</h4>");
                    builder.Append("<pre>")
                        .Append(Html(item.TurnPrefixSummary))
                        .AppendLine("</pre>");
                }

                if (!string.IsNullOrWhiteSpace(item.FirstKeptEntryId))
                {
                    builder.Append("<p class=\"event-meta-line\">first kept entry ")
                        .Append(Html(ShortId(item.FirstKeptEntryId)))
                        .AppendLine("</p>");
                }

                builder.AppendLine("</details>");
                break;

            case "branch_summary":
                AppendEventHeader(builder, item.FromHook == true ? "extension branch summary" : "branch summary", item.Timestamp);
                builder.Append("<details class=\"branch-summary-detail\" open><summary>")
                    .Append(Html(FormatBranchSummary(item)))
                    .AppendLine("</summary>");
                if (!string.IsNullOrWhiteSpace(item.Summary))
                {
                    builder.Append("<pre>")
                        .Append(Html(item.Summary))
                        .AppendLine("</pre>");
                }

                AppendBranchSummaryFiles(builder, "Read files", item.ReadFiles);
                AppendBranchSummaryFiles(builder, "Modified files", item.ModifiedFiles);
                builder.AppendLine("</details>");
                break;

            case "auto_retry_start":
            case "auto_retry_end":
                AppendEventHeader(builder, FormatRetryTitle(item), item.Timestamp);
                builder.Append("<p class=\"event-summary\">")
                    .Append(Html(FormatRetrySummary(item)))
                    .AppendLine("</p>");
                break;

            case "label":
                AppendEventHeader(builder, "label change", item.Timestamp);
                builder.Append("<p class=\"event-summary\">")
                    .Append(Html(FormatLabelSummary(item)))
                    .AppendLine("</p>");
                break;

            default:
                AppendEventHeader(builder, item.Type, item.Timestamp);
                builder.Append("<p class=\"event-summary\">")
                    .Append(Html(item.Type))
                    .AppendLine("</p>");
                break;
        }

        builder.AppendLine("</article>");
    }

    private static void AppendMessageHeader(
        StringBuilder builder,
        int index,
        string role,
        string? meta = null,
        string? entryId = null,
        string? label = null,
        DateTimeOffset? timestamp = null)
    {
        builder.AppendLine("<div class=\"message-header\">");
        builder.Append("<span class=\"message-index\">#")
            .Append(index)
            .Append("</span>");
        builder.Append("<span class=\"message-role\">")
            .Append(Html(role))
            .Append("</span>");
        if (!string.IsNullOrWhiteSpace(meta))
        {
            builder.Append("<span class=\"message-meta\">")
                .Append(Html(meta))
                .Append("</span>");
        }

        if (!string.IsNullOrWhiteSpace(label))
        {
            builder.Append("<span class=\"message-label\">")
                .Append(Html(label))
                .Append("</span>");
        }

        if (timestamp is not null)
        {
            builder.Append("<span class=\"message-meta\">")
                .Append(Html(FormatTimestamp(timestamp.Value)))
                .Append("</span>");
        }

        if (!string.IsNullOrWhiteSpace(entryId))
        {
            builder.Append("<button type=\"button\" class=\"copy-link-button\" data-entry-id=\"")
                .Append(HtmlAttribute(entryId))
                .Append("\" title=\"Copy link to this message\" aria-label=\"Copy link to message ")
                .Append(index)
                .AppendLine("\">link</button>");
        }

        builder.AppendLine("</div>");
    }

    private static void AppendSessionEntryStart(StringBuilder builder, SessionTimelineItem item, string cssClass)
    {
        builder.Append("<article");
        if (!string.IsNullOrWhiteSpace(item.Id))
        {
            builder.Append(" id=\"entry-")
                .Append(HtmlAttribute(item.Id))
                .Append("\" data-entry-id=\"")
                .Append(HtmlAttribute(item.Id))
                .Append("\"");
        }

        builder.Append(" class=\"")
            .Append(HtmlAttribute(cssClass))
            .AppendLine("\">");
    }

    private static void AppendEventHeader(StringBuilder builder, string type, DateTimeOffset? timestamp)
    {
        builder.AppendLine("<div class=\"event-header\">");
        builder.Append("<span class=\"event-type\">")
            .Append(Html(type))
            .Append("</span>");
        if (timestamp is not null)
        {
            builder.Append("<span class=\"event-time\">")
                .Append(Html(FormatTimestamp(timestamp.Value)))
                .Append("</span>");
        }

        builder.AppendLine("</div>");
    }

    private static string? FormatAssistantMeta(AssistantMessage message)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(message.Provider) || !string.IsNullOrWhiteSpace(message.Model))
        {
            parts.Add(FormatModel(message.Provider, message.Model));
        }

        if (message.StopReason is not null)
        {
            parts.Add($"stop {message.StopReason}");
        }

        if (message.Usage is { } usage)
        {
            parts.Add($"tokens {usage.InputTokens}/{usage.OutputTokens}");
        }

        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    private static void AppendMeta(StringBuilder builder, string key, string value)
    {
        builder.Append("<div><dt>")
            .Append(Html(key))
            .Append("</dt><dd>")
            .Append(Html(value))
            .AppendLine("</dd></div>");
    }

    private static string FormatModel(string? provider, string? model)
    {
        if (!string.IsNullOrWhiteSpace(provider) && !string.IsNullOrWhiteSpace(model))
        {
            return $"{provider}/{model}";
        }

        if (!string.IsNullOrWhiteSpace(model))
        {
            return model;
        }

        if (!string.IsNullOrWhiteSpace(provider))
        {
            return provider;
        }

        return "unknown";
    }

    private static string ShortId(string? id) =>
        string.IsNullOrWhiteSpace(id) ? "root" : id.Length <= 8 ? id : id[..8];

    private static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");

    private static string FormatSessionInfoTitle(SessionTimelineItem item) =>
        string.IsNullOrWhiteSpace(item.Action)
            ? "session info"
            : $"session {item.Action}";

    private static string FormatSessionInfoSummary(SessionTimelineItem item)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(item.Action))
        {
            parts.Add($"action {item.Action}");
        }

        if (!string.IsNullOrWhiteSpace(item.Name))
        {
            parts.Add($"name {item.Name}");
        }

        if (!string.IsNullOrWhiteSpace(item.Provider) || !string.IsNullOrWhiteSpace(item.Model))
        {
            parts.Add($"model {FormatModel(item.Provider, item.Model)}");
        }

        return parts.Count == 0 ? "session metadata updated" : string.Join(", ", parts);
    }

    private static string FormatCompactionSummary(SessionTimelineItem item)
    {
        var tokens = item.TokensBefore.GetValueOrDefault();
        var reason = item.FromHook == true ? " by auto-compaction hook" : string.Empty;
        var split = item.IsSplitTurn == true ? " with split-turn prefix" : string.Empty;
        return tokens > 0
            ? $"Compacted from {tokens:N0} estimated tokens{reason}{split}"
            : $"Compacted session{reason}{split}";
    }

    private static string FormatBranchSummary(SessionTimelineItem item) =>
        $"Summary for branch switch from {ShortId(item.FromId ?? item.ParentId)}";

    private static void AppendBranchSummaryFiles(
        StringBuilder builder,
        string title,
        IReadOnlyList<string>? files)
    {
        if (files is null || files.Count == 0)
        {
            return;
        }

        builder.Append("<h4>")
            .Append(Html(title))
            .AppendLine("</h4>");
        builder.AppendLine("<ul class=\"event-file-list\">");
        foreach (var file in files)
        {
            builder.Append("<li>")
                .Append(Html(file))
                .AppendLine("</li>");
        }

        builder.AppendLine("</ul>");
    }

    private static string FormatRetryTitle(SessionTimelineItem item) =>
        string.Equals(item.Type, "auto_retry_end", StringComparison.OrdinalIgnoreCase)
            ? "auto retry end"
            : "auto retry start";

    private static string FormatRetrySummary(SessionTimelineItem item)
    {
        if (string.Equals(item.Type, "auto_retry_end", StringComparison.OrdinalIgnoreCase))
        {
            var result = item.Success == true ? "succeeded" : "failed";
            var suffix = string.IsNullOrWhiteSpace(item.FinalError) ? string.Empty : $": {item.FinalError}";
            return $"Retry {result} after attempt {item.Attempt.GetValueOrDefault()}{suffix}";
        }

        var delay = item.DelayMs.GetValueOrDefault();
        var error = string.IsNullOrWhiteSpace(item.ErrorMessage) ? "Unknown error" : item.ErrorMessage;
        return $"Retry attempt {item.Attempt.GetValueOrDefault()}/{item.MaxAttempts.GetValueOrDefault()} after {delay}ms: {error}";
    }

    private static string FormatLabelSummary(SessionTimelineItem item)
    {
        var target = ShortId(item.TargetId);
        return string.IsNullOrWhiteSpace(item.Label)
            ? $"cleared label on {target}"
            : $"set label on {target}: {item.Label}";
    }

    private static string Html(string value) => WebUtility.HtmlEncode(value);

    private static string HtmlAttribute(string value) => WebUtility.HtmlEncode(value).Replace("\"", "&quot;", StringComparison.Ordinal);

    private static string EnsureTrailingNewline(string value) =>
        value.EndsWith('\n') ? value : value + "\n";

    private static string BuildStandaloneJsonl(
        IReadOnlyList<ChatMessage> messages,
        string? provider,
        string? model,
        string? sessionName)
    {
        var builder = new StringBuilder();
        var header = new CodingAgentTreeSessionHeader
        {
            Type = "session",
            Version = CodingAgentTreeSessionStore.CurrentVersion,
            Id = CreateSessionId(),
            Timestamp = DateTimeOffset.UtcNow,
            Cwd = Environment.CurrentDirectory
        };
        builder
            .Append(JsonSerializer.Serialize(header, CodingAgentTreeSessionJsonContext.Default.CodingAgentTreeSessionHeader))
            .Append('\n');

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? parentId = null;
        var sessionInfo = new CodingAgentTreeSessionEntry
        {
            Type = "session_info",
            Id = CreateEntryId(ids),
            ParentId = null,
            Timestamp = DateTimeOffset.UtcNow,
            Name = string.IsNullOrWhiteSpace(sessionName) ? null : sessionName.Trim(),
            Provider = provider,
            Model = model
        };
        builder
            .Append(JsonSerializer.Serialize(sessionInfo, CodingAgentTreeSessionJsonContext.Default.CodingAgentTreeSessionEntry))
            .Append('\n');
        ids.Add(sessionInfo.Id);
        parentId = sessionInfo.Id;

        foreach (var message in messages)
        {
            var id = CreateEntryId(ids);
            var entry = new CodingAgentTreeSessionEntry
            {
                Type = "message",
                Id = id,
                ParentId = parentId,
                Timestamp = DateTimeOffset.UtcNow,
                Message = CodingAgentSessionStore.FromMessage(message)
            };
            builder
                .Append(JsonSerializer.Serialize(entry, CodingAgentTreeSessionJsonContext.Default.CodingAgentTreeSessionEntry))
                .Append('\n');
            ids.Add(id);
            parentId = id;
        }

        return builder.ToString();
    }

    private static string CreateEntryId(ISet<string> existingIds)
    {
        for (var i = 0; i < 100; i++)
        {
            var id = Guid.NewGuid().ToString("N")[..8];
            if (!existingIds.Contains(id))
            {
                return id;
            }
        }

        return Guid.NewGuid().ToString("N");
    }

    private static string CreateSessionId() => $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value
            .Trim()
            .Select(character => char.IsWhiteSpace(character) || invalid.Contains(character) ? '-' : character)
            .ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "session" : sanitized;
    }

    private readonly record struct SessionStats(
        int UserMessages,
        int AssistantMessages,
        int ToolResults,
        int ToolCalls)
    {
        public static SessionStats FromMessages(IReadOnlyList<ChatMessage> messages) =>
            new(
                messages.OfType<UserMessage>().Count(),
                messages.OfType<AssistantMessage>().Count(),
                messages.OfType<ToolResultMessage>().Count(),
                messages.OfType<AssistantMessage>().Sum(message => message.Content.OfType<ToolCallContent>().Count()));
    }

    private sealed record SessionTimeline(
        IReadOnlyList<SessionTimelineItem> Items,
        int EntryCount,
        int CompactionCount,
        int RetryEventCount,
        int LabelChangeCount)
    {
        public static SessionTimeline FromJsonl(string jsonl, IReadOnlyList<ChatMessage> fallbackMessages)
        {
            var entries = ParseEntries(jsonl);
            if (entries.Count == 0)
            {
                return new SessionTimeline(
                    fallbackMessages
                        .Select(static message => SessionTimelineItem.FromMessage(message))
                        .ToList(),
                    fallbackMessages.Count,
                    0,
                    0,
                    0);
            }

            var labels = BuildLabels(entries);
            var items = new List<SessionTimelineItem>();
            foreach (var entry in entries)
            {
                switch (entry.Type)
                {
                    case "message" when entry.Message is not null:
                        var message = CodingAgentSessionStore.ToMessage(entry.Message);
                        if (message is not null)
                        {
                            items.Add(SessionTimelineItem.FromEntry(entry, message, GetLabel(labels, entry.Id)));
                        }

                        break;

                    case "model_change":
                    case "session_info":
                    case "compaction":
                    case "branch_summary":
                    case "auto_retry_start":
                    case "auto_retry_end":
                    case "label":
                        items.Add(SessionTimelineItem.FromEntry(entry, null, GetLabel(labels, entry.Id)));
                        break;
                }
            }

            return new SessionTimeline(
                items,
                entries.Count,
                entries.Count(static entry => string.Equals(entry.Type, "compaction", StringComparison.OrdinalIgnoreCase)),
                entries.Count(static entry =>
                    string.Equals(entry.Type, "auto_retry_start", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(entry.Type, "auto_retry_end", StringComparison.OrdinalIgnoreCase)),
                entries.Count(static entry => string.Equals(entry.Type, "label", StringComparison.OrdinalIgnoreCase)));
        }

        private static IReadOnlyList<CodingAgentTreeSessionEntry> ParseEntries(string jsonl)
        {
            var entries = new List<CodingAgentTreeSessionEntry>();
            foreach (var line in jsonl.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (!root.TryGetProperty("type", out var type))
                    {
                        continue;
                    }

                    if (string.Equals(type.GetString(), "session", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var entry = JsonSerializer.Deserialize(
                        line,
                        CodingAgentTreeSessionJsonContext.Default.CodingAgentTreeSessionEntry);
                    if (entry is not null && !string.IsNullOrWhiteSpace(entry.Type))
                    {
                        entries.Add(entry);
                    }
                }
                catch (JsonException)
                {
                }
            }

            return entries;
        }

        private static IReadOnlyDictionary<string, string> BuildLabels(IReadOnlyList<CodingAgentTreeSessionEntry> entries)
        {
            var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                if (!string.Equals(entry.Type, "label", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(entry.TargetId))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(entry.Label))
                {
                    labels.Remove(entry.TargetId);
                }
                else
                {
                    labels[entry.TargetId] = entry.Label.Trim();
                }
            }

            return labels;
        }

        private static string? GetLabel(IReadOnlyDictionary<string, string> labels, string? entryId) =>
            !string.IsNullOrWhiteSpace(entryId) && labels.TryGetValue(entryId, out var label) ? label : null;
    }

    private sealed record SessionTimelineItem(
        string Type,
        string? Id,
        string? ParentId,
        DateTimeOffset? Timestamp,
        ChatMessage? Message,
        string? Provider,
        string? Model,
        string? Name,
        string? Action,
        string? TargetId,
        string? Label,
        string? Summary,
        string? FromId,
        string? FirstKeptEntryId,
        IReadOnlyList<string>? ReadFiles,
        IReadOnlyList<string>? ModifiedFiles,
        string? TurnPrefixSummary,
        bool? IsSplitTurn,
        int? TokensBefore,
        bool? FromHook,
        int? Attempt,
        int? MaxAttempts,
        int? DelayMs,
        string? ErrorMessage,
        bool? Success,
        string? FinalError,
        string? ResolvedLabel)
    {
        public static SessionTimelineItem FromMessage(ChatMessage message) =>
            new(
                "message",
                null,
                null,
                null,
                message,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null);

        public static SessionTimelineItem FromEntry(CodingAgentTreeSessionEntry entry, ChatMessage? message, string? resolvedLabel) =>
            new(
                entry.Type,
                entry.Id,
                entry.ParentId,
                entry.Timestamp,
                message,
                entry.Provider,
                entry.Model,
                entry.Name,
                entry.Action,
                entry.TargetId,
                entry.Label,
                entry.Summary,
                entry.FromId,
                entry.FirstKeptEntryId,
                entry.ReadFiles,
                entry.ModifiedFiles,
                entry.TurnPrefixSummary,
                entry.IsSplitTurn,
                entry.TokensBefore,
                entry.FromHook,
                entry.Attempt,
                entry.MaxAttempts,
                entry.DelayMs,
                entry.ErrorMessage,
                entry.Success,
                entry.FinalError,
                resolvedLabel);
    }

    private readonly record struct TextRenderSegment(string Text, string? Language, bool IsCode);

    private sealed record ToolCallRenderContext(
        string Id,
        string Name,
        string Arguments,
        string? Path,
        string? Language,
        string? Command);

    private readonly record struct ShellToolOutput(string Stdout, string Stderr, int? ExitCode);

    private sealed record MarkdownTable(IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows)
    {
        public static readonly MarkdownTable Empty = new([], []);
    }

    private enum MarkdownListKind
    {
        None,
        Unordered,
        Ordered
    }

    private const string Css = """
        :root {
          color-scheme: dark;
          --bg: #101214;
          --panel: #171a1d;
          --panel-soft: #1d2226;
          --line: #30363d;
          --text: #e6edf3;
          --muted: #9aa7b2;
          --user: #b7f7d5;
          --assistant: #a9c7ff;
          --tool: #ffd38a;
          --error: #ff9b9b;
        }

        * {
          box-sizing: border-box;
        }

        body {
          margin: 0;
          background: var(--bg);
          color: var(--text);
          font: 14px/1.55 ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
        }

        .layout {
          display: grid;
          grid-template-columns: minmax(220px, 300px) minmax(0, 1fr);
          align-items: start;
          gap: 20px;
          width: min(1440px, calc(100vw - 32px));
          margin: 0 auto;
          padding: 32px 0 48px;
        }

        .shell {
          min-width: 0;
          width: 100%;
        }

        .session-sidebar {
          position: sticky;
          top: 20px;
          max-height: calc(100vh - 40px);
          overflow: auto;
          border: 1px solid var(--line);
          background: var(--panel);
          border-radius: 8px;
          padding: 14px;
        }

        .sidebar-title {
          color: var(--muted);
          font-size: 12px;
          font-weight: 700;
          margin-bottom: 10px;
          text-transform: uppercase;
          letter-spacing: 0.08em;
        }

        .tree-controls {
          display: grid;
          gap: 8px;
          margin-bottom: 10px;
        }

        .tree-search {
          width: 100%;
          border: 1px solid var(--line);
          background: #0c0f11;
          color: var(--text);
          border-radius: 6px;
          padding: 7px 9px;
          font: 12px/1.4 ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
        }

        .tree-search::placeholder {
          color: #6f7a84;
        }

        .tree-filter-row {
          display: flex;
          flex-wrap: wrap;
          gap: 5px;
        }

        .tree-filter-button {
          appearance: none;
          border: 1px solid var(--line);
          background: var(--panel-soft);
          color: var(--muted);
          border-radius: 4px;
          padding: 4px 7px;
          font: 11px/1.3 ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
          cursor: pointer;
        }

        .tree-filter-button:hover,
        .tree-filter-button.active {
          color: var(--text);
          border-color: #5b6975;
          background: #222930;
        }

        .tree-list {
          display: grid;
          gap: 3px;
        }

        .tree-node {
          appearance: none;
          width: 100%;
          border: 0;
          background: transparent;
          color: var(--muted);
          border-radius: 4px;
          padding: 5px 6px;
          text-align: left;
          font: 12px/1.35 ui-monospace, SFMono-Regular, Consolas, "Liberation Mono", monospace;
          cursor: pointer;
          overflow-wrap: anywhere;
        }

        .tree-node:hover,
        .tree-node.active {
          background: var(--panel-soft);
          color: var(--text);
        }

        .tree-node.no-target {
          cursor: default;
          color: #6f7a84;
        }

        .tree-status {
          margin-top: 10px;
          color: var(--muted);
          font-size: 11px;
        }

        .page-header {
          border: 1px solid var(--line);
          background: var(--panel);
          border-radius: 8px;
          padding: 24px;
          margin-bottom: 18px;
        }

        .header-actions {
          display: flex;
          justify-content: flex-start;
          margin: 0 0 18px;
        }

        .download-button {
          appearance: none;
          border: 1px solid var(--line);
          background: var(--panel-soft);
          color: var(--text);
          border-radius: 6px;
          padding: 8px 12px;
          font: inherit;
          cursor: pointer;
        }

        .download-button:hover {
          border-color: #5b6975;
          background: #222930;
        }

        .eyebrow {
          margin: 0 0 8px;
          color: var(--muted);
          font-size: 12px;
          text-transform: uppercase;
          letter-spacing: 0.08em;
        }

        h1 {
          margin: 0 0 18px;
          font-size: 28px;
          line-height: 1.2;
          letter-spacing: 0;
        }

        .meta-grid {
          display: grid;
          grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
          gap: 12px;
          margin: 0;
        }

        .meta-grid div {
          min-width: 0;
          padding: 12px;
          background: var(--panel-soft);
          border: 1px solid var(--line);
          border-radius: 6px;
        }

        dt {
          color: var(--muted);
          font-size: 12px;
          margin-bottom: 4px;
        }

        dd {
          margin: 0;
          overflow-wrap: anywhere;
        }

        .timeline {
          display: grid;
          gap: 14px;
        }

        .message,
        .timeline-event,
        .empty-state {
          border: 1px solid var(--line);
          background: var(--panel);
          border-radius: 8px;
          padding: 16px;
          scroll-margin-top: 24px;
        }

        .message.deep-linked,
        .timeline-event.deep-linked {
          animation: tau-deep-link-highlight 2s ease-out;
        }

        @keyframes tau-deep-link-highlight {
          0% {
            box-shadow: 0 0 0 3px #7cc7ff;
          }

          100% {
            box-shadow: 0 0 0 0 rgba(124, 199, 255, 0);
          }
        }

        .message.user {
          border-left: 4px solid var(--user);
        }

        .message.assistant {
          border-left: 4px solid var(--assistant);
        }

        .message.tool-result {
          border-left: 4px solid var(--tool);
        }

        .message.error {
          border-left-color: var(--error);
        }

        .message-header {
          display: flex;
          flex-wrap: wrap;
          gap: 8px;
          align-items: center;
          margin-bottom: 12px;
          color: var(--muted);
          font-size: 12px;
          text-transform: uppercase;
        }

        .message-index {
          color: var(--text);
        }

        .message-role {
          font-weight: 700;
        }

        .message-meta {
          text-transform: none;
          overflow-wrap: anywhere;
        }

        .message-label {
          border: 1px solid #43505b;
          background: #20262b;
          color: var(--text);
          border-radius: 4px;
          padding: 1px 6px;
          text-transform: none;
          overflow-wrap: anywhere;
        }

        .copy-link-button {
          appearance: none;
          border: 1px solid var(--line);
          background: var(--panel-soft);
          color: var(--muted);
          border-radius: 4px;
          padding: 2px 7px;
          font: 11px/1.4 ui-monospace, SFMono-Regular, Consolas, "Liberation Mono", monospace;
          cursor: pointer;
          text-transform: none;
        }

        .copy-link-button:hover,
        .copy-link-button.copied {
          color: var(--text);
          border-color: #5b6975;
          background: #222930;
        }

        .timeline-event {
          color: var(--muted);
          border-left: 4px solid #56616b;
        }

        .model-change-event {
          border-left-color: #91c7ff;
        }

        .session-info-event {
          border-left-color: #9aa7b2;
        }

        .compaction-event {
          border-left-color: #d0b26f;
        }

        .branch-summary-event {
          border-left-color: #7ccf98;
        }

        .retry-event {
          border-left-color: #d495ff;
        }

        .label-event {
          border-left-color: #b7f7d5;
        }

        .event-header {
          display: flex;
          flex-wrap: wrap;
          gap: 8px;
          align-items: center;
          margin-bottom: 8px;
          color: var(--muted);
          font-size: 12px;
          text-transform: uppercase;
        }

        .event-type {
          color: var(--text);
          font-weight: 700;
        }

        .event-time,
        .event-meta-line {
          color: var(--muted);
          text-transform: none;
        }

        .event-summary {
          margin: 0;
          color: var(--text);
          overflow-wrap: anywhere;
        }

        .compaction-detail,
        .branch-summary-detail {
          margin-top: 8px;
          padding: 12px;
          border: 1px solid var(--line);
          border-radius: 6px;
          background: #0c0f11;
        }

        .compaction-detail pre,
        .branch-summary-detail pre {
          margin-top: 10px;
        }

        .event-file-list {
          margin: 8px 0 0;
          padding-left: 20px;
          color: var(--muted);
        }

        pre {
          margin: 0;
          white-space: pre-wrap;
          word-break: break-word;
          font: 13px/1.55 ui-monospace, SFMono-Regular, Consolas, "Liberation Mono", monospace;
        }

        .content-text,
        .code-block,
        .error-text,
        .thinking,
        .tool-call,
        .tool-call-arguments-fold,
        .tool-result-fold {
          margin-top: 10px;
          padding: 12px;
          background: #0c0f11;
          border: 1px solid var(--line);
          border-radius: 6px;
        }

        .code-block {
          margin-bottom: 0;
        }

        .content-text a {
          color: #91c7ff;
          text-decoration: underline;
          text-underline-offset: 2px;
        }

        .content-text a:hover {
          color: #b7f7d5;
        }

        .rich-text {
          white-space: normal;
          word-break: normal;
          overflow-wrap: anywhere;
          font: 14px/1.6 ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
        }

        .rich-text > * {
          margin: 0;
        }

        .rich-text > * + * {
          margin-top: 8px;
        }

        .rich-text h1,
        .rich-text h2,
        .rich-text h3,
        .rich-text h4,
        .rich-text h5,
        .rich-text h6 {
          color: var(--text);
          font-weight: 700;
          letter-spacing: 0;
          line-height: 1.25;
        }

        .rich-text h1 {
          font-size: 20px;
        }

        .rich-text h2 {
          font-size: 18px;
        }

        .rich-text h3 {
          font-size: 16px;
        }

        .rich-text h4,
        .rich-text h5,
        .rich-text h6 {
          font-size: 14px;
        }

        .rich-text ul,
        .rich-text ol {
          padding-left: 22px;
        }

        .rich-text li + li {
          margin-top: 4px;
        }

        .rich-text .task-list-item {
          list-style: none;
          margin-left: -20px;
        }

        .rich-text .task-list-item input {
          width: 14px;
          height: 14px;
          margin: 0 6px 0 0;
          vertical-align: -2px;
          accent-color: #7dd3fc;
        }

        .rich-text blockquote {
          border-left: 3px solid #3e5f7d;
          padding-left: 10px;
          color: var(--text);
        }

        .rich-text blockquote p + p {
          margin-top: 6px;
        }

        .rich-text .table-scroll {
          overflow-x: auto;
        }

        .rich-text table {
          width: 100%;
          border-collapse: collapse;
          min-width: 320px;
          font-size: 13px;
        }

        .rich-text th,
        .rich-text td {
          border: 1px solid var(--line);
          padding: 6px 8px;
          text-align: left;
          vertical-align: top;
        }

        .rich-text th {
          background: #181d22;
          color: var(--text);
          font-weight: 700;
        }

        .rich-text td {
          color: var(--text);
        }

        .content-text .inline-code {
          color: var(--text);
          background: #181d22;
          border: 1px solid #2d363f;
          border-radius: 4px;
          padding: 1px 4px;
          font: 13px/1.45 ui-monospace, SFMono-Regular, Consolas, "Liberation Mono", monospace;
        }

        .content-text strong {
          color: var(--text);
          font-weight: 700;
        }

        .content-text em {
          color: var(--text);
          font-style: italic;
        }

        .code-block figcaption {
          margin: 0 0 8px;
          color: var(--muted);
          font: 12px/1.4 ui-monospace, SFMono-Regular, Consolas, "Liberation Mono", monospace;
        }

        .code-block pre {
          overflow-x: auto;
          white-space: pre;
          word-break: normal;
        }

        .code-block code {
          font: inherit;
        }

        .syntax-keyword {
          color: #91c7ff;
          font-weight: 600;
        }

        .syntax-string {
          color: #b7f7d5;
        }

        .syntax-number {
          color: #ffd38a;
        }

        .syntax-comment {
          color: #7f8b96;
          font-style: italic;
        }

        .syntax-property {
          color: #d7bcff;
        }

        .syntax-operator {
          color: #9aa7b2;
        }

        .syntax-added {
          color: #b7f7d5;
          background: rgba(88, 166, 106, 0.12);
        }

        .syntax-removed {
          color: #ffb4b4;
          background: rgba(248, 81, 73, 0.12);
        }

        .syntax-hunk {
          color: #91c7ff;
        }

        .error-text {
          color: var(--error);
        }

        .empty-content,
        .muted {
          color: var(--muted);
        }

        details summary {
          cursor: pointer;
          color: var(--muted);
          margin-bottom: 8px;
        }

        details summary span {
          color: var(--muted);
          font-family: ui-monospace, SFMono-Regular, Consolas, "Liberation Mono", monospace;
          text-transform: none;
        }

        .thinking {
          border-color: #39414a;
        }

        .tool-call {
          border-color: #4d432e;
        }

        .tool-call-summary {
          display: grid;
          gap: 8px;
          margin: 8px 0 10px;
          padding: 10px;
          border: 1px solid #3b3428;
          border-radius: 6px;
          background: #11100d;
        }

        .tool-summary-field {
          display: grid;
          grid-template-columns: minmax(110px, 180px) minmax(0, 1fr);
          gap: 8px;
          align-items: start;
        }

        .tool-summary-key {
          color: var(--muted);
          font-size: 12px;
          line-height: 1.5;
        }

        .tool-summary-value {
          color: var(--text);
          overflow-wrap: anywhere;
          font: 12px/1.5 ui-monospace, SFMono-Regular, Consolas, "Liberation Mono", monospace;
        }

        .tool-summary-code-block pre {
          margin-top: 4px;
          padding: 8px;
          overflow-x: auto;
          white-space: pre;
          word-break: normal;
          border: 1px solid var(--line);
          border-radius: 5px;
          background: #0c0f11;
        }

        .tool-summary-code-block code {
          font: 12px/1.5 ui-monospace, SFMono-Regular, Consolas, "Liberation Mono", monospace;
        }

        .tool-summary-more {
          margin-top: 4px;
          color: var(--muted);
          font-size: 12px;
        }

        .tool-call-arguments-fold {
          border-color: #4d432e;
        }

        .tool-result-fold {
          border-color: #4d432e;
        }

        .tool-result-render {
          display: grid;
          gap: 10px;
          margin-top: 10px;
        }

        .tool-result-status {
          padding: 8px 10px;
          border: 1px solid var(--line);
          border-radius: 5px;
          background: #101417;
          color: var(--text);
          overflow-wrap: anywhere;
        }

        .tool-result-status.success {
          border-color: #305342;
          color: #b7f7d5;
        }

        .tool-result-status.error {
          border-color: #664042;
          color: #ffb4b4;
        }

        .tool-result-section {
          min-width: 0;
        }

        .tool-result-label {
          margin-bottom: 4px;
          color: var(--muted);
          font-size: 12px;
        }

        .tool-result-section pre {
          padding: 10px;
          overflow-x: auto;
          white-space: pre;
          word-break: normal;
          border: 1px solid var(--line);
          border-radius: 5px;
          background: #0c0f11;
        }

        .tool-result-section code {
          font: 12px/1.5 ui-monospace, SFMono-Regular, Consolas, "Liberation Mono", monospace;
        }

        .tool-result-stderr pre {
          border-color: #664042;
        }

        .tool-result-list {
          display: grid;
          gap: 3px;
          margin: 0;
          padding: 8px 10px 8px 28px;
          border: 1px solid var(--line);
          border-radius: 5px;
          background: #0c0f11;
          font: 12px/1.5 ui-monospace, SFMono-Regular, Consolas, "Liberation Mono", monospace;
        }

        .tool-result-directory {
          color: #91c7ff;
        }

        .tool-call-arguments-fold:not([open]) summary,
        .tool-result-fold:not([open]) summary {
          margin-bottom: 0;
        }

        .image-block {
          margin: 12px 0 0;
        }

        .image-block img {
          display: block;
          max-width: 100%;
          border: 1px solid var(--line);
          border-radius: 6px;
        }

        .image-block figcaption {
          margin-top: 8px;
          color: var(--muted);
          font: 12px/1.4 ui-monospace, SFMono-Regular, Consolas, "Liberation Mono", monospace;
        }

        @media (max-width: 640px) {
          .layout {
            display: block;
            width: min(100vw - 20px, 1120px);
            padding-top: 16px;
          }

          .shell {
            width: 100%;
          }

          .session-sidebar {
            position: static;
            max-height: 240px;
            margin-bottom: 14px;
          }

          .page-header,
          .message,
          .timeline-event,
          .empty-state {
            padding: 14px;
          }

          h1 {
            font-size: 22px;
          }
        }
        """;

    private const string Script = """
        function getSessionEntries() {
          const source = document.getElementById('session-jsonl');
          if (!source) {
            return [];
          }

          return source.value
            .split(/\r?\n/)
            .map(line => line.trim())
            .filter(Boolean)
            .map(line => {
              try {
                return JSON.parse(line);
              } catch {
                return null;
              }
            })
            .filter(Boolean);
        }

        let treeFilterMode = 'default';
        let treeSearchQuery = '';

        function downloadSessionJsonl() {
          const source = document.getElementById('session-jsonl');
          const button = document.querySelector('.download-button');
          if (!source) {
            return;
          }

          const blob = new Blob([source.value], { type: 'application/x-ndjson;charset=utf-8' });
          const url = URL.createObjectURL(blob);
          const link = document.createElement('a');
          link.href = url;
          link.download = button?.dataset.downloadName || 'tau-session.jsonl';
          document.body.appendChild(link);
          link.click();
          document.body.removeChild(link);
          URL.revokeObjectURL(url);
        }

        function extractText(content) {
          if (!Array.isArray(content)) {
            return '';
          }

          return content
            .filter(block => block && block.type === 'text' && block.text)
            .map(block => block.text)
            .join(' ')
            .replace(/\s+/g, ' ')
            .trim();
        }

        function shortId(id) {
          if (!id) {
            return 'root';
          }

          return id.length <= 8 ? id : id.slice(0, 8);
        }

        function buildLabelMap(entries) {
          const labels = new Map();
          for (const entry of entries) {
            if (!entry || entry.type !== 'label' || !entry.targetId) {
              continue;
            }

            if (entry.label) {
              labels.set(entry.targetId, entry.label);
            } else {
              labels.delete(entry.targetId);
            }
          }

          return labels;
        }

        function describeEntry(entry, messageIndex, labelMap) {
          const labelPrefix = labelMap && labelMap.has(entry.id)
            ? '[' + labelMap.get(entry.id) + '] '
            : '';

          if (entry.type === 'message' && entry.message) {
            const role = entry.message.role || 'message';
            const text = extractText(entry.message.content);
            const suffix = text ? ': ' + text.slice(0, 80) : '';
            return labelPrefix + '#' + messageIndex + ' ' + role + suffix;
          }

          if (entry.type === 'model_change') {
            return labelPrefix + 'model: ' + [entry.provider, entry.model].filter(Boolean).join('/');
          }

          if (entry.type === 'session_info') {
            return labelPrefix + (entry.action || entry.name ? 'session: ' + (entry.action || entry.name || 'info') : 'session info');
          }

          if (entry.type === 'compaction') {
            const tokens = entry.tokensBefore ? entry.tokensBefore.toLocaleString() + ' tokens' : 'session';
            return labelPrefix + 'compaction: ' + tokens;
          }

          if (entry.type === 'branch_summary') {
            const source = entry.fromId || entry.parentId || 'root';
            return labelPrefix + 'branch summary: ' + shortId(source);
          }

          if (entry.type === 'auto_retry_start') {
            return labelPrefix + 'retry start ' + (entry.attempt || 0) + '/' + (entry.maxAttempts || 0);
          }

          if (entry.type === 'auto_retry_end') {
            return labelPrefix + 'retry ' + (entry.success ? 'success' : 'failed') + ' attempt ' + (entry.attempt || 0);
          }

          if (entry.type === 'label') {
            return 'label ' + shortId(entry.targetId) + ': ' + (entry.label || 'clear');
          }

          return labelPrefix + (entry.type || 'entry');
        }

        function hasTextContent(content) {
          return extractText(content).length > 0;
        }

        function isAssistantToolOnly(entry) {
          return entry.type === 'message' &&
            entry.message &&
            entry.message.role === 'assistant' &&
            !hasTextContent(entry.message.content) &&
            entry.message.stopReason !== 'error' &&
            entry.message.stopReason !== 'aborted';
        }

        function getSearchableText(entry, labelMap) {
          const parts = [
            entry.type || '',
            entry.id || '',
            entry.parentId || '',
            entry.provider || '',
            entry.model || '',
            entry.name || '',
            entry.action || '',
            entry.targetId || '',
            entry.label || '',
            entry.summary || '',
            entry.turnPrefixSummary || '',
            entry.isSplitTurn ? 'split turn prefix' : '',
            entry.errorMessage || '',
            entry.finalError || '',
            entry.attempt === undefined || entry.attempt === null ? '' : String(entry.attempt),
            entry.maxAttempts === undefined || entry.maxAttempts === null ? '' : String(entry.maxAttempts),
            entry.delayMs === undefined || entry.delayMs === null ? '' : String(entry.delayMs),
            entry.success === undefined || entry.success === null ? '' : (entry.success ? 'success' : 'failed')
          ];

          if (labelMap && labelMap.has(entry.id)) {
            parts.push(labelMap.get(entry.id));
          }

          if (entry.message) {
            parts.push(entry.message.role || '');
            parts.push(entry.message.toolCallId || '');
            parts.push(extractText(entry.message.content));
          }

          return parts.join(' ').toLowerCase();
        }

        function shouldShowTreeEntry(entry, labelMap, currentLeafId) {
          if (!entry || !entry.id) {
            return false;
          }

          if (entry.id === currentLeafId) {
            return true;
          }

          if (isAssistantToolOnly(entry) && treeFilterMode !== 'all') {
            return false;
          }

          const role = entry.message?.role || '';
          const isSettingsEntry = ['label', 'model_change', 'session_info'].includes(entry.type);
          let passesFilter = true;
          switch (treeFilterMode) {
            case 'user-only':
              passesFilter = entry.type === 'message' && role === 'user';
              break;
            case 'no-tools':
              passesFilter = !isSettingsEntry && !(entry.type === 'message' && role === 'toolResult');
              break;
            case 'labeled-only':
              passesFilter = labelMap.has(entry.id);
              break;
            case 'all':
              passesFilter = true;
              break;
            default:
              passesFilter = !isSettingsEntry;
              break;
          }

          if (!passesFilter) {
            return false;
          }

          const tokens = treeSearchQuery.toLowerCase().split(/\s+/).filter(Boolean);
          if (tokens.length === 0) {
            return true;
          }

          const haystack = getSearchableText(entry, labelMap);
          return tokens.every(token => haystack.includes(token));
        }

        function renderBranchOutline() {
          const container = document.getElementById('tree-list');
          if (!container) {
            return;
          }

          const allEntries = getSessionEntries();
          const entries = allEntries.slice(1);
          const byId = new Map(entries.map(entry => [entry.id, entry]));
          const labelMap = buildLabelMap(entries);
          const currentLeafId = getCurrentLeafId();
          const status = document.getElementById('tree-status');
          let messageIndex = 0;
          let visibleCount = 0;

          container.innerHTML = '';
          if (entries.length === 0) {
            container.textContent = 'No branch entries.';
            if (status) {
              status.textContent = '';
            }

            return;
          }

          for (const entry of entries) {
            let depth = 0;
            let parentId = entry.parentId;
            while (parentId && byId.has(parentId) && depth < 32) {
              depth++;
              parentId = byId.get(parentId).parentId;
            }

            const isMessage = entry.type === 'message';
            if (isMessage) {
              messageIndex++;
            }

            if (!shouldShowTreeEntry(entry, labelMap, currentLeafId)) {
              continue;
            }

            visibleCount++;

            const button = document.createElement('button');
            button.type = 'button';
            button.className = entry.id ? 'tree-node' : 'tree-node no-target';
            button.style.paddingLeft = (6 + depth * 12) + 'px';
            button.textContent = describeEntry(entry, messageIndex, labelMap);
            if (entry.id) {
              button.dataset.entryId = entry.id || '';
              button.addEventListener('click', () => {
                document.querySelectorAll('.tree-node.active').forEach(node => node.classList.remove('active'));
                button.classList.add('active');
                scrollToEntry(entry.id, true);
              });
            }

            container.appendChild(button);
          }

          if (visibleCount === 0) {
            container.textContent = 'No matching entries.';
          }

          if (status) {
            status.textContent = visibleCount === entries.length
              ? visibleCount + ' entries'
              : visibleCount + ' / ' + entries.length + ' entries';
          }
        }

        function getEntryIds() {
          return getSessionEntries()
            .slice(1)
            .filter(entry => entry && entry.id)
            .map(entry => entry.id);
        }

        function getCurrentLeafId() {
          const ids = getEntryIds();
          return ids.length === 0 ? '' : ids[ids.length - 1];
        }

        function getTargetIdFromLocation() {
          const url = new URL(window.location.href);
          const direct = url.searchParams.get('targetId');
          if (direct) {
            return direct;
          }

          const hash = url.hash.startsWith('#') ? url.hash.slice(1) : url.hash;
          const queryIndex = hash.indexOf('?');
          if (queryIndex < 0) {
            return '';
          }

          return new URLSearchParams(hash.slice(queryIndex + 1)).get('targetId') || '';
        }

        function findMessageElement(entryId) {
          if (!entryId) {
            return null;
          }

          return Array.from(document.querySelectorAll('[data-entry-id]'))
            .find(element => element.dataset.entryId === entryId) || null;
        }

        function scrollToEntry(entryId, smooth) {
          const target = findMessageElement(entryId);
          if (!target) {
            return false;
          }

          document.querySelectorAll('.tree-node.active').forEach(node => node.classList.remove('active'));
          const treeNode = Array.from(document.querySelectorAll('.tree-node[data-entry-id]'))
            .find(node => node.dataset.entryId === entryId);
          treeNode?.classList.add('active');
          target.scrollIntoView({ behavior: smooth ? 'smooth' : 'auto', block: 'start' });
          target.classList.remove('deep-linked');
          window.requestAnimationFrame(() => target.classList.add('deep-linked'));
          return true;
        }

        function buildShareUrl(entryId) {
          const url = new URL(window.location.href);
          const params = new URLSearchParams();
          const leafId = getCurrentLeafId();
          if (leafId) {
            params.set('leafId', leafId);
          }

          params.set('targetId', entryId);

          const hash = url.hash.startsWith('#') ? url.hash.slice(1) : url.hash;
          if (hash && !hash.includes('=') && !hash.includes('&')) {
            url.hash = hash + '?' + params.toString();
            return url.toString();
          }

          const gistId = Array.from(url.searchParams.keys()).find(key => !url.searchParams.get(key));
          url.search = gistId ? '?' + gistId + '&' + params.toString() : '?' + params.toString();
          return url.toString();
        }

        async function copyText(text) {
          if (navigator.clipboard && navigator.clipboard.writeText) {
            await navigator.clipboard.writeText(text);
            return;
          }

          const textarea = document.createElement('textarea');
          textarea.value = text;
          textarea.style.position = 'fixed';
          textarea.style.opacity = '0';
          document.body.appendChild(textarea);
          textarea.select();
          document.execCommand('copy');
          document.body.removeChild(textarea);
        }

        function attachCopyLinkButtons() {
          document.querySelectorAll('.copy-link-button[data-entry-id]').forEach(button => {
            button.addEventListener('click', async event => {
              event.preventDefault();
              event.stopPropagation();
              const entryId = button.dataset.entryId;
              if (!entryId) {
                return;
              }

              await copyText(buildShareUrl(entryId));
              const original = button.textContent;
              button.textContent = 'copied';
              button.classList.add('copied');
              setTimeout(() => {
                button.textContent = original;
                button.classList.remove('copied');
              }, 1200);
            });
          });
        }

        function attachTreeControls() {
          const search = document.getElementById('tree-search');
          search?.addEventListener('input', event => {
            treeSearchQuery = event.target.value || '';
            renderBranchOutline();
          });

          document.querySelectorAll('.tree-filter-button[data-filter]').forEach(button => {
            button.addEventListener('click', () => {
              treeFilterMode = button.dataset.filter || 'default';
              document.querySelectorAll('.tree-filter-button.active')
                .forEach(active => active.classList.remove('active'));
              button.classList.add('active');
              renderBranchOutline();
            });
          });
        }

        renderBranchOutline();
        attachTreeControls();
        attachCopyLinkButtons();
        scrollToEntry(getTargetIdFromLocation(), false);
        """;
}
