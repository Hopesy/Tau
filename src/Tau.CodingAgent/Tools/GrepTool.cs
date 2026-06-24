using System.Diagnostics;
using System.Text.Json;
using Tau.AgentCore;
using Tau.Ai;

namespace Tau.CodingAgent.Tools;

public sealed class GrepTool : IAgentTool
{
    public string Name => "grep";
    public string Label => "Grep";
    public string Description => "Search for a regex pattern in files. Returns matching file paths or content.";

    public JsonElement ParameterSchema { get; } = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "pattern": { "type": "string", "description": "Regex pattern to search for" },
                "path": { "type": "string", "description": "File or directory to search in" },
                "glob": { "type": "string", "description": "Glob filter for files (e.g., *.cs)" },
                "include_content": { "type": "boolean", "description": "Show matching lines (default: false, shows file paths only)" }
            },
            "required": ["pattern"]
        }
        """).RootElement.Clone();

    public async Task<ToolResult> ExecuteAsync(
        string toolCallId, JsonElement args, CancellationToken ct, Func<ToolUpdate, Task>? onUpdate)
    {
        var pattern = args.GetProperty("pattern").GetString()!;
        var path = args.TryGetProperty("path", out var p) ? p.GetString() : ".";
        var glob = args.TryGetProperty("glob", out var g) ? g.GetString() : null;
        var includeContent = args.TryGetProperty("include_content", out var ic) && ic.GetBoolean();

        path ??= ".";

        var rgArgs = includeContent ? $"-n \"{pattern}\"" : $"-l \"{pattern}\"";
        if (glob is not null) rgArgs += $" --glob \"{glob}\"";
        rgArgs += $" {path}";

        var psi = new ProcessStartInfo
        {
            FileName = "rg",
            Arguments = rgArgs,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return FallbackGrep(pattern, path, includeContent, ct);

            var output = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(output))
                return new ToolResult([new TextContent("No matches found.")]);

            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length > 250)
            {
                output = string.Join("\n", lines.Take(250));
                output += $"\n... ({lines.Length - 250} more results)";
            }

            return new ToolResult([new TextContent(output)]);
        }
        catch
        {
            return FallbackGrep(pattern, path, includeContent, ct);
        }
    }

    private static ToolResult FallbackGrep(string pattern, string path, bool includeContent, CancellationToken ct)
    {
        try
        {
            var regex = new System.Text.RegularExpressions.Regex(pattern,
                System.Text.RegularExpressions.RegexOptions.Compiled, TimeSpan.FromSeconds(5));

            var results = new List<string>();
            var searchPath = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? ".";
            var searchPattern = Directory.Exists(path) ? "*" : Path.GetFileName(path);

            foreach (var file in Directory.EnumerateFiles(searchPath, searchPattern,
                new EnumerationOptions { RecurseSubdirectories = true }))
            {
                ct.ThrowIfCancellationRequested();
                if (results.Count >= 250) break;

                try
                {
                    var lines = File.ReadLines(file);
                    var lineNum = 0;
                    var matched = false;
                    foreach (var line in lines)
                    {
                        lineNum++;
                        if (regex.IsMatch(line))
                        {
                            if (includeContent)
                                results.Add($"{file}:{lineNum}:{line}");
                            else if (!matched)
                            {
                                results.Add(file);
                                matched = true;
                            }
                            if (!includeContent) break;
                        }
                    }
                }
                catch { /* skip binary/unreadable files */ }
            }

            return results.Count == 0
                ? new ToolResult([new TextContent("No matches found.")])
                : new ToolResult([new TextContent(string.Join("\n", results))]);
        }
        catch (Exception ex)
        {
            return new ToolResult([new TextContent($"Grep failed: {ex.Message}")], IsError: true);
        }
    }
}
