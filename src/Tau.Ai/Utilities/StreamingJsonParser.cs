using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tau.Ai;

public static class StreamingJsonParser
{
    private static readonly HashSet<char> ValidJsonEscapes =
    [
        '"',
        '\\',
        '/',
        'b',
        'f',
        'n',
        'r',
        't',
        'u'
    ];

    public static string RepairJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        var repaired = new StringBuilder(json.Length);
        var inString = false;

        for (var index = 0; index < json.Length; index++)
        {
            var current = json[index];
            if (!inString)
            {
                repaired.Append(current);
                if (current == '"')
                {
                    inString = true;
                }

                continue;
            }

            if (current == '"')
            {
                repaired.Append(current);
                inString = false;
                continue;
            }

            if (current == '\\')
            {
                if (index + 1 >= json.Length)
                {
                    repaired.Append(@"\\");
                    continue;
                }

                var next = json[index + 1];
                if (next == 'u')
                {
                    var unicodeDigits = index + 6 <= json.Length
                        ? json.AsSpan(index + 2, 4)
                        : ReadOnlySpan<char>.Empty;
                    if (unicodeDigits.Length == 4 && IsHexDigits(unicodeDigits))
                    {
                        repaired.Append(@"\u");
                        repaired.Append(unicodeDigits);
                        index += 5;
                        continue;
                    }
                }

                if (ValidJsonEscapes.Contains(next))
                {
                    repaired.Append('\\');
                    repaired.Append(next);
                    index++;
                    continue;
                }

                repaired.Append(@"\\");
                continue;
            }

            AppendEscapedControlCharacter(repaired, current);
        }

        return repaired.ToString();
    }

    public static JsonElement ParseJsonWithRepair(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            var repairedJson = RepairJson(json);
            if (repairedJson != json)
            {
                using var repaired = JsonDocument.Parse(repairedJson);
                return repaired.RootElement.Clone();
            }

            throw;
        }
    }

    public static JsonElement ParseStreamingJson(string? partialJson)
    {
        if (string.IsNullOrWhiteSpace(partialJson))
        {
            return EmptyObject();
        }

        try
        {
            return ParseJsonWithRepair(partialJson);
        }
        catch (JsonException)
        {
            var parser = new PartialJsonParser(partialJson);
            var parsed = parser.Parse();
            if (parsed.Success)
            {
                return ToElement(parsed.Node);
            }

            var repairedJson = RepairJson(partialJson);
            if (repairedJson != partialJson)
            {
                var repairedParser = new PartialJsonParser(repairedJson);
                var repairedParsed = repairedParser.Parse();
                if (repairedParsed.Success)
                {
                    return ToElement(repairedParsed.Node);
                }
            }

            return EmptyObject();
        }
    }

    internal static string ParseStreamingJsonRawText(string? partialJson) =>
        ParseStreamingJson(partialJson).GetRawText();

    internal static string ParseStreamingJsonObjectRawText(string? partialJson)
    {
        var parsed = ParseStreamingJson(partialJson);
        return parsed.ValueKind == JsonValueKind.Object ? parsed.GetRawText() : "{}";
    }

    private static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private static JsonElement ToElement(JsonNode? node)
    {
        using var document = JsonDocument.Parse(node?.ToJsonString() ?? "null");
        return document.RootElement.Clone();
    }

    private static bool IsHexDigits(ReadOnlySpan<char> value)
    {
        foreach (var c in value)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private static void AppendEscapedControlCharacter(StringBuilder builder, char value)
    {
        switch (value)
        {
            case >= '\u0000' and <= '\u001F':
                builder.Append(value switch
                {
                    '\b' => @"\b",
                    '\f' => @"\f",
                    '\n' => @"\n",
                    '\r' => @"\r",
                    '\t' => @"\t",
                    _ => FormattableString.Invariant($@"\u{(int)value:x4}")
                });
                break;
            default:
                builder.Append(value);
                break;
        }
    }

    private readonly struct ParseResult(bool success, JsonNode? node = null)
    {
        public bool Success { get; } = success;

        public JsonNode? Node { get; } = node;
    }

    private sealed class PartialJsonParser(string source)
    {
        private readonly string _source = source;
        private int _index;

        public ParseResult Parse()
        {
            SkipWhitespace();
            return ParseValue();
        }

        private ParseResult ParseValue()
        {
            SkipWhitespace();
            if (_index >= _source.Length)
            {
                return new ParseResult(false);
            }

            return _source[_index] switch
            {
                '{' => ParseObject(),
                '[' => ParseArray(),
                '"' => ParseStringValue(),
                't' => ParseLiteralPrefix("true", JsonValue.Create(true)),
                'f' => ParseLiteralPrefix("false", JsonValue.Create(false)),
                'n' => ParseLiteralPrefix("null", null),
                '-' or >= '0' and <= '9' => ParseNumber(),
                _ => new ParseResult(false)
            };
        }

        private ParseResult ParseObject()
        {
            _index++;
            var obj = new JsonObject();

            while (true)
            {
                SkipWhitespace();
                if (_index >= _source.Length)
                {
                    return new ParseResult(true, obj);
                }

                if (_source[_index] == '}')
                {
                    _index++;
                    return new ParseResult(true, obj);
                }

                if (_source[_index] == ',')
                {
                    _index++;
                    continue;
                }

                if (_source[_index] != '"')
                {
                    return new ParseResult(true, obj);
                }

                var keyResult = ParseString();
                if (!keyResult.Success || keyResult.Node is null)
                {
                    return new ParseResult(true, obj);
                }

                var key = keyResult.Node.GetValue<string>();
                SkipWhitespace();
                if (_index >= _source.Length || _source[_index] != ':')
                {
                    return new ParseResult(true, obj);
                }

                _index++;
                var value = ParseValue();
                if (!value.Success)
                {
                    return new ParseResult(true, obj);
                }

                obj[key] = value.Node;
                SkipWhitespace();
                if (_index < _source.Length && _source[_index] == ',')
                {
                    _index++;
                    continue;
                }

                if (_index < _source.Length && _source[_index] == '}')
                {
                    _index++;
                }

                return new ParseResult(true, obj);
            }
        }

        private ParseResult ParseArray()
        {
            _index++;
            var array = new JsonArray();

            while (true)
            {
                SkipWhitespace();
                if (_index >= _source.Length)
                {
                    return new ParseResult(true, array);
                }

                if (_source[_index] == ']')
                {
                    _index++;
                    return new ParseResult(true, array);
                }

                if (_source[_index] == ',')
                {
                    _index++;
                    continue;
                }

                var value = ParseValue();
                if (!value.Success)
                {
                    return new ParseResult(true, array);
                }

                array.Add(value.Node);
                SkipWhitespace();
                if (_index < _source.Length && _source[_index] == ',')
                {
                    _index++;
                    continue;
                }

                if (_index < _source.Length && _source[_index] == ']')
                {
                    _index++;
                }

                return new ParseResult(true, array);
            }
        }

        private ParseResult ParseStringValue() => ParseString();

        private ParseResult ParseString()
        {
            if (_index >= _source.Length || _source[_index] != '"')
            {
                return new ParseResult(false);
            }

            _index++;
            var builder = new StringBuilder();
            while (_index < _source.Length)
            {
                var current = _source[_index++];
                if (current == '"')
                {
                    return new ParseResult(true, JsonValue.Create(builder.ToString()));
                }

                if (current != '\\')
                {
                    builder.Append(current);
                    continue;
                }

                if (_index >= _source.Length)
                {
                    return new ParseResult(true, JsonValue.Create(builder.ToString()));
                }

                var escaped = _source[_index++];
                switch (escaped)
                {
                    case '"':
                    case '\\':
                    case '/':
                        builder.Append(escaped);
                        break;
                    case 'b':
                        builder.Append('\b');
                        break;
                    case 'f':
                        builder.Append('\f');
                        break;
                    case 'n':
                        builder.Append('\n');
                        break;
                    case 'r':
                        builder.Append('\r');
                        break;
                    case 't':
                        builder.Append('\t');
                        break;
                    case 'u':
                        if (_index + 4 <= _source.Length &&
                            ushort.TryParse(
                                _source.AsSpan(_index, 4),
                                NumberStyles.HexNumber,
                                CultureInfo.InvariantCulture,
                                out var codePoint))
                        {
                            builder.Append((char)codePoint);
                            _index += 4;
                        }
                        else
                        {
                            return new ParseResult(true, JsonValue.Create(builder.ToString()));
                        }

                        break;
                    default:
                        builder.Append(escaped);
                        break;
                }
            }

            return new ParseResult(true, JsonValue.Create(builder.ToString()));
        }

        private ParseResult ParseLiteralPrefix(string literal, JsonNode? node)
        {
            var remaining = _source.AsSpan(_index);
            var compareLength = Math.Min(remaining.Length, literal.Length);
            if (literal.AsSpan(0, compareLength).SequenceEqual(remaining[..compareLength]))
            {
                _index += compareLength;
                return new ParseResult(true, node);
            }

            return new ParseResult(false);
        }

        private ParseResult ParseNumber()
        {
            var start = _index;
            if (_source[_index] == '-')
            {
                _index++;
            }

            var digitStart = _index;
            while (_index < _source.Length && char.IsDigit(_source[_index]))
            {
                _index++;
            }

            if (_index == digitStart)
            {
                return new ParseResult(false);
            }

            if (_index < _source.Length && _source[_index] == '.')
            {
                _index++;
                var decimalDigitStart = _index;
                while (_index < _source.Length && char.IsDigit(_source[_index]))
                {
                    _index++;
                }

                if (_index == decimalDigitStart)
                {
                    return new ParseResult(false);
                }
            }

            if (_index < _source.Length && (_source[_index] == 'e' || _source[_index] == 'E'))
            {
                var exponentStart = _index;
                _index++;
                if (_index < _source.Length && (_source[_index] == '+' || _source[_index] == '-'))
                {
                    _index++;
                }

                var exponentDigitStart = _index;
                while (_index < _source.Length && char.IsDigit(_source[_index]))
                {
                    _index++;
                }

                if (_index == exponentDigitStart)
                {
                    _index = exponentStart;
                }
            }

            var raw = _source[start.._index];
            if (raw.IndexOfAny(['.', 'e', 'E']) < 0 &&
                long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
            {
                return new ParseResult(true, JsonValue.Create(integer));
            }

            return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                ? new ParseResult(true, JsonValue.Create(number))
                : new ParseResult(false);
        }

        private void SkipWhitespace()
        {
            while (_index < _source.Length && char.IsWhiteSpace(_source[_index]))
            {
                _index++;
            }
        }
    }
}
