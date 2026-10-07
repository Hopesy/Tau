// 作者：xxx
using System.Globalization;
using System.Text;

namespace Tau.CodingAgent.Tools;

/// <summary>【CodingAgent】【编辑详情】显示差异、标准补丁和新文件首个变更行。</summary>
public sealed record CodingAgentEditDetails(string Diff, string Patch, int? FirstChangedLine);

/// <summary>【CodingAgent】【编辑差异】以行级最短编辑路径生成显示差异和可应用的 unified patch。</summary>
internal static class CodingAgentEditDiff
{
    private sealed record Node(char Kind, string Text, Node? Previous);
    private sealed record Line(char Kind, string Text, int OldLine, int NewLine);

    /// <summary>【CodingAgent】【差异生成】生成四行上下文的显示结果与补丁，保留末行换行差异。</summary>
    /// <param name="path">补丁中的文件路径。</param><param name="original">原始 LF 文本。</param><param name="updated">更新后的 LF 文本。</param><returns>完整差异详情。</returns>
    internal static CodingAgentEditDetails Create(string path, string original, string updated)
    {
        var lines = Compare(CodingAgentEditMatching.SplitLines(original), CodingAgentEditMatching.SplitLines(updated));
        var width = Math.Max(original.Split('\n').Length, updated.Split('\n').Length).ToString(CultureInfo.InvariantCulture).Length;
        var display = new List<string>();
        int? first = null;
        for (var index = 0; index < lines.Count;)
        {
            var end = index + 1;
            while (end < lines.Count && lines[end].Kind == lines[index].Kind) end++;
            if (lines[index].Kind != ' ')
            {
                first ??= lines[index].NewLine;
                for (var at = index; at < end; at++) display.Add(FormatLine(lines[at], width));
            }
            else
            {
                var before = index > 0;
                var after = end < lines.Count;
                var length = end - index;
                if (before && after && length <= 8)
                    for (var at = index; at < end; at++) display.Add(FormatLine(lines[at], width));
                else
                {
                    var leading = before ? Math.Min(4, length) : 0;
                    var trailing = after ? Math.Min(4, length - leading) : 0;
                    for (var at = index; at < index + leading; at++) display.Add(FormatLine(lines[at], width));
                    if ((before || after) && length > leading + trailing) display.Add(" " + new string(' ', width) + " ...");
                    for (var at = end - trailing; at < end; at++) display.Add(FormatLine(lines[at], width));
                }
            }
            index = end;
        }
        return new(string.Join('\n', display), CreatePatch(path, lines), first);
    }

    /// <summary>【CodingAgent】【行格式】输出变更符号、补齐宽度的行号及行文本。</summary>
    /// <param name="line">差异行。</param><param name="width">行号宽度。</param><returns>显示行。</returns>
    private static string FormatLine(Line line, int width) => line.Kind
        + (line.Kind == '+' ? line.NewLine : line.OldLine).ToString(CultureInfo.InvariantCulture).PadLeft(width)
        + " " + line.Text.TrimEnd('\n');

    /// <summary>【CodingAgent】【最短路径】以 Myers 对角线搜索计算插入、删除和相同行，路径节点共享历史。</summary>
    /// <param name="oldLines">旧完整行。</param><param name="newLines">新完整行。</param><returns>按文件顺序排列的差异行。</returns>
    private static IReadOnlyList<Line> Compare(IReadOnlyList<string> oldLines, IReadOnlyList<string> newLines)
    {
        var maximum = oldLines.Count + newLines.Count;
        var offset = maximum + 1;
        var positions = new int[maximum * 2 + 3];
        var paths = new Node?[positions.Length];
        for (var depth = 0; depth <= maximum; depth++)
        {
            for (var diagonal = -depth; diagonal <= depth; diagonal += 2)
            {
                var at = offset + diagonal;
                var insert = diagonal == -depth || diagonal != depth && positions[at - 1] < positions[at + 1];
                var x = insert ? positions[at + 1] : positions[at - 1] + 1;
                var y = x - diagonal;
                var node = paths[insert ? at + 1 : at - 1];
                if (depth > 0)
                {
                    if (insert && y > 0 && y <= newLines.Count) node = new('+', newLines[y - 1], node);
                    else if (!insert && x > 0 && x <= oldLines.Count) node = new('-', oldLines[x - 1], node);
                }
                while (x < oldLines.Count && y < newLines.Count && oldLines[x] == newLines[y])
                { node = new(' ', oldLines[x], node); x++; y++; }
                positions[at] = x; paths[at] = node;
                if (x < oldLines.Count || y < newLines.Count) continue;
                var reversed = new List<Node>();
                for (; node is not null; node = node.Previous) reversed.Add(node);
                reversed.Reverse();
                var oldLine = 1;
                var newLine = 1;
                var result = new List<Line>(reversed.Count);
                foreach (var item in reversed)
                {
                    result.Add(new(item.Kind, item.Text, oldLine, newLine));
                    if (item.Kind != '+') oldLine++;
                    if (item.Kind != '-') newLine++;
                }
                return result;
            }
        }
        return [];
    }

    /// <summary>【CodingAgent】【标准补丁】合并相邻的四行上下文块，并标注未以换行结束的末行。</summary>
    /// <param name="path">文件显示路径。</param><param name="lines">完整差异行。</param><returns>标准 unified patch。</returns>
    private static string CreatePatch(string path, IReadOnlyList<Line> lines)
    {
        var ranges = new List<(int Start, int End)>();
        for (var index = 0; index < lines.Count; index++)
        {
            if (lines[index].Kind == ' ') continue;
            var start = index;
            for (var count = 0; start > 0 && count < 4 && lines[start - 1].Kind == ' '; count++) start--;
            var end = index + 1;
            while (end < lines.Count && lines[end].Kind != ' ') end++;
            var lastChange = end;
            for (var count = 0; end < lines.Count && count < 4 && lines[end].Kind == ' '; count++) end++;
            if (ranges.Count > 0 && start <= ranges[^1].End) ranges[^1] = (ranges[^1].Start, Math.Max(end, ranges[^1].End));
            else ranges.Add((start, end));
            index = lastChange - 1;
        }
        var patch = new StringBuilder().Append("--- ").Append(path).Append("\n+++ ").Append(path).Append('\n');
        foreach (var range in ranges)
        {
            var oldCount = 0;
            var newCount = 0;
            for (var index = range.Start; index < range.End; index++)
            { if (lines[index].Kind != '+') oldCount++; if (lines[index].Kind != '-') newCount++; }
            var oldStart = lines[range.Start].OldLine - (oldCount == 0 ? 1 : 0);
            var newStart = lines[range.Start].NewLine - (newCount == 0 ? 1 : 0);
            patch.Append($"@@ -{oldStart},{oldCount} +{newStart},{newCount} @@\n");
            for (var index = range.Start; index < range.End; index++)
            {
                var line = lines[index];
                patch.Append(line.Kind).Append(line.Text);
                if (!line.Text.EndsWith('\n')) patch.Append("\n\\ No newline at end of file\n");
            }
        }
        return patch.ToString();
    }
}
