using Tau.CodingAgent.Runtime;
using Tau.Tui.Runtime;

var terminal = new SystemConsoleTerminal();
var ui = new InteractiveConsoleSession(terminal);
var sessionStore = new CodingAgentSessionStore();
var settingsStore = new CodingAgentSettingsStore();
var session = sessionStore.Load();
var settings = settingsStore.Load();
var providerId = Environment.GetEnvironmentVariable("TAU_PROVIDER") ?? session.Provider ?? settings.DefaultProvider;
var modelId = Environment.GetEnvironmentVariable("TAU_MODEL")
              ?? (string.Equals(providerId, session.Provider, StringComparison.OrdinalIgnoreCase) ? session.Model : null)
              ?? (string.Equals(providerId, settings.DefaultProvider, StringComparison.OrdinalIgnoreCase) ? settings.DefaultModel : null);
var runner = RuntimeCodingAgentRunner.Create(providerId, modelId, session.Messages);
runner.SessionName = session.Name;
var host = new CodingAgentHost(ui, runner, sessionStore, settingsStore);
using var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

return await host.RunAsync(cts.Token).ConfigureAwait(false);
