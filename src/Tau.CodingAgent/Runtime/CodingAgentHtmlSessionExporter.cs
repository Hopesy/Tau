using System.Net;
using System.Text;
using System.Text.Json;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

public static class CodingAgentHtmlSessionExporter
{
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
        string? sessionJsonl = null)
    {
        var exportPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(exportPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var html = Render(messages, provider, model, sessionName, treeSummary, sessionJsonl);
        File.WriteAllText(exportPath, html, Encoding.UTF8);
        return exportPath;
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
        builder.AppendLine("<div id=\"tree-list\" class=\"tree-list\">Loading...</div>");
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

        if (messages.Count == 0)
        {
            builder.AppendLine("<section class=\"empty-state\">No messages in this session.</section>");
        }
        else
        {
            builder.AppendLine("<section class=\"timeline\" aria-label=\"Session transcript\">");
            for (var i = 0; i < messages.Count; i++)
            {
                RenderMessage(builder, messages[i], i + 1);
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

    private static void RenderMessage(StringBuilder builder, ChatMessage message, int index)
    {
        switch (message)
        {
            case UserMessage user:
                AppendArticleStart(builder, index, "message user");
                AppendMessageHeader(builder, index, "user");
                RenderContentBlocks(builder, user.Content);
                builder.AppendLine("</article>");
                break;

            case AssistantMessage assistant:
                AppendArticleStart(builder, index, "message assistant");
                AppendMessageHeader(builder, index, "assistant", FormatAssistantMeta(assistant));
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
                AppendArticleStart(builder, index, toolResult.IsError ? "message tool-result error" : "message tool-result");
                AppendMessageHeader(
                    builder,
                    index,
                    "tool result",
                    string.IsNullOrWhiteSpace(toolResult.ToolCallId) ? null : $"call {toolResult.ToolCallId}");
                RenderContentBlocks(builder, toolResult.Content);
                builder.AppendLine("</article>");
                break;

            default:
                AppendArticleStart(builder, index, "message unknown");
                AppendMessageHeader(builder, index, message.Role);
                builder.Append("<pre class=\"content-text\">")
                    .Append(Html(message.ToString() ?? message.Role))
                    .AppendLine("</pre>");
                builder.AppendLine("</article>");
                break;
        }
    }

    private static void AppendArticleStart(StringBuilder builder, int index, string cssClass)
    {
        builder.Append("<article id=\"message-")
            .Append(index)
            .Append("\" data-message-index=\"")
            .Append(index)
            .Append("\" class=\"")
            .Append(HtmlAttribute(cssClass))
            .AppendLine("\">");
    }

    private static void RenderContentBlocks(StringBuilder builder, IReadOnlyList<ContentBlock> blocks)
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
                    builder.Append("<pre class=\"content-text\">")
                        .Append(Html(text.Text))
                        .AppendLine("</pre>");
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
                    builder.Append("<pre>")
                        .Append(Html(toolCall.Arguments))
                        .AppendLine("</pre>");
                    builder.AppendLine("</details>");
                    break;

                case ImageContent image:
                    builder.Append("<figure class=\"image-block\"><img alt=\"session image\" src=\"data:")
                        .Append(HtmlAttribute(image.MimeType))
                        .Append(";base64,")
                        .Append(HtmlAttribute(image.Data))
                        .AppendLine("\"></figure>");
                    break;

                default:
                    builder.Append("<pre class=\"content-text muted\">")
                        .Append(Html($"[{block.Type}]"))
                        .AppendLine("</pre>");
                    break;
            }
        }
    }

    private static void AppendMessageHeader(StringBuilder builder, int index, string role, string? meta = null)
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
        .empty-state {
          border: 1px solid var(--line);
          background: var(--panel);
          border-radius: 8px;
          padding: 16px;
          scroll-margin-top: 24px;
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

        pre {
          margin: 0;
          white-space: pre-wrap;
          word-break: break-word;
          font: 13px/1.55 ui-monospace, SFMono-Regular, Consolas, "Liberation Mono", monospace;
        }

        .content-text,
        .error-text,
        .thinking,
        .tool-call {
          margin-top: 10px;
          padding: 12px;
          background: #0c0f11;
          border: 1px solid var(--line);
          border-radius: 6px;
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

        .image-block {
          margin: 12px 0 0;
        }

        .image-block img {
          display: block;
          max-width: 100%;
          border: 1px solid var(--line);
          border-radius: 6px;
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

        function describeEntry(entry, messageIndex) {
          if (entry.type === 'message' && entry.message) {
            const role = entry.message.role || 'message';
            const text = extractText(entry.message.content);
            const suffix = text ? ': ' + text.slice(0, 80) : '';
            return '#' + messageIndex + ' ' + role + suffix;
          }

          if (entry.type === 'model_change') {
            return 'model: ' + [entry.provider, entry.model].filter(Boolean).join('/');
          }

          if (entry.type === 'session_info') {
            return entry.action || entry.name ? 'session: ' + (entry.action || entry.name || 'info') : 'session info';
          }

          if (entry.type === 'label') {
            return 'label: ' + (entry.label || 'clear');
          }

          return entry.type || 'entry';
        }

        function renderBranchOutline() {
          const container = document.getElementById('tree-list');
          if (!container) {
            return;
          }

          const allEntries = getSessionEntries();
          const entries = allEntries.slice(1);
          const byId = new Map(entries.map(entry => [entry.id, entry]));
          let messageIndex = 0;

          container.innerHTML = '';
          if (entries.length === 0) {
            container.textContent = 'No branch entries.';
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

            const button = document.createElement('button');
            button.type = 'button';
            button.className = isMessage ? 'tree-node' : 'tree-node no-target';
            button.style.paddingLeft = (6 + depth * 12) + 'px';
            button.textContent = describeEntry(entry, messageIndex);
            if (isMessage) {
              const targetIndex = messageIndex;
              button.addEventListener('click', () => {
                document.querySelectorAll('.tree-node.active').forEach(node => node.classList.remove('active'));
                button.classList.add('active');
                document.getElementById('message-' + targetIndex)?.scrollIntoView({ behavior: 'smooth', block: 'start' });
              });
            }

            container.appendChild(button);
          }
        }

        renderBranchOutline();
        """;
}
