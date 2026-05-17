using System.Text;
using Tau.Ai;
using Tau.WebUi.Contracts;

namespace Tau.WebUi.Services;

public static class WebChatMarkdownExporter
{
    public static string Render(WebChatSessionDto session, TauSecretRedactor redactor)
    {
        if (session is null)
        {
            throw new ArgumentNullException(nameof(session));
        }

        var sb = new StringBuilder(4 * 1024);
        sb.Append("# ");
        sb.AppendLine(redactor.Redact(session.Title));
        sb.AppendLine();
        sb.Append("- Provider: `").Append(session.Provider).AppendLine("`");
        sb.Append("- Model: `").Append(session.Model).AppendLine("`");
        sb.Append("- Created: `").Append(session.CreatedAt.ToString("O")).AppendLine("`");
        sb.Append("- Updated: `").Append(session.UpdatedAt.ToString("O")).AppendLine("`");
        if (redactor.Enabled)
        {
            sb.AppendLine("- Secret redaction: **on**");
        }

        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        foreach (var message in session.Messages)
        {
            RenderMessage(sb, message, redactor);
        }

        return sb.ToString();
    }

    private static void RenderMessage(StringBuilder sb, WebChatMessageDto message, TauSecretRedactor redactor)
    {
        var role = string.IsNullOrEmpty(message.Role) ? "message" : message.Role;
        sb.Append("## ").Append(char.ToUpperInvariant(role[0])).Append(role.AsSpan(1));
        sb.Append(" — ").AppendLine(message.Timestamp.ToString("O"));
        sb.AppendLine();

        if (!string.IsNullOrEmpty(message.Thinking))
        {
            sb.AppendLine("<details><summary>thinking</summary>");
            sb.AppendLine();
            sb.AppendLine(redactor.Redact(message.Thinking));
            sb.AppendLine();
            sb.AppendLine("</details>");
            sb.AppendLine();
        }

        if (!string.IsNullOrEmpty(message.Text))
        {
            sb.AppendLine(redactor.Redact(message.Text));
            sb.AppendLine();
        }

        if (message.ToolCalls is { Count: > 0 })
        {
            foreach (var tool in message.ToolCalls)
            {
                sb.Append("### Tool — `").Append(tool.ToolName).Append("` (").Append(tool.Status).AppendLine(")");
                sb.AppendLine();
                if (!string.IsNullOrEmpty(tool.Arguments))
                {
                    sb.AppendLine("Arguments:");
                    sb.AppendLine();
                    sb.AppendLine("```");
                    sb.AppendLine(redactor.Redact(tool.Arguments));
                    sb.AppendLine("```");
                    sb.AppendLine();
                }

                if (!string.IsNullOrEmpty(tool.Output))
                {
                    sb.AppendLine("Output:");
                    sb.AppendLine();
                    sb.AppendLine("```");
                    sb.AppendLine(redactor.Redact(tool.Output));
                    sb.AppendLine("```");
                    sb.AppendLine();
                }
            }
        }

        if (message.Attachments is { Count: > 0 })
        {
            sb.AppendLine("Attachments:");
            sb.AppendLine();
            foreach (var attachment in message.Attachments)
            {
                sb.Append("- **").Append(redactor.Redact(attachment.FileName)).Append("** (`");
                sb.Append(attachment.MimeType).Append("`, ");
                sb.Append(attachment.Size.ToString(System.Globalization.CultureInfo.InvariantCulture)).AppendLine(" bytes)");
                if (!string.IsNullOrEmpty(attachment.ExtractedText))
                {
                    sb.AppendLine();
                    sb.AppendLine("  ```");
                    sb.AppendLine("  " + redactor.Redact(attachment.ExtractedText).Replace("\n", "\n  ", StringComparison.Ordinal));
                    sb.AppendLine("  ```");
                }
            }
            sb.AppendLine();
        }

        if (!string.IsNullOrEmpty(message.Error))
        {
            sb.Append("> **Error:** ").AppendLine(redactor.Redact(message.Error));
            sb.AppendLine();
        }
    }
}
