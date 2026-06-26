using System.Text;

namespace Tau.Tui.Rendering;

public sealed class TuiSyntaxHighlightTheme
{
    private const string ResetForeground = "\u001b[39m";

    private static readonly IReadOnlyDictionary<string, (int R, int G, int B)> DefaultRgbColors =
        new Dictionary<string, (int R, int G, int B)>(StringComparer.Ordinal)
        {
            ["default"] = (212, 212, 212),
            ["comment"] = (106, 153, 85),
            ["keyword"] = (86, 156, 214),
            ["function"] = (220, 220, 170),
            ["variable"] = (156, 220, 254),
            ["string"] = (206, 145, 120),
            ["number"] = (181, 206, 168),
            ["type"] = (78, 201, 176),
            ["operator"] = (212, 212, 212),
            ["punctuation"] = (212, 212, 212),
            ["meta"] = (128, 128, 128),
            ["addition"] = (181, 189, 104),
            ["deletion"] = (204, 102, 102)
        };

    private static readonly IReadOnlyDictionary<string, string[]> ThemeColorKeys =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["default"] = ["syntaxDefault", "text"],
            ["comment"] = ["syntaxComment"],
            ["keyword"] = ["syntaxKeyword"],
            ["function"] = ["syntaxFunction"],
            ["variable"] = ["syntaxVariable"],
            ["string"] = ["syntaxString"],
            ["number"] = ["syntaxNumber"],
            ["type"] = ["syntaxType"],
            ["operator"] = ["syntaxOperator"],
            ["punctuation"] = ["syntaxPunctuation"],
            ["meta"] = ["syntaxMeta", "dim"],
            ["addition"] = ["syntaxAddition", "toolDiffAdded", "success"],
            ["deletion"] = ["syntaxDeletion", "toolDiffRemoved", "error"]
        };

    private readonly IReadOnlyDictionary<string, Func<string, string>> _formatters;

    public TuiSyntaxHighlightTheme(
        IReadOnlyDictionary<string, Func<string, string>>? formatters = null,
        Func<string, string>? defaultFormatter = null)
    {
        _formatters = formatters is null
            ? new Dictionary<string, Func<string, string>>(StringComparer.Ordinal)
            : new Dictionary<string, Func<string, string>>(formatters, StringComparer.Ordinal);
        DefaultFormatter = defaultFormatter;
    }

    public static TuiSyntaxHighlightTheme Default { get; } = CreateDefault();

    public Func<string, string>? DefaultFormatter { get; }

    public static TuiSyntaxHighlightTheme FromAnsiColors(IReadOnlyDictionary<string, string> colors)
    {
        ArgumentNullException.ThrowIfNull(colors);

        var formatters = new Dictionary<string, Func<string, string>>(StringComparer.Ordinal);
        foreach (var (scope, themeKeys) in ThemeColorKeys)
        {
            if (TryCreateAnsiForeground(colors, themeKeys, out var formatter))
            {
                formatters[scope] = formatter;
            }
        }

        return new TuiSyntaxHighlightTheme(
            formatters,
            formatters.TryGetValue("default", out var defaultFormatter) ? defaultFormatter : null);
    }

    public string Format(string scope, string text)
    {
        if (text.Length == 0)
        {
            return text;
        }

        return GetScopeFormatter(scope)?.Invoke(text) ??
            DefaultFormatter?.Invoke(text) ??
            text;
    }

    private Func<string, string>? GetScopeFormatter(string scope)
    {
        if (_formatters.TryGetValue(scope, out var exact))
        {
            return exact;
        }

        var dotIndex = scope.IndexOf('.', StringComparison.Ordinal);
        if (dotIndex > 0 && _formatters.TryGetValue(scope[..dotIndex], out var dotPrefix))
        {
            return dotPrefix;
        }

        var dashIndex = scope.IndexOf('-', StringComparison.Ordinal);
        return dashIndex > 0 && _formatters.TryGetValue(scope[..dashIndex], out var dashPrefix)
            ? dashPrefix
            : null;
    }

    private static TuiSyntaxHighlightTheme CreateDefault()
    {
        var formatters = DefaultRgbColors.ToDictionary(
            static pair => pair.Key,
            static pair => Foreground(pair.Value.R, pair.Value.G, pair.Value.B),
            StringComparer.Ordinal);
        return new TuiSyntaxHighlightTheme(formatters, formatters["default"]);
    }

    private static bool TryCreateAnsiForeground(string color, out Func<string, string> formatter)
    {
        formatter = static value => value;
        if (string.IsNullOrWhiteSpace(color))
        {
            return false;
        }

        var trimmed = color.Trim();
        if (trimmed.StartsWith('#') &&
            trimmed.Length == 7 &&
            byte.TryParse(trimmed.AsSpan(1, 2), System.Globalization.NumberStyles.HexNumber, null, out var r) &&
            byte.TryParse(trimmed.AsSpan(3, 2), System.Globalization.NumberStyles.HexNumber, null, out var g) &&
            byte.TryParse(trimmed.AsSpan(5, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
        {
            formatter = Foreground(r, g, b);
            return true;
        }

        if (int.TryParse(trimmed, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var ansi) &&
            ansi is >= 0 and <= 255)
        {
            formatter = value => $"\u001b[38;5;{ansi}m{value}{ResetForeground}";
            return true;
        }

        return false;
    }

    private static bool TryCreateAnsiForeground(
        IReadOnlyDictionary<string, string> colors,
        IReadOnlyList<string> keys,
        out Func<string, string> formatter)
    {
        foreach (var key in keys)
        {
            if (colors.TryGetValue(key, out var color) &&
                TryCreateAnsiForeground(color, out formatter))
            {
                return true;
            }
        }

        formatter = static value => value;
        return false;
    }

    private static Func<string, string> Foreground(int r, int g, int b) =>
        value => $"\u001b[38;2;{r};{g};{b}m{value}{ResetForeground}";
}

public static class TuiSyntaxHighlighter
{
    private static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "break", "case", "catch", "class", "const", "continue", "default",
        "delegate", "do", "else", "enum", "event", "explicit", "extern", "finally", "fixed", "for",
        "foreach", "goto", "if", "implicit", "in", "interface", "internal", "is", "lock", "namespace",
        "new", "operator", "out", "override", "params", "private", "protected", "public", "readonly",
        "ref", "return", "sealed", "sizeof", "stackalloc", "static", "struct", "switch", "this", "throw",
        "try", "typeof", "unchecked", "unsafe", "using", "virtual", "void", "volatile", "while", "with",
        "yield", "record", "init", "required", "file", "scoped", "true", "false", "null"
    };

    private static readonly HashSet<string> CSharpTypes = new(StringComparer.Ordinal)
    {
        "bool", "byte", "char", "decimal", "double", "float", "int", "long", "object", "sbyte",
        "short", "string", "uint", "ulong", "ushort", "var", "dynamic"
    };

    private static readonly HashSet<string> JavaScriptKeywords = new(StringComparer.Ordinal)
    {
        "as", "async", "await", "break", "case", "catch", "class", "const", "continue", "debugger",
        "default", "delete", "do", "else", "export", "extends", "finally", "for", "from", "function",
        "get", "if", "import", "in", "instanceof", "let", "new", "of", "return", "set", "static",
        "super", "switch", "this", "throw", "try", "typeof", "var", "void", "while", "with", "yield",
        "true", "false", "null", "undefined"
    };

    private static readonly HashSet<string> JavaScriptTypes = new(StringComparer.Ordinal)
    {
        "Array", "Boolean", "Date", "Map", "Number", "Object", "Promise", "Record", "Set", "String",
        "Symbol", "unknown", "never", "any", "boolean", "number", "string", "bigint"
    };

    private static readonly HashSet<string> PythonKeywords = new(StringComparer.Ordinal)
    {
        "and", "as", "assert", "async", "await", "break", "class", "continue", "def", "del", "elif",
        "else", "except", "False", "finally", "for", "from", "global", "if", "import", "in", "is",
        "lambda", "None", "nonlocal", "not", "or", "pass", "raise", "return", "True", "try", "while",
        "with", "yield"
    };

    private static readonly HashSet<string> PowerShellKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "begin", "break", "catch", "class", "continue", "data", "do", "dynamicparam", "else", "elseif",
        "end", "enum", "exit", "filter", "finally", "for", "foreach", "from", "function", "if", "in",
        "param", "process", "return", "switch", "throw", "trap", "try", "until", "using", "while"
    };

    private static readonly HashSet<string> ShellKeywords = new(StringComparer.Ordinal)
    {
        "case", "do", "done", "elif", "else", "esac", "fi", "for", "function", "if", "in", "select",
        "then", "until", "while"
    };

    public static IReadOnlyList<string> HighlightLines(string code, string? language, TuiSyntaxHighlightTheme? theme = null)
    {
        var normalizedCode = (code ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var normalizedLanguage = NormalizeLanguage(language);
        var effectiveTheme = theme ?? TuiSyntaxHighlightTheme.Default;
        var lines = normalizedCode.Split('\n');
        var highlighted = new string[lines.Length];

        for (var i = 0; i < lines.Length; i++)
        {
            highlighted[i] = HighlightLine(lines[i], normalizedLanguage, effectiveTheme);
        }

        return highlighted;
    }

    public static bool SupportsLanguage(string? language) => NormalizeLanguage(language) is not null;

    private static string HighlightLine(string line, string? language, TuiSyntaxHighlightTheme theme) =>
        language switch
        {
            "json" => HighlightJsonLine(line, theme),
            "csharp" => HighlightCStyleLine(line, CSharpKeywords, CSharpTypes, language, theme),
            "javascript" => HighlightCStyleLine(line, JavaScriptKeywords, JavaScriptTypes, language, theme),
            "python" => HighlightShellLikeLine(line, PythonKeywords, highlightDollarVariables: false, pythonDecorators: true, theme),
            "powershell" => HighlightShellLikeLine(line, PowerShellKeywords, highlightDollarVariables: true, pythonDecorators: false, theme),
            "shell" => HighlightShellLikeLine(line, ShellKeywords, highlightDollarVariables: true, pythonDecorators: false, theme),
            "xml" => HighlightXmlLine(line, theme),
            "diff" => HighlightDiffLine(line, theme),
            _ => Style(theme, TokenKind.Default, line)
        };

    private static string? NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        var trimmed = language.Trim();
        var separator = trimmed.IndexOfAny([' ', '\t', ':', ',']);
        var token = separator < 0 ? trimmed : trimmed[..separator];
        return token.TrimStart('.').ToLowerInvariant() switch
        {
            "cs" or "c#" or "csharp" => "csharp",
            "js" or "jsx" or "mjs" or "cjs" or "javascript" => "javascript",
            "ts" or "tsx" or "typescript" => "javascript",
            "py" or "python" or "python3" => "python",
            "ps1" or "psm1" or "psd1" or "pwsh" or "powershell" => "powershell",
            "bash" or "sh" or "shell" or "zsh" => "shell",
            "html" or "htm" or "xml" or "xaml" or "csproj" or "props" or "targets" => "xml",
            "patch" or "diff" => "diff",
            "json" or "jsonc" => "json",
            _ => null
        };
    }

    private static string HighlightJsonLine(string line, TuiSyntaxHighlightTheme theme)
    {
        var builder = new StringBuilder();
        var index = 0;
        while (index < line.Length)
        {
            var current = line[index];
            if (current == '"')
            {
                var end = FindStringEnd(line, index, '"', '\\');
                var kind = IsJsonPropertyName(line, end) ? TokenKind.Variable : TokenKind.String;
                builder.Append(Style(theme, kind, line[index..end]));
                index = end;
                continue;
            }

            if (current == '-' || char.IsDigit(current))
            {
                var end = ReadJsonNumber(line, index);
                if (end > index)
                {
                    builder.Append(Style(theme, TokenKind.Number, line[index..end]));
                    index = end;
                    continue;
                }
            }

            if (IsIdentifierStart(current))
            {
                var end = ReadIdentifier(line, index);
                var token = line[index..end];
                builder.Append(token is "true" or "false" or "null" ? Style(theme, TokenKind.Keyword, token) : token);
                index = end;
                continue;
            }

            builder.Append(IsJsonOperator(current) ? Style(theme, TokenKind.Punctuation, current.ToString()) : current);
            index++;
        }

        return builder.ToString();
    }

    private static string HighlightCStyleLine(
        string line,
        ISet<string> keywords,
        ISet<string> types,
        string language,
        TuiSyntaxHighlightTheme theme)
    {
        var builder = new StringBuilder();
        var index = 0;
        while (index < line.Length)
        {
            if (line[index] == '/' && index + 1 < line.Length && line[index + 1] == '/')
            {
                builder.Append(Style(theme, TokenKind.Comment, line[index..]));
                break;
            }

            if (line[index] == '/' && index + 1 < line.Length && line[index + 1] == '*')
            {
                var end = line.IndexOf("*/", index + 2, StringComparison.Ordinal);
                end = end < 0 ? line.Length : end + 2;
                builder.Append(Style(theme, TokenKind.Comment, line[index..end]));
                index = end;
                continue;
            }

            if (language == "csharp" && TryReadCSharpStringPrefix(line, index, out var prefixedStringEnd))
            {
                builder.Append(Style(theme, TokenKind.String, line[index..prefixedStringEnd]));
                index = prefixedStringEnd;
                continue;
            }

            if (line[index] is '"' or '\'')
            {
                var quote = line[index];
                var end = FindStringEnd(line, index, quote, '\\');
                builder.Append(Style(theme, TokenKind.String, line[index..end]));
                index = end;
                continue;
            }

            if (language == "javascript" && line[index] == '/' && TryReadJavaScriptRegex(line, index, out var regexEnd))
            {
                builder.Append(Style(theme, TokenKind.String, line[index..regexEnd]));
                index = regexEnd;
                continue;
            }

            if (language == "javascript" && line[index] == '$')
            {
                var end = ReadShellVariable(line, index);
                if (end > index + 1)
                {
                    builder.Append(Style(theme, TokenKind.Variable, line[index..end]));
                    index = end;
                    continue;
                }
            }

            if (char.IsDigit(line[index]))
            {
                var end = ReadNumber(line, index);
                builder.Append(Style(theme, TokenKind.Number, line[index..end]));
                index = end;
                continue;
            }

            if (IsIdentifierStart(line[index]) || line[index] == '@')
            {
                var start = line[index] == '@' && index + 1 < line.Length && IsIdentifierStart(line[index + 1])
                    ? index + 1
                    : index;
                var end = ReadIdentifier(line, start);
                var token = line[start..end];
                if (start > index)
                {
                    builder.Append(line[index..start]);
                }

                var next = NextNonWhitespace(line, end);
                if (keywords.Contains(token))
                {
                    builder.Append(Style(theme, TokenKind.Keyword, token));
                }
                else if (types.Contains(token) || (token.Length > 0 && char.IsUpper(token[0]) && next != '('))
                {
                    builder.Append(Style(theme, TokenKind.Type, token));
                }
                else if (next == '(')
                {
                    builder.Append(Style(theme, TokenKind.Function, token));
                }
                else
                {
                    builder.Append(token);
                }

                index = end;
                continue;
            }

            builder.Append(IsCodeOperator(line[index]) ? Style(theme, TokenKind.Operator, line[index].ToString()) : line[index]);
            index++;
        }

        return builder.ToString();
    }

    private static string HighlightShellLikeLine(
        string line,
        ISet<string> keywords,
        bool highlightDollarVariables,
        bool pythonDecorators,
        TuiSyntaxHighlightTheme theme)
    {
        var builder = new StringBuilder();
        var index = 0;
        while (index < line.Length)
        {
            if (line[index] == '#')
            {
                builder.Append(Style(theme, TokenKind.Comment, line[index..]));
                break;
            }

            if (pythonDecorators && line[index] == '@' && index + 1 < line.Length && IsIdentifierStart(line[index + 1]))
            {
                var end = ReadIdentifier(line, index + 1);
                builder.Append(Style(theme, TokenKind.Meta, line[index..end]));
                index = end;
                continue;
            }

            if (line[index] is '"' or '\'')
            {
                var quote = line[index];
                var escape = quote == '"' ? '\\' : '\0';
                var end = FindStringEnd(line, index, quote, escape);
                builder.Append(Style(theme, TokenKind.String, line[index..end]));
                index = end;
                continue;
            }

            if (highlightDollarVariables && line[index] == '$')
            {
                var end = ReadShellVariable(line, index);
                if (end > index + 1)
                {
                    builder.Append(Style(theme, TokenKind.Variable, line[index..end]));
                    index = end;
                    continue;
                }
            }

            if (char.IsDigit(line[index]))
            {
                var end = ReadNumber(line, index);
                builder.Append(Style(theme, TokenKind.Number, line[index..end]));
                index = end;
                continue;
            }

            if (IsIdentifierStart(line[index]))
            {
                var end = ReadIdentifier(line, index);
                var token = line[index..end];
                var next = NextNonWhitespace(line, end);
                if (keywords.Contains(token))
                {
                    builder.Append(Style(theme, TokenKind.Keyword, token));
                }
                else if (next == '(')
                {
                    builder.Append(Style(theme, TokenKind.Function, token));
                }
                else
                {
                    builder.Append(token);
                }

                index = end;
                continue;
            }

            builder.Append(IsCodeOperator(line[index]) ? Style(theme, TokenKind.Operator, line[index].ToString()) : line[index]);
            index++;
        }

        return builder.ToString();
    }

    private static string HighlightXmlLine(string line, TuiSyntaxHighlightTheme theme)
    {
        var builder = new StringBuilder();
        var index = 0;
        while (index < line.Length)
        {
            if (line.IndexOf("<!--", index, StringComparison.Ordinal) == index)
            {
                var end = line.IndexOf("-->", index + 4, StringComparison.Ordinal);
                end = end < 0 ? line.Length : end + 3;
                builder.Append(Style(theme, TokenKind.Comment, line[index..end]));
                index = end;
                continue;
            }

            if (line[index] != '<')
            {
                builder.Append(line[index]);
                index++;
                continue;
            }

            var tagEnd = line.IndexOf('>', index + 1);
            if (tagEnd < 0)
            {
                builder.Append(line[index..]);
                break;
            }

            builder.Append(HighlightXmlTag(line[index..(tagEnd + 1)], theme));
            index = tagEnd + 1;
        }

        return builder.ToString();
    }

    private static string HighlightXmlTag(string tag, TuiSyntaxHighlightTheme theme)
    {
        var builder = new StringBuilder();
        var index = 0;
        while (index < tag.Length)
        {
            var current = tag[index];
            if (current is '<' or '>' or '/' or '?' or '!' or '=')
            {
                builder.Append(Style(theme, TokenKind.Punctuation, current.ToString()));
                index++;
                continue;
            }

            if (current is '"' or '\'')
            {
                var end = FindStringEnd(tag, index, current, '\0');
                builder.Append(Style(theme, TokenKind.String, tag[index..end]));
                index = end;
                continue;
            }

            if (IsIdentifierStart(current) || current is ':' or '-')
            {
                var end = index + 1;
                while (end < tag.Length && (IsIdentifierPart(tag[end]) || tag[end] is ':' or '-' or '.'))
                {
                    end++;
                }

                var token = tag[index..end];
                builder.Append(Style(theme, IsXmlTagName(tag, index) ? TokenKind.Keyword : TokenKind.Variable, token));
                index = end;
                continue;
            }

            builder.Append(current);
            index++;
        }

        return builder.ToString();
    }

    private static string HighlightDiffLine(string line, TuiSyntaxHighlightTheme theme)
    {
        if (line.StartsWith("@@", StringComparison.Ordinal) ||
            line.StartsWith("diff ", StringComparison.Ordinal) ||
            line.StartsWith("index ", StringComparison.Ordinal) ||
            line.StartsWith("+++", StringComparison.Ordinal) ||
            line.StartsWith("---", StringComparison.Ordinal))
        {
            return Style(theme, TokenKind.Meta, line);
        }

        if (line.StartsWith('+'))
        {
            return Style(theme, TokenKind.Addition, line);
        }

        return line.StartsWith('-') ? Style(theme, TokenKind.Deletion, line) : Style(theme, TokenKind.Default, line);
    }

    private static bool TryReadCSharpStringPrefix(string line, int index, out int end)
    {
        end = index;
        var prefixEnd = index;
        if (line[index] == '$')
        {
            prefixEnd++;
        }

        if (prefixEnd < line.Length && line[prefixEnd] == '@')
        {
            prefixEnd++;
        }

        if (prefixEnd == index && line[index] == '@')
        {
            prefixEnd++;
        }

        if (prefixEnd <= index || prefixEnd >= line.Length || line[prefixEnd] != '"')
        {
            return false;
        }

        end = line[prefixEnd - 1] == '@'
            ? FindVerbatimStringEnd(line, prefixEnd)
            : FindStringEnd(line, prefixEnd, '"', '\\');
        return true;
    }

    private static bool TryReadJavaScriptRegex(string line, int start, out int end)
    {
        end = start;
        var previous = PreviousNonWhitespace(line, start);
        if (previous is not null && previous.Value is not ('=' or '(' or '{' or '[' or ',' or ':' or ';' or '!' or '?' or '|'))
        {
            return false;
        }

        var index = start + 1;
        var inClass = false;
        while (index < line.Length)
        {
            if (line[index] == '\\')
            {
                index = Math.Min(index + 2, line.Length);
                continue;
            }

            if (line[index] == '[')
            {
                inClass = true;
            }
            else if (line[index] == ']')
            {
                inClass = false;
            }
            else if (line[index] == '/' && !inClass)
            {
                index++;
                while (index < line.Length && char.IsLetter(line[index]))
                {
                    index++;
                }

                end = index;
                return true;
            }

            index++;
        }

        return false;
    }

    private static int FindStringEnd(string text, int start, char quote, char escape)
    {
        var index = start + 1;
        while (index < text.Length)
        {
            if (escape != '\0' && text[index] == escape)
            {
                index = Math.Min(index + 2, text.Length);
                continue;
            }

            if (text[index] == quote)
            {
                return index + 1;
            }

            index++;
        }

        return text.Length;
    }

    private static int FindVerbatimStringEnd(string text, int start)
    {
        var index = start + 1;
        while (index < text.Length)
        {
            if (text[index] == '"')
            {
                if (index + 1 < text.Length && text[index + 1] == '"')
                {
                    index += 2;
                    continue;
                }

                return index + 1;
            }

            index++;
        }

        return text.Length;
    }

    private static bool IsJsonPropertyName(string line, int stringEnd)
    {
        var index = stringEnd;
        while (index < line.Length && char.IsWhiteSpace(line[index]))
        {
            index++;
        }

        return index < line.Length && line[index] == ':';
    }

    private static int ReadJsonNumber(string line, int start)
    {
        var index = start;
        if (index < line.Length && line[index] == '-')
        {
            index++;
        }

        var hasDigit = false;
        while (index < line.Length && char.IsDigit(line[index]))
        {
            hasDigit = true;
            index++;
        }

        if (!hasDigit)
        {
            return start;
        }

        if (index < line.Length && line[index] == '.')
        {
            index++;
            while (index < line.Length && char.IsDigit(line[index]))
            {
                index++;
            }
        }

        if (index < line.Length && line[index] is 'e' or 'E')
        {
            index++;
            if (index < line.Length && line[index] is '+' or '-')
            {
                index++;
            }

            while (index < line.Length && char.IsDigit(line[index]))
            {
                index++;
            }
        }

        return index;
    }

    private static int ReadNumber(string line, int start)
    {
        var index = start;
        while (index < line.Length && (char.IsLetterOrDigit(line[index]) || line[index] is '_' or '.'))
        {
            index++;
        }

        return index;
    }

    private static int ReadIdentifier(string line, int start)
    {
        var index = start;
        while (index < line.Length && IsIdentifierPart(line[index]))
        {
            index++;
        }

        return index;
    }

    private static int ReadShellVariable(string line, int start)
    {
        if (start + 1 >= line.Length)
        {
            return start + 1;
        }

        if (line[start + 1] == '{')
        {
            var end = line.IndexOf('}', start + 2);
            return end < 0 ? start + 1 : end + 1;
        }

        if (line[start + 1] is '?' or '!' or '$' or '#')
        {
            return start + 2;
        }

        var index = start + 1;
        while (index < line.Length && (IsIdentifierPart(line[index]) || line[index] == ':'))
        {
            index++;
        }

        return index;
    }

    private static char? PreviousNonWhitespace(string line, int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            if (!char.IsWhiteSpace(line[i]))
            {
                return line[i];
            }
        }

        return null;
    }

    private static char? NextNonWhitespace(string line, int index)
    {
        for (var i = index; i < line.Length; i++)
        {
            if (!char.IsWhiteSpace(line[i]))
            {
                return line[i];
            }
        }

        return null;
    }

    private static bool IsIdentifierStart(char value) => char.IsLetter(value) || value == '_';

    private static bool IsIdentifierPart(char value) => char.IsLetterOrDigit(value) || value == '_';

    private static bool IsJsonOperator(char value) => value is '{' or '}' or '[' or ']' or ':' or ',';

    private static bool IsCodeOperator(char value) =>
        value is '{' or '}' or '[' or ']' or '(' or ')' or ';' or ',' or '.' or '<' or '>' or '+' or '-' or '*' or '/' or '=' or '!' or '?' or ':' or '&' or '|' or '%' or '^' or '~';

    private static bool IsXmlTagName(string tag, int index)
    {
        var previous = index - 1;
        while (previous >= 0 && char.IsWhiteSpace(tag[previous]))
        {
            previous--;
        }

        return previous >= 0 && tag[previous] is '<' or '/' or '?' or '!';
    }

    private static string Style(TuiSyntaxHighlightTheme theme, TokenKind kind, string text)
    {
        if (text.Length == 0)
        {
            return text;
        }

        var scope = kind switch
        {
            TokenKind.Comment => "comment",
            TokenKind.Keyword => "keyword",
            TokenKind.Function => "function",
            TokenKind.Variable => "variable",
            TokenKind.String => "string",
            TokenKind.Number => "number",
            TokenKind.Type => "type",
            TokenKind.Operator => "operator",
            TokenKind.Punctuation => "punctuation",
            TokenKind.Meta => "meta",
            TokenKind.Addition => "addition",
            TokenKind.Deletion => "deletion",
            _ => "default"
        };
        return theme.Format(scope, text);
    }

    private enum TokenKind
    {
        Default,
        Comment,
        Keyword,
        Function,
        Variable,
        String,
        Number,
        Type,
        Operator,
        Punctuation,
        Meta,
        Addition,
        Deletion
    }
}
