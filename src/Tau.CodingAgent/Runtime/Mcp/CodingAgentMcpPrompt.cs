// 作者：xxx
using System.Globalization;

namespace Tau.CodingAgent.Runtime.Mcp;

/// <summary>【CodingAgent】【MCP 提示快照】服务器定义及初始化返回的使用说明。</summary>
/// <param name="Entry">已校验的配置。</param><param name="Instructions">可选服务器说明。</param>
public sealed record CodingAgentMcpPromptServer(CodingAgentMcpServerEntry Entry, string? Instructions = null);

/// <summary>【CodingAgent】【MCP 服务器提示】按上游预算生成间接工具服务器目录。</summary>
public static class CodingAgentMcpPrompt
{
    public const string SectionName = "mcp_servers";
    public const int MaximumSectionCharacters = 4096;
    public const int MaximumDescriptionCharacters = 250;

    /// <summary>【CodingAgent】【MCP 提示生成】列出启用且具有 codemode 或 deferred 策略的服务器，预算不足时缩短描述并统计省略项。</summary>
    /// <param name="servers">配置及说明快照，不要求连接完成。</param><returns>目录正文；无符合条件的服务器时为空值。</returns>
    public static string? Render(IEnumerable<CodingAgentMcpPromptServer> servers)
    {
        // 1. 【CodingAgent】【MCP 提示筛选】混合策略优先提示 codemode，直接声明和隐藏服务器不单独列入
        var listed = servers.Where(server => server.Entry.Config["enabled"]?.GetValue<bool>() != false)
            .Select(server => (Server: server, Exposures: Exposures(server.Entry)))
            .Where(item => item.Exposures.Contains("codemode") || item.Exposures.Contains("deferred"))
            .OrderBy(item => item.Server.Entry.Name, StringComparer.Create(CultureInfo.GetCultureInfo("en-US"), false)).ToArray();
        if (listed.Length == 0) return null;
        var reaches = listed.Select(item => item.Exposures.Contains("codemode") ? "codemode" : "tool_search").ToArray();
        var intro = "MCP servers whose tools are not declared to you.";
        if (reaches.Contains("codemode")) intro += " Call the tools of \u0060codemode\u0060 servers from codemode scripts.";
        if (reaches.Contains("tool_search")) intro += " Load the tools of \u0060tool_search\u0060 servers with \u0060tool_search\u0060.";
        var heads = listed.Select((item, index) => $"- {CodingAgentMcpServers.Namespace(item.Server.Entry.Name)} ({reaches[index]})").ToArray();
        var lengths = new long[heads.Length + 1];
        for (var index = 0; index < heads.Length; index++) lengths[index + 1] = lengths[index] + heads[index].Length + 1;

        // 2. 【CodingAgent】【MCP 提示预算】先为名称和省略提示留出空间，再平均分配描述预算
        var kept = heads.Length;
        while (kept > 0 && SectionSize(intro.Length, lengths[kept], heads.Length - kept) > MaximumSectionCharacters) kept--;
        var perServer = kept == 0 ? 0 : Math.Min(MaximumDescriptionCharacters,
            (int)((MaximumSectionCharacters - SectionSize(intro.Length, lengths[kept], heads.Length - kept)) / kept) - 2);
        var lines = new List<string> { intro };
        for (var index = 0; index < kept; index++)
        {
            var summary = perServer > 0 ? Truncate(Summary(listed[index].Server), perServer) : "";
            lines.Add(heads[index] + (summary.Length == 0 ? "" : ": " + summary));
        }
        if (kept < heads.Length) lines.Add(Omitted(heads.Length - kept));
        return string.Join("\n", lines);
    }

    /// <summary>【CodingAgent】【MCP 策略集合】合并默认策略和逐工具覆盖。</summary>
    /// <param name="entry">服务器定义。</param><returns>去重策略。</returns>
    private static HashSet<string> Exposures(CodingAgentMcpServerEntry entry)
    {
        var result = new HashSet<string>(StringComparer.Ordinal) { entry.Config["exposure"]?.GetValue<string>() ?? "codemode" };
        if (entry.Config["toolExposure"] is System.Text.Json.Nodes.JsonObject overrides)
            foreach (var pair in overrides) result.Add(pair.Value!.GetValue<string>());
        return result;
    }

    /// <summary>【CodingAgent】【MCP 描述摘要】优先采用非空配置描述，否则采用服务器说明，只显示第一行。</summary>
    /// <param name="server">服务器快照。</param><returns>去除边缘空白的一行摘要。</returns>
    private static string Summary(CodingAgentMcpPromptServer server)
    {
        var text = server.Entry.Config["description"]?.GetValue<string>().Trim();
        if (string.IsNullOrEmpty(text)) text = server.Instructions ?? "";
        var newline = text.IndexOf('\n');
        return (newline < 0 ? text : text[..newline]).Trim();
    }

    /// <summary>【CodingAgent】【MCP 摘要截断】按 UTF-16 字符预算保留省略号，与上游字符串计数一致。</summary>
    /// <param name="text">原摘要。</param><param name="maximum">字符上限。</param><returns>截断后的摘要。</returns>
    private static string Truncate(string text, int maximum) =>
        text.Length <= maximum ? text : maximum <= 1 ? "" : text[..(maximum - 1)].TrimEnd() + "…";

    /// <summary>【CodingAgent】【MCP 省略说明】生成剩余服务器的发现提示。</summary>
    /// <param name="count">省略数量。</param><returns>完整提示行。</returns>
    private static string Omitted(int count) => $"- … {count} more server{(count == 1 ? "" : "s")}; find their tools with searchTools()";

    /// <summary>【CodingAgent】【MCP 段落计数】计算前言、名称行及可选省略提示的字符总数。</summary>
    /// <param name="intro">前言长度。</param><param name="heads">保留名称行及换行长度。</param><param name="omitted">省略数量。</param><returns>字符总数。</returns>
    private static long SectionSize(int intro, long heads, int omitted) => intro + heads + (omitted == 0 ? 0 : Omitted(omitted).Length + 1);
}

public sealed partial class CodingAgentMcpService
{
    /// <summary>【CodingAgent】【MCP 提示读取】在配置锁内读取一致的服务器目录，不等待间接服务器连接。</summary>
    /// <returns>间接服务器目录；没有可展示项时为空值。</returns>
    public string? GetServersPrompt()
    {
        lock (_gate) return CodingAgentMcpPrompt.Render(_connections.Values.Select(connection => new CodingAgentMcpPromptServer(connection.Entry, connection.Instructions)));
    }
}
