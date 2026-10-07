// 作者：xxx
using System.Text;

namespace Tau.CodingAgent.Tools;

/// <summary>【CodingAgent】【编辑块】基于原始文件定位的一次替换。</summary>
public sealed record CodingAgentEdit(string OldText, string NewText);

/// <summary>【CodingAgent】【编辑匹配】对齐原生精确优先、Unicode 容差、唯一性与不相交替换规则。</summary>
internal static class CodingAgentEditMatching
{
    private sealed record Match(int EditIndex, int Index, int Length, string NewText);

    /// <summary>【CodingAgent】【换行规范】把 CRLF 和独立 CR 转换为 LF。</summary>
    /// <param name="text">原文。</param><returns>规范文本。</returns>
    internal static string NormalizeLines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    /// <summary>【CodingAgent】【容差规范】规范兼容字符、行尾空白、智能引号、横线与特殊空格。</summary>
    /// <param name="text">原文。</param><returns>容差匹配文本。</returns>
    internal static string NormalizeFuzzy(string text)
    {
        var normalized = string.Join('\n', text.Normalize(NormalizationForm.FormKC).Split('\n').Select(line => line.TrimEnd()));
        var result = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
            result.Append(character switch
            {
                >= '\u2018' and <= '\u201b' => '\'', >= '\u201c' and <= '\u201f' => '"',
                >= '\u2010' and <= '\u2015' or '\u2212' => '-',
                '\u00a0' or >= '\u2002' and <= '\u200a' or '\u202f' or '\u205f' or '\u3000' => ' ',
                _ => character
            });
        return result.ToString();
    }

    /// <summary>【CodingAgent】【批量匹配】所有替换针对同一原文定位，验证通过后从后向前应用。</summary>
    /// <param name="content">LF 原文。</param><param name="edits">替换列表。</param><param name="path">用户提供的路径，用于错误说明。</param><returns>保留未触及行的替换结果。</returns>
    internal static string Apply(string content, IReadOnlyList<CodingAgentEdit> edits, string path)
    {
        if (edits.Count == 0) throw new InvalidOperationException("Edit tool input is invalid. edits must contain at least one replacement.");
        var normalized = edits.Select(edit => new CodingAgentEdit(NormalizeLines(edit.OldText), NormalizeLines(edit.NewText))).ToArray();
        for (var index = 0; index < normalized.Length; index++)
            if (normalized[index].OldText.Length == 0) throw new InvalidOperationException($"{(edits.Count == 1 ? "oldText" : $"edits[{index}].oldText")} must not be empty in {path}.");
        var fuzzy = NormalizeFuzzy(content);
        var useFuzzy = normalized.Any(edit => !content.Contains(edit.OldText, StringComparison.Ordinal) && fuzzy.Contains(NormalizeFuzzy(edit.OldText), StringComparison.Ordinal));
        var basis = useFuzzy ? fuzzy : content;
        var matches = new List<Match>();
        for (var index = 0; index < normalized.Length; index++)
        {
            var edit = normalized[index];
            var old = edit.OldText;
            var at = basis.IndexOf(old, StringComparison.Ordinal);
            if (at < 0) { old = NormalizeFuzzy(old); at = NormalizeFuzzy(basis).IndexOf(old, StringComparison.Ordinal); }
            if (at < 0) throw new InvalidOperationException(edits.Count == 1
                ? $"Could not find the exact text in {path}. The old text must match exactly including all whitespace and newlines."
                : $"Could not find edits[{index}] in {path}. The oldText must match exactly including all whitespace and newlines.");
            var occurrences = CountOccurrences(NormalizeFuzzy(basis), NormalizeFuzzy(edit.OldText));
            if (occurrences > 1) throw new InvalidOperationException(edits.Count == 1
                ? $"Found {occurrences} occurrences of the text in {path}. The text must be unique. Please provide more context to make it unique."
                : $"Found {occurrences} occurrences of edits[{index}] in {path}. Each oldText must be unique. Please provide more context to make it unique.");
            matches.Add(new(index, at, old.Length, edit.NewText));
        }
        matches.Sort((left, right) => left.Index.CompareTo(right.Index));
        for (var index = 1; index < matches.Count; index++)
            if (matches[index - 1].Index + matches[index - 1].Length > matches[index].Index)
                throw new InvalidOperationException($"edits[{matches[index - 1].EditIndex}] and edits[{matches[index].EditIndex}] overlap in {path}. Merge them into one edit or target disjoint regions.");
        var updated = useFuzzy ? PreserveUntouchedLines(content, basis, matches) : Replace(basis, matches);
        if (updated == content) throw new InvalidOperationException($"No changes made to {path}. The replacement{(edits.Count == 1 ? "" : "s")} produced identical content."
            + (edits.Count == 1 ? " This might indicate an issue with special characters or the text not existing as expected." : ""));
        return updated;
    }

    /// <summary>【CodingAgent】【唯一计数】按不重叠文本块计数，匹配原生 split 语义。</summary>
    /// <param name="content">规范内容。</param><param name="search">规范旧文本。</param><returns>出现次数。</returns>
    private static int CountOccurrences(string content, string search)
    {
        if (search.Length == 0) return Math.Max(0, content.Length - 1);
        var count = 0;
        for (var offset = 0; (offset = content.IndexOf(search, offset, StringComparison.Ordinal)) >= 0; offset += search.Length) count++;
        return count;
    }

    /// <summary>【CodingAgent】【反向替换】倒序应用已经验证的匹配，保持其余位置不变。</summary>
    /// <param name="content">基准文本。</param><param name="matches">按起点排序的匹配。</param><param name="offset">文本在完整基准中的起点。</param><returns>替换后的文本。</returns>
    private static string Replace(string content, IReadOnlyList<Match> matches, int offset = 0)
    {
        var result = new StringBuilder(content);
        for (var index = matches.Count - 1; index >= 0; index--)
            result.Remove(matches[index].Index - offset, matches[index].Length).Insert(matches[index].Index - offset, matches[index].NewText);
        return result.ToString();
    }

    /// <summary>【CodingAgent】【行级保留】仅将实际触及的行从容差空间写回，未触及行保留原有字符和空白。</summary>
    /// <param name="original">原始 LF 内容。</param><param name="basis">容差空间内容。</param><param name="matches">按起点排序的匹配。</param><returns>保留原行的结果。</returns>
    private static string PreserveUntouchedLines(string original, string basis, IReadOnlyList<Match> matches)
    {
        var originals = SplitLines(original);
        var lines = SplitLines(basis);
        if (lines.Count != originals.Count) throw new InvalidOperationException("Cannot preserve unchanged lines because the base content has a different line count.");
        var starts = new int[lines.Count + 1];
        for (var index = 0; index < lines.Count; index++) starts[index + 1] = starts[index] + lines[index].Length;
        var groups = new List<(int Start, int End, List<Match> Matches)>();
        foreach (var match in matches)
        {
            var start = Array.FindIndex(starts, at => at > match.Index) - 1;
            if (start < 0) throw new InvalidOperationException("Replacement range is outside the base content.");
            var end = start + 1;
            while (end < lines.Count && starts[end] < match.Index + match.Length) end++;
            if (groups.Count > 0 && start < groups[^1].End)
            {
                var group = groups[^1]; group.Matches.Add(match);
                groups[^1] = (group.Start, Math.Max(end, group.End), group.Matches);
            }
            else groups.Add((start, end, [match]));
        }
        var result = new StringBuilder();
        var next = 0;
        foreach (var group in groups)
        {
            while (next < group.Start) result.Append(originals[next++]);
            result.Append(Replace(basis[starts[group.Start]..starts[group.End]], group.Matches, starts[group.Start]));
            next = group.End;
        }
        while (next < originals.Count) result.Append(originals[next++]);
        return result.ToString();
    }

    /// <summary>【CodingAgent】【完整行】切分并保留换行终止符，末尾不添加虚构空行。</summary>
    /// <param name="text">LF 内容。</param><returns>完整行集合。</returns>
    internal static IReadOnlyList<string> SplitLines(string text)
    {
        var result = new List<string>();
        var start = 0;
        for (var index = 0; index < text.Length; index++)
            if (text[index] == '\n') { result.Add(text[start..(index + 1)]); start = index + 1; }
        if (start < text.Length) result.Add(text[start..]);
        return result;
    }
}
