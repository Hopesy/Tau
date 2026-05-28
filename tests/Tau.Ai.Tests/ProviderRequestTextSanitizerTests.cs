using System.Net;
using System.Text;
using System.Text.Json;
using Tau.Ai.Providers.Anthropic;
using Tau.Ai.Providers.Bedrock;
using Tau.Ai.Providers.Google;
using Tau.Ai.Providers.Mistral;

namespace Tau.Ai.Tests;

public sealed class ProviderRequestTextSanitizerTests
{
    private const char UnpairedHigh = (char)0xD83D;
    private const char UnpairedLow = (char)0xDE48;
    private const string Emoji = "🙈";

    [Fact]
    public async Task AnthropicProvider_RemovesUnpairedSurrogatesFromRequestText()
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => ErrorResponse("stop after payload"));
        using var client = new HttpClient(handler);
        var provider = new AnthropicProvider(client);

        await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            AnthropicModel(),
            BuildContext(),
            new StreamOptions { ApiKey = "anthropic-key" }));

        AssertSanitizedJsonBody(
            handler.CapturedBody,
            "system  🙈",
            "user  🙈",
            "thinking  🙈",
            "assistant  🙈",
            "tool  🙈");
    }

    [Fact]
    public async Task GoogleProvider_RemovesUnpairedSurrogatesFromRequestText()
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => ErrorResponse("stop after payload"));
        using var client = new HttpClient(handler);
        var provider = new GoogleProvider(client);

        await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            GoogleModel("google-generative-language", "google", "https://generativelanguage.googleapis.com"),
            BuildContext(),
            new StreamOptions { ApiKey = "google-key" }));

        AssertSanitizedJsonBody(
            handler.CapturedBody,
            "system  🙈",
            "user  🙈",
            "assistant  🙈",
            "tool  🙈");
    }

    [Fact]
    public async Task GoogleVertexProvider_RemovesUnpairedSurrogatesFromRequestText()
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => ErrorResponse("stop after payload"));
        using var client = new HttpClient(handler);
        var provider = new GoogleVertexProvider(client);

        await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            GoogleModel("google-vertex", "google-vertex", "https://vertex.example/v1/projects/tau/locations/us-central1/publishers/google"),
            BuildContext(),
            new StreamOptions { ApiKey = "vertex-key" }));

        AssertSanitizedJsonBody(
            handler.CapturedBody,
            "system  🙈",
            "user  🙈",
            "assistant  🙈",
            "tool  🙈");
    }

    [Fact]
    public async Task GeminiCliProvider_RemovesUnpairedSurrogatesFromRequestText()
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => ErrorResponse("stop after payload"));
        using var client = new HttpClient(handler);
        var provider = new GoogleGeminiCliProvider(client);

        await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            GoogleModel("google-gemini-cli", "google-gemini-cli", "https://cloudcode-pa.googleapis.com"),
            BuildContext(),
            new StreamOptions { ApiKey = """{"token":"token","projectId":"project"}""" }));

        AssertSanitizedJsonBody(
            handler.CapturedBody,
            "system  🙈",
            "user  🙈",
            "assistant  🙈",
            "tool  🙈");
    }

    [Fact]
    public async Task MistralProvider_RemovesUnpairedSurrogatesFromRequestText()
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => ErrorResponse("stop after payload"));
        using var client = new HttpClient(handler);
        var provider = new MistralProvider(client);

        await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            MistralModel(),
            BuildContext(),
            new StreamOptions { ApiKey = "mistral-key" }));

        AssertSanitizedJsonBody(
            handler.CapturedBody,
            "system  🙈",
            "user  🙈",
            "thinking  🙈",
            "assistant  🙈",
            "tool  🙈");
    }

    [Fact]
    public async Task BedrockProvider_RemovesUnpairedSurrogatesFromRequestText()
    {
        using var handler = new OpenAiResponsesProviderTests.StubHandler(_ => ErrorResponse("stop after payload"));
        using var client = new HttpClient(handler);
        var provider = new BedrockProvider(client);

        await OpenAiResponsesProviderTests.CollectAsync(provider.Stream(
            BedrockModel(),
            BuildContext(),
            new BedrockOptions { Region = "us-east-1", BearerToken = "bedrock-token" }));

        AssertSanitizedJsonBody(
            handler.CapturedBody,
            "system  🙈",
            "user  🙈",
            "thinking  🙈",
            "assistant  🙈",
            "tool  🙈");
    }

    private static LlmContext BuildContext() => new()
    {
        SystemPrompt = $"system {UnpairedHigh} {Emoji}",
        Messages =
        [
            new UserMessage($"user {UnpairedHigh} {Emoji}"),
            new AssistantMessage(
            [
                new ThinkingContent($"thinking {UnpairedLow} {Emoji}"),
                new TextContent($"assistant {UnpairedHigh} {Emoji}"),
                new ToolCallContent("call_1", "read_file", "{}")
            ]),
            new ToolResultMessage("call_1", [new TextContent($"tool {UnpairedLow} {Emoji}")])
        ]
    };

    private static Model AnthropicModel() => new()
    {
        Id = "claude-sonnet-4-20250514",
        Name = "Claude Sonnet 4",
        Api = "anthropic-messages",
        Provider = "anthropic",
        BaseUrl = "https://api.anthropic.com",
        Reasoning = true
    };

    private static Model GoogleModel(string api, string provider, string baseUrl) => new()
    {
        Id = "gemini-2.5-flash",
        Name = "Gemini 2.5 Flash",
        Api = api,
        Provider = provider,
        BaseUrl = baseUrl,
        Reasoning = true
    };

    private static Model MistralModel() => new()
    {
        Id = "mistral-small-latest",
        Name = "Mistral Small",
        Api = "mistral-conversations",
        Provider = "mistral",
        BaseUrl = "https://api.mistral.ai/v1",
        Reasoning = true
    };

    private static Model BedrockModel() => new()
    {
        Id = "anthropic.claude-3-7-sonnet-20250219-v1:0",
        Name = "Claude 3.7 Sonnet",
        Api = "bedrock-converse-stream",
        Provider = "amazon-bedrock",
        BaseUrl = "https://bedrock-runtime.us-east-1.amazonaws.com",
        Reasoning = true,
        MaxOutputTokens = 4096
    };

    private static HttpResponseMessage ErrorResponse(string text) => new(HttpStatusCode.BadRequest)
    {
        Content = new StringContent(text, Encoding.UTF8, "text/plain")
    };

    private static void AssertSanitizedJsonBody(string body, params string[] expectedTexts)
    {
        using var document = JsonDocument.Parse(body);
        var allText = CollectJsonStrings(document.RootElement);

        Assert.False(ContainsUnpairedSurrogate(allText));
        foreach (var expected in expectedTexts)
        {
            Assert.Contains(expected, allText, StringComparison.Ordinal);
        }
    }

    private static string CollectJsonStrings(JsonElement element)
    {
        var builder = new StringBuilder();
        AppendJsonStrings(element, builder);
        return builder.ToString();
    }

    private static void AppendJsonStrings(JsonElement element, StringBuilder builder)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                builder.AppendLine(element.GetString());
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    AppendJsonStrings(property.Value, builder);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    AppendJsonStrings(item, builder);
                }
                break;
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
