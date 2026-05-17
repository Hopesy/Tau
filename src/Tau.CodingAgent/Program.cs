using Tau.CodingAgent.Runtime;
using Tau.Tui.Runtime;

var terminal = new SystemConsoleTerminal();
var editor = CreateInteractiveEditorIfAttached();
var ui = new InteractiveConsoleSession(terminal, editor);

static InteractiveInputEditor? CreateInteractiveEditorIfAttached()
{
    if (Console.IsInputRedirected || Console.IsOutputRedirected)
    {
        return null;
    }

    if (string.Equals(Environment.GetEnvironmentVariable("TAU_CODING_AGENT_DISABLE_INPUT_EDITOR"), "1", StringComparison.Ordinal))
    {
        return null;
    }

    return new InteractiveInputEditor(new SystemConsoleKeyReader(), new SystemConsoleInteractiveRenderer());
}
var sessionFile = Environment.GetEnvironmentVariable("TAU_CODING_AGENT_SESSION_FILE");
var sessionStore = CodingAgentTreeSessionStore.IsJsonlPath(sessionFile)
    ? null
    : new CodingAgentSessionStore();
var treeSessionController = CodingAgentTreeSessionController.OpenOrCreate();
var settingsStore = new CodingAgentSettingsStore();
var extensionCommandStore = new CodingAgentExtensionCommandStore();
var extensionResources = extensionCommandStore.LoadResources();
var promptTemplateStore = new CodingAgentPromptTemplateStore(explicitPaths: extensionResources.PromptPaths);
var skillStore = new CodingAgentSkillStore(explicitPaths: extensionResources.SkillPaths);
var flatSession = sessionStore?.Load() ?? new CodingAgentSessionSnapshot([], null, null, null);
var treeSession = treeSessionController.LoadSnapshot();
var session = CodingAgentTreeSessionStore.HasExplicitTreeSessionPath || treeSession.Messages.Count > 0
    ? treeSession.ToFlatSnapshot()
    : flatSession;
var settings = settingsStore.Load();
var providerId = Environment.GetEnvironmentVariable("TAU_PROVIDER") ?? session.Provider ?? settings.DefaultProvider;
var modelId = Environment.GetEnvironmentVariable("TAU_MODEL")
              ?? (string.Equals(providerId, session.Provider, StringComparison.OrdinalIgnoreCase) ? session.Model : null)
              ?? (string.Equals(providerId, settings.DefaultProvider, StringComparison.OrdinalIgnoreCase) ? settings.DefaultModel : null);
var runner = RuntimeCodingAgentRunner.Create(providerId, modelId, session.Messages, skills: skillStore.Load());
runner.SessionName = session.Name;
var host = new CodingAgentHost(
    ui,
    runner,
    sessionStore,
    settingsStore,
    treeSessionController: treeSessionController,
    promptTemplateStore: promptTemplateStore,
    skillStore: skillStore,
    extensionCommandStore: extensionCommandStore,
    autoCompaction: CodingAgentAutoCompactionOptions.FromEnvironment(),
    retryOptions: CodingAgentRetryOptions.FromSettingsOrEnvironment(settings));
using var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

return await host.RunAsync(cts.Token).ConfigureAwait(false);
