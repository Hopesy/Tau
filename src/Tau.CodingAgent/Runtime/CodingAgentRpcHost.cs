using System.Text.Json;
using System.Text.Json.Serialization;
using Tau.Agent;
using Tau.Agent.Runtime;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

public sealed class CodingAgentRpcHost
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ICodingAgentRunner _runner;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly CodingAgentSessionStore? _sessionStore;
    private readonly CodingAgentSettingsStore? _settingsStore;
    private readonly CodingAgentTreeSessionController? _treeSessionController;
    private readonly ICodingAgentShellRunner _shellRunner;
    private readonly CodingAgentAutoCompactionOptions _autoCompaction;
    private CodingAgentRetryOptions _retryOptions;
    private bool? _autoCompactionEnabled;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _gate = new();
    private Task? _activePromptTask;
    private CancellationTokenSource? _activePromptCts;
    private CancellationTokenSource? _activeRetryDelayCts;
    private Task? _activeBashTask;
    private CancellationTokenSource? _activeBashCts;

    public CodingAgentRpcHost(
        ICodingAgentRunner runner,
        TextReader input,
        TextWriter output,
        CodingAgentSessionStore? sessionStore = null,
        CodingAgentSettingsStore? settingsStore = null,
        CodingAgentTreeSessionController? treeSessionController = null,
        CodingAgentAutoCompactionOptions? autoCompaction = null,
        CodingAgentRetryOptions? retryOptions = null,
        ICodingAgentShellRunner? shellRunner = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        _runner = runner;
        _input = input;
        _output = output;
        _sessionStore = sessionStore;
        _settingsStore = settingsStore;
        _treeSessionController = treeSessionController;
        _shellRunner = shellRunner ?? new SystemCodingAgentShellRunner();
        _autoCompaction = autoCompaction ?? CodingAgentAutoCompactionOptions.Disabled;
        _retryOptions = retryOptions ?? CodingAgentRetryOptions.Disabled;
        _autoCompactionEnabled = settingsStore?.Load().AutoCompactionEnabled;
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await _input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (line.EndsWith('\r'))
            {
                line = line[..^1];
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            await HandleLineAsync(line, cancellationToken).ConfigureAwait(false);
        }

        var active = GetActivePromptTask();
        if (active is not null)
        {
            await active.ConfigureAwait(false);
        }

        var activeBash = GetActiveBashTask();
        if (activeBash is not null)
        {
            await activeBash.ConfigureAwait(false);
        }

        return 0;
    }

    private async Task HandleLineAsync(string line, CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                await WriteErrorAsync(null, "unknown", "RPC command must be a JSON object.", cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var id = GetOptionalString(root, "id");
            var type = GetOptionalString(root, "type");
            if (string.IsNullOrWhiteSpace(type))
            {
                await WriteErrorAsync(id, "unknown", "RPC command is missing a type.", cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            await HandleCommandAsync(id, type, root, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            await WriteErrorAsync(null, "unknown", $"Invalid JSON: {ex.Message}", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task HandleCommandAsync(
        string? id,
        string type,
        JsonElement command,
        CancellationToken cancellationToken)
    {
        try
        {
            switch (type)
            {
                case "prompt":
                    await HandlePromptAsync(id, command, cancellationToken).ConfigureAwait(false);
                    break;
                case "steer":
                    await HandleSteerAsync(id, command, cancellationToken).ConfigureAwait(false);
                    break;
                case "follow_up":
                    await HandleFollowUpAsync(id, command, cancellationToken).ConfigureAwait(false);
                    break;
                case "abort":
                    AbortActivePrompt();
                    await WriteSuccessAsync(id, "abort", cancellationToken: cancellationToken).ConfigureAwait(false);
                    break;
                case "new_session":
                    await HandleNewSessionAsync(id, command, cancellationToken).ConfigureAwait(false);
                    break;
                case "get_state":
                    await WriteSuccessAsync(id, "get_state", CreateState(), cancellationToken).ConfigureAwait(false);
                    break;
                case "get_settings":
                    await HandleGetSettingsAsync(id, cancellationToken).ConfigureAwait(false);
                    break;
                case "update_settings":
                    await HandleUpdateSettingsAsync(id, command, cancellationToken).ConfigureAwait(false);
                    break;
                case "set_model":
                    await HandleSetModelAsync(id, command, cancellationToken).ConfigureAwait(false);
                    break;
                case "cycle_model":
                    await HandleCycleModelAsync(id, cancellationToken).ConfigureAwait(false);
                    break;
                case "get_available_models":
                    await WriteSuccessAsync(id, "get_available_models", new
                    {
                        models = _runner.GetProviders()
                            .SelectMany(provider => _runner.GetModels(provider))
                            .ToArray()
                    }, cancellationToken).ConfigureAwait(false);
                    break;
                case "set_thinking_level":
                    await HandleSetThinkingLevelAsync(id, command, cancellationToken).ConfigureAwait(false);
                    break;
                case "cycle_thinking_level":
                    await HandleCycleThinkingLevelAsync(id, cancellationToken).ConfigureAwait(false);
                    break;
                case "set_auto_retry":
                    await HandleSetAutoRetryAsync(id, command, cancellationToken).ConfigureAwait(false);
                    break;
                case "set_steering_mode":
                    await HandleSetSteeringModeAsync(id, command, cancellationToken).ConfigureAwait(false);
                    break;
                case "set_follow_up_mode":
                    await HandleSetFollowUpModeAsync(id, command, cancellationToken).ConfigureAwait(false);
                    break;
                case "set_auto_compaction":
                    await HandleSetAutoCompactionAsync(id, command, cancellationToken).ConfigureAwait(false);
                    break;
                case "abort_retry":
                    AbortActiveRetry();
                    await WriteSuccessAsync(id, "abort_retry", cancellationToken: cancellationToken).ConfigureAwait(false);
                    break;
                case "bash":
                    await HandleBashAsync(id, command, cancellationToken).ConfigureAwait(false);
                    break;
                case "abort_bash":
                    AbortActiveBash();
                    await WriteSuccessAsync(id, "abort_bash", cancellationToken: cancellationToken).ConfigureAwait(false);
                    break;
                case "export_html":
                    await HandleExportHtmlAsync(id, command, cancellationToken).ConfigureAwait(false);
                    break;
                case "switch_session":
                    await HandleSwitchSessionAsync(id, command, cancellationToken).ConfigureAwait(false);
                    break;
                case "get_last_assistant_text":
                    await WriteSuccessAsync(
                        id,
                        "get_last_assistant_text",
                        new Dictionary<string, string?> { ["text"] = GetLastAssistantText() },
                        cancellationToken).ConfigureAwait(false);
                    break;
                case "get_fork_messages":
                    await HandleGetForkMessagesAsync(id, cancellationToken).ConfigureAwait(false);
                    break;
                case "set_session_name":
                    await HandleSetSessionNameAsync(id, command, cancellationToken).ConfigureAwait(false);
                    break;
                case "compact":
                    await HandleCompactAsync(id, command, cancellationToken).ConfigureAwait(false);
                    break;
                case "fork":
                    await HandleForkAsync(id, command, cancellationToken).ConfigureAwait(false);
                    break;
                case "clone":
                    await HandleCloneAsync(id, cancellationToken).ConfigureAwait(false);
                    break;
                case "get_session_stats":
                    await WriteSuccessAsync(id, "get_session_stats", _runner.GetSessionStats(_sessionStore?.Path), cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case "get_messages":
                    await WriteSuccessAsync(id, "get_messages", new
                    {
                        messages = _runner.Messages.Select(CodingAgentSessionStore.FromMessage).ToArray()
                    }, cancellationToken).ConfigureAwait(false);
                    break;
                case "get_commands":
                    await WriteSuccessAsync(id, "get_commands", new
                    {
                        commands = CodingAgentCommandCatalog.SupportedCommands
                            .Select(commandInfo => new RpcCommandInfo(
                                commandInfo.Name.TrimStart('/'),
                                commandInfo.Usage,
                                commandInfo.Description,
                                "local"))
                            .ToArray()
                    }, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    await WriteErrorAsync(id, type, $"Unsupported RPC command '{type}'.", cancellationToken)
                        .ConfigureAwait(false);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException)
        {
            await WriteErrorAsync(id, type, ex.Message, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandlePromptAsync(string? id, JsonElement command, CancellationToken cancellationToken)
    {
        var message = GetRequiredString(command, "message");
        var streamingBehavior = GetOptionalString(command, "streamingBehavior");
        if (IsPromptActive)
        {
            if (streamingBehavior is not null &&
                streamingBehavior.Equals("steer", StringComparison.OrdinalIgnoreCase))
            {
                _runner.Steer(message);
                await WriteSuccessAsync(id, "prompt", cancellationToken: cancellationToken).ConfigureAwait(false);
                return;
            }

            if (streamingBehavior is not null &&
                (streamingBehavior.Equals("followUp", StringComparison.OrdinalIgnoreCase) ||
                 streamingBehavior.Equals("follow_up", StringComparison.OrdinalIgnoreCase)))
            {
                _runner.FollowUp(message);
                await WriteSuccessAsync(id, "prompt", cancellationToken: cancellationToken).ConfigureAwait(false);
                return;
            }

            await WriteErrorAsync(id, "prompt", "Agent is already running; use steer, follow_up, or prompt.streamingBehavior.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var content = TryReadPromptContent(command, message);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_gate)
        {
            _activePromptCts = cts;
        }

        await WriteSuccessAsync(id, "prompt", cancellationToken: cancellationToken).ConfigureAwait(false);

        var task = Task.Run(() => RunPromptAsync(message, content, cts), CancellationToken.None);
        lock (_gate)
        {
            _activePromptTask = task;
        }
    }

    private async Task HandleSteerAsync(string? id, JsonElement command, CancellationToken cancellationToken)
    {
        if (!IsPromptActive)
        {
            await WriteErrorAsync(id, "steer", "Agent is not running.", cancellationToken).ConfigureAwait(false);
            return;
        }

        _runner.Steer(GetRequiredString(command, "message"));
        await WriteSuccessAsync(id, "steer", cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleFollowUpAsync(string? id, JsonElement command, CancellationToken cancellationToken)
    {
        if (!IsPromptActive)
        {
            await WriteErrorAsync(id, "follow_up", "Agent is not running.", cancellationToken).ConfigureAwait(false);
            return;
        }

        _runner.FollowUp(GetRequiredString(command, "message"));
        await WriteSuccessAsync(id, "follow_up", cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleNewSessionAsync(string? id, JsonElement command, CancellationToken cancellationToken)
    {
        if (IsPromptActive)
        {
            await WriteErrorAsync(id, "new_session", "Cannot start a new session while the agent is running.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        _runner.ResetSession();
        _treeSessionController?.StartNewFromRunner(_runner, GetOptionalString(command, "parentSession"));
        PersistSession();
        await WriteSuccessAsync(id, "new_session", new { cancelled = false }, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleSetModelAsync(string? id, JsonElement command, CancellationToken cancellationToken)
    {
        if (IsPromptActive)
        {
            await WriteErrorAsync(id, "set_model", "Cannot change model while the agent is running.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var provider = GetRequiredString(command, "provider");
        var modelId = GetRequiredString(command, "modelId");
        var model = _runner.SelectModel(provider, modelId);
        _settingsStore?.SaveDefaultModel(model);
        _treeSessionController?.SyncFromRunner(_runner);
        PersistSession();
        await WriteSuccessAsync(id, "set_model", model, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleGetSettingsAsync(string? id, CancellationToken cancellationToken)
    {
        if (_settingsStore is null)
        {
            await WriteErrorAsync(id, "get_settings", "Settings are not enabled.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await WriteSuccessAsync(id, "get_settings", CreateSettingsData(_settingsStore.Load()), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task HandleUpdateSettingsAsync(string? id, JsonElement command, CancellationToken cancellationToken)
    {
        if (IsPromptActive)
        {
            await WriteErrorAsync(id, "update_settings", "Cannot update settings while the agent is running.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (_settingsStore is null)
        {
            await WriteErrorAsync(id, "update_settings", "Settings are not enabled.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!command.TryGetProperty("settings", out var settingsElement) ||
            settingsElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("RPC command requires object 'settings'.");
        }

        var current = _settingsStore.Load();
        var updated = BuildUpdatedSettings(current, settingsElement);
        Model? selectedModel = null;
        if (!string.IsNullOrWhiteSpace(updated.DefaultProvider) &&
            !string.IsNullOrWhiteSpace(updated.DefaultModel))
        {
            selectedModel = _runner.SelectModel(updated.DefaultProvider, updated.DefaultModel);
            updated = updated with
            {
                DefaultProvider = selectedModel.Provider,
                DefaultModel = selectedModel.Id
            };
        }

        _settingsStore.Save(updated);

        if (selectedModel is not null)
        {
            _treeSessionController?.SyncFromRunner(_runner);
            PersistSession();
        }

        _runner.ThinkingLevel = string.IsNullOrWhiteSpace(updated.DefaultThinkingLevel)
            ? null
            : ParseThinkingLevelOrNull(updated.DefaultThinkingLevel);
        _runner.SteeringMode = CodingAgentQueueModes.ToAgentQueueMode(updated.SteeringMode);
        _runner.FollowUpMode = CodingAgentQueueModes.ToAgentQueueMode(updated.FollowUpMode);
        SetRetryOptions(CodingAgentRetryOptions.FromSettingsOrEnvironment(updated));
        _autoCompactionEnabled = updated.AutoCompactionEnabled;

        await WriteSuccessAsync(id, "update_settings", CreateSettingsData(updated), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task HandleCycleModelAsync(string? id, CancellationToken cancellationToken)
    {
        if (IsPromptActive)
        {
            await WriteErrorAsync(id, "cycle_model", "Cannot change model while the agent is running.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var candidates = GetModelCycleCandidates(out var isScoped);
        if (candidates.Count <= 1)
        {
            await WriteExplicitDataSuccessAsync(id, "cycle_model", null, cancellationToken).ConfigureAwait(false);
            return;
        }

        var currentIndex = candidates.FindIndex(model => SameModel(model, _runner.Model));
        var nextIndex = currentIndex < 0 ? 0 : (currentIndex + 1) % candidates.Count;
        var next = candidates[nextIndex];
        var selected = _runner.SelectModel(next.Provider, next.Id);
        _settingsStore?.SaveDefaultModel(selected);
        _treeSessionController?.SyncFromRunner(_runner);
        PersistSession();
        await WriteSuccessAsync(
            id,
            "cycle_model",
            new
            {
                model = selected,
                thinkingLevel = FormatThinkingLevel(_runner.ThinkingLevel),
                isScoped
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleSetThinkingLevelAsync(string? id, JsonElement command, CancellationToken cancellationToken)
    {
        if (IsPromptActive)
        {
            await WriteErrorAsync(id, "set_thinking_level", "Cannot change thinking level while the agent is running.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var rawLevel = GetRequiredString(command, "level").Trim();
        var level = ParseThinkingLevelOrNull(rawLevel);
        if (level is null &&
            !rawLevel.Equals("off", StringComparison.OrdinalIgnoreCase) &&
            !rawLevel.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Unsupported thinking level '{rawLevel}'. Expected off, minimal, low, medium, high, or xhigh.");
        }

        _runner.ThinkingLevel = level;
        SaveThinkingLevel(level);
        await WriteSuccessAsync(id, "set_thinking_level", cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleCycleThinkingLevelAsync(string? id, CancellationToken cancellationToken)
    {
        if (IsPromptActive)
        {
            await WriteErrorAsync(id, "cycle_thinking_level", "Cannot change thinking level while the agent is running.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        _runner.ThinkingLevel = CycleThinkingLevel(_runner.ThinkingLevel);
        SaveThinkingLevel(_runner.ThinkingLevel);
        var level = FormatThinkingLevelRaw(_runner.ThinkingLevel);
        await WriteExplicitDataSuccessAsync(
            id,
            "cycle_thinking_level",
            level is null ? null : new { level },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleSetAutoRetryAsync(string? id, JsonElement command, CancellationToken cancellationToken)
    {
        var enabled = GetRequiredBoolean(command, "enabled");
        var current = _settingsStore?.Load();
        var options = enabled ? ResolveEnabledRetryOptions(current) : CodingAgentRetryOptions.Disabled;
        SetRetryOptions(options);
        SaveRetryOptions(current, options, enabled);
        await WriteSuccessAsync(id, "set_auto_retry", cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleSetSteeringModeAsync(string? id, JsonElement command, CancellationToken cancellationToken)
    {
        if (IsPromptActive)
        {
            await WriteErrorAsync(id, "set_steering_mode", "Cannot change steering mode while the agent is running.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var mode = ReadQueueMode(command);
        _runner.SteeringMode = CodingAgentQueueModes.ToAgentQueueMode(mode);
        SaveQueueModes(steeringMode: mode, followUpMode: null);
        await WriteSuccessAsync(id, "set_steering_mode", cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleSetFollowUpModeAsync(string? id, JsonElement command, CancellationToken cancellationToken)
    {
        if (IsPromptActive)
        {
            await WriteErrorAsync(id, "set_follow_up_mode", "Cannot change follow-up mode while the agent is running.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var mode = ReadQueueMode(command);
        _runner.FollowUpMode = CodingAgentQueueModes.ToAgentQueueMode(mode);
        SaveQueueModes(steeringMode: null, followUpMode: mode);
        await WriteSuccessAsync(id, "set_follow_up_mode", cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleSetAutoCompactionAsync(string? id, JsonElement command, CancellationToken cancellationToken)
    {
        if (IsPromptActive)
        {
            await WriteErrorAsync(id, "set_auto_compaction", "Cannot change auto-compaction while the agent is running.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var enabled = GetRequiredBoolean(command, "enabled");
        _autoCompactionEnabled = enabled;
        SaveAutoCompactionEnabled(enabled);
        await WriteSuccessAsync(id, "set_auto_compaction", cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleBashAsync(string? id, JsonElement command, CancellationToken cancellationToken)
    {
        if (IsBashActive)
        {
            await WriteErrorAsync(id, "bash", "A bash command is already running.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var shellCommand = GetRequiredString(command, "command");
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var accepted = false;
        lock (_gate)
        {
            if (_activeBashCts is not null)
            {
                cts.Dispose();
            }
            else
            {
                _activeBashCts = cts;
                _activeBashTask = Task.Run(() => RunBashAsync(id, shellCommand, cts), CancellationToken.None);
                accepted = true;
            }
        }

        if (!accepted)
        {
            await WriteErrorAsync(id, "bash", "A bash command is already running.", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task HandleExportHtmlAsync(string? id, JsonElement command, CancellationToken cancellationToken)
    {
        if (IsPromptActive)
        {
            await WriteErrorAsync(id, "export_html", "Cannot export HTML while the agent is running.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var outputPath = GetOptionalString(command, "outputPath");
        var exportPath = string.IsNullOrWhiteSpace(outputPath)
            ? GetDefaultHtmlExportPath()
            : outputPath.Trim();
        var htmlPath = ExportHtmlTranscript(exportPath);
        await WriteSuccessAsync(id, "export_html", new { path = htmlPath }, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleSwitchSessionAsync(string? id, JsonElement command, CancellationToken cancellationToken)
    {
        if (IsPromptActive)
        {
            await WriteErrorAsync(id, "switch_session", "Cannot switch session while the agent is running.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (_treeSessionController is null)
        {
            await WriteErrorAsync(id, "switch_session", "Tree sessions are not enabled.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var sessionPath = GetRequiredString(command, "sessionPath");
        var snapshot = _treeSessionController.Resume(sessionPath);
        _runner.RestoreSession(snapshot.ToFlatSnapshot());
        PersistSession();
        await WriteSuccessAsync(id, "switch_session", new { cancelled = false }, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleGetForkMessagesAsync(string? id, CancellationToken cancellationToken)
    {
        if (_treeSessionController is null)
        {
            await WriteErrorAsync(id, "get_fork_messages", "Tree sessions are not enabled.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        _treeSessionController.SyncFromRunner(_runner);
        await WriteSuccessAsync(id, "get_fork_messages", new
        {
            messages = _treeSessionController.GetUserMessagesForForking()
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleSetSessionNameAsync(string? id, JsonElement command, CancellationToken cancellationToken)
    {
        if (IsPromptActive)
        {
            await WriteErrorAsync(id, "set_session_name", "Cannot set session name while the agent is running.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        _runner.SessionName = GetRequiredString(command, "name").Trim();
        PersistSession();
        await WriteSuccessAsync(id, "set_session_name", cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleCompactAsync(string? id, JsonElement command, CancellationToken cancellationToken)
    {
        if (IsPromptActive)
        {
            await WriteErrorAsync(id, "compact", "Cannot compact while the agent is running.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        _treeSessionController?.SyncFromRunner(_runner);
        var result = await _runner
            .CompactAsync(GetOptionalString(command, "customInstructions"), cancellationToken)
            .ConfigureAwait(false);
        _treeSessionController?.RecordCompaction(_runner, result);
        PersistSession();
        await WriteSuccessAsync(id, "compact", result, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleForkAsync(string? id, JsonElement command, CancellationToken cancellationToken)
    {
        if (IsPromptActive)
        {
            await WriteErrorAsync(id, "fork", "Cannot fork while the agent is running.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (_treeSessionController is null)
        {
            await WriteErrorAsync(id, "fork", "Tree sessions are not enabled.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var entryId = GetRequiredString(command, "entryId");
        _treeSessionController.SyncFromRunner(_runner);
        var snapshot = _treeSessionController.Branch(entryId);
        _runner.RestoreSession(snapshot.ToFlatSnapshot());
        PersistSession();
        await WriteSuccessAsync(id, "fork", new
        {
            cancelled = false,
            leafId = snapshot.LeafId,
            messageCount = snapshot.Messages.Count,
            provider = _runner.Model.Provider,
            model = _runner.Model.Id
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleCloneAsync(string? id, CancellationToken cancellationToken)
    {
        if (IsPromptActive)
        {
            await WriteErrorAsync(id, "clone", "Cannot clone while the agent is running.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (_treeSessionController is null)
        {
            await WriteErrorAsync(id, "clone", "Tree sessions are not enabled.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        _treeSessionController.SyncFromRunner(_runner);
        var snapshot = _treeSessionController.CloneCurrentBranch();
        if (snapshot is null)
        {
            await WriteErrorAsync(id, "clone", "Nothing to clone yet.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        _runner.RestoreSession(snapshot.ToFlatSnapshot());
        PersistSession();
        await WriteSuccessAsync(id, "clone", new
        {
            cancelled = false,
            sessionFile = snapshot.FilePath,
            leafId = snapshot.LeafId,
            messageCount = snapshot.Messages.Count,
            provider = _runner.Model.Provider,
            model = _runner.Model.Id
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task RunPromptAsync(
        string message,
        IReadOnlyList<ContentBlock>? content,
        CancellationTokenSource cts)
    {
        var rollbackSnapshot = CreateRollbackSnapshot();
        var retryAttempt = 0;

        try
        {
            while (true)
            {
                var result = await RunSinglePromptAttemptAsync(message, content, cts.Token).ConfigureAwait(false);
                if (result.IsSuccess)
                {
                    if (retryAttempt > 0)
                    {
                        _treeSessionController?.SyncFromRunner(_runner);
                        await RecordAutoRetryEndAsync(success: true, retryAttempt, null, cts.Token)
                            .ConfigureAwait(false);
                    }

                    return;
                }

                var retryOptions = GetRetryOptions();
                var isRetryable = retryOptions.IsEnabled &&
                    CodingAgentRetryClassifier.IsRetryable(result.ErrorMessage, _runner.Model.ContextWindow ?? 0);
                if (!isRetryable || retryAttempt >= retryOptions.MaxAttempts)
                {
                    if (retryOptions.IsEnabled && retryAttempt > 0)
                    {
                        await RecordAutoRetryEndAsync(success: false, retryAttempt, result.ErrorMessage, cts.Token)
                            .ConfigureAwait(false);
                    }

                    RestorePromptSnapshot(rollbackSnapshot);
                    return;
                }

                retryAttempt++;
                RestorePromptSnapshot(rollbackSnapshot);

                var delay = retryOptions.GetDelay(retryAttempt);
                var delayMs = (int)delay.TotalMilliseconds;
                CancellationTokenSource? retryDelayCts = null;
                try
                {
                    if (delay > TimeSpan.Zero)
                    {
                        retryDelayCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                        SetActiveRetryDelay(retryDelayCts);
                    }

                    await RecordAutoRetryStartAsync(
                        retryAttempt,
                        retryOptions.MaxAttempts,
                        delayMs,
                        result.ErrorMessage,
                        cts.Token).ConfigureAwait(false);

                    var shouldContinue = await DelayBeforeRetryAsync(delay, cts.Token, retryDelayCts)
                        .ConfigureAwait(false);
                    if (shouldContinue)
                    {
                        continue;
                    }

                    await RecordAutoRetryEndAsync(success: false, retryAttempt, "Retry cancelled", CancellationToken.None)
                        .ConfigureAwait(false);
                    RestorePromptSnapshot(rollbackSnapshot);
                    return;
                }
                finally
                {
                    ClearActiveRetryDelay(retryDelayCts);
                    retryDelayCts?.Dispose();
                }
            }
        }
        catch (OperationCanceledException)
        {
            await WriteJsonLineAsync(new { type = "agent_end", errorMessage = "Cancelled" }, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await WriteJsonLineAsync(new { type = "agent_end", errorMessage = ex.Message }, CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            try
            {
                PersistSession();
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_activePromptCts, cts))
                    {
                        _activePromptCts = null;
                        _activePromptTask = null;
                    }
                }

                cts.Dispose();
            }
        }
    }

    private async Task RunBashAsync(
        string? id,
        string command,
        CancellationTokenSource cts)
    {
        try
        {
            var result = await _shellRunner.ExecuteAsync(command, cts.Token).ConfigureAwait(false);
            await WriteSuccessAsync(id, "bash", result, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await WriteSuccessAsync(
                id,
                "bash",
                new CodingAgentShellResult(string.Empty, null, Cancelled: true, Truncated: false),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(id, "bash", ex.Message, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_activeBashCts, cts))
                {
                    _activeBashCts = null;
                    _activeBashTask = null;
                }
            }

            cts.Dispose();
        }
    }

    private async Task<RpcPromptAttemptResult> RunSinglePromptAttemptAsync(
        string message,
        IReadOnlyList<ContentBlock>? content,
        CancellationToken cancellationToken)
    {
        try
        {
            var events = content is null
                ? _runner.RunAsync(message, cancellationToken)
                : _runner.RunAsync(content, cancellationToken);

            await foreach (var evt in events.ConfigureAwait(false))
            {
                await WriteJsonLineAsync(ToRpcEvent(evt), cancellationToken).ConfigureAwait(false);
                if (evt is AgentEndEvent { ErrorMessage: not null } end)
                {
                    return RpcPromptAttemptResult.Failed(end.ErrorMessage);
                }
            }

            return RpcPromptAttemptResult.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await WriteJsonLineAsync(new { type = "agent_end", errorMessage = ex.Message }, CancellationToken.None)
                .ConfigureAwait(false);
            return RpcPromptAttemptResult.Failed(ex.Message);
        }
    }

    private async Task<bool> DelayBeforeRetryAsync(
        TimeSpan delay,
        CancellationToken cancellationToken,
        CancellationTokenSource? retryDelayCts)
    {
        if (delay <= TimeSpan.Zero)
        {
            return true;
        }

        try
        {
            await Task.Delay(delay, retryDelayCts?.Token ?? cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private object CreateState()
    {
        CodingAgentTreeSessionSummary? treeSummary = null;
        if (_treeSessionController is not null)
        {
            try
            {
                treeSummary = _treeSessionController.GetSummary();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                treeSummary = null;
            }
        }

        return new
        {
            model = _runner.Model,
            thinkingLevel = FormatThinkingLevel(_runner.ThinkingLevel),
            isStreaming = IsPromptActive,
            isCompacting = false,
            steeringMode = CodingAgentQueueModes.FromAgentQueueMode(_runner.SteeringMode),
            followUpMode = CodingAgentQueueModes.FromAgentQueueMode(_runner.FollowUpMode),
            sessionFile = treeSummary?.FilePath ?? _sessionStore?.Path,
            sessionId = treeSummary?.SessionId,
            sessionName = _runner.SessionName,
            autoCompactionEnabled = GetAutoCompactionEnabled(),
            autoRetryEnabled = GetRetryOptions().IsEnabled,
            messageCount = _runner.Messages.Count,
            pendingMessageCount = 0
        };
    }

    private object CreateSettingsData(CodingAgentSettingsSnapshot settings)
    {
        var retryOptions = CodingAgentRetryOptions.FromSettingsOrEnvironment(settings);
        return new
        {
            path = _settingsStore?.Path,
            defaultProvider = settings.DefaultProvider,
            defaultModel = settings.DefaultModel,
            treeFilterMode = NormalizeTreeFilterMode(settings.TreeFilterMode) ?? "default",
            retry = new
            {
                enabled = retryOptions.IsEnabled,
                maxAttempts = retryOptions.MaxAttempts,
                baseDelayMilliseconds = retryOptions.BaseDelayMilliseconds
            },
            defaultThinkingLevel = NormalizeThinkingLevelOrNull(settings.DefaultThinkingLevel) ?? "off",
            enabledModels = settings.EnabledModels?.ToArray(),
            steeringMode = CodingAgentQueueModes.NormalizeOrDefault(settings.SteeringMode),
            followUpMode = CodingAgentQueueModes.NormalizeOrDefault(settings.FollowUpMode),
            autoCompactionEnabled = settings.AutoCompactionEnabled ?? _autoCompaction.IsEnabled,
            theme = string.IsNullOrWhiteSpace(settings.Theme)
                ? CodingAgentThemeStore.DefaultThemeName
                : settings.Theme
        };
    }

    private CodingAgentSettingsSnapshot BuildUpdatedSettings(
        CodingAgentSettingsSnapshot current,
        JsonElement settingsElement)
    {
        var updated = current;
        if (settingsElement.TryGetProperty("defaultProvider", out var defaultProvider))
        {
            updated = updated with { DefaultProvider = ReadNullableString(defaultProvider) };
        }

        if (settingsElement.TryGetProperty("defaultModel", out var defaultModel))
        {
            updated = updated with { DefaultModel = ReadNullableString(defaultModel) };
        }

        if (settingsElement.TryGetProperty("model", out var modelElement))
        {
            if (modelElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("settings.model must be an object.");
            }

            updated = updated with
            {
                DefaultProvider = GetRequiredString(modelElement, "provider"),
                DefaultModel = GetRequiredString(modelElement, "modelId")
            };
        }

        if (settingsElement.TryGetProperty("treeFilterMode", out var treeFilterMode))
        {
            var normalized = NormalizeTreeFilterMode(ReadNullableString(treeFilterMode));
            updated = updated with { TreeFilterMode = normalized };
        }

        if (settingsElement.TryGetProperty("retry", out var retryElement))
        {
            updated = ApplyRetrySettings(updated, retryElement);
        }

        if (settingsElement.TryGetProperty("defaultThinkingLevel", out var defaultThinkingLevel))
        {
            var normalized = NormalizeThinkingLevelOrNull(ReadNullableString(defaultThinkingLevel));
            updated = updated with { DefaultThinkingLevel = normalized };
        }

        if (settingsElement.TryGetProperty("enabledModels", out var enabledModels))
        {
            updated = updated with { EnabledModels = ReadEnabledModels(enabledModels) };
        }

        if (settingsElement.TryGetProperty("steeringMode", out var steeringMode))
        {
            var mode = NormalizeQueueModeOrThrow(ReadNullableString(steeringMode));
            updated = updated with { SteeringMode = mode };
        }

        if (settingsElement.TryGetProperty("followUpMode", out var followUpMode))
        {
            var mode = NormalizeQueueModeOrThrow(ReadNullableString(followUpMode));
            updated = updated with { FollowUpMode = mode };
        }

        if (settingsElement.TryGetProperty("autoCompactionEnabled", out var autoCompactionEnabled))
        {
            updated = updated with { AutoCompactionEnabled = ReadNullableBoolean(autoCompactionEnabled) };
        }

        if (settingsElement.TryGetProperty("theme", out var theme))
        {
            updated = updated with { Theme = ReadNullableString(theme) };
        }

        return updated;
    }

    private static CodingAgentSettingsSnapshot ApplyRetrySettings(
        CodingAgentSettingsSnapshot current,
        JsonElement retryElement)
    {
        if (retryElement.ValueKind == JsonValueKind.Null)
        {
            return current with
            {
                RetryMaxAttempts = null,
                RetryBaseDelayMilliseconds = null
            };
        }

        if (retryElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("settings.retry must be an object or null.");
        }

        var retryMaxAttempts = current.RetryMaxAttempts;
        var retryBaseDelayMilliseconds = current.RetryBaseDelayMilliseconds;
        if (retryElement.TryGetProperty("enabled", out var enabled))
        {
            var isEnabled = ReadRequiredBoolean(enabled, "settings.retry.enabled");
            if (!isEnabled)
            {
                retryMaxAttempts = 0;
                retryBaseDelayMilliseconds = 0;
            }
            else if (retryMaxAttempts is null or <= 0)
            {
                retryMaxAttempts = CodingAgentRetryOptions.Default.MaxAttempts;
                retryBaseDelayMilliseconds ??= CodingAgentRetryOptions.Default.BaseDelayMilliseconds;
            }
        }

        if (retryElement.TryGetProperty("maxAttempts", out var maxAttempts))
        {
            retryMaxAttempts = ReadNullableNonNegativeInt(maxAttempts, "settings.retry.maxAttempts");
        }

        if (retryElement.TryGetProperty("baseDelayMilliseconds", out var baseDelayMilliseconds))
        {
            retryBaseDelayMilliseconds = ReadNullableNonNegativeInt(
                baseDelayMilliseconds,
                "settings.retry.baseDelayMilliseconds");
        }

        if (retryElement.TryGetProperty("baseDelayMs", out var baseDelayMs))
        {
            retryBaseDelayMilliseconds = ReadNullableNonNegativeInt(baseDelayMs, "settings.retry.baseDelayMs");
        }

        return current with
        {
            RetryMaxAttempts = retryMaxAttempts,
            RetryBaseDelayMilliseconds = retryBaseDelayMilliseconds
        };
    }

    private List<Model> GetModelCycleCandidates(out bool isScoped)
    {
        var availableModels = GetAvailableModels();
        var enabledModels = _settingsStore?.Load().EnabledModels;
        if (enabledModels is null || enabledModels.Count == 0)
        {
            isScoped = false;
            return availableModels;
        }

        var candidates = new List<Model>();
        foreach (var enabledModel in enabledModels)
        {
            if (TryResolveModelReference(enabledModel, availableModels, out var model) &&
                !candidates.Any(candidate => SameModel(candidate, model)))
            {
                candidates.Add(model);
            }
        }

        if (candidates.Count == 0)
        {
            isScoped = false;
            return availableModels;
        }

        isScoped = true;
        return candidates;
    }

    private List<Model> GetAvailableModels()
    {
        var models = new List<Model>();
        foreach (var provider in _runner.GetProviders())
        {
            models.AddRange(_runner.GetModels(provider));
        }

        return models;
    }

    private static bool TryResolveModelReference(string reference, IReadOnlyList<Model> availableModels, out Model model)
    {
        model = null!;
        var trimmed = reference.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return false;
        }

        if (trimmed.Contains('/', StringComparison.Ordinal))
        {
            var parts = trimmed.Split('/', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
            {
                return false;
            }

            var match = availableModels.FirstOrDefault(candidate =>
                candidate.Provider.Equals(parts[0], StringComparison.OrdinalIgnoreCase) &&
                candidate.Id.Equals(parts[1], StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                return false;
            }

            model = match;
            return true;
        }

        var matches = availableModels
            .Where(candidate => candidate.Id.Equals(trimmed, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length != 1)
        {
            return false;
        }

        model = matches[0];
        return true;
    }

    private static bool SameModel(Model left, Model right) =>
        left.Provider.Equals(right.Provider, StringComparison.OrdinalIgnoreCase) &&
        left.Id.Equals(right.Id, StringComparison.OrdinalIgnoreCase);

    private void AbortActivePrompt()
    {
        lock (_gate)
        {
            _activePromptCts?.Cancel();
        }
    }

    private void AbortActiveRetry()
    {
        lock (_gate)
        {
            _activeRetryDelayCts?.Cancel();
        }
    }

    private void AbortActiveBash()
    {
        lock (_gate)
        {
            _activeBashCts?.Cancel();
        }

        _shellRunner.Abort();
    }

    private void SetActiveRetryDelay(CancellationTokenSource retryDelayCts)
    {
        lock (_gate)
        {
            _activeRetryDelayCts = retryDelayCts;
        }
    }

    private void ClearActiveRetryDelay(CancellationTokenSource? retryDelayCts)
    {
        if (retryDelayCts is null)
        {
            return;
        }

        lock (_gate)
        {
            if (ReferenceEquals(_activeRetryDelayCts, retryDelayCts))
            {
                _activeRetryDelayCts = null;
            }
        }
    }

    private Task? GetActivePromptTask()
    {
        lock (_gate)
        {
            return _activePromptTask;
        }
    }

    private Task? GetActiveBashTask()
    {
        lock (_gate)
        {
            return _activeBashTask;
        }
    }

    private bool IsPromptActive
    {
        get
        {
            lock (_gate)
            {
                return _activePromptCts is not null;
            }
        }
    }

    private bool IsBashActive
    {
        get
        {
            lock (_gate)
            {
                return _activeBashCts is not null;
            }
        }
    }

    private void PersistSession()
    {
        _sessionStore?.Save(_runner.Messages, _runner.Model, _runner.SessionName);
        _treeSessionController?.SyncFromRunner(_runner);
    }

    private CodingAgentSessionSnapshot CreateRollbackSnapshot() =>
        new([.. _runner.Messages], _runner.Model.Provider, _runner.Model.Id, _runner.SessionName);

    private void RestorePromptSnapshot(CodingAgentSessionSnapshot snapshot)
    {
        _runner.RestoreSession(snapshot);
    }

    private CodingAgentRetryOptions GetRetryOptions()
    {
        lock (_gate)
        {
            return _retryOptions;
        }
    }

    private void SetRetryOptions(CodingAgentRetryOptions options)
    {
        lock (_gate)
        {
            _retryOptions = options;
        }
    }

    private static CodingAgentRetryOptions ResolveEnabledRetryOptions(CodingAgentSettingsSnapshot? settings)
    {
        if (settings?.RetryMaxAttempts is > 0)
        {
            return new CodingAgentRetryOptions(
                settings.RetryMaxAttempts.Value,
                Math.Max(0, settings.RetryBaseDelayMilliseconds ?? CodingAgentRetryOptions.Default.BaseDelayMilliseconds));
        }

        return CodingAgentRetryOptions.Default;
    }

    private void SaveRetryOptions(CodingAgentSettingsSnapshot? current, CodingAgentRetryOptions options, bool enabled)
    {
        if (_settingsStore is null)
        {
            return;
        }

        current ??= _settingsStore.Load();
        _settingsStore.Save(current with
        {
            RetryMaxAttempts = enabled ? options.MaxAttempts : 0,
            RetryBaseDelayMilliseconds = enabled ? options.BaseDelayMilliseconds : 0
        });
    }

    private void SaveQueueModes(string? steeringMode, string? followUpMode)
    {
        if (_settingsStore is null)
        {
            return;
        }

        var settings = _settingsStore.Load();
        _settingsStore.Save(settings with
        {
            SteeringMode = steeringMode ?? settings.SteeringMode,
            FollowUpMode = followUpMode ?? settings.FollowUpMode
        });
    }

    private void SaveAutoCompactionEnabled(bool enabled)
    {
        if (_settingsStore is null)
        {
            return;
        }

        var settings = _settingsStore.Load();
        _settingsStore.Save(settings with { AutoCompactionEnabled = enabled });
    }

    private bool GetAutoCompactionEnabled()
    {
        if (_autoCompactionEnabled is not null)
        {
            return _autoCompactionEnabled.Value;
        }

        _autoCompactionEnabled = _settingsStore?.Load().AutoCompactionEnabled;
        return _autoCompactionEnabled ?? _autoCompaction.IsEnabled;
    }

    private async Task RecordAutoRetryStartAsync(
        int attempt,
        int maxAttempts,
        int delayMs,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        var normalizedError = string.IsNullOrWhiteSpace(errorMessage) ? "Unknown error" : errorMessage;
        _treeSessionController?.AppendAutoRetryStart(attempt, maxAttempts, delayMs, normalizedError);
        await WriteJsonLineAsync(new
        {
            type = "auto_retry_start",
            attempt,
            maxAttempts,
            delayMs,
            errorMessage = normalizedError
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task RecordAutoRetryEndAsync(
        bool success,
        int attempt,
        string? finalError,
        CancellationToken cancellationToken)
    {
        _treeSessionController?.AppendAutoRetryEnd(success, attempt, finalError);
        await WriteJsonLineAsync(new
        {
            type = "auto_retry_end",
            success,
            attempt,
            finalError
        }, cancellationToken).ConfigureAwait(false);
    }

    private string ExportHtmlTranscript(string exportPath)
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

        return CodingAgentHtmlSessionExporter.Export(
            exportPath,
            snapshot.Messages,
            snapshot.Provider ?? _runner.Model.Provider,
            snapshot.Model ?? _runner.Model.Id,
            snapshot.Name ?? _runner.SessionName,
            treeSummary,
            sessionJsonl);
    }

    private string GetDefaultHtmlExportPath()
    {
        var sourcePath = _treeSessionController?.Path ?? _sessionStore?.Path;
        var directory = string.IsNullOrWhiteSpace(sourcePath)
            ? Environment.CurrentDirectory
            : (Path.GetDirectoryName(sourcePath) ?? Environment.CurrentDirectory);
        var basename = string.IsNullOrWhiteSpace(sourcePath)
            ? "session"
            : Path.GetFileNameWithoutExtension(sourcePath);
        basename = SanitizeFileName(string.IsNullOrWhiteSpace(basename) ? "session" : basename);
        return Path.Combine(directory, $"tau-session-{basename}.html");
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

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray();
        return new string(chars);
    }

    private async Task WriteSuccessAsync(
        string? id,
        string command,
        object? data = null,
        CancellationToken cancellationToken = default)
    {
        await WriteJsonLineAsync(new RpcResponse(id, "response", command, true, data), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task WriteExplicitDataSuccessAsync(
        string? id,
        string command,
        object? data,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["type"] = "response",
            ["command"] = command,
            ["success"] = true,
            ["data"] = data
        };
        if (id is not null)
        {
            payload["id"] = id;
        }

        await WriteJsonLineAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteErrorAsync(
        string? id,
        string command,
        string error,
        CancellationToken cancellationToken)
    {
        await WriteJsonLineAsync(new RpcResponse(id, "response", command, false, null, error), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task WriteJsonLineAsync(object payload, CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(payload, JsonOptions);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _output.WriteLineAsync(line).ConfigureAwait(false);
            await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static object ToRpcEvent(AgentEvent evt) => evt switch
    {
        AgentStartEvent => new { type = evt.Type },
        AgentEndEvent end => new { type = evt.Type, errorMessage = end.ErrorMessage },
        TurnStartEvent turn => new { type = evt.Type, turnIndex = turn.TurnIndex },
        TurnEndEvent turn => new { type = evt.Type, turnIndex = turn.TurnIndex },
        MessageStartEvent message => new { type = evt.Type, message = ToRpcMessage(message.Partial) },
        MessageUpdateEvent update => new { type = evt.Type, streamEvent = ToRpcStreamEvent(update.StreamEvent) },
        MessageEndEvent message => new { type = evt.Type, message = ToRpcMessage(message.Message) },
        ToolExecutionStartEvent tool => new { type = evt.Type, toolCallId = tool.ToolCallId, toolName = tool.ToolName },
        ToolExecutionUpdateEvent tool => new { type = evt.Type, toolCallId = tool.ToolCallId, update = new { text = tool.Update.Text } },
        ToolExecutionEndEvent tool => new
        {
            type = evt.Type,
            toolCallId = tool.ToolCallId,
            result = new
            {
                isError = tool.Result.IsError,
                content = tool.Result.Content.Select(ToRpcContent).ToArray()
            }
        },
        _ => new { type = evt.Type }
    };

    private static object ToRpcStreamEvent(StreamEvent evt) => evt switch
    {
        StartEvent start => new { type = evt.Type, partial = ToRpcMessage(start.Partial) },
        TextStartEvent text => new { type = evt.Type, contentIndex = text.ContentIndex },
        TextDeltaEvent text => new { type = evt.Type, contentIndex = text.ContentIndex, delta = text.Delta },
        TextEndEvent text => new { type = evt.Type, contentIndex = text.ContentIndex },
        ThinkingStartEvent thinking => new { type = evt.Type, contentIndex = thinking.ContentIndex },
        ThinkingDeltaEvent thinking => new { type = evt.Type, contentIndex = thinking.ContentIndex, delta = thinking.Delta },
        ThinkingEndEvent thinking => new { type = evt.Type, contentIndex = thinking.ContentIndex },
        ToolCallStartEvent tool => new { type = evt.Type, contentIndex = tool.ContentIndex },
        ToolCallDeltaEvent tool => new { type = evt.Type, contentIndex = tool.ContentIndex, delta = tool.Delta },
        ToolCallEndEvent tool => new { type = evt.Type, contentIndex = tool.ContentIndex },
        DoneEvent done => new { type = evt.Type, message = ToRpcMessage(done.Message) },
        ErrorEvent error => new { type = evt.Type, error = error.Error, partial = error.Partial is null ? null : ToRpcMessage(error.Partial) },
        _ => new { type = evt.Type }
    };

    private static object ToRpcMessage(ChatMessage message)
    {
        var converted = CodingAgentSessionStore.FromMessage(message);
        return converted;
    }

    private static object ToRpcContent(ContentBlock block) => block switch
    {
        TextContent text => new { type = block.Type, text = text.Text },
        ThinkingContent thinking => new { type = block.Type, thinking = thinking.Thinking, redacted = thinking.Redacted },
        ImageContent image => new { type = block.Type, data = image.Data, mimeType = image.MimeType },
        ToolCallContent tool => new { type = block.Type, id = tool.Id, name = tool.Name, arguments = tool.Arguments },
        _ => new { type = block.Type }
    };

    private static IReadOnlyList<ContentBlock>? TryReadPromptContent(JsonElement command, string message)
    {
        if (!command.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var blocks = new List<ContentBlock> { new TextContent(message) };
        foreach (var image in images.EnumerateArray())
        {
            if (image.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var data = GetOptionalString(image, "data");
            var mimeType = GetOptionalString(image, "mimeType");
            if (!string.IsNullOrWhiteSpace(data) && !string.IsNullOrWhiteSpace(mimeType))
            {
                blocks.Add(new ImageContent(data, mimeType));
            }
        }

        return blocks;
    }

    private static string? GetOptionalString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
            _ => null
        };
    }

    private static string GetRequiredString(JsonElement element, string name)
    {
        var value = GetOptionalString(element, name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"RPC command requires non-empty '{name}'.");
        }

        return value;
    }

    private static bool GetRequiredBoolean(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new ArgumentException($"RPC command requires boolean '{name}'.");
        }

        return value.GetBoolean();
    }

    private static string? ReadNullableString(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => string.IsNullOrWhiteSpace(element.GetString())
                ? null
                : element.GetString()!.Trim(),
            _ => throw new ArgumentException("Expected string or null setting value.")
        };
    }

    private static bool? ReadNullableBoolean(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new ArgumentException("Expected boolean or null setting value.")
        };
    }

    private static bool ReadRequiredBoolean(JsonElement element, string name)
    {
        if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new ArgumentException($"{name} must be a boolean.");
        }

        return element.GetBoolean();
    }

    private static int? ReadNullableNonNegativeInt(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value) || value < 0)
        {
            throw new ArgumentException($"{name} must be a non-negative integer or null.");
        }

        return value;
    }

    private static string[]? ReadEnabledModels(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("settings.enabledModels must be an array or null.");
        }

        var models = element
            .EnumerateArray()
            .Select(ReadNullableString)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return models.Length == 0 ? null : models;
    }

    private static string? NormalizeTreeFilterMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "default" => "default",
            "no-tools" or "notools" => "no-tools",
            "user" or "user-only" => "user-only",
            "labeled" or "labels" or "labeled-only" => "labeled-only",
            "all" => "all",
            _ => throw new ArgumentException($"Unsupported tree filter mode '{value}'. Expected default, no-tools, user-only, labeled-only, or all.")
        };
    }

    private static string? NormalizeThinkingLevelOrNull(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Equals("off", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var level = ParseThinkingLevelOrNull(value);
        if (level is null)
        {
            throw new ArgumentException($"Unsupported thinking level '{value}'. Expected off, minimal, low, medium, high, or xhigh.");
        }

        return FormatThinkingLevelRaw(level);
    }

    private static string? NormalizeQueueModeOrThrow(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!CodingAgentQueueModes.TryNormalize(value, out var mode))
        {
            throw new ArgumentException($"Unsupported queue mode '{value}'. Expected all or one-at-a-time.");
        }

        return mode;
    }

    private static string ReadQueueMode(JsonElement command)
    {
        var rawMode = GetRequiredString(command, "mode");
        if (!CodingAgentQueueModes.TryNormalize(rawMode, out var mode))
        {
            throw new ArgumentException($"Unsupported queue mode '{rawMode}'. Expected all or one-at-a-time.");
        }

        return mode;
    }

    private static string FormatThinkingLevel(ThinkingLevel? level) => level switch
    {
        null => "off",
        ThinkingLevel.Minimal => "minimal",
        ThinkingLevel.Low => "low",
        ThinkingLevel.Medium => "medium",
        ThinkingLevel.High => "high",
        ThinkingLevel.ExtraHigh => "xhigh",
        _ => "off"
    };

    private static string? FormatThinkingLevelRaw(ThinkingLevel? level) =>
        level is null ? null : FormatThinkingLevel(level);

    private static ThinkingLevel? ParseThinkingLevelOrNull(string value)
    {
        if (value.Equals("off", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return value.ToLowerInvariant() switch
        {
            "minimal" => ThinkingLevel.Minimal,
            "low" => ThinkingLevel.Low,
            "medium" or "med" => ThinkingLevel.Medium,
            "high" => ThinkingLevel.High,
            "xhigh" or "extrahigh" or "extra-high" => ThinkingLevel.ExtraHigh,
            _ => null
        };
    }

    private static ThinkingLevel? CycleThinkingLevel(ThinkingLevel? current) => current switch
    {
        null => ThinkingLevel.Low,
        ThinkingLevel.Minimal => ThinkingLevel.Low,
        ThinkingLevel.Low => ThinkingLevel.Medium,
        ThinkingLevel.Medium => ThinkingLevel.High,
        ThinkingLevel.High => ThinkingLevel.ExtraHigh,
        ThinkingLevel.ExtraHigh => null,
        _ => ThinkingLevel.Low
    };

    private void SaveThinkingLevel(ThinkingLevel? level)
    {
        if (_settingsStore is null)
        {
            return;
        }

        var settings = _settingsStore.Load();
        _settingsStore.Save(settings with { DefaultThinkingLevel = FormatThinkingLevelRaw(level) });
    }

    private sealed record RpcResponse(
        string? Id,
        string Type,
        string Command,
        bool Success,
        object? Data = null,
        string? Error = null);

    private sealed record RpcCommandInfo(
        string Name,
        string Usage,
        string Description,
        string Source);

    private sealed record RpcPromptAttemptResult(bool IsSuccess, string? ErrorMessage)
    {
        public static RpcPromptAttemptResult Success() => new(true, null);
        public static RpcPromptAttemptResult Failed(string? errorMessage) => new(false, errorMessage);
    }
}
