using Tau.Agent;
using Tau.Ai;
using Tau.Ai.Observability;
using Tau.CodingAgent.Runtime;

namespace Tau.WebUi.Services;

public static class WebUiRunnerFactory
{
    public static ICodingAgentRunner Create(
        string provider,
        string model,
        IReadOnlyList<ChatMessage>? history = null,
        ITauLogSink? logSink = null,
        TauRuntimeLogContext? logContext = null)
    {
        return RuntimeCodingAgentRunner.Create(provider, model, history, logSink: logSink, logContext: logContext);
    }

    public static ICodingAgentRunner Create(
        string provider,
        string model,
        IReadOnlyList<ChatMessage>? history,
        WebArtifactService artifacts,
        string sessionId,
        ITauLogSink? logSink = null,
        TauRuntimeLogContext? logContext = null)
    {
        var tools = RuntimeCodingAgentRunner.CreateDefaultTools()
            .Concat(WebUiTools.CreateSessionTools(sessionId, artifacts))
            .ToArray();

        return RuntimeCodingAgentRunner.Create(
            provider,
            model,
            history,
            toolsOverride: tools,
            logSink: logSink,
            logContext: logContext);
    }
}
