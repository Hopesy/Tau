using System.Text.Json;
using Tau.AgentCore;
using Tau.AgentCore.Platform;
using Tau.Ai;
using Tau.Ai.Observability;
using Tau.Ai.Providers;
using Tau.Ai.Providers.Faux;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
var sessions = new InMemoryAgentSessionStore();
var logSink = new ConsoleTauLogSink();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.MapPost("/agent", async (AgentPromptRequest request, CancellationToken cancellationToken) =>
{
    var registry = new ProviderRegistry();
    var faux = Faux.Register(registry);
    faux.SetResponses([
        Faux.AssistantMessage(
            [Faux.ToolCall("echo", new Dictionary<string, object?> { ["text"] = request.Prompt }, "http-call")],
            stopReason: StopReason.ToolUse),
        Faux.AssistantMessage($"http example complete: {request.Prompt}")
    ]);

    var sessionId = string.IsNullOrWhiteSpace(request.SessionId)
        ? "http-example"
        : request.SessionId.Trim();
    var agent = AgentApplication.CreateBuilder()
        .UseProviderRegistry(registry)
        .UseModel(faux.GetModel())
        .UseSystemPrompt("You are a concise HTTP example agent.")
        .UseSessionId(sessionId)
        .UseSessionStore(sessions)
        .UseLogSink(logSink)
        .AddTool(
            "echo",
            "Echo",
            "Echoes the request prompt.",
            Schema("""
            {
                "type": "object",
                "properties": {
                    "text": { "type": "string" }
                },
                "required": ["text"]
            }
            """),
            (context, _) => new ToolResult([
                new TextContent(context.Arguments.GetProperty("text").GetString() ?? string.Empty)
            ]))
        .Build();

    var result = await agent.PromptAsync(request.Prompt, cancellationToken);

    return Results.Ok(new AgentPromptResponse(
        result.AssistantText ?? string.Empty,
        result.Messages.Count,
        result.ToolStarts.Count,
        result.ToolEnds.Count,
        result.StopReason?.ToString() ?? string.Empty,
        result.IsSuccess,
        result.SavedSession,
        result.LogContext.CorrelationId ?? string.Empty,
        result.LogContext.SessionId ?? string.Empty,
        result.LogContext.MessageId ?? string.Empty));
});

await app.RunAsync();

static JsonElement Schema(string json)
{
    using var document = JsonDocument.Parse(json);
    return document.RootElement.Clone();
}

internal sealed record AgentPromptRequest(string Prompt, string? SessionId = null);

internal sealed record AgentPromptResponse(
    string AssistantText,
    int MessageCount,
    int ToolStartCount,
    int ToolEndCount,
    string StopReason,
    bool Success,
    bool SavedSession,
    string CorrelationId,
    string SessionId,
    string MessageId);

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
