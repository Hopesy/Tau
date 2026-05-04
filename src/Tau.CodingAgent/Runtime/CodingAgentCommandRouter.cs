using System.Text.Json;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

public sealed class CodingAgentCommandRouter
{
    private readonly ICodingAgentRunner _runner;
    private readonly CodingAgentSettingsStore? _settingsStore;
    private readonly ICodingAgentClipboard _clipboard;
    private readonly string? _sessionFile;

    public CodingAgentCommandRouter(
        ICodingAgentRunner runner,
        CodingAgentSettingsStore? settingsStore = null,
        string? sessionFile = null,
        ICodingAgentClipboard? clipboard = null)
    {
        _runner = runner;
        _settingsStore = settingsStore;
        _clipboard = clipboard ?? new SystemCodingAgentClipboard();
        _sessionFile = sessionFile;
    }

    public async Task<CodingAgentCommandResult> TryHandleAsync(
        string input,
        CancellationToken cancellationToken = default)
    {
        if (!input.StartsWith("/", StringComparison.Ordinal))
        {
            return CodingAgentCommandResult.NotCommand;
        }

        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return CodingAgentCommandResult.NotCommand;
        }

        var command = parts[0].ToLowerInvariant();
        try
        {
            return command switch
            {
                "/help" => HandleHelpCommand(parts),
                "/name" => HandleNameCommand(input, parts),
                "/copy" => await HandleCopyCommandAsync(parts, cancellationToken).ConfigureAwait(false),
                "/export" => HandleExportCommand(input, parts),
                "/import" => HandleImportCommand(input, parts),
                "/new" => HandleNewCommand(parts),
                "/quit" => HandleQuitCommand(parts),
                "/session" => HandleSessionCommand(parts),
                "/model" => HandleModelCommand(parts),
                "/provider" => HandleProviderCommand(parts),
                "/models" => HandleModelsCommand(parts),
                "/providers" => HandleProvidersCommand(parts),
                "/auth" => HandleAuthCommand(parts),
                "/login" => HandleLoginCommand(parts),
                "/compact" => await HandleCompactCommandAsync(input, parts, cancellationToken).ConfigureAwait(false),
                _ => CodingAgentCommandResult.Error($"unknown command '{parts[0]}'")
            };
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or IOException or UnauthorizedAccessException or JsonException)
        {
            return CodingAgentCommandResult.Error(ex.Message);
        }
    }

    private static CodingAgentCommandResult HandleHelpCommand(IReadOnlyList<string> parts)
    {
        if (parts.Count != 1)
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/help"));
        }

        return CodingAgentCommandResult.Status(CodingAgentCommandCatalog.HelpLine);
    }

    private CodingAgentCommandResult HandleNameCommand(string input, IReadOnlyList<string> parts)
    {
        if (parts.Count == 1)
        {
            return CodingAgentCommandResult.Status($"session name: {FormatSessionName(_runner.SessionName)}");
        }

        var name = input[(input.IndexOf(' ') + 1)..].Trim();
        _runner.SessionName = name.Equals("clear", StringComparison.OrdinalIgnoreCase)
            ? null
            : name;
        return CodingAgentCommandResult.Status($"session name: {FormatSessionName(_runner.SessionName)}");
    }

    private async Task<CodingAgentCommandResult> HandleCopyCommandAsync(
        IReadOnlyList<string> parts,
        CancellationToken cancellationToken)
    {
        if (parts.Count != 1)
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/copy"));
        }

        var text = GetLastAssistantText();
        if (string.IsNullOrWhiteSpace(text))
        {
            return CodingAgentCommandResult.Error("no assistant text to copy");
        }

        await _clipboard.SetTextAsync(text, cancellationToken).ConfigureAwait(false);
        return CodingAgentCommandResult.Status("copied last assistant message to clipboard");
    }

    private CodingAgentCommandResult HandleExportCommand(string input, IReadOnlyList<string> parts)
    {
        if (parts.Count < 2)
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/export"));
        }

        var exportPath = input[(input.IndexOf(' ') + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(exportPath))
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/export"));
        }

        var store = new CodingAgentSessionStore(exportPath);
        store.Save(_runner.Messages, _runner.Model, _runner.SessionName);
        return CodingAgentCommandResult.Status($"exported session to {store.Path}");
    }

    private CodingAgentCommandResult HandleImportCommand(string input, IReadOnlyList<string> parts)
    {
        if (parts.Count < 2)
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/import"));
        }

        var importPath = input[(input.IndexOf(' ') + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(importPath))
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/import"));
        }

        var store = new CodingAgentSessionStore(importPath);
        var snapshot = store.LoadStrict();
        _runner.RestoreSession(snapshot);
        return CodingAgentCommandResult.Status(
            $"imported session from {store.Path}: {snapshot.Messages.Count} messages, model {_runner.Model.Provider}/{_runner.Model.Id}, name {FormatSessionName(_runner.SessionName)}");
    }

    private CodingAgentCommandResult HandleNewCommand(IReadOnlyList<string> parts)
    {
        if (parts.Count != 1)
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/new"));
        }

        _runner.ResetSession();
        return CodingAgentCommandResult.Status($"started new session with model {_runner.Model.Provider}/{_runner.Model.Id}");
    }

    private static CodingAgentCommandResult HandleQuitCommand(IReadOnlyList<string> parts)
    {
        if (parts.Count != 1)
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/quit"));
        }

        return CodingAgentCommandResult.Exit("Goodbye!");
    }

    private CodingAgentCommandResult HandleSessionCommand(IReadOnlyList<string> parts)
    {
        if (parts.Count != 1)
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/session"));
        }

        var stats = _runner.GetSessionStats(_sessionFile);
        var file = string.IsNullOrWhiteSpace(stats.SessionFile) ? "none" : stats.SessionFile;
        return CodingAgentCommandResult.Status(
            $"session: name {FormatSessionName(stats.SessionName)}, model {stats.Provider}/{stats.Model}, messages {stats.TotalMessages} (user {stats.UserMessages}, assistant {stats.AssistantMessages}, tool {stats.ToolResultMessages}, toolCalls {stats.ToolCalls}), file {file}");
    }

    private CodingAgentCommandResult HandleModelCommand(IReadOnlyList<string> parts)
    {
        if (parts.Count == 1 || IsCurrentKeyword(parts[1]))
        {
            return CodingAgentCommandResult.Status($"model: {_runner.Model.Provider}/{_runner.Model.Id}");
        }

        Model selected;
        if (parts.Count == 2)
        {
            selected = _runner.SelectModel(null, parts[1]);
        }
        else if (parts.Count == 3)
        {
            selected = _runner.SelectModel(parts[1], parts[2]);
        }
        else
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/model"));
        }

        SaveDefaultModel(selected);
        return CodingAgentCommandResult.Status($"model: {selected.Provider}/{selected.Id}");
    }

    private CodingAgentCommandResult HandleProviderCommand(IReadOnlyList<string> parts)
    {
        if (parts.Count == 1 || IsCurrentKeyword(parts[1]))
        {
            return CodingAgentCommandResult.Status($"provider: {_runner.Model.Provider}");
        }

        if (parts.Count != 2)
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/provider"));
        }

        var selected = _runner.SelectModel(parts[1], null);
        SaveDefaultModel(selected);
        return CodingAgentCommandResult.Status($"model: {selected.Provider}/{selected.Id}");
    }

    private CodingAgentCommandResult HandleModelsCommand(IReadOnlyList<string> parts)
    {
        if (parts.Count > 2)
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/models"));
        }

        var provider = parts.Count == 2 ? parts[1] : _runner.Model.Provider;
        var models = _runner.GetModels(provider);
        if (models.Count == 0)
        {
            return CodingAgentCommandResult.Error($"provider '{provider}' has no registered models");
        }

        return CodingAgentCommandResult.Status($"models {provider}: {string.Join(", ", models.Select(model => model.Id))}");
    }

    private CodingAgentCommandResult HandleProvidersCommand(IReadOnlyList<string> parts)
    {
        if (parts.Count != 1)
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/providers"));
        }

        return CodingAgentCommandResult.Status($"providers: {string.Join(", ", _runner.GetProviders())}");
    }

    private CodingAgentCommandResult HandleAuthCommand(IReadOnlyList<string> parts)
    {
        if (parts.Count > 2)
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/auth"));
        }

        var status = parts.Count == 2
            ? _runner.GetAuthStatus(parts[1])
            : _runner.GetAuthStatus();
        var configured = status.IsConfigured ? "configured" : "missing";
        var oauth = status.UsesOAuth ? ", oauth" : string.Empty;
        return CodingAgentCommandResult.Status($"auth {status.Provider}: {configured} via {status.Source}{oauth}. {status.Message}");
    }

    private CodingAgentCommandResult HandleLoginCommand(IReadOnlyList<string> parts)
    {
        if (parts.Count > 2)
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/login"));
        }

        var status = parts.Count == 2
            ? _runner.GetAuthStatus(parts[1])
            : _runner.GetAuthStatus();
        if (status.IsConfigured)
        {
            return CodingAgentCommandResult.Status($"auth {status.Provider}: already configured via {status.Source}.");
        }

        var guidance = status.CanLogin
            ? "OAuth login flow is not yet ported in Tau; import credentials into auth.json or configure environment variables."
            : "No OAuth login flow is registered for this provider; configure environment variables, auth.json, or models.json.";
        return CodingAgentCommandResult.Error($"login {status.Provider}: {guidance}");
    }

    private async Task<CodingAgentCommandResult> HandleCompactCommandAsync(
        string input,
        IReadOnlyList<string> parts,
        CancellationToken cancellationToken)
    {
        var instructions = parts.Count == 1
            ? null
            : input[(input.IndexOf(' ') + 1)..].Trim();
        var result = await _runner
            .CompactAsync(string.IsNullOrWhiteSpace(instructions) ? null : instructions, cancellationToken)
            .ConfigureAwait(false);
        return CodingAgentCommandResult.Status(
            $"compacted session: {result.MessagesBefore} -> {result.MessagesAfter} messages");
    }

    private void SaveDefaultModel(Model model)
    {
        _settingsStore?.SaveDefaultModel(model);
    }

    private static bool IsCurrentKeyword(string value) =>
        value.Equals("current", StringComparison.OrdinalIgnoreCase);

    private static string FormatSessionName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "none" : name;

    private string? GetLastAssistantText()
    {
        var assistant = _runner.Messages
            .OfType<AssistantMessage>()
            .LastOrDefault();
        if (assistant is null)
        {
            return null;
        }

        var text = string.Join(
            "\n\n",
            assistant.Content
                .OfType<TextContent>()
                .Select(content => content.Text.Trim())
                .Where(content => !string.IsNullOrWhiteSpace(content)));
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
