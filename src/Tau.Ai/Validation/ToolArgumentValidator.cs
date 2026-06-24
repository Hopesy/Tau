using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

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
               schema.TryGetProperty("oneOf", out _) ||
               schema.TryGetProperty("allOf", out _) ||
               schema.TryGetProperty("enum", out _) ||
               schema.TryGetProperty("const", out _) ||
               schema.TryGetProperty("items", out _) ||
               schema.TryGetProperty("additionalProperties", out _) ||
               schema.TryGetProperty("patternProperties", out _) ||
               schema.TryGetProperty("minLength", out _) ||
               schema.TryGetProperty("maxLength", out _) ||
               schema.TryGetProperty("pattern", out _) ||
               schema.TryGetProperty("format", out _) ||
               schema.TryGetProperty("minimum", out _) ||
               schema.TryGetProperty("maximum", out _) ||
               schema.TryGetProperty("exclusiveMinimum", out _) ||
               schema.TryGetProperty("exclusiveMaximum", out _) ||
               schema.TryGetProperty("multipleOf", out _) ||
               schema.TryGetProperty("minItems", out _) ||
               schema.TryGetProperty("maxItems", out _) ||
               schema.TryGetProperty("uniqueItems", out _) ||
               schema.TryGetProperty("minProperties", out _) ||
               schema.TryGetProperty("maxProperties", out _);
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

        if (schema.TryGetProperty("const", out var constElement))
        {
            ValidateConst(constElement, node, path, errors);
        }

        if (schema.TryGetProperty("enum", out var enumElement))
        {
            ValidateEnum(enumElement, node, path, errors);
        }

        if (node is JsonObject obj)
        {
            ValidateRequired(schema, obj, path, errors);
            ValidateProperties(schema, obj, path, errors);
            ValidatePatternProperties(schema, obj, path, errors);
            ValidateAdditionalProperties(schema, obj, path, errors);
            ValidatePropertyCount(schema, obj, path, errors);
        }
        else if (node is JsonArray array && schema.TryGetProperty("items", out var itemsElement))
        {
            if (itemsElement.ValueKind == JsonValueKind.Array)
            {
                var itemSchemas = itemsElement.EnumerateArray().ToArray();
                for (var i = 0; i < array.Count && i < itemSchemas.Length; i++)
                {
                    var item = array[i];
                    var validated = ValidateSchema(itemSchemas[i], item, $"{path}/{i}", errors);
                    if (!ReferenceEquals(validated, item))
                    {
                        array[i] = validated;
                    }
                }
            }
            else
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
        }

        if (node is JsonArray validatedArray)
        {
            ValidateArrayConstraints(schema, validatedArray, path, errors);
        }

        ValidateStringConstraints(schema, node, path, errors);
        ValidateNumericConstraints(schema, node, path, errors);

        if (schema.TryGetProperty("allOf", out var allOfElement))
        {
            node = ValidateAllOf(allOfElement, node, path, errors);
        }

        if (schema.TryGetProperty("anyOf", out var anyOfElement))
        {
            node = ValidateAnyOf(anyOfElement, node, path, errors);
        }

        if (schema.TryGetProperty("oneOf", out var oneOfElement))
        {
            node = ValidateOneOf(oneOfElement, node, path, errors);
        }

        return node;
    }

    private static JsonNode? ValidateAllOf(
        JsonElement allOfElement,
        JsonNode? node,
        string path,
        List<string> errors)
    {
        if (allOfElement.ValueKind != JsonValueKind.Array)
        {
            return node;
        }

        var current = node;
        foreach (var branch in allOfElement.EnumerateArray())
        {
            current = ValidateSchema(branch, current, path, errors);
        }

        return current;
    }

    private static JsonNode? ValidateAnyOf(
        JsonElement anyOfElement,
        JsonNode? node,
        string path,
        List<string> errors)
    {
        if (anyOfElement.ValueKind != JsonValueKind.Array)
        {
            return node;
        }

        foreach (var branch in anyOfElement.EnumerateArray())
        {
            var branchErrors = new List<string>();
            var candidate = node?.DeepClone();
            candidate = ValidateSchema(branch, candidate, path, branchErrors);
            if (branchErrors.Count == 0)
            {
                return candidate;
            }
        }

        errors.Add($"{DisplayPath(path)}: must match at least one anyOf schema");
        return node;
    }

    private static JsonNode? ValidateOneOf(
        JsonElement oneOfElement,
        JsonNode? node,
        string path,
        List<string> errors)
    {
        if (oneOfElement.ValueKind != JsonValueKind.Array)
        {
            return node;
        }

        var matchCount = 0;
        JsonNode? matched = null;
        foreach (var branch in oneOfElement.EnumerateArray())
        {
            var branchErrors = new List<string>();
            var candidate = node?.DeepClone();
            candidate = ValidateSchema(branch, candidate, path, branchErrors);
            if (branchErrors.Count == 0)
            {
                matchCount++;
                matched = candidate;
            }
        }

        if (matchCount == 1)
        {
            return matched;
        }

        errors.Add($"{DisplayPath(path)}: must match exactly one oneOf schema");
        return node;
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

    private static void ValidatePatternProperties(JsonElement schema, JsonObject obj, string path, List<string> errors)
    {
        if (!schema.TryGetProperty("patternProperties", out var patternPropertiesElement) ||
            patternPropertiesElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var patternProperty in patternPropertiesElement.EnumerateObject())
        {
            Regex regex;
            try
            {
                regex = new Regex(patternProperty.Name, RegexOptions.CultureInvariant);
            }
            catch (ArgumentException)
            {
                continue;
            }

            foreach (var property in obj.ToArray())
            {
                if (!regex.IsMatch(property.Key) || property.Value is null)
                {
                    continue;
                }

                var validated = ValidateSchema(patternProperty.Value, property.Value, JoinPath(path, property.Key), errors);
                if (!ReferenceEquals(validated, property.Value))
                {
                    obj[property.Key] = validated;
                }
            }
        }
    }

    private static void ValidateAdditionalProperties(JsonElement schema, JsonObject obj, string path, List<string> errors)
    {
        if (!schema.TryGetProperty("additionalProperties", out var additionalElement))
        {
            return;
        }

        foreach (var property in obj.ToArray())
        {
            if (IsDeclaredProperty(schema, property.Key) || IsPatternProperty(schema, property.Key))
            {
                continue;
            }

            if (additionalElement.ValueKind is JsonValueKind.False)
            {
                errors.Add($"{DisplayPath(JoinPath(path, property.Key))}: additional property is not allowed");
                continue;
            }

            if (additionalElement.ValueKind == JsonValueKind.Object && property.Value is not null)
            {
                var validated = ValidateSchema(additionalElement, property.Value, JoinPath(path, property.Key), errors);
                if (!ReferenceEquals(validated, property.Value))
                {
                    obj[property.Key] = validated;
                }
            }
        }
    }

    private static bool IsDeclaredProperty(JsonElement schema, string name)
    {
        return schema.TryGetProperty("properties", out var propertiesElement) &&
               propertiesElement.ValueKind == JsonValueKind.Object &&
               propertiesElement.TryGetProperty(name, out _);
    }

    private static bool IsPatternProperty(JsonElement schema, string name)
    {
        if (!schema.TryGetProperty("patternProperties", out var patternPropertiesElement) ||
            patternPropertiesElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in patternPropertiesElement.EnumerateObject())
        {
            try
            {
                if (Regex.IsMatch(name, property.Name, RegexOptions.CultureInvariant))
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                continue;
            }
        }

        return false;
    }

    private static void ValidatePropertyCount(JsonElement schema, JsonObject obj, string path, List<string> errors)
    {
        if (schema.TryGetProperty("minProperties", out var minElement) &&
            minElement.ValueKind == JsonValueKind.Number &&
            minElement.TryGetInt32(out var min) &&
            obj.Count < min)
        {
            errors.Add($"{DisplayPath(path)}: must have at least {min.ToString(CultureInfo.InvariantCulture)} properties");
        }

        if (schema.TryGetProperty("maxProperties", out var maxElement) &&
            maxElement.ValueKind == JsonValueKind.Number &&
            maxElement.TryGetInt32(out var max) &&
            obj.Count > max)
        {
            errors.Add($"{DisplayPath(path)}: must have at most {max.ToString(CultureInfo.InvariantCulture)} properties");
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
            if (MatchesJsonType(node, allowedType))
            {
                return node;
            }
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

    private static bool MatchesJsonType(JsonNode? node, string expectedType)
    {
        var raw = node?.ToJsonString() ?? "null";
        using var document = JsonDocument.Parse(raw);
        var element = document.RootElement;

        return expectedType switch
        {
            "object" => node is JsonObject,
            "array" => node is JsonArray,
            "null" => element.ValueKind == JsonValueKind.Null,
            "string" => element.ValueKind == JsonValueKind.String,
            "integer" => element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out _),
            "number" => element.ValueKind == JsonValueKind.Number,
            "boolean" => element.ValueKind is JsonValueKind.True or JsonValueKind.False,
            _ => false
        };
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
                if (element.ValueKind == JsonValueKind.Null)
                {
                    coerced = null;
                    return true;
                }

                if (element.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(element.GetString()))
                {
                    coerced = null;
                    return true;
                }

                if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var nullNumber) && nullNumber == 0)
                {
                    coerced = null;
                    return true;
                }

                if (element.ValueKind == JsonValueKind.False)
                {
                    coerced = null;
                    return true;
                }

                return false;
            case "string":
                if (element.ValueKind == JsonValueKind.String)
                {
                    return true;
                }

                if (element.ValueKind == JsonValueKind.Null)
                {
                    coerced = JsonValue.Create(string.Empty);
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

                if (element.ValueKind == JsonValueKind.Null)
                {
                    coerced = JsonValue.Create(0);
                    return true;
                }

                if (element.ValueKind == JsonValueKind.String &&
                    long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInteger))
                {
                    coerced = JsonValue.Create(parsedInteger);
                    return true;
                }

                if (element.ValueKind == JsonValueKind.True)
                {
                    coerced = JsonValue.Create(1);
                    return true;
                }

                if (element.ValueKind == JsonValueKind.False)
                {
                    coerced = JsonValue.Create(0);
                    return true;
                }

                return false;
            case "number":
                if (element.ValueKind == JsonValueKind.Number)
                {
                    return true;
                }

                if (element.ValueKind == JsonValueKind.Null)
                {
                    coerced = JsonValue.Create(0);
                    return true;
                }

                if (element.ValueKind == JsonValueKind.String &&
                    double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedNumber))
                {
                    coerced = JsonValue.Create(parsedNumber);
                    return true;
                }

                if (element.ValueKind == JsonValueKind.True)
                {
                    coerced = JsonValue.Create(1);
                    return true;
                }

                if (element.ValueKind == JsonValueKind.False)
                {
                    coerced = JsonValue.Create(0);
                    return true;
                }

                return false;
            case "boolean":
                if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    return true;
                }

                if (element.ValueKind == JsonValueKind.Null)
                {
                    coerced = JsonValue.Create(false);
                    return true;
                }

                if (element.ValueKind == JsonValueKind.String &&
                    bool.TryParse(element.GetString(), out var parsedBoolean))
                {
                    coerced = JsonValue.Create(parsedBoolean);
                    return true;
                }

                if (element.ValueKind == JsonValueKind.Number &&
                    element.TryGetDouble(out var booleanNumber) &&
                    (booleanNumber == 1 || booleanNumber == 0))
                {
                    coerced = JsonValue.Create(booleanNumber == 1);
                    return true;
                }

                return false;
            default:
                return true;
        }
    }

    private static void ValidateConst(JsonElement constElement, JsonNode? node, string path, List<string> errors)
    {
        var expected = JsonNode.Parse(constElement.GetRawText());
        if (JsonNode.DeepEquals(expected, node))
        {
            return;
        }

        errors.Add($"{DisplayPath(path)}: must be constant {constElement.GetRawText()}");
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

    private static void ValidateStringConstraints(JsonElement schema, JsonNode? node, string path, List<string> errors)
    {
        if (node is not JsonValue)
        {
            return;
        }

        using var document = JsonDocument.Parse(node.ToJsonString());
        var element = document.RootElement;
        if (element.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var value = element.GetString() ?? string.Empty;
        var length = new StringInfo(value).LengthInTextElements;

        if (schema.TryGetProperty("minLength", out var minElement) &&
            minElement.ValueKind == JsonValueKind.Number &&
            minElement.TryGetInt32(out var min) &&
            length < min)
        {
            errors.Add($"{DisplayPath(path)}: must NOT have fewer than {min.ToString(CultureInfo.InvariantCulture)} characters");
        }

        if (schema.TryGetProperty("maxLength", out var maxElement) &&
            maxElement.ValueKind == JsonValueKind.Number &&
            maxElement.TryGetInt32(out var max) &&
            length > max)
        {
            errors.Add($"{DisplayPath(path)}: must NOT have more than {max.ToString(CultureInfo.InvariantCulture)} characters");
        }

        if (schema.TryGetProperty("pattern", out var patternElement) &&
            patternElement.ValueKind == JsonValueKind.String &&
            !string.IsNullOrEmpty(patternElement.GetString()))
        {
            try
            {
                if (!Regex.IsMatch(value, patternElement.GetString()!, RegexOptions.CultureInvariant))
                {
                    errors.Add($"{DisplayPath(path)}: must match pattern {patternElement.GetString()}");
                }
            }
            catch (ArgumentException)
            {
                // AJV treats invalid schemas as compile-time failures. Tau keeps runtime validation best-effort.
            }
        }

        if (schema.TryGetProperty("format", out var formatElement) &&
            formatElement.ValueKind == JsonValueKind.String &&
            !IsKnownFormat(value, formatElement.GetString()))
        {
            errors.Add($"{DisplayPath(path)}: must match format {formatElement.GetString()}");
        }
    }

    private static bool IsKnownFormat(string value, string? format)
    {
        return format switch
        {
            null or "" => true,
            "date-time" => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _),
            "date" => DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            "time" => TimeOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            "email" => IsEmail(value),
            "uri" or "url" => Uri.TryCreate(value, UriKind.Absolute, out _),
            "uuid" => Guid.TryParse(value, out _),
            "hostname" => Uri.CheckHostName(value) == UriHostNameType.Dns,
            "ipv4" => IPAddress.TryParse(value, out var ip4) && ip4.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork,
            "ipv6" => IPAddress.TryParse(value, out var ip6) && ip6.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6,
            _ => true
        };
    }

    private static bool IsEmail(string value)
    {
        try
        {
            var address = new MailAddress(value);
            return string.Equals(address.Address, value, StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void ValidateNumericConstraints(JsonElement schema, JsonNode? node, string path, List<string> errors)
    {
        if (node is not JsonValue)
        {
            return;
        }

        using var document = JsonDocument.Parse(node.ToJsonString());
        var element = document.RootElement;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out var value))
        {
            return;
        }

        if (schema.TryGetProperty("minimum", out var minimumElement) &&
            minimumElement.ValueKind == JsonValueKind.Number &&
            minimumElement.TryGetDouble(out var minimum) &&
            value < minimum)
        {
            errors.Add($"{DisplayPath(path)}: must be >= {minimum.ToString(CultureInfo.InvariantCulture)}");
        }

        if (schema.TryGetProperty("maximum", out var maximumElement) &&
            maximumElement.ValueKind == JsonValueKind.Number &&
            maximumElement.TryGetDouble(out var maximum) &&
            value > maximum)
        {
            errors.Add($"{DisplayPath(path)}: must be <= {maximum.ToString(CultureInfo.InvariantCulture)}");
        }

        ValidateExclusiveNumericConstraint(schema, "exclusiveMinimum", value, path, errors, greaterThan: true);
        ValidateExclusiveNumericConstraint(schema, "exclusiveMaximum", value, path, errors, greaterThan: false);

        if (schema.TryGetProperty("multipleOf", out var multipleElement) &&
            multipleElement.ValueKind == JsonValueKind.Number &&
            multipleElement.TryGetDouble(out var multiple) &&
            multiple > 0 &&
            Math.Abs(value / multiple - Math.Round(value / multiple)) > 1e-10)
        {
            errors.Add($"{DisplayPath(path)}: must be multiple of {multiple.ToString(CultureInfo.InvariantCulture)}");
        }
    }

    private static void ValidateExclusiveNumericConstraint(
        JsonElement schema,
        string propertyName,
        double value,
        string path,
        List<string> errors,
        bool greaterThan)
    {
        if (!schema.TryGetProperty(propertyName, out var element))
        {
            return;
        }

        double? limit = null;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var numericLimit))
        {
            limit = numericLimit;
        }
        else if (element.ValueKind == JsonValueKind.True)
        {
            var fallbackName = greaterThan ? "minimum" : "maximum";
            if (schema.TryGetProperty(fallbackName, out var fallback) &&
                fallback.ValueKind == JsonValueKind.Number &&
                fallback.TryGetDouble(out var fallbackLimit))
            {
                limit = fallbackLimit;
            }
        }

        if (limit is null)
        {
            return;
        }

        if (greaterThan && value <= limit.Value)
        {
            errors.Add($"{DisplayPath(path)}: must be > {limit.Value.ToString(CultureInfo.InvariantCulture)}");
        }
        else if (!greaterThan && value >= limit.Value)
        {
            errors.Add($"{DisplayPath(path)}: must be < {limit.Value.ToString(CultureInfo.InvariantCulture)}");
        }
    }

    private static void ValidateArrayConstraints(JsonElement schema, JsonArray array, string path, List<string> errors)
    {
        if (schema.TryGetProperty("minItems", out var minElement) &&
            minElement.ValueKind == JsonValueKind.Number &&
            minElement.TryGetInt32(out var min) &&
            array.Count < min)
        {
            errors.Add($"{DisplayPath(path)}: must NOT have fewer than {min.ToString(CultureInfo.InvariantCulture)} items");
        }

        if (schema.TryGetProperty("maxItems", out var maxElement) &&
            maxElement.ValueKind == JsonValueKind.Number &&
            maxElement.TryGetInt32(out var max) &&
            array.Count > max)
        {
            errors.Add($"{DisplayPath(path)}: must NOT have more than {max.ToString(CultureInfo.InvariantCulture)} items");
        }

        if (schema.TryGetProperty("uniqueItems", out var uniqueElement) &&
            uniqueElement.ValueKind == JsonValueKind.True)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in array)
            {
                var raw = item?.ToJsonString() ?? "null";
                if (!seen.Add(raw))
                {
                    errors.Add($"{DisplayPath(path)}: must NOT have duplicate items");
                    break;
                }
            }
        }
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
