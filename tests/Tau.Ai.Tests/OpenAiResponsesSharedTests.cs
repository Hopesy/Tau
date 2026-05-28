using System.Text;
using Tau.Ai.Providers.OpenAiResponses;

namespace Tau.Ai.Tests;

public sealed class OpenAiResponsesSharedTests
{
    [Fact]
    public void NormalizeToolCallId_RewritesForeignResponsesItemId()
    {
        var target = new Model
        {
            Id = "gpt-5.4",
            Name = "GPT-5.4",
            Api = "openai-responses",
            Provider = "openai",
            BaseUrl = "https://api.openai.com/v1"
        };
        var source = new AssistantMessage([new ToolCallContent("call-1|very-long-foreign-item-id", "read_file", "{}")])
        {
            Api = "anthropic-messages",
            Provider = "anthropic",
            Model = "claude"
        };

        var normalized = OpenAiResponsesShared.NormalizeToolCallId("call-1|very-long-foreign-item-id", target, source);

        Assert.StartsWith("call-1|fc_", normalized, StringComparison.Ordinal);
        Assert.True(OpenAiResponsesShared.SplitToolCallId(normalized).ItemId!.Length <= 64);
    }

    [Fact]
    public void ConvertResponsesMessages_InsertsSyntheticToolResultForOrphanedToolCall()
    {
        var model = new Model
        {
            Id = "gpt-5.4",
            Name = "GPT-5.4",
            Api = "openai-responses",
            Provider = "openai",
            BaseUrl = "https://api.openai.com/v1"
        };
        var context = new LlmContext
        {
            Messages =
            [
                new AssistantMessage([new ToolCallContent("call_1|fc_1", "read_file", "{}")])
                {
                    Api = "openai-responses",
                    Provider = "openai",
                    Model = "gpt-5.4"
                },
                new UserMessage("continue")
            ]
        };

        var messages = OpenAiResponsesShared.ConvertResponsesMessages(model, context);

        Assert.Contains(messages, item => item is Dictionary<string, object> dict &&
            dict.TryGetValue("type", out var type) &&
            Equals(type, "function_call_output") &&
            dict.TryGetValue("call_id", out var callId) &&
            Equals(callId, "call_1"));
    }

    [Fact]
    public void ConvertResponsesMessages_RemovesUnpairedSurrogatesFromRequestText()
    {
        const char high = (char)0xD83D;
        const char low = (char)0xDE48;
        const string emoji = "🙈";
        var model = new Model
        {
            Id = "gpt-5.4",
            Name = "GPT-5.4",
            Api = "openai-responses",
            Provider = "openai",
            BaseUrl = "https://api.openai.com/v1",
            Reasoning = true,
            InputModalities = ["text", "image"]
        };
        var context = new LlmContext
        {
            SystemPrompt = $"system {high} {emoji}",
            Messages =
            [
                new UserMessage([new TextContent($"user {high} {emoji}")]),
                new AssistantMessage(
                [
                    new ThinkingContent($"thinking {low} {emoji}"),
                    new TextContent($"assistant {high} {emoji}"),
                    new ToolCallContent("call_1|fc_1", "read_file", "{}")
                ])
                {
                    Api = "openai-responses",
                    Provider = "openai",
                    Model = "gpt-5.4"
                },
                new ToolResultMessage("call_1|fc_1", [new TextContent($"tool {low} {emoji}")])
            ]
        };

        var messages = OpenAiResponsesShared.ConvertResponsesMessages(model, context);
        var allText = CollectStrings(messages);

        Assert.False(ContainsUnpairedSurrogate(allText));
        Assert.Contains("system  🙈", allText, StringComparison.Ordinal);
        Assert.Contains("user  🙈", allText, StringComparison.Ordinal);
        Assert.Contains("thinking  🙈", allText, StringComparison.Ordinal);
        Assert.Contains("assistant  🙈", allText, StringComparison.Ordinal);
        Assert.Contains("tool  🙈", allText, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractAccountIdFromJwt_ReadsBase64UrlPayload()
    {
        var payload = """{"https://api.openai.com/auth":{"chatgpt_account_id":"acc_123"}}""";
        var jwt = $"stub.{Base64Url(payload)}.sig";

        var accountId = OpenAiResponsesShared.ExtractAccountIdFromJwt(jwt);

        Assert.Equal("acc_123", accountId);
    }

    public static string BuildFakeJwt(string accountId)
    {
        var payload = "{\"https://api.openai.com/auth\":{\"chatgpt_account_id\":\"" + accountId + "\"}}";
        return $"stub.{Base64Url(payload)}.sig";
    }

    private static string Base64Url(string text) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string CollectStrings(object? value)
    {
        var builder = new StringBuilder();
        AppendStrings(value, builder);
        return builder.ToString();
    }

    private static void AppendStrings(object? value, StringBuilder builder)
    {
        switch (value)
        {
            case null:
                return;
            case string text:
                builder.AppendLine(text);
                return;
            case Dictionary<string, object> dictionary:
                foreach (var child in dictionary.Values)
                {
                    AppendStrings(child, builder);
                }

                return;
            case IEnumerable<object> values:
                foreach (var child in values)
                {
                    AppendStrings(child, builder);
                }

                return;
        }
    }

    private static bool ContainsUnpairedSurrogate(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var current = text[i];
            if (char.IsHighSurrogate(current))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    i++;
                    continue;
                }

                return true;
            }

            if (char.IsLowSurrogate(current))
            {
                return true;
            }
        }

        return false;
    }
}
