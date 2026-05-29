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
