// 作者：xxx
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Tau.Tui.Components;
using Tau.Tui.Rendering;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【脚本显示】源码、嵌套工具状态和输出采用独立预览预算，模型仍接收完整协议结果。</summary>
public static partial class CodingAgentCodeModeRenderer
{
    /// <summary>【CodingAgent】【脚本正文】按实际终端宽度预览源码和输出，展开后显示全部嵌套调用及错误。</summary>
    /// <param name="context">当前组件快照。</param><param name="color">是否使用语法和状态颜色。</param><returns>正文显示行。</returns>
    public static IReadOnlyList<string> Render(TuiToolExecutionRenderContext context, bool color = false)
    {
        var lines = new List<string>();
        var code = Field(Json(context.Args), "code");
        if (code is null) lines.Add(Style("[invalid arg]", "31", color));
        else if (code.Length > 0)
        {
            // 1. 【CodingAgent】【源码预览】先语法高亮再按显示宽度换行，选项行作为源码一起展示
            code = code.Replace("\r", "", StringComparison.Ordinal).TrimEnd().Replace("\t", "  ", StringComparison.Ordinal);
            var highlighted = color ? string.Join("\n", TuiSyntaxHighlighter.HighlightLines(code, "javascript")) : code;
            AppendPreview(lines, highlighted, context, 10, color);
        }
        var details = Json(context.Result?.Details);
        var calls = details is { ValueKind: JsonValueKind.Object } data && data.TryGetProperty("calls", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object).ToArray() : [];
        if (calls.Length > 0)
        {
            AddSpace(lines);
            var shown = context.Expanded ? calls : calls.TakeLast(8).ToArray();
            if (shown.Length != calls.Length) lines.Add(Style($"... ({calls.Length - shown.Length} earlier calls, {context.ExpandHint})", "90", color));
            foreach (var call in shown) lines.AddRange(TuiText.WrapTextWithAnsi(FormatCall(call, context.Expanded, color), context.Width));
            var costs = calls.Select(call => Number(call, "cost")).Where(cost => cost is > 0).Select(cost => cost!.Value).ToArray();
            if (costs.Length > 1) lines.Add(Style("Model calls: " + FormatCost(costs.Sum()), "90", color));
        }

        // 2. 【CodingAgent】【输出预览】执行中只显示调用状态；结束后移除脚本协议头，保留真实输出及截断文件位置
        if (!context.IsPartial && context.Result is { } result)
        {
            var content = result.Content.ToArray();
            if (content.FirstOrDefault() is TuiToolTextBlock first)
                content[0] = first with { Text = ScriptHeader().Replace(first.Text, "", 1) };
            var output = TuiToolExecution.GetTextOutput(result with { Content = content }, context.ShowImages).Trim().Replace("\t", "  ", StringComparison.Ordinal);
            if (output.Length > 0)
            {
                AddSpace(lines);
                AppendPreview(lines, Style(output, result.IsError ? "31" : "39", color), context, 5, color);
                if (!context.Expanded && Field(details, "fullOutputPath") is { Length: > 0 } path) lines.Add(Style("Full output: " + path, "90", color));
            }
        }
        return lines;
    }

    /// <summary>【CodingAgent】【嵌套调用行】展示状态、紧凑参数、耗时和费用，展开时追加多行错误。</summary>
    /// <param name="call">调用详情。</param><param name="expanded">是否展开。</param><param name="color">是否上色。</param><returns>一条调用的显示文本。</returns>
    private static string FormatCall(JsonElement call, bool expanded, bool color)
    {
        var status = Field(call, "status");
        var icon = status switch { "running" => "…", "ok" => "✓", "error" => "✗", "cancelled" => "⊘", _ => "?" };
        var args = Field(call, "args") ?? "";
        if (!expanded && args.Length > 80) args = args[..77] + "...";
        var text = Style(icon, status switch { "ok" => "32", "error" => "31", "running" => "33", _ => "90" }, color) + " " +
            (Field(call, "name") ?? "tool") + (args.Length == 0 ? "" : " " + args);
        if (Number(call, "durationMs") is { } duration) text += " " + (duration < 1000 ?
            Math.Floor(duration + 0.5).ToString(CultureInfo.InvariantCulture) + "ms" :
            Math.Round(duration / 1000, 1, MidpointRounding.AwayFromZero).ToString("F1", CultureInfo.InvariantCulture) + "s");
        if (Number(call, "cost") is > 0 and var cost) text += " " + FormatCost(cost);
        if (expanded && Field(call, "error") is { Length: > 0 } error)
            text += "\n    " + Style(error.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\n    ", StringComparison.Ordinal), "31", color);
        return text.Replace("\t", "  ", StringComparison.Ordinal);
    }

    /// <summary>【CodingAgent】【显示行预算】按终端实际折行保留开头，省略提示使用当前展开快捷键。</summary>
    /// <param name="lines">输出集合。</param><param name="text">完整文本。</param><param name="context">宽度及展开状态。</param><param name="limit">折叠行数。</param><param name="color">是否上色。</param>
    private static void AppendPreview(List<string> lines, string text, TuiToolExecutionRenderContext context, int limit, bool color)
    {
        var wrapped = TuiText.WrapTextWithAnsi(text, Math.Max(1, context.Width));
        lines.AddRange(context.Expanded ? wrapped : wrapped.Take(limit));
        if (!context.Expanded && wrapped.Count > limit) lines.Add(Style($"... ({wrapped.Count - limit} more lines, {context.ExpandHint})", "90", color));
    }

    /// <summary>【CodingAgent】【费用显示】较大费用保留两位小数，小额分类费用保留两位有效数字。</summary><param name="cost">美元费用。</param><returns>带美元符号的金额。</returns>
    private static string FormatCost(double cost)
    {
        if (cost >= 0.01) return "$" + Math.Round(cost, 2, MidpointRounding.AwayFromZero).ToString("F2", CultureInfo.InvariantCulture);
        var scientific = cost.ToString("e1", CultureInfo.InvariantCulture).Split('e');
        var exponent = int.Parse(scientific[1], CultureInfo.InvariantCulture);
        return exponent < -6 ? "$" + scientific[0] + "e" + exponent.ToString(CultureInfo.InvariantCulture) :
            "$0." + new string('0', -exponent - 1) + scientific[0].Replace(".", "", StringComparison.Ordinal);
    }
    /// <summary>【CodingAgent】【段落间距】已有内容时添加一个空行。</summary><param name="lines">正文集合。</param>
    private static void AddSpace(List<string> lines) { if (lines.Count > 0) lines.Add(""); }
    /// <summary>【CodingAgent】【状态样式】可选应用 ANSI 前景色。</summary><param name="text">文本。</param><param name="code">颜色代码。</param><param name="enabled">是否启用。</param><returns>显示文本。</returns>
    private static string Style(string text, string code, bool enabled) => enabled ? "\u001b[" + code + "m" + text + "\u001b[39m" : text;
    /// <summary>【CodingAgent】【显示字段】读取对象中的字符串，非法或缺失字段返回空值。</summary><param name="json">对象。</param><param name="name">字段名。</param><returns>字段文本。</returns>
    private static string? Field(JsonElement? json, string name) => json is { ValueKind: JsonValueKind.Object } item &&
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    /// <summary>【CodingAgent】【显示数值】只读取有限数值，忽略未知或损坏的可选元数据。</summary><param name="json">对象。</param><param name="name">字段名。</param><returns>数值或空值。</returns>
    private static double? Number(JsonElement json, string name) => json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;
    /// <summary>【CodingAgent】【显示 JSON】接受宿主常用参数类型，不使用反射序列化。</summary><param name="value">参数或详情。</param><returns>可独立使用的 JSON 值。</returns>
    private static JsonElement? Json(object? value)
    {
        if (value is JsonElement element) return element;
        var text = value is JsonNode node ? node.ToJsonString() : value as string;
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
        catch (JsonException) { return null; }
    }
    /// <summary>【CodingAgent】【脚本头识别】兼容独立协议头和与首个文本块合并的协议头。</summary><returns>只匹配结果开头的表达式。</returns>
    [GeneratedRegex(@"\AScript (completed|failed)\r?\nWall time [\d.]+ seconds\r?\nOutput:\r?\n", RegexOptions.CultureInvariant)]
    private static partial Regex ScriptHeader();
}
