using System.Text.Json;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

public sealed class CodingAgentCommandRouter
{
    private readonly ICodingAgentRunner _runner;
    private readonly CodingAgentSettingsStore? _settingsStore;

    public CodingAgentCommandRouter(ICodingAgentRunner runner, CodingAgentSettingsStore? settingsStore = null)
    {
        _runner = runner;
        _settingsStore = settingsStore;
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
                "/new" => HandleNewCommand(parts),
                "/model" => HandleModelCommand(parts),
                "/provider" => HandleProviderCommand(parts),
                "/models" => HandleModelsCommand(parts),
                "/providers" => CodingAgentCommandResult.Status($"providers: {string.Join(", ", _runner.GetProviders())}"),
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

    private CodingAgentCommandResult HandleNewCommand(IReadOnlyList<string> parts)
    {
        if (parts.Count != 1)
        {
            return CodingAgentCommandResult.Error("usage: /new");
        }

        _runner.ResetSession();
        return CodingAgentCommandResult.Status($"started new session with model {_runner.Model.Provider}/{_runner.Model.Id}");
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
            return CodingAgentCommandResult.Error("usage: /model [provider/model | model] or /model <provider> <model>");
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
            return CodingAgentCommandResult.Error("usage: /provider [provider]");
        }

        var selected = _runner.SelectModel(parts[1], null);
        SaveDefaultModel(selected);
        return CodingAgentCommandResult.Status($"model: {selected.Provider}/{selected.Id}");
    }

    private CodingAgentCommandResult HandleModelsCommand(IReadOnlyList<string> parts)
    {
        if (parts.Count > 2)
        {
            return CodingAgentCommandResult.Error("usage: /models [provider]");
        }

        var provider = parts.Count == 2 ? parts[1] : _runner.Model.Provider;
        var models = _runner.GetModels(provider);
        if (models.Count == 0)
        {
            return CodingAgentCommandResult.Error($"provider '{provider}' has no registered models");
        }

        return CodingAgentCommandResult.Status($"models {provider}: {string.Join(", ", models.Select(model => model.Id))}");
    }

    private CodingAgentCommandResult HandleAuthCommand(IReadOnlyList<string> parts)
    {
        if (parts.Count > 2)
        {
            return CodingAgentCommandResult.Error("usage: /auth [provider]");
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
            return CodingAgentCommandResult.Error("usage: /login [provider]");
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
}
