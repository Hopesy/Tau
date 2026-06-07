using System.Text.Json;
using Tau.Agent;
using Tau.Agent.Platform;
using Tau.Ai;
using Tau.Ai.Observability;
using Tau.Ai.Providers;
using Tau.Ai.Providers.Faux;

var registry = new ProviderRegistry();
var faux = Faux.Register(registry);
faux.SetResponses([
    Faux.AssistantMessage(
        [Faux.ToolCall("echo", new Dictionary<string, object?> { ["text"] = "console hello" }, "console-call")],
        stopReason: StopReason.ToolUse),
    Faux.AssistantMessage("console example complete")
]);

var sessions = new InMemoryAgentSessionStore();
var logSink = new ConsoleTauLogSink();

var app = AgentApplication.CreateBuilder()
    .UseProviderRegistry(registry)
    .UseModel(faux.GetModel())
    .UseSystemPrompt("You are a concise example agent.")
    .UseSessionId("console-example")
    .UseSessionStore(sessions)
    .UseLogSink(logSink)
    .AddTool(
        "echo",
        "Echo",
        "Echoes the provided text.",
        Schema("""
        {
            "type": "object",
            "properties": {
                "text": { "type": "string" }
            },
            "required": ["text"]
        }
        """),
        async (context, _) =>
        {
            await context.ReportUpdateAsync(new ToolUpdate("echoing", [new TextContent("echoing")]));
            return new ToolResult([new TextContent(context.Arguments.GetProperty("text").GetString() ?? string.Empty)]);
        })
    .Build();

var result = await app.PromptAsync("Run the console example.");
var snapshot = sessions.Load("console-example");

Console.WriteLine($"assistant: {result.AssistantText}");
Console.WriteLine($"success: {result.IsSuccess}");
Console.WriteLine($"messages: {result.Messages.Count}");
Console.WriteLine($"toolStarts: {result.ToolStarts.Count}");
Console.WriteLine($"toolEnds: {result.ToolEnds.Count}");
Console.WriteLine($"savedSession: {result.SavedSession}");
Console.WriteLine($"sessionMessages: {snapshot?.Messages.Count ?? 0}");
Console.WriteLine($"correlationId: {result.LogContext.CorrelationId}");

return result.IsSuccess && result.SavedSession && snapshot?.Messages.Count == 4 ? 0 : 1;

static JsonElement Schema(string json)
{
    using var document = JsonDocument.Parse(json);
    return document.RootElement.Clone();
}

internal sealed class ConsoleTauLogSink : ITauLogSink
{
    public void Log(TauLogEvent evt)
    {
        evt.Fields.TryGetValue("correlationId", out var correlationId);
        evt.Fields.TryGetValue("sessionId", out var sessionId);
        evt.Fields.TryGetValue("messageId", out var messageId);
        evt.Fields.TryGetValue("toolName", out var toolName);
        evt.Fields.TryGetValue("failureKind", out var failureKind);

        Console.WriteLine(
            $"log: {evt.Category}/{evt.Event} correlationId={correlationId} sessionId={sessionId} messageId={messageId} toolName={toolName} failureKind={failureKind}");
    }
}
