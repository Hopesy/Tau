using System.Net;
using System.Net.Http;
using System.Text;
using System.Net.Http.Headers;
using System.Text.Json;
using Tau.Ai.Providers.PiMessages;
using Tau.Ai.Streaming;
using Tau.Ai.Registry;
using Tau.Ai.Utilities;

namespace Tau.Ai.Tests;

public sealed class CurrentReferenceParityTests
{
    [Fact]
    public void ModelCatalog_ContainsCurrentReferenceProviderEntrypoints()
    {
        var catalog = new ModelCatalog();
        Assert.Contains("baseten", catalog.GetProviders(), StringComparer.OrdinalIgnoreCase);
        Assert.Contains("kimi-coding", catalog.GetProviders(), StringComparer.OrdinalIgnoreCase);
        Assert.Contains("radius", catalog.GetProviders(), StringComparer.OrdinalIgnoreCase);
        Assert.Equal("openai-responses", catalog.GetModel("xai", "grok-3").Api);
    }

    [Fact]
    public void StrictSchema_MakesOptionalPropertiesNullableAndRequired()
    {
        using var document = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"}}}");
        var strict = ConstrainedSampling.MakeStrictJsonSchema(document.RootElement);
        Assert.Equal(JsonValueKind.Array, strict.GetProperty("required").ValueKind);
        Assert.False(strict.GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public void Retry_RespectsExplicitHeaderOverride()
    {
        using var request = new HttpRequestMessage();
        var headers = request.Headers;
        headers.TryAddWithoutValidation("x-should-retry", "false");
        Assert.False(ProviderRetry.ShouldRetry(HttpStatusCode.ServiceUnavailable, headers));
    }

    [Fact]
    public async Task PiMessagesProvider_ParsesCurrentSseEvents()
    {
        using var client = new HttpClient(new StubHandler()) { BaseAddress = new Uri("https://test.invalid") };
        var provider = new PiMessagesProvider(client);
        var model = new Model { Id = "m", Name = "m", Api = "pi-messages", Provider = "radius", BaseUrl = "https://test.invalid" };
        var events = new List<StreamEvent>();
        await foreach (var item in provider.Stream(model, new LlmContext(null, [new UserMessage("hi")], null), new StreamOptions { ApiKey = "test-key" }))
            events.Add(item);
        Assert.Contains(events, item => item is DoneEvent);
        Assert.Contains(events, item => item is TextDeltaEvent delta && delta.Delta == "ok");
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = "data: {\"type\":\"start\"}\n\n" +
                       "data: {\"type\":\"text_start\",\"contentIndex\":0}\n\n" +
                       "data: {\"type\":\"text_delta\",\"contentIndex\":0,\"delta\":\"ok\"}\n\n" +
                       "data: {\"type\":\"done\",\"reason\":\"stop\",\"usage\":{\"input\":1,\"output\":1}}\n\n";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
            });
        }
    }
}
