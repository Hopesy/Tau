// 作者：xxx
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tau.CodingAgent.Runtime.Mcp;

namespace Tau.CodingAgent.Runtime;

public static partial class CodingAgentMcpCli
{
    /// <summary>【CodingAgent】【MCP 命令选项】记录有序位置参数、单值及重复选项。</summary>
    private sealed record Parsed(List<string> Positionals, Dictionary<string, string> Values, Dictionary<string, List<string>> Lists);

    /// <summary>【CodingAgent】【MCP 选项解析】识别短别名和 -- 分隔符，达到可执行命令位置后原样传递所有剩余参数。</summary>
    /// <param name="args">子命令参数。</param><param name="known">允许选项及类型。</param><param name="maxPositionals">停止解析的参数个数。</param><returns>已解析选项。</returns>
    private static Parsed Parse(IReadOnlyList<string> args, Dictionary<string, string> known, int maxPositionals = int.MaxValue)
    {
        var result = new Parsed([], new(StringComparer.Ordinal), new(StringComparer.Ordinal));
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index] == "-l" ? "--local" : args[index];
            if (argument == "--" || result.Positionals.Count >= maxPositionals)
            { result.Positionals.AddRange(args.Skip(argument == "--" ? index + 1 : index)); break; }
            if (!argument.StartsWith("--", StringComparison.Ordinal)) { result.Positionals.Add(argument); continue; }
            var name = argument[2..];
            if (!known.TryGetValue(name, out var kind)) throw new ArgumentException($"Unknown option {argument}. Use \"tau mcp --help\" for usage.");
            if (kind == "flag") { result.Values[name] = "true"; continue; }
            if (++index >= args.Count) throw new ArgumentException(argument + " needs a value.");
            if (kind == "list")
            {
                if (!result.Lists.TryGetValue(name, out var values)) result.Lists[name] = values = [];
                values.Add(args[index]);
            }
            else result.Values[name] = args[index];
        }
        return result;
    }

    /// <summary>【CodingAgent】【MCP 配置命令】按全局或项目作用域增删服务器，先验证完整配置再写入。</summary>
    /// <param name="command">add 或 remove。</param><param name="args">参数。</param><param name="agent">全局目录。</param><param name="cwd">项目目录。</param><param name="output">结果输出。</param><returns>退出码。</returns>
    private static int Configure(string command, string[] args, string agent, string cwd, TextWriter output)
    {
        var known = new Dictionary<string, string>(StringComparer.Ordinal) { ["local"] = "flag" };
        if (command == "add")
        {
            foreach (var key in new[] { "url", "cwd", "bearer-token-env-var", "oauth-client-id", "oauth-client-secret", "oauth-callback-port", "oauth-client-name", "exposure", "description" }) known[key] = "value";
            known["env"] = "list"; known["header"] = "list";
        }
        var parsed = Parse(args, known, command == "add" ? 2 : int.MaxValue);
        if (parsed.Positionals.Count == 0 || command == "remove" && parsed.Positionals.Count != 1)
            throw new ArgumentException($"Usage: tau mcp {command} <server> [options]");
        var name = parsed.Positionals[0]; var local = parsed.Values.ContainsKey("local");
        var path = Path.Combine(local ? Path.Combine(cwd, ".tau") : agent, "mcp.json");
        var scope = local ? "project" : "global";
        if (command == "remove")
        {
            if (CodingAgentMcpConfiguration.Remove(path, name)) { output.WriteLine($"Removed {scope} MCP server \"{name}\" from {path}."); return 0; }
            var other = CodingAgentMcpConfiguration.Load(agent, cwd, true).Servers.FirstOrDefault(server => server.Name == name && server.Scope != scope);
            throw new ArgumentException($"No {scope} MCP server named \"{name}\" in {path}." +
                (other is null ? "" : $" It is defined in {other.Source}; {(other.Scope == "project" ? "use" : "omit")} --local."));
        }
        var hasUrl = parsed.Values.TryGetValue("url", out var url);
        if (hasUrl == (parsed.Positionals.Count > 1)) throw new ArgumentException("Usage: tau mcp add <server> [options] (--url <url> | -- <command> [args...])");
        var incompatible = hasUrl ? new[] { "env", "cwd" } : ["header", "bearer-token-env-var", "oauth-client-id", "oauth-client-secret", "oauth-callback-port", "oauth-client-name"];
        foreach (var key in incompatible)
            if (parsed.Values.ContainsKey(key) || parsed.Lists.ContainsKey(key)) throw new ArgumentException($"--{key} only applies to {(hasUrl ? "stdio servers" : "HTTP servers (--url)")}");
        JsonObject config;
        if (hasUrl)
        {
            config = new() { ["url"] = url };
            var headers = Pairs(parsed, "header");
            if (parsed.Values.TryGetValue("bearer-token-env-var", out var bearer)) headers["Authorization"] = "Bearer $" + "{" + bearer + "}";
            if (headers.Count > 0) config["headers"] = headers;
            var oauth = new JsonObject();
            foreach (var (option, field) in new[] { ("oauth-client-id", "clientId"), ("oauth-client-secret", "clientSecret"), ("oauth-client-name", "clientName") })
                if (parsed.Values.TryGetValue(option, out var value)) oauth[field] = value;
            if (parsed.Values.TryGetValue("oauth-callback-port", out var port))
            {
                if (!double.TryParse(port, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number)) throw new ArgumentException("--oauth-callback-port must be a port number.");
                oauth["callbackPort"] = number;
            }
            if (oauth.Count > 0) config["oauth"] = oauth;
        }
        else
        {
            config = new() { ["command"] = parsed.Positionals[1] };
            if (parsed.Positionals.Count > 2) config["args"] = new JsonArray(parsed.Positionals.Skip(2).Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
            var env = Pairs(parsed, "env"); if (env.Count > 0) config["env"] = env;
            if (parsed.Values.TryGetValue("cwd", out var working)) config["cwd"] = working;
        }
        foreach (var key in new[] { "exposure", "description" }) if (parsed.Values.TryGetValue(key, out var value)) config[key] = value;
        var validated = CodingAgentMcpServers.Validate(name, config);
        var replaced = CodingAgentMcpConfiguration.Add(path, name, validated);
        output.WriteLine($"{(replaced ? "Replaced" : "Added")} {scope} MCP server \"{name}\" in {path}.");
        if (local && new CodingAgentProjectTrustStore(agent).Get(cwd) != true)
            output.WriteLine($"The project is not trusted, so {path} is ignored until you start tau in the project and trust it.");
        output.WriteLine("Check it with: tau mcp list" +
            (CodingAgentMcpService.UsesOAuth(new(name, validated, path, scope)) ? $". If it requires sign-in: tau mcp login {name}" : ""));
        return 0;
    }

    /// <summary>【CodingAgent】【MCP 键值选项】只拆分第一个等号，同名键后值覆盖前值，不执行环境引用。</summary>
    /// <param name="parsed">选项。</param><param name="option">env 或 header。</param><returns>JSON 映射。</returns>
    private static JsonObject Pairs(Parsed parsed, string option)
    {
        var result = new JsonObject();
        foreach (var pair in parsed.Lists.GetValueOrDefault(option) ?? [])
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0) throw new ArgumentException($"--{option} expects KEY=VALUE.");
            result[pair[..separator]] = pair[(separator + 1)..];
        }
        return result;
    }

    /// <summary>【CodingAgent】【MCP 检查输出】JSON 输出保持机器可读，文本输出展示工具策略、资源计数和错误。</summary>
    /// <param name="reports">服务器报告。</param><param name="errors">配置错误。</param><param name="note">未信任提示。</param><param name="json">是否 JSON。</param><param name="agent">全局目录。</param><param name="output">输出。</param>
    internal static void PrintReports(JsonArray reports, IReadOnlyList<string> errors, string? note, bool json, string agent, TextWriter output)
    {
        if (json)
        {
            var document = new JsonObject { ["servers"] = reports.DeepClone(), ["errors"] = new JsonArray(errors.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()) };
            if (note is not null) document["note"] = note;
            output.WriteLine(document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return;
        }
        if (reports.Count == 0 && errors.Count == 0) output.WriteLine($"No MCP servers configured. Add them to {Path.Combine(agent, "mcp.json")} or .tau/mcp.json.");
        foreach (var item in reports)
        {
            var report = item!.AsObject(); var name = report["name"]!.GetValue<string>(); var state = report["state"]!.GetValue<string>();
            var tools = report["tools"]!.AsArray();
            output.WriteLine($"{name}: {(state == "connected" ? $"connected, {tools.Count} tools" : state == "needs-auth" ? "needs sign-in" : state)} ({report["exposure"]!.GetValue<string>()}, {report["scope"]!.GetValue<string>()})");
            output.WriteLine("  " + report["transport"]!.GetValue<string>());
            if (report["override"] is { } source) output.WriteLine("  project override: " + source.GetValue<string>());
            if (state == "needs-auth") output.WriteLine("  sign in with: tau mcp login " + name);
            if (tools.Count > 0) output.WriteLine("  tools: " + string.Join(", ", tools.Select(tool =>
            {
                var toolName = tool!.GetValue<string>(); var exposure = report["toolExposure"]?[toolName]?.GetValue<string>();
                return toolName + (exposure is null ? "" : " [" + exposure + "]");
            })));
            if (report["resources"] is { } resources) output.WriteLine($"  resources: {resources}, URI templates: {report["resourceTemplates"]}");
            if (report["error"] is { } failure) output.WriteLine("  " + failure.GetValue<string>().Replace("\n", "\n  ", StringComparison.Ordinal));
        }
        foreach (var failure in errors) output.WriteLine("config error: " + failure);
        if (note is not null) output.WriteLine(note);
    }
}
