using System.Net;
using System.Text.Json;

namespace Tau.Ai.Tests;

public sealed class AiUtilityHelpersTests
{
    [Theory]
    [InlineData("", "k4n83c7h0j2b")]
    [InlineData("hello", "1h6qa0qrowduu")]
    [InlineData("openai/responses:tool-call-id", "lar1lzhsi97v")]
    [InlineData("emoji 🧪", "1mfhsn5e3irzm")]
    public void ShortHash_Compute_MatchesUpstreamHash(string input, string expected)
    {
        Assert.Equal(expected, ShortHash.Compute(input));
    }

    [Fact]
    public void AiHeaderUtilities_ToDictionary_CopiesResponseAndContentHeaders()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("X-Request-Id", "request-1");
        response.Headers.TryAddWithoutValidation("X-Multi", ["a", "b"]);
        response.Content = new StringContent("body");
        response.Content.Headers.TryAddWithoutValidation("X-Content-Trace", "content-1");

        var headers = AiHeaderUtilities.ToDictionary(response);

        Assert.Equal("request-1", headers["X-Request-Id"]);
        Assert.Equal("a, b", headers["x-multi"]);
        Assert.Equal("content-1", headers["x-content-trace"]);
    }

    [Fact]
    public void AiHeaderUtilities_ToDictionary_CopiesHttpHeadersOnlyWhenRequested()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("X-Request-Id", "request-1");
        response.Content = new StringContent("body");
        response.Content.Headers.TryAddWithoutValidation("X-Content-Trace", "content-1");

        var headers = AiHeaderUtilities.ToDictionary(response, includeContentHeaders: false);

        Assert.Equal("request-1", headers["x-request-id"]);
        Assert.False(headers.ContainsKey("x-content-trace"));
    }

    [Fact]
    public void AiHeaderUtilities_ToDictionary_CopiesDirectHeadersWithCaseInsensitiveLookup()
    {
        using var content = new StringContent("body");
        content.Headers.TryAddWithoutValidation("X-Trace-Id", "trace-1");

        var headers = AiHeaderUtilities.ToDictionary(content.Headers);

        Assert.Equal("trace-1", headers["x-trace-id"]);
    }

    [Theory]
    [InlineData("prompt is too long: 213462 tokens > 200000 maximum")]
    [InlineData("Your input exceeds the context window of this model")]
    [InlineData("The input token count (1196265) exceeds the maximum number of tokens allowed (1048575)")]
    [InlineData("This endpoint's maximum context length is 8192 tokens. However, you requested about 12000 tokens")]
    [InlineData("prompt too long; exceeded max context length by 42 tokens")]
    [InlineData("context window exceeded")]
    [InlineData("413 status code (no body)")]
    public void ContextOverflowDetector_IsContextOverflowError_MatchesUpstreamPatterns(string error)
    {
        Assert.True(ContextOverflowDetector.IsContextOverflowError(error));
    }

    [Theory]
    [InlineData("Throttling error: Too many tokens, please wait before trying again.")]
    [InlineData("429 rate limit: too many requests")]
    [InlineData("Service unavailable: too many tokens queued")]
    public void ContextOverflowDetector_IsContextOverflowError_ExcludesKnownNonOverflowErrors(string error)
    {
        Assert.False(ContextOverflowDetector.IsContextOverflowError(error));
    }

    [Fact]
    public void ContextOverflowDetector_IsContextOverflow_DetectsSilentUsageOverflow()
    {
        var message = new AssistantMessage([new TextContent("accepted")])
        {
            StopReason = StopReason.EndTurn,
            Usage = new Usage(InputTokens: 900, OutputTokens: 10, CacheReadTokens: 200)
        };

        Assert.True(ContextOverflowDetector.IsContextOverflow(message, contextWindow: 1_000));
        Assert.False(ContextOverflowDetector.IsContextOverflow(message, contextWindow: 2_000));
    }

    [Fact]
    public void ContextOverflowDetector_IsContextOverflow_RequiresErrorStopReasonForErrorText()
    {
        var message = new AssistantMessage([new TextContent("not an error")])
        {
            StopReason = StopReason.EndTurn,
            ErrorMessage = "Your input exceeds the context window of this model"
        };

        Assert.False(ContextOverflowDetector.IsContextOverflow(message));
    }

    [Fact]
    public void ContextOverflowDetector_GetOverflowPatternSources_ReturnsCopy()
    {
        var patterns = ContextOverflowDetector.GetOverflowPatternSources();

        Assert.Contains(patterns, static pattern => pattern.Contains("prompt is too long", StringComparison.Ordinal));
        Assert.Contains(patterns, static pattern => pattern.Contains("context", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AssistantMessageDiagnostics_ExtractDiagnosticError_FormatsExceptionsAndThrownValues()
    {
        var exception = new InvalidOperationException("diagnostic failed");
        exception.Data["code"] = 429;
        var error = AssistantMessageDiagnostics.ExtractDiagnosticError(exception);

        Assert.Equal(nameof(InvalidOperationException), error.Name);
        Assert.Equal("diagnostic failed", error.Message);
        Assert.Equal(429, error.Code);

        var thrownValue = AssistantMessageDiagnostics.ExtractDiagnosticError("plain failure");
        Assert.Equal("ThrownValue", thrownValue.Name);
        Assert.Equal("plain failure", thrownValue.Message);
        Assert.Equal("null", AssistantMessageDiagnostics.FormatThrownValue(null));
    }

    [Fact]
    public void AssistantMessageDiagnostics_AppendAssistantMessageDiagnostic_PreservesExistingDiagnostics()
    {
        var originalDiagnostic = AssistantMessageDiagnostics.CreateAssistantMessageDiagnostic(
            "first",
            "first error",
            timestamp: DateTimeOffset.UnixEpoch);
        var nextDiagnostic = AssistantMessageDiagnostics.CreateAssistantMessageDiagnostic(
            "second",
            new InvalidOperationException("second error"),
            new Dictionary<string, object?> { ["phase"] = "test" },
            DateTimeOffset.UnixEpoch.AddSeconds(1));
        var message = new AssistantMessage([new TextContent("done")])
        {
            Diagnostics = [originalDiagnostic]
        };

        var updated = AssistantMessageDiagnostics.AppendAssistantMessageDiagnostic(message, nextDiagnostic);

        Assert.Single(message.Diagnostics!);
        Assert.Equal(["first", "second"], updated.Diagnostics!.Select(static diagnostic => diagnostic.Type).ToArray());
        Assert.Equal("test", updated.Diagnostics![1].Details!["phase"]);
    }

    [Fact]
    public void JsonSchemaHelpers_StringEnum_BuildsProviderCompatibleSchema()
    {
        var schema = JsonSchemaHelpers.StringEnum(
            ["add", "subtract"],
            description: "The operation to perform.",
            defaultValue: "add");

        Assert.Equal("string", schema.GetProperty("type").GetString());
        Assert.Equal(
            ["add", "subtract"],
            schema.GetProperty("enum").EnumerateArray().Select(static item => item.GetString()!).ToArray());
        Assert.Equal("The operation to perform.", schema.GetProperty("description").GetString());
        Assert.Equal("add", schema.GetProperty("default").GetString());
    }

    [Fact]
    public void JsonSchemaHelpers_StringEnum_OmitsEmptyOptionalFields()
    {
        var schema = JsonSchemaHelpers.StringEnum(["add"], description: "", defaultValue: "");

        Assert.Equal("string", schema.GetProperty("type").GetString());
        Assert.Equal("add", schema.GetProperty("enum").EnumerateArray().First().GetString());
        Assert.False(schema.TryGetProperty("description", out _));
        Assert.False(schema.TryGetProperty("default", out _));
    }

    [Fact]
    public void JsonSchemaHelpers_StringEnum_PreservesWhitespaceDescription()
    {
        var schema = JsonSchemaHelpers.StringEnum(["add"], description: "   ");

        Assert.Equal("   ", schema.GetProperty("description").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    public void StreamingJsonParser_ParseStreamingJson_ReturnsEmptyObjectForEmptyOrInvalidInput(string? json)
    {
        var parsed = StreamingJsonParser.ParseStreamingJson(json);

        Assert.Equal(JsonValueKind.Object, parsed.ValueKind);
        Assert.Empty(parsed.EnumerateObject());
    }

    [Fact]
    public void StreamingJsonParser_ParseStreamingJson_PrefersCompleteJsonParse()
    {
        var parsed = StreamingJsonParser.ParseStreamingJson("""{"name":"echo","count":2,"enabled":false}""");

        Assert.Equal("echo", parsed.GetProperty("name").GetString());
        Assert.Equal(2, parsed.GetProperty("count").GetInt32());
        Assert.False(parsed.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public void StreamingJsonParser_ParseStreamingJson_RecoversIncompleteNestedObject()
    {
        var parsed = StreamingJsonParser.ParseStreamingJson("""{"tool":"read_file","arguments":{"path":"src/Tau""");

        Assert.Equal("read_file", parsed.GetProperty("tool").GetString());
        Assert.Equal("src/Tau", parsed.GetProperty("arguments").GetProperty("path").GetString());
    }

    [Fact]
    public void StreamingJsonParser_ParseStreamingJson_RecoversIncompleteArrayAndDropsIncompleteValue()
    {
        var parsed = StreamingJsonParser.ParseStreamingJson("""{"items":[1,2,{"name":"third"}],"pending":""");

        var items = parsed.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(3, items.Length);
        Assert.Equal(1, items[0].GetInt32());
        Assert.Equal(2, items[1].GetInt32());
        Assert.Equal("third", items[2].GetProperty("name").GetString());
        Assert.False(parsed.TryGetProperty("pending", out _));
    }

    [Fact]
    public void StreamingJsonParser_ParseStreamingJson_RecoversIncompleteLiterals()
    {
        var trueLiteral = StreamingJsonParser.ParseStreamingJson("""{"ok":tru""");
        var falseLiteral = StreamingJsonParser.ParseStreamingJson("""{"enabled":fals""");
        var nullLiteral = StreamingJsonParser.ParseStreamingJson("""{"missing":nul""");

        Assert.True(trueLiteral.GetProperty("ok").GetBoolean());
        Assert.False(falseLiteral.GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, nullLiteral.GetProperty("missing").ValueKind);
    }

    [Fact]
    public void StreamingJsonParser_ParseStreamingJson_DropsIncompleteDecimal()
    {
        var parsed = StreamingJsonParser.ParseStreamingJson("""{"integer":1,"decimal":1.""");

        Assert.Equal(1, parsed.GetProperty("integer").GetInt32());
        Assert.False(parsed.TryGetProperty("decimal", out _));
    }

    [Fact]
    public void StreamingJsonParser_ParseStreamingJson_KeepsIncompleteExponentBase()
    {
        var parsed = StreamingJsonParser.ParseStreamingJson("""{"exponent":1e+""");

        Assert.Equal(1, parsed.GetProperty("exponent").GetInt32());
    }

    [Fact]
    public void StreamingJsonParser_ParseStreamingJson_RecoversIncompleteEscapedString()
    {
        var parsed = StreamingJsonParser.ParseStreamingJson("""{"text":"hello \"stream""");

        Assert.Equal("hello \"stream", parsed.GetProperty("text").GetString());
    }
}
