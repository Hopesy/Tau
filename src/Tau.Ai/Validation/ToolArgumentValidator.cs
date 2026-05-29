using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tau.Ai;

public static class ToolArgumentValidator
{
    public static JsonElement ValidateToolCall(
        IReadOnlyList<Tool> tools,
        ToolCallContent toolCall)
    {
        var tool = tools.FirstOrDefault(tool => string.Equals(tool.Name, toolCall.Name, StringComparison.Ordinal));
        if (tool is null)
        {
            throw new ToolArgumentValidationException($"Tool \"{toolCall.Name}\" not found.");
        }

        return ValidateToolArguments(tool, toolCall);
    }

    public static JsonElement ValidateToolArguments(
        Tool tool,
        ToolCallContent toolCall)
    {
        var arguments = string.IsNullOrWhiteSpace(toolCall.Arguments)
            ? JsonDocument.Parse("{}").RootElement.Clone()
            : JsonDocument.Parse(toolCall.Arguments).RootElement.Clone();
        return ValidateToolArguments(tool, toolCall, arguments);
    }

    public static JsonElement ValidateToolArguments(
        Tool tool,
        ToolCallContent toolCall,
        JsonElement arguments)
    {
        if (!HasValidationRules(tool.ParameterSchema))
        {
            return arguments;
        }

        var errors = new List<string>();
        var node = JsonNode.Parse(arguments.GetRawText());
        node = ValidateSchema(tool.ParameterSchema, node, "root", errors);

        if (errors.Count > 0)
        {
            throw new ToolArgumentValidationException(FormatError(toolCall.Name, errors, arguments));
        }

        using var document = JsonDocument.Parse(node?.ToJsonString() ?? "null");
        return document.RootElement.Clone();
    }

    private static bool HasValidationRules(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return schema.TryGetProperty("type", out _) ||
               schema.TryGetProperty("properties", out _) ||
               schema.TryGetProperty("required", out _) ||
               schema.TryGetProperty("anyOf", out _) ||
               schema.TryGetProperty("enum", out _) ||
               schema.TryGetProperty("items", out _);
    }

    private static JsonNode? ValidateSchema(
        JsonElement schema,
        JsonNode? node,
        string path,
        List<string> errors)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return node;
        }

        if (schema.TryGetProperty("type", out var typeElement))
        {
            node = ValidateType(typeElement, node, path, errors);
        }

        if (schema.TryGetProperty("enum", out var enumElement))
        {
            ValidateEnum(enumElement, node, path, errors);
        }

        if (node is JsonObject obj)
        {
            ValidateRequired(schema, obj, path, errors);
            ValidateProperties(schema, obj, path, errors);

            if (schema.TryGetProperty("anyOf", out var anyOfElement))
            {
                node = ValidateAnyOf(anyOfElement, obj, path, errors);
            }
        }
        else if (node is JsonArray array && schema.TryGetProperty("items", out var itemsElement))
        {
            for (var i = 0; i < array.Count; i++)
            {
                var item = array[i];
                var validated = ValidateSchema(itemsElement, item, $"{path}/{i}", errors);
                if (!ReferenceEquals(validated, item))
                {
                    array[i] = validated;
                }
            }
        }

        return node;
    }

    private static JsonNode? ValidateAnyOf(
        JsonElement anyOfElement,
        JsonObject obj,
        string path,
        List<string> errors)
    {
        if (anyOfElement.ValueKind != JsonValueKind.Array)
        {
            return obj;
        }

        foreach (var branch in anyOfElement.EnumerateArray())
        {
            var branchErrors = new List<string>();
            var candidate = obj.DeepClone();
            ValidateSchema(branch, candidate, path, branchErrors);
            if (branchErrors.Count == 0)
            {
                return candidate;
            }
        }

        errors.Add($"{DisplayPath(path)}: must match at least one anyOf schema");
        return obj;
    }

    private static void ValidateRequired(JsonElement schema, JsonObject obj, string path, List<string> errors)
    {
        if (!schema.TryGetProperty("required", out var requiredElement) ||
            requiredElement.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var required in requiredElement.EnumerateArray())
        {
            var name = required.GetString();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (!obj.ContainsKey(name))
            {
                errors.Add($"{DisplayPath(JoinPath(path, name))}: is required");
            }
        }
    }

    private static void ValidateProperties(JsonElement schema, JsonObject obj, string path, List<string> errors)
    {
        if (!schema.TryGetProperty("properties", out var propertiesElement) ||
            propertiesElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in propertiesElement.EnumerateObject())
        {
            if (!obj.TryGetPropertyValue(property.Name, out var value))
            {
                continue;
            }

            var validated = ValidateSchema(property.Value, value, JoinPath(path, property.Name), errors);
            if (!ReferenceEquals(validated, value))
            {
                obj[property.Name] = validated;
            }
        }
    }

    private static JsonNode? ValidateType(
        JsonElement typeElement,
        JsonNode? node,
        string path,
        List<string> errors)
    {
        var allowedTypes = ReadAllowedTypes(typeElement);
        if (allowedTypes.Count == 0)
        {
            return node;
        }

        foreach (var allowedType in allowedTypes)
        {
            if (TryCoerce(node, allowedType, out var coerced))
            {
                return coerced;
            }
        }

        errors.Add($"{DisplayPath(path)}: must be {string.Join(" or ", allowedTypes)}");
        return node;
    }

    private static IReadOnlyList<string> ReadAllowedTypes(JsonElement typeElement)
    {
        if (typeElement.ValueKind == JsonValueKind.String)
        {
            var value = typeElement.GetString();
            return string.IsNullOrWhiteSpace(value) ? [] : [value.Trim()];
        }

        if (typeElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return typeElement
            .EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!.Trim())
            .ToArray();
    }

    private static bool TryCoerce(JsonNode? node, string expectedType, out JsonNode? coerced)
    {
        coerced = node;
        var raw = node?.ToJsonString() ?? "null";
        using var document = JsonDocument.Parse(raw);
        var element = document.RootElement;

        switch (expectedType)
        {
            case "object":
                return node is JsonObject;
            case "array":
                return node is JsonArray;
            case "null":
                coerced = null;
                return element.ValueKind == JsonValueKind.Null;
            case "string":
                if (element.ValueKind == JsonValueKind.String)
                {
                    return true;
                }

                if (element.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                {
                    coerced = JsonValue.Create(element.GetRawText());
                    return true;
                }

                return false;
            case "integer":
                if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var integer))
                {
                    coerced = JsonValue.Create(integer);
                    return true;
                }

                if (element.ValueKind == JsonValueKind.String &&
                    long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInteger))
                {
                    coerced = JsonValue.Create(parsedInteger);
                    return true;
                }

                return false;
            case "number":
                if (element.ValueKind == JsonValueKind.Number)
                {
                    return true;
                }

                if (element.ValueKind == JsonValueKind.String &&
                    double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedNumber))
                {
                    coerced = JsonValue.Create(parsedNumber);
                    return true;
                }

                return false;
            case "boolean":
                if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    return true;
                }

                if (element.ValueKind == JsonValueKind.String &&
                    bool.TryParse(element.GetString(), out var parsedBoolean))
                {
                    coerced = JsonValue.Create(parsedBoolean);
                    return true;
                }

                return false;
            default:
                return true;
        }
    }

    private static void ValidateEnum(JsonElement enumElement, JsonNode? node, string path, List<string> errors)
    {
        if (enumElement.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var actual = node?.ToJsonString() ?? "null";
        if (enumElement.EnumerateArray().Any(item => string.Equals(item.GetRawText(), actual, StringComparison.Ordinal)))
        {
            return;
        }

        errors.Add($"{DisplayPath(path)}: must be one of {string.Join(", ", enumElement.EnumerateArray().Select(static item => item.GetRawText()))}");
    }

    private static string JoinPath(string path, string segment) =>
        string.Equals(path, "root", StringComparison.Ordinal) ? segment : $"{path}/{segment}";

    private static string DisplayPath(string path) =>
        string.Equals(path, "root", StringComparison.Ordinal) ? "root" : path;

    private static string FormatError(string toolName, IReadOnlyList<string> errors, JsonElement arguments)
    {
        var formattedErrors = string.Join(
            "\n",
            errors.Select(static error => $"  - {error}"));
        var receivedArguments = FormatJson(arguments);
        return $"Validation failed for tool \"{toolName}\":\n{formattedErrors}\n\nReceived arguments:\n{receivedArguments}";
    }

    private static string FormatJson(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            element.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}

public sealed class ToolArgumentValidationException(string message) : Exception(message);
