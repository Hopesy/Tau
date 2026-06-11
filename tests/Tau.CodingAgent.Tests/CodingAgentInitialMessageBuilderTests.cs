using Tau.Ai;
using Tau.CodingAgent.Runtime;
using Tau.Tui.Rendering;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentInitialMessageBuilderTests
{
    [Fact]
    public void Parse_SeparatesOptionsFilesAndMessages()
    {
        var parsed = CodingAgentCliArguments.Parse(
            [
                "--mode",
                "text",
                "--theme",
                "dark.json",
                "--provider",
                "openai",
                "--model=gpt-5.4",
                "--system-prompt",
                "custom system",
                "@prompt.md",
                "first prompt",
                "--extension",
                "plugin.json",
                "--unknown",
                "value",
                "--print",
                "--json",
                "second prompt"
            ]);

        Assert.True(parsed.PrintMode);
        Assert.False(parsed.RpcMode);
        Assert.Equal(["dark.json"], parsed.ThemePaths);
        Assert.Equal("openai", parsed.Provider);
        Assert.Equal("gpt-5.4", parsed.Model);
        Assert.Equal("custom system", parsed.SystemPrompt);
        Assert.Equal(["prompt.md"], parsed.FileArguments);
        Assert.Equal(["first prompt", "second prompt"], parsed.Messages);
        Assert.True(parsed.ExtensionFlags.TryGetValue("unknown", out var unknownValue));
        Assert.Equal("value", unknownValue);
    }

    [Fact]
    public void Parse_CapturesUnknownFlagsAsExtensionFlags()
    {
        var parsed = CodingAgentCliArguments.Parse(
            [
                "--plan",
                "--mode-name=fast",
                "--label",
                "release",
                "--toggle"
            ]);

        Assert.Equal(4, parsed.ExtensionFlags.Count);
        Assert.True(parsed.ExtensionFlags.TryGetValue("plan", out var plan));
        Assert.Null(plan);
        Assert.True(parsed.ExtensionFlags.TryGetValue("mode-name", out var modeName));
        Assert.Equal("fast", modeName);
        Assert.True(parsed.ExtensionFlags.TryGetValue("label", out var label));
        Assert.Equal("release", label);
        Assert.True(parsed.ExtensionFlags.TryGetValue("toggle", out var toggle));
        Assert.Null(toggle);
        Assert.Empty(parsed.Messages);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public void Parse_RecognizesHelpFlag(string flag)
    {
        var parsed = CodingAgentCliArguments.Parse([flag]);

        Assert.True(parsed.Help);
        Assert.False(parsed.Version);
        Assert.Empty(parsed.ExtensionFlags);
        Assert.Empty(parsed.Messages);
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("-v")]
    public void Parse_RecognizesVersionFlag(string flag)
    {
        var parsed = CodingAgentCliArguments.Parse([flag]);

        Assert.True(parsed.Version);
        Assert.False(parsed.Help);
        Assert.Empty(parsed.ExtensionFlags);
        Assert.Empty(parsed.Messages);
    }

    [Fact]
    public void Parse_DoesNotTreatHelpOrVersionAsExtensionFlags()
    {
        var parsed = CodingAgentCliArguments.Parse(["--help", "--version", "extra prompt"]);

        Assert.True(parsed.Help);
        Assert.True(parsed.Version);
        Assert.Empty(parsed.ExtensionFlags);
        Assert.Equal(["extra prompt"], parsed.Messages);
    }

    [Fact]
    public void BuildHelpText_IncludesUsageCommandsAndOptions()
    {
        var help = CodingAgentCliHelp.BuildHelpText("pi", []);

        Assert.Contains("pi - AI coding assistant", help, StringComparison.Ordinal);
        Assert.Contains("Usage:", help, StringComparison.Ordinal);
        Assert.Contains("pi [options] [@files...] [messages...]", help, StringComparison.Ordinal);
        Assert.Contains("pi install <source>", help, StringComparison.Ordinal);
        Assert.Contains("--provider <name>", help, StringComparison.Ordinal);
        Assert.Contains("--help, -h", help, StringComparison.Ordinal);
        Assert.Contains("--version, -v", help, StringComparison.Ordinal);
        Assert.DoesNotContain("Extension CLI Flags:", help, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildHelpText_ListsRegisteredExtensionFlags()
    {
        var flags = new[]
        {
            new CodingAgentExtensionFlag(
                Name: "plan",
                Description: "Enable plan mode",
                Type: "boolean",
                DefaultValue: null,
                FilePath: "/ext/plan.js",
                Scope: "user",
                Runtime: "javascript"),
            new CodingAgentExtensionFlag(
                Name: "label",
                Description: string.Empty,
                Type: "string",
                DefaultValue: null,
                FilePath: "/ext/label.js",
                Scope: "project",
                Runtime: "javascript"),
        };

        var help = CodingAgentCliHelp.BuildHelpText("pi", flags);

        Assert.Contains("Extension CLI Flags:", help, StringComparison.Ordinal);
        Assert.Contains("--plan", help, StringComparison.Ordinal);
        Assert.Contains("Enable plan mode", help, StringComparison.Ordinal);
        Assert.Contains("--label <value>", help, StringComparison.Ordinal);
        Assert.Contains("Registered by /ext/label.js", help, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_MapsToolNamesToTauBuiltInTools()
    {
        var parsed = CodingAgentCliArguments.Parse(["--tools", "read,bash,find,ls"]);

        Assert.False(parsed.NoTools);
        Assert.NotNull(parsed.Tools);
        Assert.Equal(["read_file", "shell", "glob", "ls"], parsed.Tools);
        Assert.Empty(parsed.Diagnostics);
    }

    [Fact]
    public void Parse_AcceptsInlineToolsValueAndDeduplicates()
    {
        var parsed = CodingAgentCliArguments.Parse(["--tools=read,read,edit"]);

        Assert.NotNull(parsed.Tools);
        Assert.Equal(["read_file", "edit_file"], parsed.Tools);
        Assert.Empty(parsed.Diagnostics);
    }

    [Fact]
    public void Parse_WarnsOnUnknownToolName()
    {
        var parsed = CodingAgentCliArguments.Parse(["--tools", "read,bogus"]);

        Assert.NotNull(parsed.Tools);
        Assert.Equal(["read_file"], parsed.Tools);
        var diagnostic = Assert.Single(parsed.Diagnostics);
        Assert.Equal("warning", diagnostic.Type);
        Assert.Contains("Unknown tool \"bogus\"", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RecognizesNoToolsFlag()
    {
        var parsed = CodingAgentCliArguments.Parse(["--no-tools"]);

        Assert.True(parsed.NoTools);
        Assert.Null(parsed.Tools);
        Assert.Empty(parsed.Diagnostics);
    }

    [Fact]
    public void Parse_NoToolsWithExplicitToolsKeepsSelection()
    {
        var parsed = CodingAgentCliArguments.Parse(["--no-tools", "--tools", "read"]);

        Assert.True(parsed.NoTools);
        Assert.NotNull(parsed.Tools);
        Assert.Equal(["read_file"], parsed.Tools);
    }

    [Fact]
    public void Parse_ReportsUnknownSingleDashOptionAsError()
    {
        var parsed = CodingAgentCliArguments.Parse(["-x", "prompt"]);

        var diagnostic = Assert.Single(parsed.Diagnostics);
        Assert.Equal("error", diagnostic.Type);
        Assert.Equal("Unknown option: -x", diagnostic.Message);
        Assert.Equal(["prompt"], parsed.Messages);
    }

    [Theory]
    [InlineData("--thinking", "high", "high")]
    [InlineData("--thinking", "OFF", "off")]
    [InlineData("--thinking=xhigh", null, "xhigh")]
    public void Parse_AcceptsValidThinkingLevel(string flag, string? value, string expected)
    {
        var args = value is null ? new[] { flag } : [flag, value];
        var parsed = CodingAgentCliArguments.Parse(args);

        Assert.Equal(expected, parsed.Thinking);
        Assert.Empty(parsed.Diagnostics);
    }

    [Fact]
    public void Parse_WarnsOnInvalidThinkingLevel()
    {
        var parsed = CodingAgentCliArguments.Parse(["--thinking", "bogus"]);

        Assert.Null(parsed.Thinking);
        var diagnostic = Assert.Single(parsed.Diagnostics);
        Assert.Equal("warning", diagnostic.Type);
        Assert.Contains("Invalid thinking level \"bogus\"", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("off, minimal, low, medium, high, xhigh", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RecognizesOfflineFlag()
    {
        var parsed = CodingAgentCliArguments.Parse(["--offline", "prompt"]);

        Assert.True(parsed.Offline);
        Assert.Empty(parsed.Diagnostics);
        Assert.Empty(parsed.ExtensionFlags);
        Assert.Equal(["prompt"], parsed.Messages);
    }

    [Fact]
    public void Parse_OfflineDefaultsToFalse()
    {
        var parsed = CodingAgentCliArguments.Parse(["prompt"]);

        Assert.False(parsed.Offline);
    }

    [Fact]
    public void ResolveCommandName_PrefersEnvironmentOverride()
    {
        var original = Environment.GetEnvironmentVariable("TAU_CODING_AGENT_COMMAND_NAME");
        try
        {
            Environment.SetEnvironmentVariable("TAU_CODING_AGENT_COMMAND_NAME", "tau-coding-agent");
            Assert.Equal("tau-coding-agent", CodingAgentCliHelp.ResolveCommandName());

            Environment.SetEnvironmentVariable("TAU_CODING_AGENT_COMMAND_NAME", null);
            Assert.Equal("pi", CodingAgentCliHelp.ResolveCommandName());
        }
        finally
        {
            Environment.SetEnvironmentVariable("TAU_CODING_AGENT_COMMAND_NAME", original);
        }
    }

    [Fact]
    public void ResolveVersion_ReturnsSemanticVersion()
    {
        var version = CodingAgentCliHelp.ResolveVersion();

        Assert.Matches(@"^\d+\.\d+\.\d+", version);
    }

    [Fact]
    public void Parse_CollectsRepeatableAppendSystemPrompt()
    {
        var parsed = CodingAgentCliArguments.Parse(
            [
                "--append-system-prompt",
                "first append",
                "--append-system-prompt=second append",
                "message"
            ]);

        Assert.Equal(["first append", "second append"], parsed.AppendSystemPrompt);
        Assert.Equal(["message"], parsed.Messages);
    }

    [Fact]
    public async Task BuildAsync_MergesTextFilesAndFirstMessage()
    {
        using var temp = TempDirectory.Create();
        var file = Path.Combine(temp.Path, "prompt.txt");
        await File.WriteAllTextAsync(file, "file body");

        var prompt = await CodingAgentInitialMessageBuilder.BuildAsync(
            ["user request", "later message"],
            ["prompt.txt"],
            options: new CodingAgentInitialMessageOptions(WorkingDirectory: temp.Path));

        Assert.NotNull(prompt);
        Assert.Empty(prompt!.Images);
        Assert.Contains($"<file name=\"{file}\">", prompt.Text, StringComparison.Ordinal);
        Assert.Contains("file body", prompt.Text, StringComparison.Ordinal);
        Assert.EndsWith("user request", prompt.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildAsync_PrependsRedirectedStdinBeforeFilesAndMessage()
    {
        using var temp = TempDirectory.Create();
        var file = Path.Combine(temp.Path, "prompt.txt");
        await File.WriteAllTextAsync(file, "file body");

        var prompt = await CodingAgentInitialMessageBuilder.BuildAsync(
            ["user request"],
            ["prompt.txt"],
            stdinContent: "stdin body\n",
            options: new CodingAgentInitialMessageOptions(WorkingDirectory: temp.Path));

        Assert.NotNull(prompt);
        Assert.StartsWith("stdin body\n", prompt!.Text, StringComparison.Ordinal);
        var fileIndex = prompt.Text.IndexOf("<file", StringComparison.Ordinal);
        var requestIndex = prompt.Text.IndexOf("user request", StringComparison.Ordinal);
        Assert.True(fileIndex >= "stdin body\n".Length);
        Assert.True(requestIndex > fileIndex);
    }

    [Fact]
    public async Task BuildAsync_ReadsPngFilesAsImageContent()
    {
        using var temp = TempDirectory.Create();
        var image = Path.Combine(temp.Path, "pixel.png");
        var bytes = CreateTinyPngBytes();
        await File.WriteAllBytesAsync(image, bytes);

        var prompt = await CodingAgentInitialMessageBuilder.BuildAsync(
            ["describe"],
            ["@pixel.png"],
            options: new CodingAgentInitialMessageOptions(WorkingDirectory: temp.Path));

        Assert.NotNull(prompt);
        Assert.Contains($"<file name=\"{image}\"></file>", prompt!.Text, StringComparison.Ordinal);
        var content = Assert.Single(prompt.Images);
        Assert.Equal("image/png", content.MimeType);
        Assert.Equal(Convert.ToBase64String(bytes), content.Data);
    }

    [Fact]
    public async Task BuildAsync_BlockImagesReplacesAttachmentWithTextNotice()
    {
        using var temp = TempDirectory.Create();
        var image = Path.Combine(temp.Path, "pixel.png");
        await File.WriteAllBytesAsync(image, CreateTinyPngBytes());

        var prompt = await CodingAgentInitialMessageBuilder.BuildAsync(
            ["describe"],
            ["pixel.png"],
            options: new CodingAgentInitialMessageOptions(
                BlockImages: true,
                WorkingDirectory: temp.Path));

        Assert.NotNull(prompt);
        Assert.Empty(prompt!.Images);
        Assert.Contains("[Image blocked by settings.]", prompt.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildAsync_AutoResizesLargePngFilesAndAddsDimensionNote()
    {
        using var temp = TempDirectory.Create();
        var image = Path.Combine(temp.Path, "wide.png");
        await File.WriteAllBytesAsync(image, ImageTestData.CreatePng(2501, 10));

        var prompt = await CodingAgentInitialMessageBuilder.BuildAsync(
            ["describe"],
            ["wide.png"],
            options: new CodingAgentInitialMessageOptions(WorkingDirectory: temp.Path));

        Assert.NotNull(prompt);
        Assert.Contains(
            $"<file name=\"{image}\">[Image: original 2501x10, displayed at 2000x8. Multiply coordinates by 1.25 to map to original image.]</file>",
            prompt!.Text,
            StringComparison.Ordinal);
        var content = Assert.Single(prompt.Images);
        Assert.Equal("image/png", content.MimeType);
        Assert.Equal(new TuiImageDimensions(2000, 8), TuiTerminalImage.GetPngDimensions(content.Data));
    }

    [Fact]
    public async Task BuildAsync_SkipsEmptyFilesAndReturnsNullWhenNoPromptRemains()
    {
        using var temp = TempDirectory.Create();
        var file = Path.Combine(temp.Path, "empty.txt");
        await File.WriteAllTextAsync(file, string.Empty);

        var prompt = await CodingAgentInitialMessageBuilder.BuildAsync(
            [],
            ["empty.txt"],
            options: new CodingAgentInitialMessageOptions(WorkingDirectory: temp.Path));

        Assert.Null(prompt);
    }

    private static byte[] CreateTinyPngBytes() =>
    [
        0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a,
        0x00, 0x00, 0x00, 0x0d, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x06, 0x00, 0x00, 0x00, 0x1f, 0x15, 0xc4,
        0x89, 0x00, 0x00, 0x00, 0x0a, 0x49, 0x44, 0x41,
        0x54, 0x78, 0x9c, 0x63, 0x00, 0x01, 0x00, 0x00,
        0x05, 0x00, 0x01, 0x0d, 0x0a, 0x2d, 0xb4, 0x00,
        0x00, 0x00, 0x00, 0x49, 0x45, 0x4e, 0x44, 0xae,
        0x42, 0x60, 0x82
    ];

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path) => Path = path;

        public string Path { get; }

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tau-initial-message-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
