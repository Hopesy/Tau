using Tau.Ai;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentSessionFileExporterTests
{
    [Fact]
    public void Export_RendersJsonlSessionToHtmlWithDefaultOutputName()
    {
        var input = CreateTempJsonlPath("tau-export-default");
        var store = new CodingAgentTreeSessionStore(input);
        store.AppendSessionInfo("export session", "openai", "gpt-5.4");
        store.AppendMessages([new UserMessage("hello from export")], 0);

        try
        {
            var result = CodingAgentSessionFileExporter.Export(input, outputPath: null);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.NotNull(result.OutputPath);
            Assert.EndsWith(
                $"pi-session-{Path.GetFileNameWithoutExtension(input)}.html",
                result.OutputPath!,
                StringComparison.Ordinal);
            Assert.True(File.Exists(result.OutputPath));
            var html = File.ReadAllText(result.OutputPath!);
            Assert.Contains("hello from export", html, StringComparison.Ordinal);
            Assert.Contains("<html", html, StringComparison.OrdinalIgnoreCase);

            File.Delete(result.OutputPath!);
        }
        finally
        {
            TryDelete(input);
        }
    }

    [Fact]
    public void Export_HonorsExplicitOutputPath()
    {
        var input = CreateTempJsonlPath("tau-export-explicit");
        var output = Path.Combine(Path.GetTempPath(), $"tau-export-explicit-{Guid.NewGuid():N}.html");
        var store = new CodingAgentTreeSessionStore(input);
        store.AppendSessionInfo("export session", "openai", "gpt-5.4");
        store.AppendMessages([new UserMessage("explicit output prompt")], 0);

        try
        {
            var result = CodingAgentSessionFileExporter.Export(input, output);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(Path.GetFullPath(output), result.OutputPath);
            Assert.True(File.Exists(output));
        }
        finally
        {
            TryDelete(input);
            TryDelete(output);
        }
    }

    [Fact]
    public void Export_ReturnsErrorForMissingFile()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"tau-export-missing-{Guid.NewGuid():N}.jsonl");

        var result = CodingAgentSessionFileExporter.Export(missing, outputPath: null);

        Assert.False(result.Success);
        Assert.Null(result.OutputPath);
        Assert.Contains("File not found", result.ErrorMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public void Export_ReturnsErrorForNonJsonlPath()
    {
        var input = Path.Combine(Path.GetTempPath(), $"tau-export-nonjsonl-{Guid.NewGuid():N}.txt");
        File.WriteAllText(input, "not a session");

        try
        {
            var result = CodingAgentSessionFileExporter.Export(input, outputPath: null);

            Assert.False(result.Success);
            Assert.Contains(".jsonl session file", result.ErrorMessage!, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(input);
        }
    }

    [Fact]
    public void Export_ReturnsErrorForInvalidSessionHeader()
    {
        var input = CreateTempJsonlPath("tau-export-invalid");
        File.WriteAllText(input, "{\"type\":\"not-a-session\"}\n");

        try
        {
            var result = CodingAgentSessionFileExporter.Export(input, outputPath: null);

            Assert.False(result.Success);
            Assert.NotNull(result.ErrorMessage);
        }
        finally
        {
            TryDelete(input);
        }
    }

    private static string CreateTempJsonlPath(string prefix) =>
        Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}.jsonl");

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }
}
