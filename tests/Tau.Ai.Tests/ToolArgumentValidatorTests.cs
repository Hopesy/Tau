using System.Text.Json;

namespace Tau.Ai.Tests;

public sealed class ToolArgumentValidatorTests
{
    [Fact]
    public void ValidateToolCall_FindsToolAndCoercesArguments()
    {
        var tool = CreateTool("count", """
            {
              "type": "object",
              "properties": {
                "count": { "type": "integer" },
                "enabled": { "type": "boolean" },
                "label": { "type": "string" }
              },
              "required": ["count", "enabled"]
            }
            """);
        var toolCall = new ToolCallContent("call-1", "count", """{"count":"42","enabled":"true","label":7}""");

        var validated = ToolArgumentValidator.ValidateToolCall([tool], toolCall);

        Assert.Equal(42, validated.GetProperty("count").GetInt32());
        Assert.True(validated.GetProperty("enabled").GetBoolean());
        Assert.Equal("7", validated.GetProperty("label").GetString());
    }

    [Fact]
    public void ValidateToolArguments_ValidatesNestedArraysAndEnum()
    {
        var tool = CreateTool("batch", """
            {
              "type": "object",
              "properties": {
                "items": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "mode": { "enum": ["fast", "safe"] },
                      "score": { "type": "number" }
                    },
                    "required": ["mode", "score"]
                  }
                }
              },
              "required": ["items"]
            }
            """);
        var toolCall = new ToolCallContent("call-1", "batch", """{"items":[{"mode":"fast","score":"12.5"}]}""");

        var validated = ToolArgumentValidator.ValidateToolArguments(tool, toolCall);
        var item = validated.GetProperty("items")[0];

        Assert.Equal("fast", item.GetProperty("mode").GetString());
        Assert.Equal(12.5, item.GetProperty("score").GetDouble());
    }

    [Fact]
    public void ValidateToolArguments_RejectsAdditionalPropertiesAndValidatesPatternProperties()
    {
        var tool = CreateTool("headers", """
            {
              "type": "object",
              "properties": {
                "name": { "type": "string" }
              },
              "patternProperties": {
                "^x-": { "type": "string" }
              },
              "additionalProperties": false
            }
            """);
        var validCall = new ToolCallContent("call-1", "headers", """{"name":"sample","x-count":7}""");

        var validated = ToolArgumentValidator.ValidateToolArguments(tool, validCall);

        Assert.Equal("7", validated.GetProperty("x-count").GetString());

        var invalidCall = new ToolCallContent("call-2", "headers", """{"name":"sample","other":true}""");
        var error = Assert.Throws<ToolArgumentValidationException>(() =>
            ToolArgumentValidator.ValidateToolArguments(tool, invalidCall));

        Assert.Contains("other: additional property is not allowed", error.Message);
    }

    [Fact]
    public void ValidateToolArguments_ValidatesCommonStringNumberAndArrayConstraints()
    {
        var tool = CreateTool("bounded", """
            {
              "type": "object",
              "properties": {
                "slug": { "type": "string", "minLength": 3, "maxLength": 8, "pattern": "^[a-z0-9-]+$" },
                "email": { "type": "string", "format": "email" },
                "count": { "type": "integer", "minimum": 2, "maximum": 8, "multipleOf": 2 },
                "items": { "type": "array", "minItems": 2, "maxItems": 3, "uniqueItems": true, "items": { "type": "string" } }
              },
              "required": ["slug", "email", "count", "items"]
            }
            """);
        var validCall = new ToolCallContent("call-1", "bounded",
            """{"slug":"a-123","email":"agent@example.com","count":"4","items":[1,"two"]}""");

        var validated = ToolArgumentValidator.ValidateToolArguments(tool, validCall);

        Assert.Equal(4, validated.GetProperty("count").GetInt32());
        Assert.Equal("1", validated.GetProperty("items")[0].GetString());

        var invalidCall = new ToolCallContent("call-2", "bounded",
            """{"slug":"NO","email":"not-email","count":5,"items":["one","one","two","three"]}""");
        var error = Assert.Throws<ToolArgumentValidationException>(() =>
            ToolArgumentValidator.ValidateToolArguments(tool, invalidCall));

        Assert.Contains("slug: must NOT have fewer than 3 characters", error.Message);
        Assert.Contains("slug: must match pattern", error.Message);
        Assert.Contains("email: must match format email", error.Message);
        Assert.Contains("count: must be multiple of 2", error.Message);
        Assert.Contains("items: must NOT have more than 3 items", error.Message);
        Assert.Contains("items: must NOT have duplicate items", error.Message);
    }

    [Fact]
    public void ValidateToolArguments_ValidatesFormatsExclusiveBoundsAndPropertyCounts()
    {
        var tool = CreateTool("record", """
            {
              "type": "object",
              "properties": {
                "email": { "type": "string", "format": "email" },
                "url": { "type": "string", "format": "uri" },
                "when": { "type": "string", "format": "date-time" },
                "id": { "type": "string", "format": "uuid" },
                "score": { "type": "number", "minimum": 0, "exclusiveMinimum": true, "maximum": 10, "exclusiveMaximum": true }
              },
              "required": ["email", "url", "when", "id", "score"],
              "minProperties": 5,
              "maxProperties": 5
            }
            """);
        var validCall = new ToolCallContent(
            "call-1",
            "record",
            """{"email":"agent@example.com","url":"https://example.com/docs","when":"2026-06-10T12:34:56Z","id":"123e4567-e89b-12d3-a456-426614174000","score":5}""");

        var validated = ToolArgumentValidator.ValidateToolArguments(tool, validCall);

        Assert.Equal(5, validated.GetProperty("score").GetDouble());

        var invalidCall = new ToolCallContent(
            "call-2",
            "record",
            """{"email":"not-email","url":"not-a-uri","when":"not-date","id":"bad","score":0,"extra":"value"}""");
        var error = Assert.Throws<ToolArgumentValidationException>(() =>
            ToolArgumentValidator.ValidateToolArguments(tool, invalidCall));

        Assert.Contains("email: must match format email", error.Message);
        Assert.Contains("url: must match format uri", error.Message);
        Assert.Contains("when: must match format date-time", error.Message);
        Assert.Contains("id: must match format uuid", error.Message);
        Assert.Contains("score: must be > 0", error.Message);
        Assert.Contains("root: must have at most 5 properties", error.Message);
    }

    [Fact]
    public void ValidateToolArguments_ValidatesAllOfAnyOfOneOfAndConst()
    {
        var tool = CreateTool("composed", """
            {
              "type": "object",
              "properties": {
                "kind": { "const": "file" },
                "path": {
                  "allOf": [
                    { "type": "string" },
                    { "minLength": 3 },
                    { "pattern": "^/" }
                  ]
                },
                "mode": {
                  "anyOf": [
                    { "const": "read" },
                    { "const": "write" }
                  ]
                },
                "target": {
                  "oneOf": [
                    { "type": "integer" },
                    { "type": "string", "pattern": "^id:" }
                  ]
                }
              },
              "required": ["kind", "path", "mode", "target"]
            }
            """);
        var validCall = new ToolCallContent("call-1", "composed",
            """{"kind":"file","path":"/tmp","mode":"read","target":"42"}""");

        var validated = ToolArgumentValidator.ValidateToolArguments(tool, validCall);

        Assert.Equal(42, validated.GetProperty("target").GetInt32());

        var invalidCall = new ToolCallContent("call-2", "composed",
            """{"kind":"dir","path":"x","mode":"delete","target":true}""");
        var error = Assert.Throws<ToolArgumentValidationException>(() =>
            ToolArgumentValidator.ValidateToolArguments(tool, invalidCall));

        Assert.Contains("kind: must be constant", error.Message);
        Assert.Contains("path: must NOT have fewer than 3 characters", error.Message);
        Assert.Contains("path: must match pattern", error.Message);
        Assert.Contains("mode: must match at least one anyOf schema", error.Message);
        Assert.Contains("target: must match exactly one oneOf schema", error.Message);
    }

    [Fact]
    public void ValidateToolArguments_ThrowsFormattedErrorForInvalidArguments()
    {
        var tool = CreateTool("echo", """
            {
              "type": "object",
              "properties": {
                "text": { "type": "string" },
                "mode": { "enum": ["plain", "json"] }
              },
              "required": ["text"]
            }
            """);
        var toolCall = new ToolCallContent("call-1", "echo", """{"mode":"xml"}""");

        var error = Assert.Throws<ToolArgumentValidationException>(() =>
            ToolArgumentValidator.ValidateToolArguments(tool, toolCall));

        Assert.Contains("Validation failed for tool \"echo\"", error.Message);
        Assert.Contains("text: is required", error.Message);
        Assert.Contains("mode: must be one of", error.Message);
        Assert.Contains("Received arguments:", error.Message);
        Assert.Contains("\"mode\": \"xml\"", error.Message);
    }

    [Fact]
    public void ValidateToolCall_ThrowsWhenToolIsMissing()
    {
        var error = Assert.Throws<ToolArgumentValidationException>(() =>
            ToolArgumentValidator.ValidateToolCall([], new ToolCallContent("call-1", "missing", "{}")));

        Assert.Equal("Tool \"missing\" not found.", error.Message);
    }

    private static Tool CreateTool(string name, string schemaJson)
    {
        using var document = JsonDocument.Parse(schemaJson);
        return new Tool(name, $"{name} description", document.RootElement.Clone());
    }
}
