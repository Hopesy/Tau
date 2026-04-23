using Tau.Ai.Streaming;

namespace Tau.Ai.Providers.Bedrock;

public sealed class BedrockProvider : IStreamProvider
{
    public string Api => "bedrock-converse-stream";

    public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options)
    {
        var stream = new AssistantMessageStream();
        stream.Push(new ErrorEvent("Amazon Bedrock provider registry and auth detection are wired, but SigV4 request signing is not yet implemented in Tau."));
        return stream;
    }

    public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options) =>
        Stream(model, context, options);
}
