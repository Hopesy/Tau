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
}
