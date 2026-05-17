using Tau.Agent;
using Tau.Ai;
using Tau.CodingAgent.Runtime;

namespace Tau.WebUi.Services;

public static class WebUiRunnerFactory
{
    public static ICodingAgentRunner Create(string provider, string model, IReadOnlyList<ChatMessage>? history = null)
    {
        return RuntimeCodingAgentRunner.Create(provider, model, history);
    }
}
