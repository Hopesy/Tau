using Tau.Agent;
using Tau.CodingAgent.Runtime;
using Tau.Tui.Runtime;

var terminal = new SystemConsoleTerminal();
var ui = new InteractiveConsoleSession(terminal);
var runner = RuntimeCodingAgentRunner.CreateDefault();
var host = new CodingAgentHost(ui, runner);
using var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

return await host.RunAsync(cts.Token).ConfigureAwait(false);
