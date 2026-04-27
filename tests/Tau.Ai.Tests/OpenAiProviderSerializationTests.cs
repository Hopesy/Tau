using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Tau.Ai.Providers.OpenAi;

namespace Tau.Ai.Tests;

public sealed class OpenAiProviderSerializationTests
{
    [Fact]
    public async Task Stream_WithToolSchema_DoesNotFailOnListObjectSerialization()
    {
        using var handler = new StubHandler();
        using var client = new HttpClient(handler);
        var provider = new OpenAiProvider(client);

        var model = new Model
        {
            Id = "gpt-5.4",
            Name = "GPT-5.4",
            Api = "openai-chat-completions",
            Provider = "openai",
            BaseUrl = "https://example.invalid/v1"
        };

        var toolSchema = """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string" }
          },
          "required": ["path"]
        }
        """;

        var context = new LlmContext
        {
            Messages =
            [
                new UserMessage([new TextContent("hello")])
            ],
            Tools =
            [
                new Tool("read_file", "Read a file", JsonDocument.Parse(toolSchema).RootElement.Clone())
            ]
        };

        var stream = provider.Stream(model, context, new StreamOptions { ApiKey = "test-key" });

        var events = new List<StreamEvent>();
        await foreach (var evt in stream)
        {
            events.Add(evt);
        }

        var error = Assert.Single(events.OfType<ErrorEvent>());
        Assert.Contains("stubbed-openai-error", error.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("Unable to cast object of type", error.Error, StringComparison.Ordinal);
        Assert.NotNull(handler.CapturedBody);
        Assert.Contains("\"tools\"", handler.CapturedBody, StringComparison.Ordinal);
        Assert.Contains("\"function\"", handler.CapturedBody, StringComparison.Ordinal);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public string? CapturedBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("stubbed-openai-error", Encoding.UTF8, "text/plain")
            };
        }
    }
}
