using Tau.Ai.Providers.Google;
using Tau.Ai.Streaming;

namespace Tau.Ai.Tests;

public sealed class GoogleStreamParserTests
{
    [Fact]
    public async Task ParseChunk_PreservesResponseIdUsageSignaturesAndToolUseStopReason()
    {
        var parser = CreateParser(out var stream);

        parser.EmitStart();
        Assert.False(parser.ParseChunk(
            """
            {
              "responseId": "resp_google_1",
              "candidates": [
                {
                  "content": {
                    "parts": [
                      {"text": "plan", "thought": true, "thoughtSignature": "think_sig_1"},
                      {"text": " answer", "thoughtSignature": "text_sig_1"}
                    ]
                  }
                }
              ]
            }
            """));
        Assert.False(parser.ParseChunk(
            """
            {
              "responseId": "resp_google_ignored",
              "candidates": [
                {
                  "content": {
                    "parts": [
                      {
                        "functionCall": {
                          "id": "provided_tool_1",
                          "name": "read_file",
                          "args": {"path": "README.md"}
                        },
                        "thoughtSignature": "tool_sig_1"
                      }
                    ]
                  },
                  "finishReason": "STOP"
                }
              ],
              "usageMetadata": {
                "promptTokenCount": 10,
                "cachedContentTokenCount": 3,
                "candidatesTokenCount": 4,
                "thoughtsTokenCount": 2,
                "totalTokenCount": 16
              }
            }
            """));
        parser.EmitDone();

        var events = await OpenAiResponsesProviderTests.CollectAsync(stream);

        Assert.Equal(0, Assert.Single(events.OfType<ThinkingStartEvent>()).ContentIndex);
        Assert.Equal(0, Assert.Single(events.OfType<ThinkingDeltaEvent>()).ContentIndex);
        Assert.Equal(0, Assert.Single(events.OfType<ThinkingEndEvent>()).ContentIndex);
        Assert.Equal(1, Assert.Single(events.OfType<TextStartEvent>()).ContentIndex);
        Assert.Equal(1, Assert.Single(events.OfType<TextDeltaEvent>()).ContentIndex);
        Assert.Equal(1, Assert.Single(events.OfType<TextEndEvent>()).ContentIndex);
        Assert.Equal(2, Assert.Single(events.OfType<ToolCallStartEvent>()).ContentIndex);
        Assert.Equal(2, Assert.Single(events.OfType<ToolCallDeltaEvent>()).ContentIndex);
        Assert.Equal(2, Assert.Single(events.OfType<ToolCallEndEvent>()).ContentIndex);

        var done = Assert.Single(events.OfType<DoneEvent>());
        Assert.Equal("resp_google_1", done.Message.ResponseId);
        Assert.Equal(new Usage(7, 6, 3), done.Message.Usage);
        Assert.Equal(StopReason.ToolUse, done.Message.StopReason);

        var thinking = Assert.IsType<ThinkingContent>(done.Message.Content[0]);
        Assert.Equal("plan", thinking.Thinking);
        Assert.Equal("think_sig_1", thinking.ThinkingSignature);

        var text = Assert.IsType<TextContent>(done.Message.Content[1]);
        Assert.Equal(" answer", text.Text);
        Assert.Equal("text_sig_1", text.TextSignature);

        var toolCall = Assert.IsType<ToolCallContent>(done.Message.Content[2]);
        Assert.Equal("provided_tool_1", toolCall.Id);
        Assert.Equal("read_file", toolCall.Name);
        Assert.Equal("""{"path": "README.md"}""", toolCall.Arguments);
        Assert.Equal("tool_sig_1", toolCall.ThoughtSignature);
    }

    [Theory]
    [InlineData("SAFETY")]
    [InlineData("OTHER")]
    [InlineData("MALFORMED_FUNCTION_CALL")]
    public async Task ParseChunk_WithProviderErrorFinishReasonTerminatesWithErrorEvent(string finishReason)
    {
        var parser = CreateParser(out var stream);

        parser.EmitStart();
        var terminal = parser.ParseChunk(
            """
            {
              "candidates": [
                {
                  "content": {"parts": [{"text": "blocked"}]},
                  "finishReason": "__FINISH_REASON__"
                }
              ],
              "usageMetadata": {"promptTokenCount": 2, "candidatesTokenCount": 0}
            }
            """.Replace("__FINISH_REASON__", finishReason, StringComparison.Ordinal));

        Assert.True(terminal);
        var events = await OpenAiResponsesProviderTests.CollectAsync(stream);

        Assert.Empty(events.OfType<DoneEvent>());
        var error = Assert.Single(events.OfType<ErrorEvent>());
        Assert.Equal($"Provider finishReason: {finishReason}", error.Error);
        Assert.Equal(StopReason.Error, error.Message?.StopReason);
        Assert.Equal($"Provider finishReason: {finishReason}", error.Message?.ErrorMessage);
        Assert.Equal(new Usage(2, 0), error.Message?.Usage);
        Assert.Equal("blocked", Assert.IsType<TextContent>(Assert.Single(error.Message!.Content)).Text);

        var result = await stream.ResultAsync;
        Assert.Equal(StopReason.Error, result.StopReason);
        Assert.Equal($"Provider finishReason: {finishReason}", result.ErrorMessage);
    }

    [Fact]
    public async Task ParseChunk_WithMalformedJsonTerminatesWithErrorEvent()
    {
        var parser = CreateParser(out var stream);

        parser.EmitStart();
        Assert.True(parser.ParseChunk("{not-json}"));

        var events = await OpenAiResponsesProviderTests.CollectAsync(stream);

        Assert.Empty(events.OfType<DoneEvent>());
        var error = Assert.Single(events.OfType<ErrorEvent>());
        Assert.StartsWith("Malformed Google stream JSON:", error.Error, StringComparison.Ordinal);

        var result = await stream.ResultAsync;
        Assert.Equal(StopReason.Error, result.StopReason);
        Assert.StartsWith("Malformed Google stream JSON:", result.ErrorMessage, StringComparison.Ordinal);
    }

    private static GoogleStreamParser CreateParser(out AssistantMessageStream stream)
    {
        stream = new AssistantMessageStream();
        return new GoogleStreamParser(new AssistantMessage
        {
            Api = "google-generative-language",
            Provider = "google",
            Model = "gemini-test",
            Content = []
        }, stream);
    }
}
