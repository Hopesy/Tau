using System.Text.Json;

namespace Tau.Ai;

public static class JsonSchemaHelpers
{
    public static JsonElement StringEnum(
        IEnumerable<string> values,
        string? description = null,
        string? defaultValue = null)
    {
        ArgumentNullException.ThrowIfNull(values);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "string");
            writer.WritePropertyName("enum");
            writer.WriteStartArray();
            foreach (var value in values)
            {
                writer.WriteStringValue(value);
            }

            writer.WriteEndArray();

            if (!string.IsNullOrWhiteSpace(description))
            {
                writer.WriteString("description", description);
            }

            if (defaultValue is not null)
            {
                writer.WriteString("default", defaultValue);
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }
}
