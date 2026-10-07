// 作者：xxx
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tau.CodingAgent.Runtime.Mcp;
using Tau.CodingAgent.Runtime.Mcp.OAuth;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【MCP 命令参数】允许宿主覆盖目录、连接和登录展示，测试不访问真实用户配置。</summary>
public sealed record CodingAgentMcpCliOptions
{
    public string Cwd { get; init; } = Environment.CurrentDirectory;
    public string? AgentDirectory { get; init; }
    public CodingAgentMcpServiceOptions? ServiceOptions { get; init; }
    public ICodingAgentMcpSignInPrompt? SignInPrompt { get; init; }
}

/// <summary>【CodingAgent】【MCP 独立命令】在启动模型会话前处理服务器配置、连接检查、登录和注销。</summary>
public static partial class CodingAgentMcpCli
{
    private const string Help = """
        Usage:
          tau mcp add <server> [options] -- <command> [args...]
          tau mcp add <server> [options] --url <url>
          tau mcp remove <server> [-l]
          tau mcp list [--json]
          tau mcp login <server> [--timeout <seconds>]
          tau mcp logout <server>

        Add/remove: -l, --local uses .tau/mcp.json in the current project.
        Add: --env KEY=VALUE (repeatable), --cwd DIR, --header KEY=VALUE (repeatable),
             --bearer-token-env-var NAME, --oauth-client-id ID, --oauth-client-secret VALUE,
             --oauth-callback-port PORT, --oauth-client-name NAME,
             --exposure codemode|deferred|direct|hidden, --description TEXT.
        List checks connections and exits 1 on failure. Login timeout defaults to 300 seconds.
        Project MCP configuration is only loaded in trusted projects.
        """;

    /// <summary>【CodingAgent】【MCP 命令入口】仅消费 mcp 子命令，其他命令返回空值交给正常启动。</summary>
    /// <param name="args">完整命令参数。</param><param name="output">标准输出。</param><param name="error">错误输出。</param><param name="options">目录和宿主选项。</param><param name="token">取消。</param><returns>退出码或未处理时的空值。</returns>
    public static async Task<int?> TryHandleAsync(IReadOnlyList<string> args, TextWriter output, TextWriter error, CodingAgentMcpCliOptions? options = null, CancellationToken token = default)
    {
        if (args.Count == 0 || args[0] != "mcp") return null;
        var rest = args.Skip(1).ToArray();
        if (rest.Length == 0 || rest[0] == "help" || rest.Contains("--help") || rest.Contains("-h")) { await output.WriteLineAsync(Help).ConfigureAwait(false); return 0; }
        options ??= new();
        var cwd = Path.GetFullPath(options.Cwd);
        var agent = Path.GetFullPath(options.AgentDirectory ?? new CodingAgentPackageManager().UserInstallDirectory);
        try
        {
            token.ThrowIfCancellationRequested();
            if (rest[0] is "add" or "remove") return Configure(rest[0], rest.Skip(1).ToArray(), agent, cwd, output);
            if (rest[0] is not ("list" or "login" or "logout")) throw new ArgumentException($"Unknown mcp command \"{rest[0]}\". Use \"tau mcp --help\" for usage.");
            var parsed = Parse(rest.Skip(1).ToArray(), rest[0] == "list" ? new() { ["json"] = "flag" } : rest[0] == "login" ? new() { ["timeout"] = "value" } : new());
            var trusted = new CodingAgentProjectTrustStore(agent).Get(cwd) == true;
            var loaded = CodingAgentMcpConfiguration.Load(agent, cwd, trusted);
            var project = Path.Combine(cwd, ".tau", "mcp.json");
            var note = !trusted && File.Exists(project) ? $"{project} is ignored because the project is not trusted. Start tau in the project to trust it." : null;
            if (rest[0] == "list")
            {
                if (parsed.Positionals.Count != 0) throw new ArgumentException("Usage: tau mcp list [--json]");
                await using var service = Service(loaded, options, agent, cwd, trusted);
                await service.ReloadAsync(token).ConfigureAwait(false);
                var reports = await service.GetReportsAsync(token).ConfigureAwait(false);
                PrintReports(reports, loaded.Errors, note, parsed.Values.ContainsKey("json"), agent, output);
                return loaded.Errors.Count > 0 || reports.Any(item => item!["enabled"]!.GetValue<bool>() && (item["state"]!.GetValue<string>() != "connected" || item["error"] is not null)) ? 1 : 0;
            }
            if (parsed.Positionals.Count != 1) throw new ArgumentException($"Usage: tau mcp {rest[0]} <server>");
            var name = parsed.Positionals[0];
            var entry = loaded.Servers.FirstOrDefault(server => server.Name == name) ??
                throw new ArgumentException($"No MCP server named \"{name}\". {note} Configured: {string.Join(", ", loaded.Servers.Select(server => server.Name).DefaultIfEmpty("none"))}.");
            if (!CodingAgentMcpService.UsesOAuth(entry)) throw new ArgumentException($"MCP server \"{name}\" does not use OAuth. Only HTTP servers without an Authorization header or auth.provider do.");
            if (rest[0] == "logout")
            {
                var removed = await new CodingAgentMcpOAuthCredentialStore(agent).RemoveAsync(name, entry.Config["url"]!.GetValue<string>(), token).ConfigureAwait(false);
                output.WriteLine(removed ? $"Signed out of MCP server \"{name}\"." : $"No stored credentials for MCP server \"{name}\".");
                return 0;
            }
            var seconds = parsed.Values.TryGetValue("timeout", out var text) ? double.Parse(text, CultureInfo.InvariantCulture) : 300;
            if (!double.IsFinite(seconds) || seconds <= 0 || seconds * 1000 > uint.MaxValue - 1) throw new ArgumentException("--timeout must be a positive number of seconds within the supported timer range.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
            var config = entry.Config.DeepClone().AsObject(); config["enabled"] = true;
            await using var loginService = Service(new([entry with { Config = config }], loaded.AutoEnableCodemode, [], loaded.ProjectConfig), options, agent, cwd, trusted);
            await loginService.ReloadAsync(timeout.Token).ConfigureAwait(false); await loginService.WaitForServersAsync(timeout.Token).ConfigureAwait(false);
            var status = loginService.GetStatus().Single();
            if (status.State == "connected") { output.WriteLine($"Already signed in to MCP server \"{name}\" ({status.ToolCount} tools)."); return 0; }
            if (status.State != "needs-auth") throw new InvalidOperationException($"MCP server \"{name}\" failed to connect: {status.Error ?? status.State}");
            await loginService.SignInAsync(name, options.SignInPrompt ?? new ConsolePrompt(name, output), timeout.Token).ConfigureAwait(false);
            status = loginService.GetStatus().Single();
            if (status.State != "connected") throw new InvalidOperationException($"Signed in, but {status.Error ?? status.State}");
            output.WriteLine($"Signed in to MCP server \"{name}\" ({status.ToolCount} tools).");
            return 0;
        }
        catch (OperationCanceledException) { error.WriteLine("MCP command cancelled or timed out."); return 1; }
        catch (Exception failure) when (failure is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or JsonException or FormatException or OverflowException or System.Net.Sockets.SocketException or HttpRequestException)
        { error.WriteLine(failure.Message); return 1; }
    }

    /// <summary>【CodingAgent】【MCP 命令服务】使用已选配置快照，登录单台服务器不会启动其他服务器。</summary>
    /// <param name="loaded">选定配置。</param><param name="options">宿主选项。</param><param name="agent">代理目录。</param><param name="cwd">工作目录。</param><param name="trusted">信任状态。</param><returns>待加载服务。</returns>
    private static CodingAgentMcpService Service(CodingAgentMcpConfig loaded, CodingAgentMcpCliOptions options, string agent, string cwd, bool trusted) =>
        new(agent, cwd, () => trusted, new(), (options.ServiceOptions ?? new()) with { ConfigurationLoader = () => loaded });

    /// <summary>【CodingAgent】【MCP 控制台登录】显示授权 URL，重定向输入场景只等待浏览器，不抢读调用者的标准输入。</summary><param name="name">服务器。</param><param name="output">展示输出。</param>
    private sealed class ConsolePrompt(string name, TextWriter output) : ICodingAgentMcpSignInPrompt
    {
        /// <summary>【CodingAgent】【MCP 浏览器登录】显示并尝试用平台默认浏览器打开 URL，打开失败仍允许手工复制。</summary><param name="url">授权地址。</param><param name="token">取消。</param><returns>展示任务。</returns>
        public Task ShowAuthorizationUrlAsync(Uri url, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); output.WriteLine($"Sign in to MCP server \"{name}\" in your browser:\n{url.AbsoluteUri}");
            try
            {
                var start = OperatingSystem.IsWindows() ? new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true } : new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { UseShellExecute = false, CreateNoWindow = true };
                if (!OperatingSystem.IsWindows()) start.ArgumentList.Add(url.AbsoluteUri);
                using var process = Process.Start(start);
            }
            catch (Exception failure) when (failure is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            return Task.CompletedTask;
        }
        /// <summary>【CodingAgent】【MCP 手工回调】使用可取消且不记录历史的输入；非交互模式等待浏览器或超时。</summary><param name="token">取消。</param><returns>回调 URL。</returns>
        public async Task<string?> PromptForRedirectUrlAsync(CancellationToken token)
        {
            if (Console.IsInputRedirected) { await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false); return null; }
            output.WriteLine("If the browser cannot reach this machine, paste its full redirect URL and press Enter:");
            return await CodingAgentSecretInput.ReadAsync(token).ConfigureAwait(false);
        }
    }
}
