using System.Text;
using System.Text.Json;

namespace Tau.Ai;

public static class JsonlSecretRedactor
{
    public static string RedactLine(string? line, TauSecretRedactor? redactor = null)
    {
        if (line is null)
        {
            return string.Empty;
        }

        if (line.Length == 0 || string.IsNullOrWhiteSpace(line))
        {
            return line;
        }

        redactor ??= new TauSecretRedactor(enabled: true);
        if (!redactor.Enabled)
        {
            return line;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                WriteRedactedValue(document.RootElement, writer, redactor);
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            return redactor.Redact(line);
        }
    }

    public static IReadOnlyList<string> RedactLines(IEnumerable<string> lines, TauSecretRedactor? redactor = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        redactor ??= new TauSecretRedactor(enabled: true);
        return lines.Select(line => RedactLine(line, redactor)).ToArray();
    }

    private static void WriteRedactedValue(JsonElement element, Utf8JsonWriter writer, TauSecretRedactor redactor)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteRedactedValue(property.Value, writer, redactor);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteRedactedValue(item, writer, redactor);
                }
                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                writer.WriteStringValue(redactor.Redact(element.GetString()));
                break;

            case JsonValueKind.Number:
                element.WriteTo(writer);
                break;

            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;

            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;

            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }
}
