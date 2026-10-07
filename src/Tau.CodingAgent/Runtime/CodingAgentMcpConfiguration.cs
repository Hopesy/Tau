// 作者：xxx
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【MCP 来源】保留服务器定义及可编辑的覆盖位置。</summary>
/// <param name="Name">服务器名称。</param><param name="Config">独立配置副本。</param><param name="Source">定义文件或扩展路径。</param>
/// <param name="Scope">global、project 或 extension。</param><param name="Override">项目覆盖文件。</param>
public sealed record CodingAgentMcpServerEntry(string Name, JsonObject Config, string Source, string Scope, string? Override = null);

/// <summary>【CodingAgent】【MCP 加载】配置加载结果，错误不影响其他有效服务器。</summary>
/// <param name="Servers">按配置顺序排列的服务器，包含禁用项。</param><param name="AutoEnableCodemode">是否自动启用脚本工具，空值使用默认值。</param>
/// <param name="Errors">逐项诊断。</param><param name="ProjectConfig">可信项目的配置文件位置。</param>
public sealed record CodingAgentMcpConfig(IReadOnlyList<CodingAgentMcpServerEntry> Servers, bool? AutoEnableCodemode,
    IReadOnlyList<string> Errors, string? ProjectConfig);

/// <summary>【CodingAgent】【MCP 配置】读取分层配置并保存管理界面中的增删改。</summary>
public static class CodingAgentMcpConfiguration
{
    private static readonly string[] OverrideKeys = ["enabled", "exposure", "toolExposure"];

    /// <summary>【CodingAgent】【MCP 加载】读取全局配置以及受信任项目的覆盖，配置值此时不执行命令。</summary>
    /// <param name="agentDirectory">全局代理目录。</param><param name="cwd">会话工作目录。</param>
    /// <param name="projectTrusted">是否允许读取项目文件。</param><returns>服务器及错误集合。</returns>
    public static CodingAgentMcpConfig Load(string agentDirectory, string cwd, bool projectTrusted)
    {
        var servers = new Dictionary<string, CodingAgentMcpServerEntry>(StringComparer.Ordinal);
        var errors = new List<string>();
        bool? autoEnable = null;
        var project = projectTrusted ? Path.Combine(Path.GetFullPath(cwd), ".tau", "mcp.json") : null;
        ReadFile(Path.Combine(Path.GetFullPath(agentDirectory), "mcp.json"), "global", servers, errors, ref autoEnable);
        if (project is not null) ReadFile(project, "project", servers, errors, ref autoEnable);
        return new(servers.Values.ToArray(), autoEnable, errors, project);
    }

    /// <summary>【CodingAgent】【MCP 分层】逐项合并一个配置文件，禁止项目重新指定提供方凭据的发送目标。</summary>
    /// <param name="path">配置路径。</param><param name="scope">global 或 project。</param><param name="servers">已有服务器。</param>
    /// <param name="errors">诊断集合。</param><param name="autoEnable">脚本工具自动启用设置。</param>
    private static void ReadFile(string path, string scope, Dictionary<string, CodingAgentMcpServerEntry> servers, List<string> errors, ref bool? autoEnable)
    {
        if (!File.Exists(path)) return;
        JsonObject parsed;
        try { parsed = ParseFile(path, File.ReadAllText(path)); }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        { errors.Add(error.Message.StartsWith(path + ":", StringComparison.Ordinal) ? error.Message : $"{path}: {error.Message}"); return; }
        if (parsed["autoEnableCodemode"] is JsonValue flag && flag.TryGetValue<bool>(out var enabled)) autoEnable = enabled;
        else if (parsed.ContainsKey("autoEnableCodemode")) errors.Add($"{path}: autoEnableCodemode must be a boolean");
        foreach (var pair in (parsed["mcpServers"] as JsonObject ?? []))
        {
            try
            {
                // 1. 【CodingAgent】【MCP 项目覆盖】轻量覆盖保留全局地址及凭据，只改变启用和工具访问策略
                if (scope == "project" && pair.Value is JsonObject patch && IsOverride(patch))
                {
                    if (!servers.TryGetValue(pair.Key, out var previous))
                        throw new ArgumentException($"server \"{pair.Key}\" needs \"command\" or \"url\", or a global server to override");
                    if (patch.Any(field => !OverrideKeys.Contains(field.Key, StringComparer.Ordinal)))
                        throw new ArgumentException($"server \"{pair.Key}\": an override can only set {string.Join(", ", OverrideKeys)}");
                    var merged = (JsonObject)previous.Config.DeepClone();
                    foreach (var field in patch) merged[field.Key] = field.Value?.DeepClone();
                    servers[pair.Key] = previous with { Config = CodingAgentMcpServers.Validate(pair.Key, merged), Override = path };
                    continue;
                }
                // 2. 【CodingAgent】【MCP 完整定义】同名覆盖允许替换服务器，但不同名称不能产生同一工具命名空间
                var config = CodingAgentMcpServers.Validate(pair.Key, pair.Value);
                var clash = servers.Keys.FirstOrDefault(name => name != pair.Key && CodingAgentMcpServers.Namespace(name) == CodingAgentMcpServers.Namespace(pair.Key));
                if (clash is not null) throw new ArgumentException($"server \"{pair.Key}\" conflicts with \"{clash}\"");
                if (scope == "project" && config.ContainsKey("url") && config.ContainsKey("auth"))
                    throw new ArgumentException($"server \"{pair.Key}\": auth is only allowed in the global mcp.json");
                servers[pair.Key] = new(pair.Key, config, path, scope);
            }
            catch (ArgumentException error) { errors.Add($"{path}: {error.Message}"); }
        }
    }

    /// <summary>【CodingAgent】【MCP 编辑】修改开关或访问策略，完整定义的默认值省略，项目覆盖保留默认值。</summary>
    /// <param name="path">目标配置文件。</param><param name="name">服务器名称。</param><param name="enabled">可选启用状态。</param>
    /// <param name="exposure">可选访问策略。</param><param name="createOverride">不存在时是否创建轻量覆盖。</param>
    public static void Update(string path, string name, bool? enabled = null, string? exposure = null, bool createOverride = false) =>
        Edit(path, (servers, parsed) =>
        {
            if ((servers is null || !servers.ContainsKey(name)) && createOverride)
            {
                servers ??= new();
                servers[name] = new JsonObject();
                if (parsed["mcpServers"] is null) parsed["mcpServers"] = servers;
            }
            if (servers?[name] is not JsonObject server) throw new InvalidOperationException($"{path} does not define MCP server \"{name}\"");
            var keepDefaults = IsOverride(server);
            if (enabled is { } active)
            {
                if (active && !keepDefaults) server.Remove("enabled"); else server["enabled"] = active;
            }
            if (exposure is not null)
            {
                if (exposure == "codemode" && !keepDefaults) server.Remove("exposure"); else server["exposure"] = exposure;
            }
            return true;
        });

    /// <summary>【CodingAgent】【MCP 新增】添加或替换一个完整服务器定义，保留其他配置内容。</summary>
    /// <param name="path">配置路径。</param><param name="name">服务器名称。</param><param name="config">服务器定义。</param><returns>是否替换旧定义。</returns>
    public static bool Add(string path, string name, JsonObject config)
    {
        var replaced = false;
        Edit(path, (servers, parsed) =>
        {
            servers ??= new();
            replaced = servers.ContainsKey(name);
            servers[name] = config.DeepClone();
            if (parsed["mcpServers"] is null) parsed["mcpServers"] = servers;
            return true;
        });
        return replaced;
    }

    /// <summary>【CodingAgent】【MCP 删除】删除指定定义；文件或条目不存在时不创建文件。</summary>
    /// <param name="path">配置路径。</param><param name="name">服务器名称。</param><returns>是否删除条目。</returns>
    public static bool Remove(string path, string name)
    {
        if (!File.Exists(path)) return false;
        var removed = false;
        Edit(path, (servers, _) => removed = servers?.Remove(name) == true);
        return removed;
    }

    /// <summary>【CodingAgent】【MCP 写入】验证根对象，按原有缩进写回，不丢失其他配置字段。</summary>
    /// <param name="path">目标文件。</param><param name="edit">修改回调，返回真时写回。</param>
    private static void Edit(string path, Func<JsonObject?, JsonObject, bool> edit)
    {
        path = Path.GetFullPath(path);
        var text = File.Exists(path) ? File.ReadAllText(path) : null;
        var parsed = text is null ? new JsonObject() : ParseFile(path, text);
        if (!edit(parsed["mcpServers"] as JsonObject, parsed)) return;
        var match = Regex.Match(text ?? "", "^([ \\t]+)\\S", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        var indent = match.Success ? match.Groups[1].Value : "  ";
        var serialized = parsed.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        serialized = Regex.Replace(serialized, "^ +", value => string.Concat(Enumerable.Repeat(indent, value.Length / 2)), RegexOptions.Multiline);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, serialized.Replace("\r\n", "\n") + "\n", new UTF8Encoding(false));
    }

    /// <summary>【CodingAgent】【MCP 根配置】读取严格 JSON 并验证服务器表。</summary>
    /// <param name="path">诊断路径。</param><param name="text">配置文本。</param><returns>根对象。</returns>
    private static JsonObject ParseFile(string path, string text)
    {
        if (JsonNode.Parse(text) is not JsonObject parsed || parsed.ContainsKey("mcpServers") && parsed["mcpServers"] is not JsonObject)
            throw new JsonException($"{path}: expected an object with an \"mcpServers\" object");
        return parsed;
    }

    /// <summary>【CodingAgent】【MCP 覆盖】识别没有任何传输字段的轻量覆盖。</summary>
    /// <param name="config">服务器条目。</param><returns>是否只覆盖上层定义。</returns>
    private static bool IsOverride(JsonObject config) => !config.ContainsKey("command") && !config.ContainsKey("url") && !config.ContainsKey("type");
}
