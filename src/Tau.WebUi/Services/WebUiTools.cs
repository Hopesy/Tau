using System.Text.Json;
using Tau.Agent;
using Tau.Ai;
using Tau.WebUi.Contracts;

namespace Tau.WebUi.Services;

internal static class WebUiTools
{
    public static IAgentTool[] CreateSessionTools(
        string sessionId,
        WebArtifactService artifacts,
        WebUiJavaScriptReplBridge? replBridge = null)
    {
        var tools = new List<IAgentTool>
        {
            new WebUiArtifactsTool(sessionId, artifacts)
        };

        if (replBridge is not null)
        {
            tools.Add(new WebUiJavaScriptReplTool(sessionId, replBridge));
        }

        return tools.ToArray();
    }

    public static WebUiJavaScriptReplTool CreateJavaScriptReplTool(string sessionId, WebUiJavaScriptReplBridge bridge) =>
        new(sessionId, bridge);
}

internal static class WebUiToolPrompts
{
    public static string JavaScriptReplToolDescription(IReadOnlyList<string> runtimeProviderDescriptions) =>
        $"""
        # JavaScript REPL

        Execute JavaScript code in a sandboxed browser environment with browser APIs.

        Use this tool for calculations, data transformations, JavaScript snippets, browser API experiments,
        and generating files from data. The execution environment supports async JavaScript, DOM, Canvas,
        WebGL, Fetch, Crypto, and remote ES module imports such as `await import("https://esm.run/xlsx")`.

        Objects on global scope do not persist between calls. Use artifact helper functions to persist data
        between calls, and use attachment helper functions to read user-uploaded files.

        Console output is captured for the model. Returned downloadable files are one-time user downloads.

        Helper functions available in the sandbox:

        {string.Join("\n\n", runtimeProviderDescriptions)}
        """;

    public static string ArtifactsToolDescription(IReadOnlyList<string> runtimeProviderDescriptions) =>
        $"""
        # Artifacts

        Create and manage persistent files that live alongside the conversation.

        Use artifacts when authoring files for the user: markdown notes, HTML applications, JavaScript,
        CSS, JSON, CSV, SVG, and other text-based files. Prefer targeted update operations over rewriting
        entire files.

        Input commands:
        - create: create a new file and fail if it already exists.
        - update: replace the first old_str occurrence with new_str.
        - rewrite: replace an existing file in full.
        - get: retrieve file content.
        - delete: delete an artifact.
        - logs: get console logs from an HTML artifact.

        HTML artifacts can read existing artifacts and attachments through the helper functions below.

        Helper functions available in HTML artifact sandbox:

        {string.Join("\n\n", runtimeProviderDescriptions)}
        """;

    public const string ArtifactsRuntimeProviderReadWriteDescription = """
        ### Artifacts Storage

        Create, read, update, and delete files in artifact storage.

        Functions:
        - listArtifacts() returns Promise<string[]>
        - getArtifact(filename) returns Promise<string | object>; JSON files are parsed to objects
        - createOrUpdateArtifact(filename, content, mimeType?) returns Promise<void>
        - createArtifact(filename, content, mimeType?) returns Promise<void>
        - updateArtifact(filename, old_str, new_str) returns Promise<void>
        - rewriteArtifact(filename, content, mimeType?) returns Promise<void>
        - deleteArtifact(filename) returns Promise<void>
        - htmlArtifactLogs(filename) returns Promise<string>
        """;

    public const string ArtifactsRuntimeProviderReadOnlyDescription = """
        ### Artifacts Storage

        Read files from artifact storage.

        Functions:
        - listArtifacts() returns Promise<string[]>
        - getArtifact(filename) returns Promise<string | object>; JSON files are parsed to objects
        - htmlArtifactLogs(filename) returns Promise<string>
        """;

    public const string AttachmentsRuntimeDescription = """
        ### User Attachments

        Read files the user uploaded to the conversation.

        Functions:
        - listAttachments() returns array of { id, fileName, mimeType, size }
        - readTextAttachment(id) returns string
        - readBinaryAttachment(id) returns Uint8Array
        """;

    public const string FileDownloadRuntimeDescription = """
        ### Downloadable Files

        Create one-time downloadable files for the user.

        Functions:
        - returnDownloadableFile(fileName, content, mimeType?) returns Promise<{ fileName, mimeType }>
        """;
}

internal sealed class WebUiArtifactsTool : IAgentTool
{
    private readonly string _sessionId;
    private readonly WebArtifactService _artifacts;

    public WebUiArtifactsTool(string sessionId, WebArtifactService artifacts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(artifacts);
        _sessionId = sessionId;
        _artifacts = artifacts;
    }

    public string Name => "artifacts";
    public string Label => "Artifacts";
    public string Description => WebUiToolPrompts.ArtifactsToolDescription(
        [
            WebUiToolPrompts.AttachmentsRuntimeDescription,
            WebUiToolPrompts.ArtifactsRuntimeProviderReadOnlyDescription
        ]);

    public JsonElement ParameterSchema { get; } = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "command": {
              "type": "string",
              "enum": ["create", "update", "rewrite", "get", "delete", "logs"],
              "description": "The operation to perform"
            },
            "filename": {
              "type": "string",
              "description": "Filename including extension, for example 'index.html' or 'data.json'"
            },
            "content": {
              "type": "string",
              "description": "File content for create or rewrite commands"
            },
            "old_str": {
              "type": "string",
              "description": "String to replace for update command"
            },
            "new_str": {
              "type": "string",
              "description": "Replacement string for update command"
            }
          },
          "required": ["command", "filename"]
        }
        """).RootElement.Clone();

    public Task<ToolResult> ExecuteAsync(
        string toolCallId,
        JsonElement args,
        CancellationToken ct,
        Func<ToolUpdate, Task>? onUpdate = null)
    {
        ct.ThrowIfCancellationRequested();
        var command = RequiredString(args, "command");
        var filename = RequiredString(args, "filename");
        var request = new WebRuntimeMessageRequest(
            "artifact-operation",
            MessageId: string.IsNullOrWhiteSpace(toolCallId) ? Guid.NewGuid().ToString("N") : toolCallId,
            SandboxId: $"tool-{_sessionId}",
            Action: command.Equals("logs", StringComparison.OrdinalIgnoreCase) ? "htmlArtifactLogs" : command,
            Filename: filename,
            Content: OptionalString(args, "content"),
            OldString: OptionalString(args, "old_str"),
            NewString: OptionalString(args, "new_str"));

        var response = _artifacts.HandleRuntimeMessage(_sessionId, request);
        if (!TryGetSuccess(response, out var success))
        {
            success = false;
        }

        var output = success
            ? FormatSuccess(command, filename, response)
            : $"Error: {GetString(response, "error") ?? "Artifact operation failed."}";
        return Task.FromResult(new ToolResult([new TextContent(output)], IsError: !success));
    }

    private static string FormatSuccess(string command, string filename, IDictionary<string, object?> response)
    {
        var result = response.TryGetValue("result", out var value) ? value : null;
        return command switch
        {
            "create" => $"Created file {filename}",
            "update" => $"Updated file {filename}",
            "rewrite" => $"Rewrote file {filename}",
            "get" => Convert.ToString(result, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            "delete" => $"Deleted file {filename}",
            "logs" => Convert.ToString(result, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            _ => Convert.ToString(result, System.Globalization.CultureInfo.InvariantCulture) ?? $"Completed {command} for {filename}"
        };
    }

    private static string RequiredString(JsonElement args, string propertyName)
    {
        if (args.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value.GetString()))
        {
            return value.GetString()!;
        }

        throw new InvalidOperationException($"{propertyName} is required.");
    }

    private static string? OptionalString(JsonElement args, string propertyName) =>
        args.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryGetSuccess(IDictionary<string, object?> response, out bool success)
    {
        if (response.TryGetValue("success", out var value) && value is bool boolValue)
        {
            success = boolValue;
            return true;
        }

        success = false;
        return false;
    }

    private static string? GetString(IDictionary<string, object?> response, string key) =>
        response.TryGetValue(key, out var value)
            ? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
            : null;
}

internal sealed class WebUiJavaScriptReplTool : IAgentTool
{
    private readonly string _sessionId;
    private readonly WebUiJavaScriptReplBridge _bridge;

    public WebUiJavaScriptReplTool(string sessionId, WebUiJavaScriptReplBridge bridge)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(bridge);
        _sessionId = sessionId;
        _bridge = bridge;
    }

    public string Name => "javascript_repl";
    public string Label => "JavaScript REPL";
    public string Description => WebUiToolPrompts.JavaScriptReplToolDescription(
        [
            WebUiToolPrompts.AttachmentsRuntimeDescription,
            WebUiToolPrompts.ArtifactsRuntimeProviderReadWriteDescription,
            WebUiToolPrompts.FileDownloadRuntimeDescription
        ]);

    public JsonElement ParameterSchema { get; } = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "title": {
              "type": "string",
              "description": "Brief active title describing what the code snippet tries to achieve"
            },
            "code": {
              "type": "string",
              "description": "JavaScript code to execute"
            }
          },
          "required": ["title", "code"]
        }
        """).RootElement.Clone();

    public async Task<ToolResult> ExecuteAsync(
        string toolCallId,
        JsonElement args,
        CancellationToken ct,
        Func<ToolUpdate, Task>? onUpdate = null)
    {
        ct.ThrowIfCancellationRequested();
        var title = RequiredString(args, "title");
        var code = RequiredString(args, "code");
        var result = await _bridge.ExecuteAsync(_sessionId, toolCallId, title, code, ct).ConfigureAwait(false);
        return new ToolResult(
            [new TextContent(result.Output)],
            IsError: result.IsError,
            Details: new WebJavaScriptReplToolDetails(result.Files));
    }

    private static string RequiredString(JsonElement args, string propertyName)
    {
        if (args.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value.GetString()))
        {
            return value.GetString()!;
        }

        throw new InvalidOperationException($"{propertyName} is required.");
    }
}
