using System.Text;

namespace Tau.Mom;

internal static class MomRuntimeContext
{
    public static string Build(MomOptions options, string workingDirectory)
    {
        var layout = ChannelWorkspaceLayout.For(options, workingDirectory);

        var builder = new StringBuilder();
        builder.AppendLine("<mom_runtime_context>");
        builder.AppendLine("role: Tau.Mom local delegation worker");
        builder.AppendLine("tone: concise, direct, no emoji");
        builder.AppendLine("slack_adapter: not connected in this local worker slice");
        builder.AppendLine();

        builder.AppendLine("workspace_layout:");
        builder.Append("- workspace: ").AppendLine(layout.WorkspaceDirectory);
        builder.Append("- channel: ").AppendLine(layout.ChannelDirectory);
        builder.Append("- workspace_memory: ").AppendLine(layout.WorkspaceMemoryPath);
        builder.Append("- channel_memory: ").AppendLine(layout.ChannelMemoryPath);
        builder.Append("- system_log: ").AppendLine(layout.SystemLogPath);
        builder.Append("- channel_log: ").AppendLine(layout.ChannelLogPath);
        builder.Append("- channel_status: ").AppendLine(layout.ChannelStatusPath);
        builder.Append("- channel_context: ").AppendLine(layout.ChannelContextPath);
        builder.Append("- prompt_debug: ").AppendLine(layout.PromptDebugPath);
        builder.Append("- attachments: ").AppendLine(layout.AttachmentsDirectory);
        builder.Append("- attachment_manifest: ").AppendLine(layout.AttachmentManifestPath);
        builder.Append("- scratch: ").AppendLine(layout.ScratchDirectory);
        builder.Append("- workspace_skills: ").AppendLine(layout.WorkspaceSkillsDirectory);
        builder.Append("- channel_skills: ").AppendLine(layout.ChannelSkillsDirectory);
        builder.Append("- events: ").AppendLine(layout.EventsDirectory);
        builder.AppendLine();

        builder.AppendLine("skill_docs:");
        builder.AppendLine(layout.BuildSkillInventory());
        builder.AppendLine();

        builder.AppendLine("local_rules:");
        builder.AppendLine("- Treat the channel directory as the current persistent workspace for this delegation.");
        builder.AppendLine("- Use scratch/ for temporary or generated working files that should stay scoped to this channel.");
        builder.AppendLine("- Use MEMORY.md files for durable facts only when the user asks to remember something or when a stable project fact is learned.");
        builder.AppendLine("- Use SYSTEM.md to record environment modifications such as installed packages, changed config files, or persistent environment variables.");
        builder.AppendLine("- Use log.jsonl for older channel history; it stores user messages and final bot responses, not tool traces.");
        builder.AppendLine("- Use attachment paths from the delegation context as local files. Relative attachment paths are relative to the channel directory.");
        builder.AppendLine("- Skill directories are documented for local context only; do not claim custom mom skills are executable tools until a runtime loader is wired.");
        builder.AppendLine("- If an event asks for a routine check and there is nothing useful to report, respond exactly [SILENT].");
        builder.AppendLine("- Do not claim Slack, Docker sandboxing, or custom mom skills are available unless the current prompt or files prove they are wired.");
        builder.AppendLine();

        builder.AppendLine("event_files:");
        builder.AppendLine("- immediate: { \"type\": \"immediate\", \"channelId\": \"<channel>\", \"text\": \"...\" }");
        builder.AppendLine("- one-shot: { \"type\": \"one-shot\", \"channelId\": \"<channel>\", \"text\": \"...\", \"at\": \"2026-05-10T10:00:00+00:00\" }");
        builder.AppendLine("- periodic: { \"type\": \"periodic\", \"channelId\": \"<channel>\", \"text\": \"...\", \"schedule\": \"0 9 * * 1-5\", \"timezone\": \"UTC\" }");
        builder.AppendLine("- Event-triggered prompts begin with [EVENT:file:type:schedule].");
        builder.AppendLine("</mom_runtime_context>");

        return builder.ToString();
    }
}
