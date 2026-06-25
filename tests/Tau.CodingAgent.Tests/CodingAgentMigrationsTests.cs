using System.Text.Json;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentMigrationsTests
{
    [Fact]
    public void Run_MigratesLegacyOauthAndSettingsApiKeysToAuthJson()
    {
        using var temp = TempDirectory.Create();
        var agentDir = Path.Combine(temp.Path, ".tau");
        Directory.CreateDirectory(agentDir);
        File.WriteAllText(
            Path.Combine(agentDir, "oauth.json"),
            """
            {
              "anthropic": {
                "refresh": "refresh-token",
                "access": "access-token",
                "expiresAt": "2026-01-01T00:00:00.0000000Z"
              }
            }
            """);
        File.WriteAllText(
            Path.Combine(agentDir, "settings.json"),
            """
            {
              "apiKeys": {
                "openai": "sk-openai",
                "anthropic": "sk-ignored"
              },
              "theme": "dark"
            }
            """);

        var result = CodingAgentMigrations.Run(new CodingAgentMigrationOptions(
            AgentDirectory: agentDir,
            Cwd: temp.Path,
            AuthPath: Path.Combine(agentDir, "auth.json"),
            KeybindingsPath: Path.Combine(agentDir, "coding-agent-keybindings.json")));

        Assert.Equal(["anthropic", "openai"], result.MigratedAuthProviders);
        Assert.False(File.Exists(Path.Combine(agentDir, "oauth.json")));
        Assert.True(File.Exists(Path.Combine(agentDir, "oauth.json.migrated")));

        using var auth = JsonDocument.Parse(File.ReadAllText(Path.Combine(agentDir, "auth.json")));
        Assert.Equal("oauth", auth.RootElement.GetProperty("anthropic").GetProperty("type").GetString());
        Assert.Equal("refresh-token", auth.RootElement.GetProperty("anthropic").GetProperty("refresh").GetString());
        Assert.Equal("api_key", auth.RootElement.GetProperty("openai").GetProperty("type").GetString());
        Assert.Equal("sk-openai", auth.RootElement.GetProperty("openai").GetProperty("key").GetString());

        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(agentDir, "settings.json")));
        Assert.False(settings.RootElement.TryGetProperty("apiKeys", out _));
        Assert.Equal("dark", settings.RootElement.GetProperty("theme").GetString());
    }

    [Fact]
    public void Run_LeavesLegacyAuthFilesWhenAuthJsonAlreadyExists()
    {
        using var temp = TempDirectory.Create();
        var agentDir = Path.Combine(temp.Path, ".tau");
        Directory.CreateDirectory(agentDir);
        File.WriteAllText(Path.Combine(agentDir, "auth.json"), """{"existing":{"type":"api_key","key":"keep"}}""");
        File.WriteAllText(Path.Combine(agentDir, "oauth.json"), """{"anthropic":{"access":"a","refresh":"r"}}""");

        var result = CodingAgentMigrations.Run(new CodingAgentMigrationOptions(
            AgentDirectory: agentDir,
            Cwd: temp.Path,
            AuthPath: Path.Combine(agentDir, "auth.json"),
            KeybindingsPath: Path.Combine(agentDir, "coding-agent-keybindings.json")));

        Assert.Empty(result.MigratedAuthProviders);
        Assert.True(File.Exists(Path.Combine(agentDir, "oauth.json")));
        using var auth = JsonDocument.Parse(File.ReadAllText(Path.Combine(agentDir, "auth.json")));
        Assert.True(auth.RootElement.TryGetProperty("existing", out _));
        Assert.False(auth.RootElement.TryGetProperty("anthropic", out _));
    }

    [Fact]
    public void Run_MigratesRootJsonlSessionsToProjectSessionDirectory()
    {
        using var temp = TempDirectory.Create();
        var agentDir = Path.Combine(temp.Path, "home", ".tau");
        var projectDir = Path.Combine(temp.Path, "project");
        Directory.CreateDirectory(agentDir);
        Directory.CreateDirectory(projectDir);
        var sessionPath = Path.Combine(agentDir, "session-a.jsonl");
        var encodedProjectDir = JsonEncodedText.Encode(projectDir).ToString();
        File.WriteAllText(
            sessionPath,
            $$"""
            {"type":"session","version":3,"id":"session-a","timestamp":"2026-01-01T00:00:00.0000000Z","cwd":"{{encodedProjectDir}}"}
            {"type":"message","id":"entry-a","timestamp":"2026-01-01T00:00:01.0000000Z"}
            """);

        CodingAgentMigrations.Run(new CodingAgentMigrationOptions(
            AgentDirectory: agentDir,
            Cwd: projectDir,
            AuthPath: Path.Combine(agentDir, "auth.json"),
            KeybindingsPath: Path.Combine(agentDir, "coding-agent-keybindings.json")));

        var migratedPath = Path.Combine(projectDir, ".tau", "coding-agent-sessions", "session-a.jsonl");
        Assert.False(File.Exists(sessionPath));
        Assert.True(File.Exists(migratedPath));
        Assert.Contains("\"session-a\"", File.ReadAllText(migratedPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Run_MigratesCommandsToolsAndReportsDeprecatedExtensionDirectories()
    {
        using var temp = TempDirectory.Create();
        var agentDir = Path.Combine(temp.Path, "home", ".tau");
        var projectDir = Path.Combine(temp.Path, "project");
        Directory.CreateDirectory(Path.Combine(agentDir, "commands"));
        Directory.CreateDirectory(Path.Combine(agentDir, "tools"));
        Directory.CreateDirectory(Path.Combine(agentDir, "hooks"));
        Directory.CreateDirectory(Path.Combine(projectDir, ".tau", "commands"));
        File.WriteAllText(Path.Combine(agentDir, "commands", "review.md"), "review");
        File.WriteAllText(Path.Combine(projectDir, ".tau", "commands", "fix.md"), "fix");
        File.WriteAllText(Path.Combine(agentDir, "tools", "rg.exe"), "managed");
        File.WriteAllText(Path.Combine(agentDir, "tools", "custom.ps1"), "custom");

        var result = CodingAgentMigrations.Run(new CodingAgentMigrationOptions(
            AgentDirectory: agentDir,
            Cwd: projectDir,
            AuthPath: Path.Combine(agentDir, "auth.json"),
            KeybindingsPath: Path.Combine(agentDir, "coding-agent-keybindings.json")));

        Assert.False(Directory.Exists(Path.Combine(agentDir, "commands")));
        Assert.True(File.Exists(Path.Combine(agentDir, "prompts", "review.md")));
        Assert.False(Directory.Exists(Path.Combine(projectDir, ".tau", "commands")));
        Assert.True(File.Exists(Path.Combine(projectDir, ".tau", "prompts", "fix.md")));
        Assert.False(File.Exists(Path.Combine(agentDir, "tools", "rg.exe")));
        Assert.True(File.Exists(Path.Combine(agentDir, "bin", "rg.exe")));
        Assert.True(File.Exists(Path.Combine(agentDir, "tools", "custom.ps1")));
        Assert.Contains(result.DeprecationWarnings, warning => warning.Contains("Global hooks/ directory found", StringComparison.Ordinal));
        Assert.Contains(result.DeprecationWarnings, warning => warning.Contains("Global tools/ directory contains custom tools", StringComparison.Ordinal));
    }

    [Fact]
    public void Run_MigratesLegacyKeybindingNamesWithoutOverwritingNewNames()
    {
        using var temp = TempDirectory.Create();
        var agentDir = Path.Combine(temp.Path, ".tau");
        Directory.CreateDirectory(agentDir);
        var keybindingsPath = Path.Combine(agentDir, "keybindings.json");
        File.WriteAllText(
            keybindingsPath,
            """
            {
              "submit": "enter",
              "interrupt": "ctrl+c",
              "app.interrupt": "escape",
              "custom.extra": "ctrl+x"
            }
            """);

        CodingAgentMigrations.Run(new CodingAgentMigrationOptions(
            AgentDirectory: agentDir,
            Cwd: temp.Path,
            AuthPath: Path.Combine(agentDir, "auth.json"),
            KeybindingsPath: Path.Combine(agentDir, "coding-agent-keybindings.json")));

        using var migrated = JsonDocument.Parse(File.ReadAllText(keybindingsPath));
        Assert.True(migrated.RootElement.TryGetProperty("tui.input.submit", out var submit));
        Assert.Equal("enter", submit.GetString());
        Assert.Equal("escape", migrated.RootElement.GetProperty("app.interrupt").GetString());
        Assert.False(migrated.RootElement.TryGetProperty("interrupt", out _));
        Assert.False(migrated.RootElement.TryGetProperty("submit", out _));
        Assert.Equal("ctrl+x", migrated.RootElement.GetProperty("custom.extra").GetString());
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path) => Path = path;

        public string Path { get; }

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tau-migrations-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
