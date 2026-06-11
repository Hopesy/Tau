using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

/// <summary>
/// Implements the <c>--export &lt;file.jsonl&gt; [output.html]</c> CLI flow, mirroring upstream
/// <c>core/export-html/index.ts</c> <c>exportFromFile</c>: read a JSONL session file, render it to a
/// standalone HTML transcript and return the written path. Output defaults to
/// <c>pi-session-&lt;input&gt;.html</c> when no explicit output path is supplied.
/// </summary>
internal static class CodingAgentSessionFileExporter
{
    public sealed record Result(bool Success, string? OutputPath, string? ErrorMessage);

    public static Result Export(string inputPath, string? outputPath)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            return new Result(false, null, "--export requires a session file path");
        }

        if (!File.Exists(inputPath))
        {
            return new Result(false, null, $"File not found: {inputPath}");
        }

        if (!CodingAgentTreeSessionStore.IsJsonlPath(inputPath))
        {
            return new Result(false, null, $"--export expects a .jsonl session file: {inputPath}");
        }

        try
        {
            var controller = CodingAgentTreeSessionController.OpenOrCreate(inputPath);
            var snapshot = controller.LoadSnapshot().ToFlatSnapshot();
            var summary = controller.GetSummary();
            var sessionJsonl = controller.ExportCurrentBranchText();

            var resolvedOutput = string.IsNullOrWhiteSpace(outputPath)
                ? $"pi-session-{Path.GetFileNameWithoutExtension(inputPath)}.html"
                : outputPath;

            var written = CodingAgentHtmlSessionExporter.Export(
                resolvedOutput,
                snapshot.Messages,
                snapshot.Provider,
                snapshot.Model,
                snapshot.Name,
                summary,
                sessionJsonl);
            return new Result(true, written, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or InvalidOperationException or System.Text.Json.JsonException)
        {
            return new Result(false, null, ex.Message);
        }
    }
}
