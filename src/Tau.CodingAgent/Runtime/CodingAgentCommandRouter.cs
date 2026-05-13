using System.Text.Json;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

public sealed class CodingAgentCommandRouter
{
    private readonly ICodingAgentRunner _runner;
    private readonly CodingAgentSettingsStore? _settingsStore;
    private readonly ICodingAgentClipboard _clipboard;
    private readonly CodingAgentTreeSessionController? _treeSessionController;
    private readonly CodingAgentPromptTemplateStore? _promptTemplateStore;
    private readonly CodingAgentSkillStore? _skillStore;
    private readonly CodingAgentExtensionCommandStore? _extensionCommandStore;
    private readonly CodingAgentAutoCompactionOptions _autoCompaction;
    private readonly string? _sessionFile;

    public CodingAgentCommandRouter(
        ICodingAgentRunner runner,
        CodingAgentSettingsStore? settingsStore = null,
        string? sessionFile = null,
        ICodingAgentClipboard? clipboard = null,
        CodingAgentTreeSessionController? treeSessionController = null,
        CodingAgentPromptTemplateStore? promptTemplateStore = null,
        CodingAgentSkillStore? skillStore = null,
        CodingAgentExtensionCommandStore? extensionCommandStore = null,
        CodingAgentAutoCompactionOptions? autoCompaction = null)
    {
        _runner = runner;
        _settingsStore = settingsStore;
        _clipboard = clipboard ?? new SystemCodingAgentClipboard();
        _treeSessionController = treeSessionController;
        _promptTemplateStore = promptTemplateStore;
        _skillStore = skillStore;
        _extensionCommandStore = extensionCommandStore;
        _autoCompaction = autoCompaction ?? CodingAgentAutoCompactionOptions.Disabled;
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
                "/tree" => HandleTreeCommand(parts),
                "/label" => HandleLabelCommand(input, parts),
                "/fork" => HandleForkCommand(parts),
                "/resume" => HandleResumeCommand(input, parts),
                "/model" => HandleModelCommand(parts),
                "/provider" => HandleProviderCommand(parts),
                "/models" => HandleModelsCommand(parts),
                "/providers" => HandleProvidersCommand(parts),
                "/prompts" => HandlePromptsCommand(parts),
                "/skills" => HandleSkillsCommand(parts),
                "/extensions" => HandleExtensionsCommand(parts),
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
        var exportPath = parts.Count == 1
            ? GetDefaultHtmlExportPath()
            : input[(input.IndexOf(' ') + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(exportPath))
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/export"));
        }

        if (CodingAgentHtmlSessionExporter.IsHtmlPath(exportPath))
        {
            CodingAgentSessionSnapshot snapshot;
            CodingAgentTreeSessionSummary? treeSummary = null;
            string? sessionJsonl = null;
            if (_treeSessionController is not null)
            {
                _treeSessionController.SyncFromRunner(_runner);
                snapshot = _treeSessionController.LoadSnapshot().ToFlatSnapshot();
                treeSummary = _treeSessionController.GetSummary();
                sessionJsonl = _treeSessionController.ExportCurrentBranchText();
            }
            else
            {
                snapshot = new CodingAgentSessionSnapshot(
                    _runner.Messages,
                    _runner.Model.Provider,
                    _runner.Model.Id,
                    _runner.SessionName);
            }

            var htmlPath = CodingAgentHtmlSessionExporter.Export(
                exportPath,
                snapshot.Messages,
                snapshot.Provider ?? _runner.Model.Provider,
                snapshot.Model ?? _runner.Model.Id,
                snapshot.Name ?? _runner.SessionName,
                treeSummary,
                sessionJsonl);
            return CodingAgentCommandResult.Status($"exported session transcript to {htmlPath}");
        }

        if (CodingAgentTreeSessionStore.IsJsonlPath(exportPath))
        {
            if (_treeSessionController is null)
            {
                return CodingAgentCommandResult.Error("JSONL export requires an active tree session");
            }

            _treeSessionController.SyncFromRunner(_runner);
            var treeExportPath = _treeSessionController.ExportCurrentBranch(exportPath);
            return CodingAgentCommandResult.Status($"exported session branch to {treeExportPath}");
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

        if (CodingAgentTreeSessionStore.IsJsonlPath(importPath))
        {
            if (_treeSessionController is null)
            {
                return CodingAgentCommandResult.Error("JSONL import requires an active tree session");
            }

            var treeSnapshot = _treeSessionController.Resume(importPath);
            _runner.RestoreSession(treeSnapshot.ToFlatSnapshot());
            return CodingAgentCommandResult.Status(
                $"resumed session from {treeSnapshot.FilePath}: {treeSnapshot.Messages.Count} messages, model {_runner.Model.Provider}/{_runner.Model.Id}, name {FormatSessionName(_runner.SessionName)}, leaf {FormatTreeId(treeSnapshot.LeafId)}");
        }

        var store = new CodingAgentSessionStore(importPath);
        var snapshot = store.LoadStrict();
        _runner.RestoreSession(snapshot);
        _treeSessionController?.ReplaceWithRunnerSession(_runner);
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
        _treeSessionController?.StartNewFromRunner(_runner);
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
        var tokenBudget = FormatTokenBudget(stats);
        if (_treeSessionController is not null)
        {
            _treeSessionController.SyncFromRunner(_runner);
            var tree = _treeSessionController.GetSummary();
            return CodingAgentCommandResult.Status(
                $"session: name {FormatSessionName(stats.SessionName)}, model {stats.Provider}/{stats.Model}, messages {stats.TotalMessages} (user {stats.UserMessages}, assistant {stats.AssistantMessages}, tool {stats.ToolResultMessages}, toolCalls {stats.ToolCalls}), tokens {tokenBudget}, file {file}, tree {tree.FilePath}, leaf {FormatTreeId(tree.LeafId)}, entries {tree.EntryCount}, messages {tree.TotalMessageCount}, branch entries {tree.BranchEntryCount}, branch messages {tree.BranchMessageCount}, branches {tree.BranchPointCount}, labels {tree.LabelCount}");
        }

        return CodingAgentCommandResult.Status(
            $"session: name {FormatSessionName(stats.SessionName)}, model {stats.Provider}/{stats.Model}, messages {stats.TotalMessages} (user {stats.UserMessages}, assistant {stats.AssistantMessages}, tool {stats.ToolResultMessages}, toolCalls {stats.ToolCalls}), tokens {tokenBudget}, file {file}");
    }

    private CodingAgentCommandResult HandleTreeCommand(IReadOnlyList<string> parts)
    {
        if (_treeSessionController is null)
        {
            return CodingAgentCommandResult.Error("tree sessions are not enabled");
        }

        if (!TryParseTreeOptions(parts, GetDefaultTreeFilterMode(), out var options))
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/tree"));
        }

        _treeSessionController.SyncFromRunner(_runner);
        return CodingAgentCommandResult.Status(_treeSessionController.FormatTree(options));
    }

    private CodingAgentCommandResult HandleLabelCommand(string input, IReadOnlyList<string> parts)
    {
        if (parts.Count < 2)
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/label"));
        }

        if (_treeSessionController is null)
        {
            return CodingAgentCommandResult.Error("tree sessions are not enabled");
        }

        _treeSessionController.SyncFromRunner(_runner);
        var entryId = parts[1];
        if (parts.Count == 2)
        {
            var current = _treeSessionController.GetLabel(entryId);
            return CodingAgentCommandResult.Status($"label {entryId}: {FormatSessionName(current)}");
        }

        var label = string.Join(" ", parts.Skip(2));
        var normalized = label.Equals("clear", StringComparison.OrdinalIgnoreCase) ? null : label;
        _treeSessionController.AppendLabelChange(entryId, normalized);
        return CodingAgentCommandResult.Status($"label {entryId}: {FormatSessionName(normalized)}");
    }

    private CodingAgentCommandResult HandleForkCommand(IReadOnlyList<string> parts)
    {
        if (parts.Count != 2)
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/fork"));
        }

        if (_treeSessionController is null)
        {
            return CodingAgentCommandResult.Error("tree sessions are not enabled");
        }

        _treeSessionController.SyncFromRunner(_runner);
        var snapshot = _treeSessionController.Branch(parts[1]);
        _runner.RestoreSession(snapshot.ToFlatSnapshot());
        return CodingAgentCommandResult.Status(
            $"forked session at {parts[1]}: leaf {FormatTreeId(snapshot.LeafId)}, messages {snapshot.Messages.Count}, model {_runner.Model.Provider}/{_runner.Model.Id}");
    }

    private CodingAgentCommandResult HandleResumeCommand(string input, IReadOnlyList<string> parts)
    {
        if (parts.Count > 2)
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/resume"));
        }

        if (_treeSessionController is null)
        {
            return CodingAgentCommandResult.Error("tree sessions are not enabled");
        }

        var resumeTarget = parts.Count == 1
            ? "latest"
            : input[(input.IndexOf(' ') + 1)..].Trim();
        var path = resumeTarget.Equals("latest", StringComparison.OrdinalIgnoreCase)
            ? CodingAgentTreeSessionStore.FindMostRecentSession()
            : resumeTarget;
        if (string.IsNullOrWhiteSpace(path))
        {
            return CodingAgentCommandResult.Error("no JSONL session found to resume");
        }

        var snapshot = _treeSessionController.Resume(path);
        _runner.RestoreSession(snapshot.ToFlatSnapshot());
        return CodingAgentCommandResult.Status(
            $"resumed session from {snapshot.FilePath}: {snapshot.Messages.Count} messages, model {_runner.Model.Provider}/{_runner.Model.Id}, name {FormatSessionName(_runner.SessionName)}, leaf {FormatTreeId(snapshot.LeafId)}");
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
        _treeSessionController?.SyncFromRunner(_runner);
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
        _treeSessionController?.SyncFromRunner(_runner);
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

    private CodingAgentCommandResult HandlePromptsCommand(IReadOnlyList<string> parts)
    {
        if (parts.Count != 1)
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/prompts"));
        }

        var templates = _promptTemplateStore?.Load() ?? [];
        if (templates.Count == 0)
        {
            return CodingAgentCommandResult.Status("prompts: none");
        }

        var promptList = templates.Select(template =>
        {
            var hint = string.IsNullOrWhiteSpace(template.ArgumentHint) ? string.Empty : $" {template.ArgumentHint}";
            var description = string.IsNullOrWhiteSpace(template.Description) ? string.Empty : $" - {template.Description}";
            return $"/{template.Name}{hint}{description}";
        });
        return CodingAgentCommandResult.Status($"prompts: {string.Join("; ", promptList)}");
    }

    private CodingAgentCommandResult HandleSkillsCommand(IReadOnlyList<string> parts)
    {
        if (parts.Count != 1)
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/skills"));
        }

        var skills = _skillStore?.Load() ?? [];
        if (skills.Count == 0)
        {
            return CodingAgentCommandResult.Status("skills: none");
        }

        var skillList = skills.Select(skill =>
        {
            var description = string.IsNullOrWhiteSpace(skill.Description) ? string.Empty : $" - {skill.Description}";
            return $"/skill:{skill.Name}{description}";
        });
        return CodingAgentCommandResult.Status($"skills: {string.Join("; ", skillList)}");
    }

    private CodingAgentCommandResult HandleExtensionsCommand(IReadOnlyList<string> parts)
    {
        if (parts.Count != 1)
        {
            return CodingAgentCommandResult.Error(CodingAgentCommandCatalog.Usage("/extensions"));
        }

        var commands = _extensionCommandStore?.Load() ?? [];
        if (commands.Count == 0)
        {
            return CodingAgentCommandResult.Status("extensions: none");
        }

        var commandList = commands.Select(command =>
        {
            var hint = string.IsNullOrWhiteSpace(command.ArgumentHint) ? string.Empty : $" {command.ArgumentHint}";
            var description = string.IsNullOrWhiteSpace(command.Description) ? string.Empty : $" - {command.Description}";
            var mode = command.SendToRunner ? ", runner" : string.Empty;
            return $"/{command.InvocationName}{hint}{description} ({command.Scope}{mode})";
        });
        return CodingAgentCommandResult.Status($"extensions: {string.Join("; ", commandList)}");
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
        _treeSessionController?.SyncFromRunner(_runner);
        var result = await _runner
            .CompactAsync(string.IsNullOrWhiteSpace(instructions) ? null : instructions, cancellationToken)
            .ConfigureAwait(false);
        _treeSessionController?.RecordCompaction(_runner, result);
        return CodingAgentCommandResult.Status(
            $"compacted session: {result.MessagesBefore} -> {result.MessagesAfter} messages");
    }

    private void SaveDefaultModel(Model model)
    {
        _settingsStore?.SaveDefaultModel(model);
    }

    private string GetDefaultHtmlExportPath()
    {
        var sourcePath = _treeSessionController?.Path ?? _sessionFile;
        var directory = string.IsNullOrWhiteSpace(sourcePath)
            ? Environment.CurrentDirectory
            : (Path.GetDirectoryName(sourcePath) ?? Environment.CurrentDirectory);
        var basename = string.IsNullOrWhiteSpace(sourcePath)
            ? "session"
            : Path.GetFileNameWithoutExtension(sourcePath);
        basename = SanitizeFileName(string.IsNullOrWhiteSpace(basename) ? "session" : basename);
        return Path.Combine(directory, $"tau-session-{basename}.html");
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray();
        return new string(chars);
    }

    private static bool IsCurrentKeyword(string value) =>
        value.Equals("current", StringComparison.OrdinalIgnoreCase);

    private static string FormatSessionName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "none" : name;

    private static string FormatTreeId(string? id) =>
        string.IsNullOrWhiteSpace(id) ? "root" : id.Length <= 8 ? id : id[..8];

    private static bool TryParseTreeOptions(
        IReadOnlyList<string> parts,
        CodingAgentTreeFilterMode defaultFilterMode,
        out CodingAgentTreeFormatOptions options)
    {
        var maxEntries = 24;
        var filterMode = defaultFilterMode;
        var showLabelTimestamps = false;
        string? searchQuery = null;
        var hasMaxEntries = false;
        var hasFilter = false;

        for (var i = 1; i < parts.Count; i++)
        {
            var part = parts[i];
            if (IsSearchOption(part))
            {
                if (!string.IsNullOrWhiteSpace(searchQuery) || i == parts.Count - 1)
                {
                    options = new CodingAgentTreeFormatOptions();
                    return false;
                }

                searchQuery = string.Join(' ', parts.Skip(i + 1));
                break;
            }

            if (int.TryParse(part, out var parsedMax))
            {
                if (hasMaxEntries || parsedMax <= 0)
                {
                    options = new CodingAgentTreeFormatOptions();
                    return false;
                }

                maxEntries = parsedMax;
                hasMaxEntries = true;
                continue;
            }

            if (IsLabelTimestampOption(part))
            {
                showLabelTimestamps = true;
                continue;
            }

            if (TryParseTreeFilterMode(part, out var parsedFilter))
            {
                if (hasFilter)
                {
                    options = new CodingAgentTreeFormatOptions();
                    return false;
                }

                filterMode = parsedFilter;
                hasFilter = true;
                continue;
            }

            options = new CodingAgentTreeFormatOptions();
            return false;
        }

        options = new CodingAgentTreeFormatOptions(maxEntries, filterMode, showLabelTimestamps, searchQuery);
        return true;
    }

    private CodingAgentTreeFilterMode GetDefaultTreeFilterMode()
    {
        var configured = _settingsStore?.Load().TreeFilterMode;
        return !string.IsNullOrWhiteSpace(configured) &&
               TryParseTreeFilterMode(configured, out var filterMode)
            ? filterMode
            : CodingAgentTreeFilterMode.Default;
    }

    private static bool IsSearchOption(string value) =>
        value.Equals("--search", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("-s", StringComparison.OrdinalIgnoreCase);

    private static bool IsLabelTimestampOption(string value) =>
        value.Equals("--label-time", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("--label-timestamps", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("+label-time", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseTreeFilterMode(string value, out CodingAgentTreeFilterMode filterMode)
    {
        filterMode = value.ToLowerInvariant() switch
        {
            "default" => CodingAgentTreeFilterMode.Default,
            "no-tools" or "notools" => CodingAgentTreeFilterMode.NoTools,
            "user" or "user-only" => CodingAgentTreeFilterMode.UserOnly,
            "labeled" or "labels" or "labeled-only" => CodingAgentTreeFilterMode.LabeledOnly,
            "all" => CodingAgentTreeFilterMode.All,
            _ => (CodingAgentTreeFilterMode)(-1)
        };
        return filterMode != (CodingAgentTreeFilterMode)(-1);
    }

    private string FormatTokenBudget(CodingAgentSessionStats stats)
    {
        var estimate = Math.Max(0, stats.EstimatedTokens);
        var context = stats.ContextWindowTokens.GetValueOrDefault();
        var contextBudget = context > 0
            ? $"~{estimate}/{context} context ({Math.Max(0, context - estimate)} remaining)"
            : $"~{estimate}";

        if (!_autoCompaction.IsEnabled)
        {
            return contextBudget;
        }

        var threshold = _autoCompaction.ThresholdTokens;
        return $"{contextBudget}, auto-compact {threshold} ({Math.Max(0, threshold - estimate)} remaining)";
    }

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
