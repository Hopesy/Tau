using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Tau.Agent;
using Tau.Agent.Runtime;
using Tau.Ai;
using Tau.Ai.Observability;

namespace Tau.CodingAgent.Runtime;

public sealed class CodingAgentRpcHost
{
    private const int BashOutputQueueCapacity = 1024;

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
    private readonly CodingAgentPromptTemplateStore? _promptTemplateStore;
    private readonly CodingAgentSkillStore? _skillStore;
    private readonly CodingAgentExtensionCommandStore? _extensionCommandStore;
    private readonly ICodingAgentShellRunner _shellRunner;
    private readonly CodingAgentAutoCompactionOptions _autoCompaction;
    private readonly CodingAgentSessionSwitchCoordinator _sessionSwitchCoordinator;
    private CodingAgentRetryOptions _retryOptions;
    private bool? _autoCompactionEnabled;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _gate = new();
    private Task? _activePromptTask;
    private CancellationTokenSource? _activePromptCts;
    private CancellationTokenSource? _activeRetryDelayCts;
    private Task? _activeBashTask;
    private CancellationTokenSource? _activeBashCts;
    private BashOutputQueueState? _activeBashOutputQueue;
    private Task? _activeCompactionTask;
    private CancellationTokenSource? _activeCompactionCts;

    public CodingAgentRpcExtensionUiBridge ExtensionUi { get; }

    public CodingAgentRpcHost(
        ICodingAgentRunner runner,
        TextReader input,
        TextWriter output,
        CodingAgentSessionStore? sessionStore = null,
        CodingAgentSettingsStore? settingsStore = null,
        CodingAgentTreeSessionController? treeSessionController = null,
        CodingAgentAutoCompactionOptions? autoCompaction = null,
        CodingAgentRetryOptions? retryOptions = null,
        ICodingAgentShellRunner? shellRunner = null,
        Func<CodingAgentSessionSwitchHookState, CancellationToken, Task<CodingAgentSessionSwitchHookResult?>>? sessionSwitchHook = null,
        CodingAgentPromptTemplateStore? promptTemplateStore = null,
        CodingAgentSkillStore? skillStore = null,
        CodingAgentExtensionCommandStore? extensionCommandStore = null,
        CodingAgentRpcExtensionUiBridge? extensionUi = null)
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
        _promptTemplateStore = promptTemplateStore;
        _skillStore = skillStore;
        _extensionCommandStore = extensionCommandStore;
        ExtensionUi = extensionUi ?? new CodingAgentRpcExtensionUiBridge();
        ExtensionUi.Attach(WriteJsonLineAsync);
        _shellRunner = shellRunner ?? new SystemCodingAgentShellRunner();
        _autoCompaction = autoCompaction ?? CodingAgentAutoCompactionOptions.Disabled;
        _sessionSwitchCoordinator = new CodingAgentSessionSwitchCoordinator(
            runner,
            treeSessionController,
            sessionSwitchPrompt: null,
            sessionSwitchHook: sessionSwitchHook);
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

        var activeCompaction = GetActiveCompactionTask();
        if (activeCompaction is not null)
        {
            await activeCompaction.ConfigureAwait(false);
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

            if (type.Equals("extension_ui_response", StringComparison.Ordinal))
            {
                ExtensionUi.TryHandleResponse(root);
                return;
            }

            if (!type.Equals("get_state", StringComparison.Ordinal))
            {
                var activeCompaction = GetActiveCompactionTask();
                if (activeCompaction is not null)
                {
                    await activeCompaction.ConfigureAwait(false);
                }
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
                        models = GetAuthConfiguredModels().ToArray()
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
                    await WriteSuccessAsync(id, "get_session_stats", CreateSessionStats(), cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case "get_messages":
                    await WriteSuccessAsync(id, "get_messages", new
                    {
                        messages = _runner.Messages.Select(ToRpcMessage).ToArray()
                    }, cancellationToken).ConfigureAwait(false);
                    break;
                case "get_commands":
                    await WriteSuccessAsync(id, "get_commands", new
                    {
                        commands = CreateRpcCommandInfos()
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
        var content = TryReadPromptContent(command, message);
        if (IsPromptActive)
        {
            if (streamingBehavior is not null &&
                streamingBehavior.Equals("steer", StringComparison.OrdinalIgnoreCase))
            {
                if (content is null)
                {
                    _runner.Steer(message);
                }
                else
                {
                    _runner.Steer(content);
                }

                await WriteSuccessAsync(id, "prompt", cancellationToken: cancellationToken).ConfigureAwait(false);
                return;
            }

            if (streamingBehavior is not null &&
                (streamingBehavior.Equals("followUp", StringComparison.OrdinalIgnoreCase) ||
                 streamingBehavior.Equals("follow_up", StringComparison.OrdinalIgnoreCase)))
            {
                if (content is null)
                {
                    _runner.FollowUp(message);
                }
                else
                {
                    _runner.FollowUp(content);
                }

                await WriteSuccessAsync(id, "prompt", cancellationToken: cancellationToken).ConfigureAwait(false);
                return;
            }

            await WriteErrorAsync(id, "prompt", "Agent is already running; use steer, follow_up, or prompt.streamingBehavior.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_gate)
        {
            _activePromptCts = cts;
        }

        var logContext = CreatePromptLogContext(id);
        var task = Task.Run(() => RunPromptAsync(id, message, content, logContext, cts), CancellationToken.None);
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

        var message = GetRequiredString(command, "message");
        var content = TryReadPromptContent(command, message);
        if (content is null)
        {
            _runner.Steer(message);
        }
        else
        {
            _runner.Steer(content);
        }

        await WriteSuccessAsync(id, "steer", cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleFollowUpAsync(string? id, JsonElement command, CancellationToken cancellationToken)
    {
        if (!IsPromptActive)
        {
            await WriteErrorAsync(id, "follow_up", "Agent is not running.", cancellationToken).ConfigureAwait(false);
            return;
        }

        var message = GetRequiredString(command, "message");
        var content = TryReadPromptContent(command, message);
        if (content is null)
        {
            _runner.FollowUp(message);
        }
        else
        {
            _runner.FollowUp(content);
        }

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

        var summary = await _sessionSwitchCoordinator.TrySummarizeBeforeRpcSwitchAsync(
                command,
                CodingAgentTreeNavigationReason.NewSession,
                targetSessionPath: null,
                cancellationToken)
            .ConfigureAwait(false);
        if (summary.Cancelled)
        {
            await WriteSuccessAsync(
                    id,
                    "new_session",
                    CreateSessionSwitchRpcData(summary),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        _runner.ResetSession();
        _treeSessionController?.StartNewFromRunner(_runner, GetOptionalString(command, "parentSession"));
        PersistSession();
        await WriteSuccessAsync(
                id,
                "new_session",
                CreateSessionSwitchRpcData(summary),
                cancellationToken)
            .ConfigureAwait(false);
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
        var model = SelectConfiguredModel(provider, modelId);
        _settingsStore?.SaveDefaultModel(model);
        ClampCurrentThinkingLevel();
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
            selectedModel = SelectConfiguredModel(updated.DefaultProvider, updated.DefaultModel);
            updated = updated with
            {
                DefaultProvider = selectedModel.Provider,
                DefaultModel = selectedModel.Id
            };
        }

        var effectiveThinking = CodingAgentThinkingLevels.ClampForModel(
            _runner.Model,
            ParseThinkingLevelOrNull(updated.DefaultThinkingLevel));
        updated = updated with { DefaultThinkingLevel = FormatThinkingLevelRaw(effectiveThinking) };
        _settingsStore.Save(updated);

        _runner.ThinkingLevel = effectiveThinking;
        _runner.SteeringMode = CodingAgentQueueModes.ToAgentQueueMode(updated.SteeringMode);
        _runner.FollowUpMode = CodingAgentQueueModes.ToAgentQueueMode(updated.FollowUpMode);
        SetRetryOptions(CodingAgentRetryOptions.FromSettingsOrEnvironment(updated));
        _autoCompactionEnabled = updated.AutoCompactionEnabled;

        if (selectedModel is not null)
        {
            _treeSessionController?.SyncFromRunner(_runner);
            PersistSession();
        }

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
        if (candidates.Count == 0)
        {
            await WriteErrorAsync(
                    id,
                    "cycle_model",
                    "No models with configured auth are available. Use login or configure provider credentials.",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (candidates.Count == 1)
        {
            await WriteExplicitDataSuccessAsync(id, "cycle_model", null, cancellationToken).ConfigureAwait(false);
            return;
        }

        var currentIndex = candidates.FindIndex(candidate => SameModel(candidate.Model, _runner.Model));
        var nextIndex = currentIndex < 0 ? 0 : (currentIndex + 1) % candidates.Count;
        var next = candidates[nextIndex];
        var selected = _runner.SelectModel(next.Model.Provider, next.Model.Id);
        _settingsStore?.SaveDefaultModel(selected);
        ApplyScopedThinkingOverride(next.ThinkingLevel);
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
        if (!CodingAgentThinkingLevels.TryParse(rawLevel, out var level))
        {
            throw new ArgumentException($"Unsupported thinking level '{rawLevel}'. Expected off, minimal, low, medium, high, or xhigh.");
        }

        SetAndSaveThinkingLevel(level);
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

        _runner.ThinkingLevel = CodingAgentThinkingLevels.CycleForModel(_runner.Model, _runner.ThinkingLevel);
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
        var outputQueue = new BashOutputQueueState();
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
                _activeBashOutputQueue = outputQueue;
                _activeBashTask = Task.Run(() => RunBashAsync(id, shellCommand, cts, outputQueue), CancellationToken.None);
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
        var currentPath = Path.GetFullPath(_treeSessionController.Path);
        var normalizedSessionPath = Path.GetFullPath(sessionPath);
        if (normalizedSessionPath.Equals(currentPath, StringComparison.OrdinalIgnoreCase))
        {
            await WriteSuccessAsync(
                    id,
                    "switch_session",
                    CreateSessionSwitchRpcData(
                        CodingAgentSessionSwitchSummaryResult.None(
                            CodingAgentTreeNavigationReason.ResumeSession,
                            currentPath,
                            normalizedSessionPath),
                        alreadyCurrent: true),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var summary = await _sessionSwitchCoordinator.TrySummarizeBeforeRpcSwitchAsync(
                command,
                CodingAgentTreeNavigationReason.ResumeSession,
                normalizedSessionPath,
                cancellationToken)
            .ConfigureAwait(false);
        if (summary.Cancelled)
        {
            await WriteSuccessAsync(
                    id,
                    "switch_session",
                    CreateSessionSwitchRpcData(summary),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var snapshot = _treeSessionController.Resume(normalizedSessionPath);
        _runner.RestoreSession(snapshot.ToFlatSnapshot());
        PersistSession();
        await WriteSuccessAsync(
                id,
                "switch_session",
                CreateSessionSwitchRpcData(summary),
                cancellationToken)
            .ConfigureAwait(false);
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

    private CodingAgentSessionStats CreateSessionStats()
    {
        if (_treeSessionController is null)
        {
            return _runner.GetSessionStats(_sessionStore?.Path);
        }

        _treeSessionController.SyncFromRunner(_runner);
        return _runner
            .GetSessionStats(_sessionStore?.Path)
            .WithUsage(_treeSessionController.GetCurrentBranchUsageSummary());
    }

    private async Task HandleCompactAsync(string? id, JsonElement command, CancellationToken cancellationToken)
    {
        if (IsPromptActive)
        {
            await WriteErrorAsync(id, "compact", "Cannot compact while the agent is running.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (IsCompacting)
        {
            await WriteErrorAsync(id, "compact", "Compaction is already running.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var customInstructions = GetOptionalString(command, "customInstructions");
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_gate)
        {
            _activeCompactionCts = cts;
        }

        var task = Task.Run(() => RunCompactAsync(id, customInstructions, cts), CancellationToken.None);
        lock (_gate)
        {
            if (ReferenceEquals(_activeCompactionCts, cts))
            {
                _activeCompactionTask = task;
            }
        }
    }

    private async Task RunCompactAsync(
        string? id,
        string? customInstructions,
        CancellationTokenSource cts)
    {
        try
        {
            _treeSessionController?.SyncFromRunner(_runner);
            var result = await _runner
                .CompactAsync(customInstructions, cts.Token)
                .ConfigureAwait(false);
            _treeSessionController?.RecordCompaction(_runner, result);
            PersistSession();
            await WriteSuccessAsync(id, "compact", result, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await WriteErrorAsync(id, "compact", "Compaction cancelled.", CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(id, "compact", ex.Message, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_activeCompactionCts, cts))
                {
                    _activeCompactionCts = null;
                    _activeCompactionTask = null;
                }
            }

            cts.Dispose();
        }
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
        var forkTarget = _treeSessionController.GetForkTarget(entryId)
            ?? throw new InvalidOperationException("Invalid entry ID for forking");
        var snapshot = _treeSessionController.BranchTo(forkTarget.ParentEntryId);
        _runner.RestoreSession(snapshot.ToFlatSnapshot());
        PersistSession();
        await WriteSuccessAsync(id, "fork", new
        {
            text = forkTarget.Text,
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
        string? id,
        string message,
        IReadOnlyList<ContentBlock>? content,
        TauRuntimeLogContext logContext,
        CancellationTokenSource cts)
    {
        var rollbackSnapshot = CreateRollbackSnapshot();
        var retryAttempt = 0;
        var response = new RpcPromptResponseState(this, id);

        try
        {
            while (true)
            {
                var result = await RunSinglePromptAttemptAsync(message, content, logContext, response, cts.Token).ConfigureAwait(false);
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

                if (result.IsPreflightFailure)
                {
                    RestorePromptSnapshot(rollbackSnapshot);
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
        CancellationTokenSource cts,
        BashOutputQueueState outputQueue)
    {
        var outputChannel = CreateBashOutputChannel();
        var outputDrainTask = DrainBashOutputAsync(id, outputChannel.Reader, CancellationToken.None);
        var outputDrained = false;

        try
        {
            await WriteBashEventAsync(
                    id,
                    "started",
                    new { running = true, outputQueueCapacity = BashOutputQueueCapacity },
                    CancellationToken.None)
                .ConfigureAwait(false);
            var progress = new ImmediateProgress<CodingAgentShellEvent>(
                evt => QueueBashOutput(outputChannel.Writer, evt, outputQueue));
            CodingAgentShellResult? result = null;
            Exception? failure = null;

            try
            {
                result = await _shellRunner.ExecuteAsync(command, progress, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            await CompleteBashOutputDrainAsync(outputChannel.Writer, outputDrainTask).ConfigureAwait(false);
            outputDrained = true;

            if (failure is null && result is not null && (result.Truncated || result.FullOutputPath is not null))
            {
                await WriteBashOutputSummaryAsync(
                        id,
                        result,
                        outputQueue.DroppedOutputEvents,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }

            if (failure is OperationCanceledException)
            {
                await WriteBashEventAsync(
                        id,
                        "cancelled",
                        new { cancelled = true, droppedOutputEvents = outputQueue.DroppedOutputEvents },
                        CancellationToken.None)
                    .ConfigureAwait(false);
                await WriteSuccessAsync(
                    id,
                    "bash",
                    new CodingAgentShellResult(string.Empty, null, Cancelled: true, Truncated: false),
                    CancellationToken.None).ConfigureAwait(false);
            }
            else if (failure is not null)
            {
                await WriteBashEventAsync(
                        id,
                        "failed",
                        new { error = failure.Message, droppedOutputEvents = outputQueue.DroppedOutputEvents },
                        CancellationToken.None)
                    .ConfigureAwait(false);
                await WriteErrorAsync(id, "bash", failure.Message, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                var eventName = result!.Cancelled ? "cancelled" : "completed";
                await WriteBashEventAsync(id, eventName, CreateBashEventResult(result, outputQueue.DroppedOutputEvents), CancellationToken.None)
                    .ConfigureAwait(false);
                await WriteSuccessAsync(id, "bash", result, CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            Exception? outputDrainFailure = null;
            if (!outputDrained)
            {
                outputChannel.Writer.TryComplete();
                try
                {
                    await outputDrainTask.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    outputDrainFailure = ex;
                }
            }

            lock (_gate)
            {
                if (ReferenceEquals(_activeBashCts, cts))
                {
                    _activeBashCts = null;
                    _activeBashTask = null;
                    _activeBashOutputQueue = null;
                }
            }

            cts.Dispose();
            if (outputDrainFailure is not null)
            {
                ExceptionDispatchInfo.Capture(outputDrainFailure).Throw();
            }
        }
    }

    private async Task<RpcPromptAttemptResult> RunSinglePromptAttemptAsync(
        string message,
        IReadOnlyList<ContentBlock>? content,
        TauRuntimeLogContext logContext,
        RpcPromptResponseState response,
        CancellationToken cancellationToken)
    {
        try
        {
            var events = content is null
                ? _runner.RunAsync(message, logContext, cancellationToken)
                : _runner.RunAsync(content, logContext, cancellationToken);

            await foreach (var evt in events.ConfigureAwait(false))
            {
                await response.AcceptAsync(CancellationToken.None).ConfigureAwait(false);
                await WriteJsonLineAsync(ToRpcEvent(evt), cancellationToken).ConfigureAwait(false);
                if (evt is AgentEndEvent { ErrorMessage: not null } end)
                {
                    return RpcPromptAttemptResult.Failed(end.ErrorMessage);
                }
            }

            await response.AcceptAsync(CancellationToken.None).ConfigureAwait(false);
            return RpcPromptAttemptResult.Success();
        }
        catch (Exception ex) when (!response.IsAccepted && ex is not OperationCanceledException)
        {
            await response.FailAsync(ex.Message, CancellationToken.None).ConfigureAwait(false);
            return RpcPromptAttemptResult.PreflightFailed(ex.Message);
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
            isBashRunning = IsBashActive,
            bashOutputQueueCapacity = BashOutputQueueCapacity,
            bashDroppedOutputEvents = GetActiveBashDroppedOutputEvents(),
            isCompacting = IsCompacting,
            steeringMode = CodingAgentQueueModes.FromAgentQueueMode(_runner.SteeringMode),
            followUpMode = CodingAgentQueueModes.FromAgentQueueMode(_runner.FollowUpMode),
            sessionFile = treeSummary?.FilePath ?? _sessionStore?.Path,
            sessionId = treeSummary?.SessionId,
            sessionName = _runner.SessionName,
            autoCompactionEnabled = GetAutoCompactionEnabled(),
            autoRetryEnabled = GetRetryOptions().IsEnabled,
            messageCount = _runner.Messages.Count,
            pendingMessageCount = _runner.PendingMessageCount
        };
    }

    private TauRuntimeLogContext CreatePromptLogContext(string? commandId)
    {
        var id = string.IsNullOrWhiteSpace(commandId)
            ? Guid.NewGuid().ToString("N")
            : commandId.Trim();
        return new TauRuntimeLogContext(
            CorrelationId: id,
            SessionId: TryGetTreeSessionId(),
            MessageId: id);
    }

    private string? TryGetTreeSessionId()
    {
        if (_treeSessionController is null)
        {
            return null;
        }

        try
        {
            return _treeSessionController.GetSummary().SessionId;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
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
                : settings.Theme,
            shellPath = settings.ShellPath,
            shellCommandPrefix = settings.ShellCommandPrefix,
            npmCommand = settings.NpmCommand?.ToArray(),
            quietStartup = settings.QuietStartup ?? false,
            collapseChangelog = settings.CollapseChangelog ?? false,
            enableInstallTelemetry = settings.EnableInstallTelemetry ?? true,
            lastChangelogVersion = settings.LastChangelogVersion,
            terminal = new
            {
                showImages = settings.TerminalShowImages ?? true,
                clearOnShrink = settings.TerminalClearOnShrink ?? false
            },
            images = new
            {
                autoResize = settings.ImagesAutoResize ?? true,
                blockImages = settings.ImagesBlockImages ?? false
            },
            markdown = new
            {
                codeBlockIndent = settings.MarkdownCodeBlockIndent ?? "  "
            },
            showHardwareCursor = settings.ShowHardwareCursor ?? false,
            editorPaddingX = settings.EditorPaddingX ?? 0,
            autocompleteMaxVisible = settings.AutocompleteMaxVisible ?? 5
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

        if (settingsElement.TryGetProperty("shellPath", out var shellPath))
        {
            updated = updated with { ShellPath = ReadNullableString(shellPath) };
        }

        if (settingsElement.TryGetProperty("shellCommandPrefix", out var shellCommandPrefix))
        {
            updated = updated with { ShellCommandPrefix = ReadNullableString(shellCommandPrefix) };
        }

        if (settingsElement.TryGetProperty("npmCommand", out var npmCommand))
        {
            updated = updated with { NpmCommand = ReadNullableStringArrayPreserveOrder(npmCommand, "settings.npmCommand") };
        }

        if (settingsElement.TryGetProperty("quietStartup", out var quietStartup))
        {
            updated = updated with { QuietStartup = ReadNullableBoolean(quietStartup) };
        }

        if (settingsElement.TryGetProperty("collapseChangelog", out var collapseChangelog))
        {
            updated = updated with { CollapseChangelog = ReadNullableBoolean(collapseChangelog) };
        }

        if (settingsElement.TryGetProperty("enableInstallTelemetry", out var enableInstallTelemetry))
        {
            updated = updated with { EnableInstallTelemetry = ReadNullableBoolean(enableInstallTelemetry) };
        }

        if (settingsElement.TryGetProperty("lastChangelogVersion", out var lastChangelogVersion))
        {
            updated = updated with { LastChangelogVersion = ReadNullableString(lastChangelogVersion) };
        }

        if (settingsElement.TryGetProperty("terminal", out var terminal))
        {
            updated = ApplyTerminalSettings(updated, terminal);
        }

        if (settingsElement.TryGetProperty("images", out var images))
        {
            updated = ApplyImageSettings(updated, images);
        }

        if (settingsElement.TryGetProperty("markdown", out var markdown))
        {
            updated = ApplyMarkdownSettings(updated, markdown);
        }

        if (settingsElement.TryGetProperty("showHardwareCursor", out var showHardwareCursor))
        {
            updated = updated with { ShowHardwareCursor = ReadNullableBoolean(showHardwareCursor) };
        }

        if (settingsElement.TryGetProperty("editorPaddingX", out var editorPaddingX))
        {
            updated = updated with
            {
                EditorPaddingX = ReadNullableBoundedInt(editorPaddingX, "settings.editorPaddingX", 0, 3)
            };
        }

        if (settingsElement.TryGetProperty("autocompleteMaxVisible", out var autocompleteMaxVisible))
        {
            updated = updated with
            {
                AutocompleteMaxVisible = ReadNullableBoundedInt(
                    autocompleteMaxVisible,
                    "settings.autocompleteMaxVisible",
                    3,
                    20)
            };
        }

        return updated;
    }

    private static CodingAgentSettingsSnapshot ApplyTerminalSettings(
        CodingAgentSettingsSnapshot current,
        JsonElement terminalElement)
    {
        if (terminalElement.ValueKind == JsonValueKind.Null)
        {
            return current with
            {
                TerminalShowImages = null,
                TerminalClearOnShrink = null
            };
        }

        if (terminalElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("settings.terminal must be an object or null.");
        }

        var showImages = current.TerminalShowImages;
        var clearOnShrink = current.TerminalClearOnShrink;
        if (terminalElement.TryGetProperty("showImages", out var showImagesElement))
        {
            showImages = ReadNullableBoolean(showImagesElement);
        }

        if (terminalElement.TryGetProperty("clearOnShrink", out var clearOnShrinkElement))
        {
            clearOnShrink = ReadNullableBoolean(clearOnShrinkElement);
        }

        return current with
        {
            TerminalShowImages = showImages,
            TerminalClearOnShrink = clearOnShrink
        };
    }

    private static CodingAgentSettingsSnapshot ApplyImageSettings(
        CodingAgentSettingsSnapshot current,
        JsonElement imagesElement)
    {
        if (imagesElement.ValueKind == JsonValueKind.Null)
        {
            return current with
            {
                ImagesAutoResize = null,
                ImagesBlockImages = null
            };
        }

        if (imagesElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("settings.images must be an object or null.");
        }

        var autoResize = current.ImagesAutoResize;
        var blockImages = current.ImagesBlockImages;
        if (imagesElement.TryGetProperty("autoResize", out var autoResizeElement))
        {
            autoResize = ReadNullableBoolean(autoResizeElement);
        }

        if (imagesElement.TryGetProperty("blockImages", out var blockImagesElement))
        {
            blockImages = ReadNullableBoolean(blockImagesElement);
        }

        return current with
        {
            ImagesAutoResize = autoResize,
            ImagesBlockImages = blockImages
        };
    }

    private static CodingAgentSettingsSnapshot ApplyMarkdownSettings(
        CodingAgentSettingsSnapshot current,
        JsonElement markdownElement)
    {
        if (markdownElement.ValueKind == JsonValueKind.Null)
        {
            return current with { MarkdownCodeBlockIndent = null };
        }

        if (markdownElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("settings.markdown must be an object or null.");
        }

        var codeBlockIndent = current.MarkdownCodeBlockIndent;
        if (markdownElement.TryGetProperty("codeBlockIndent", out var codeBlockIndentElement))
        {
            codeBlockIndent = ReadNullableRawString(codeBlockIndentElement, "settings.markdown.codeBlockIndent");
        }

        return current with { MarkdownCodeBlockIndent = codeBlockIndent };
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

    private List<CodingAgentScopedModelEntry> GetModelCycleCandidates(out bool isScoped)
    {
        var registeredModels = GetRegisteredModels();
        var availableModels = GetAuthConfiguredModels(registeredModels);
        var enabledModels = _settingsStore?.Load().EnabledModels;
        if (enabledModels is null || enabledModels.Count == 0)
        {
            isScoped = false;
            return availableModels
                .Select(static model => new CodingAgentScopedModelEntry(model, null))
                .ToList();
        }

        var candidates = new List<CodingAgentScopedModelEntry>();
        foreach (var enabledModel in enabledModels)
        {
            if (!CodingAgentScopedModelPatterns.TryResolve(enabledModel, registeredModels, out var entry, out _))
            {
                continue;
            }

            var model = availableModels.FirstOrDefault(candidate => SameModel(candidate, entry.Model));
            if (model is not null && !candidates.Any(candidate => SameModel(candidate.Model, model)))
            {
                candidates.Add(new CodingAgentScopedModelEntry(model, entry.ThinkingLevel));
            }
        }

        if (candidates.Count == 0)
        {
            isScoped = false;
            return availableModels
                .Select(static model => new CodingAgentScopedModelEntry(model, null))
                .ToList();
        }

        isScoped = true;
        return candidates;
    }

    private IReadOnlyList<Model> GetRegisteredModels()
    {
        return CodingAgentModelAvailability.GetRegisteredModels(_runner);
    }

    private IReadOnlyList<Model> GetAuthConfiguredModels(IReadOnlyList<Model>? registeredModels = null)
    {
        return CodingAgentModelAvailability.GetAuthConfiguredModels(_runner, registeredModels);
    }

    private Model SelectConfiguredModel(string provider, string modelId)
    {
        var registeredModels = GetRegisteredModels();
        if (!TryResolveModelReference($"{provider}/{modelId}", registeredModels, out var registeredModel))
        {
            throw new KeyNotFoundException($"model '{provider}/{modelId}' is not registered");
        }

        if (!GetAuthConfiguredModels(registeredModels).Any(model => SameModel(model, registeredModel)))
        {
            throw new InvalidOperationException($"model '{registeredModel.Provider}/{registeredModel.Id}' does not have configured auth");
        }

        return _runner.SelectModel(registeredModel.Provider, registeredModel.Id);
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

    private Task? GetActiveCompactionTask()
    {
        lock (_gate)
        {
            return _activeCompactionTask;
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

    private bool IsCompacting
    {
        get
        {
            lock (_gate)
            {
                return _activeCompactionCts is not null || _runner.IsCompacting;
            }
        }
    }

    private long GetActiveBashDroppedOutputEvents()
    {
        lock (_gate)
        {
            return _activeBashOutputQueue?.DroppedOutputEvents ?? 0;
        }
    }

    private void PersistSession()
    {
        _sessionStore?.Save(_runner.Messages, _runner.Model, _runner.SessionName);
        _treeSessionController?.SyncFromRunner(_runner);
    }

    private static Dictionary<string, object?> CreateSessionSwitchRpcData(
        CodingAgentSessionSwitchSummaryResult result,
        bool alreadyCurrent = false)
    {
        var data = new Dictionary<string, object?>
        {
            ["cancelled"] = result.Cancelled,
            ["summarizedCurrentBranch"] = result.SummarizedCurrentBranch
        };
        if (alreadyCurrent)
        {
            data["alreadyCurrent"] = true;
        }

        if (result.Summary is not null)
        {
            data["branchSummary"] = new
            {
                entryCount = result.SummaryEntryCount,
                tokensBefore = result.TokensBeforeSummary
            };
        }

        return data;
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
            sessionJsonl,
            (_runner as ICodingAgentToolResultDetailsProvider)?.ToolResultDetailsByToolCallId);
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

    private static Channel<CodingAgentShellEvent> CreateBashOutputChannel() =>
        Channel.CreateBounded<CodingAgentShellEvent>(
            new BoundedChannelOptions(BashOutputQueueCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });

    private async Task WriteBashEventAsync(
        string? id,
        string eventName,
        object? data,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["type"] = "bash_event",
            ["command"] = "bash",
            ["requestId"] = id,
            ["event"] = eventName,
            ["timestamp"] = DateTimeOffset.UtcNow
        };
        if (id is not null)
        {
            payload["id"] = id;
        }

        if (data is not null)
        {
            payload["data"] = data;
        }

        await WriteJsonLineAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteBashOutputAsync(
        string? id,
        CodingAgentShellEvent evt,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["type"] = "bash_output",
            ["command"] = "bash",
            ["requestId"] = id,
            ["stream"] = evt.Stream,
            ["text"] = evt.Text,
            ["timestamp"] = evt.Timestamp
        };
        if (id is not null)
        {
            payload["id"] = id;
        }

        await WriteJsonLineAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteBashOutputSummaryAsync(
        string? id,
        CodingAgentShellResult result,
        long droppedOutputEvents,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["type"] = "bash_output",
            ["command"] = "bash",
            ["requestId"] = id,
            ["truncated"] = result.Truncated,
            ["fullOutputPath"] = result.FullOutputPath,
            ["exitCode"] = result.ExitCode,
            ["cancelled"] = result.Cancelled,
            ["droppedOutputEvents"] = droppedOutputEvents,
            ["timestamp"] = DateTimeOffset.UtcNow
        };
        if (id is not null)
        {
            payload["id"] = id;
        }

        await WriteJsonLineAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    private async Task DrainBashOutputAsync(
        string? id,
        ChannelReader<CodingAgentShellEvent> reader,
        CancellationToken cancellationToken)
    {
        await foreach (var evt in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await WriteBashOutputAsync(id, evt, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task CompleteBashOutputDrainAsync(
        ChannelWriter<CodingAgentShellEvent> writer,
        Task drainTask)
    {
        writer.TryComplete();
        await drainTask.ConfigureAwait(false);
    }

    private static void QueueBashOutput(
        ChannelWriter<CodingAgentShellEvent> writer,
        CodingAgentShellEvent evt,
        BashOutputQueueState outputQueue)
    {
        // FullMode.Wait + TryWrite means a full bounded buffer drops the newest event instead of growing unbounded.
        if (!writer.TryWrite(evt))
        {
            outputQueue.IncrementDroppedOutputEvents();
        }
    }

    private static object CreateBashEventResult(CodingAgentShellResult result, long droppedOutputEvents) => new
    {
        exitCode = result.ExitCode,
        cancelled = result.Cancelled,
        truncated = result.Truncated,
        fullOutputPath = result.FullOutputPath,
        droppedOutputEvents
    };

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
        AgentEndEvent end => new
        {
            type = evt.Type,
            errorMessage = end.ErrorMessage,
            messages = end.Messages.Select(ToRpcMessage).ToArray()
        },
        TurnStartEvent turn => new { type = evt.Type, turnIndex = turn.TurnIndex },
        TurnEndEvent turn => new
        {
            type = evt.Type,
            turnIndex = turn.TurnIndex,
            message = turn.Message is null ? null : ToRpcMessage(turn.Message),
            toolResults = turn.ToolResults.Select(ToRpcMessage).ToArray()
        },
        MessageStartEvent message => new { type = evt.Type, message = ToRpcMessage(message.Partial) },
        MessageUpdateEvent update => new
        {
            type = evt.Type,
            assistantMessageEvent = ToRpcAssistantMessageEvent(update.StreamEvent),
            message = update.Message is null ? null : ToRpcMessage(update.Message)
        },
        MessageEndEvent message => new { type = evt.Type, message = ToRpcMessage(message.Message) },
        ToolExecutionStartEvent tool => new { type = evt.Type, toolCallId = tool.ToolCallId, toolName = tool.ToolName, args = tool.Args },
        ToolExecutionUpdateEvent tool => new
        {
            type = evt.Type,
            toolCallId = tool.ToolCallId,
            toolName = tool.ToolName,
            args = tool.Args,
            update = new { text = tool.Update.Text },
            partialResult = tool.PartialResult is null ? null : ToRpcToolResult(tool.PartialResult)
        },
        ToolExecutionEndEvent tool => new
        {
            type = evt.Type,
            toolCallId = tool.ToolCallId,
            toolName = tool.ToolName,
            isError = tool.IsError,
            result = ToRpcToolResult(tool.Result)
        },
        _ => new { type = evt.Type }
    };

    private static object ToRpcToolResult(ToolResult result) => new
    {
        isError = result.IsError,
        content = result.Content.Select(ToRpcContent).ToArray()
    };

    private static object ToRpcAssistantMessageEvent(StreamEvent evt) => evt switch
    {
        StartEvent start => new { type = evt.Type, partial = ToRpcMessage(start.Partial) },
        TextStartEvent text => new
        {
            type = evt.Type,
            contentIndex = text.ContentIndex,
            partial = ToRpcMessage(text.Partial)
        },
        TextDeltaEvent text => new
        {
            type = evt.Type,
            contentIndex = text.ContentIndex,
            delta = text.Delta,
            partial = ToRpcMessage(text.Partial)
        },
        TextEndEvent text => new
        {
            type = evt.Type,
            contentIndex = text.ContentIndex,
            content = GetContentAt<TextContent>(text.Partial, text.ContentIndex)?.Text ?? string.Empty,
            partial = ToRpcMessage(text.Partial)
        },
        ThinkingStartEvent thinking => new
        {
            type = evt.Type,
            contentIndex = thinking.ContentIndex,
            partial = ToRpcMessage(thinking.Partial)
        },
        ThinkingDeltaEvent thinking => new
        {
            type = evt.Type,
            contentIndex = thinking.ContentIndex,
            delta = thinking.Delta,
            partial = ToRpcMessage(thinking.Partial)
        },
        ThinkingEndEvent thinking => new
        {
            type = evt.Type,
            contentIndex = thinking.ContentIndex,
            content = GetContentAt<ThinkingContent>(thinking.Partial, thinking.ContentIndex)?.Thinking ?? string.Empty,
            partial = ToRpcMessage(thinking.Partial)
        },
        ToolCallStartEvent tool => new
        {
            type = evt.Type,
            contentIndex = tool.ContentIndex,
            partial = ToRpcMessage(tool.Partial)
        },
        ToolCallDeltaEvent tool => new
        {
            type = evt.Type,
            contentIndex = tool.ContentIndex,
            delta = tool.Delta,
            partial = ToRpcMessage(tool.Partial)
        },
        ToolCallEndEvent tool => new
        {
            type = evt.Type,
            contentIndex = tool.ContentIndex,
            toolCall = GetContentAt<ToolCallContent>(tool.Partial, tool.ContentIndex) is { } toolCall
                ? ToRpcContent(toolCall)
                : null,
            partial = ToRpcMessage(tool.Partial)
        },
        DoneEvent done => new
        {
            type = evt.Type,
            reason = ToRpcDoneReason(done.Message.StopReason),
            message = ToRpcMessage(done.Message)
        },
        ErrorEvent error => new
        {
            type = evt.Type,
            reason = ToRpcErrorReason(error),
            error = ToRpcMessage(error.Message ?? error.Partial ?? new AssistantMessage())
        },
        _ => new { type = evt.Type }
    };

    private static TContent? GetContentAt<TContent>(AssistantMessage message, int contentIndex)
        where TContent : ContentBlock
    {
        if (contentIndex < 0 || contentIndex >= message.Content.Count)
        {
            return null;
        }

        return message.Content[contentIndex] as TContent;
    }

    private static string ToRpcDoneReason(StopReason? stopReason) => stopReason switch
    {
        StopReason.MaxTokens => "length",
        StopReason.ToolUse => "toolUse",
        _ => "stop"
    };

    private static string ToRpcErrorReason(ErrorEvent error)
    {
        var stopReason = error.Message?.StopReason ?? error.Partial?.StopReason;
        return stopReason == StopReason.Aborted ? "aborted" : "error";
    }

    private static object ToRpcMessage(ChatMessage message)
    {
        var converted = CodingAgentSessionStore.FromMessage(message);
        return new
        {
            role = converted.Role,
            toolCallId = converted.ToolCallId,
            isError = converted.Role.Equals("toolResult", StringComparison.Ordinal) ? converted.IsError : (bool?)null,
            usage = converted.Usage,
            api = converted.Api,
            provider = converted.Provider,
            model = converted.Model,
            timestamp = converted.Timestamp,
            content = GetContent(message).Select(ToRpcContent).ToArray()
        };
    }

    private static object ToRpcContent(ContentBlock block) => block switch
    {
        TextContent text => new { type = block.Type, text = text.Text, textSignature = text.TextSignature },
        ThinkingContent thinking => new { type = block.Type, thinking = thinking.Thinking, thinkingSignature = thinking.ThinkingSignature, redacted = thinking.Redacted },
        ImageContent image => new { type = block.Type, data = image.Data, mimeType = image.MimeType },
        ToolCallContent tool => new
        {
            type = block.Type,
            id = tool.Id,
            name = tool.Name,
            arguments = ToRpcToolCallArguments(tool.Arguments),
            thoughtSignature = tool.ThoughtSignature
        },
        _ => new { type = block.Type }
    };

    private static IReadOnlyList<ContentBlock> GetContent(ChatMessage message) => message switch
    {
        UserMessage user => user.Content,
        AssistantMessage assistant => assistant.Content,
        ToolResultMessage toolResult => toolResult.Content,
        _ => []
    };

    private static object ToRpcToolCallArguments(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return new Dictionary<string, object?>();
        }

        try
        {
            using var document = JsonDocument.Parse(arguments);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return document.RootElement.Clone();
            }
        }
        catch (JsonException)
        {
        }

        return new Dictionary<string, object?>();
    }

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

    private static bool? GetOptionalBoolean(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new ArgumentException($"RPC command property '{name}' must be boolean when provided.");
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

    private static string? ReadNullableRawString(JsonElement element, string name)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => element.GetString(),
            _ => throw new ArgumentException($"{name} must be a string or null.")
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

    private static int? ReadNullableBoundedInt(JsonElement element, string name, int min, int max)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value))
        {
            throw new ArgumentException($"{name} must be an integer or null.");
        }

        return Math.Clamp(value, min, max);
    }

    private static string[]? ReadNullableStringArrayPreserveOrder(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException($"{name} must be an array or null.");
        }

        var values = element
            .EnumerateArray()
            .Select(ReadNullableString)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!)
            .ToArray();
        return values.Length == 0 ? null : values;
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
            .Select(static value => value!.Trim())
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
        if (!CodingAgentThinkingLevels.TryParse(value, out var level))
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

    private static string FormatThinkingLevel(ThinkingLevel? level) =>
        CodingAgentThinkingLevels.Format(level);

    private static string? FormatThinkingLevelRaw(ThinkingLevel? level) =>
        CodingAgentThinkingLevels.FormatRaw(level);

    private static ThinkingLevel? ParseThinkingLevelOrNull(string? value) =>
        CodingAgentThinkingLevels.ParseOrNull(value);

    private ThinkingLevel? SetAndSaveThinkingLevel(ThinkingLevel? requested)
    {
        var effective = CodingAgentThinkingLevels.ClampForModel(_runner.Model, requested);
        _runner.ThinkingLevel = effective;
        SaveThinkingLevel(effective);
        return effective;
    }

    private void ClampCurrentThinkingLevel()
    {
        SetAndSaveThinkingLevel(_runner.ThinkingLevel);
    }

    private void SaveThinkingLevel(ThinkingLevel? level)
    {
        if (_settingsStore is null)
        {
            return;
        }

        var settings = _settingsStore.Load();
        _settingsStore.Save(settings with { DefaultThinkingLevel = FormatThinkingLevelRaw(level) });
    }

    private void ApplyScopedThinkingOverride(string? thinkingLevel)
    {
        if (thinkingLevel is null)
        {
            ClampCurrentThinkingLevel();
            return;
        }

        var level = CodingAgentScopedModelPatterns.ParseThinkingLevelOrNull(thinkingLevel);
        SetAndSaveThinkingLevel(level);
    }

    private RpcCommandInfo[] CreateRpcCommandInfos()
    {
        var commands = new List<RpcCommandInfo>();
        var extensionCommands = _extensionCommandStore?.Load() ?? [];
        commands.AddRange(extensionCommands.Select(command => new RpcCommandInfo(
            command.InvocationName,
            command.Description,
            "extension",
            CreateSourceInfo(command.FilePath, command.Scope, Path.GetDirectoryName(command.FilePath)))));

        var promptTemplates = _promptTemplateStore?.Load() ?? [];
        commands.AddRange(promptTemplates.Select(template => new RpcCommandInfo(
            template.Name,
            template.Description,
            "prompt",
            CreateSourceInfo(template.FilePath, template.Scope, Path.GetDirectoryName(template.FilePath)))));

        var skills = _skillStore?.Load() ?? [];
        commands.AddRange(skills.Select(skill => new RpcCommandInfo(
            $"skill:{skill.Name}",
            skill.Description,
            "skill",
            CreateSourceInfo(skill.FilePath, skill.Scope, skill.BaseDirectory))));

        return commands.ToArray();
    }

    private static RpcCommandSourceInfo CreateSourceInfo(string path, string scope, string? baseDir)
    {
        var normalizedScope = scope.Equals("user", StringComparison.OrdinalIgnoreCase)
            ? "user"
            : scope.Equals("project", StringComparison.OrdinalIgnoreCase)
                ? "project"
                : "temporary";
        return new RpcCommandSourceInfo(
            path,
            "local",
            normalizedScope,
            "top-level",
            string.IsNullOrWhiteSpace(baseDir) ? null : baseDir);
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
        string Description,
        string Source,
        RpcCommandSourceInfo? SourceInfo = null);

    private sealed record RpcCommandSourceInfo(
        string Path,
        string Source,
        string Scope,
        string Origin,
        string? BaseDir = null);

    private sealed class ImmediateProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private sealed class BashOutputQueueState
    {
        private long _droppedOutputEvents;

        public long DroppedOutputEvents => Interlocked.Read(ref _droppedOutputEvents);

        public void IncrementDroppedOutputEvents() => Interlocked.Increment(ref _droppedOutputEvents);
    }

    private sealed class RpcPromptResponseState(CodingAgentRpcHost host, string? id)
    {
        private int _completed;

        public bool IsAccepted => Volatile.Read(ref _completed) == 1;

        public async Task AcceptAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref _completed, 1, 0) != 0)
            {
                return;
            }

            await host.WriteSuccessAsync(id, "prompt", cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        public async Task FailAsync(string error, CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref _completed, -1, 0) != 0)
            {
                return;
            }

            await host.WriteErrorAsync(id, "prompt", error, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed record RpcPromptAttemptResult(bool IsSuccess, string? ErrorMessage, bool IsPreflightFailure = false)
    {
        public static RpcPromptAttemptResult Success() => new(true, null);
        public static RpcPromptAttemptResult Failed(string? errorMessage) => new(false, errorMessage);
        public static RpcPromptAttemptResult PreflightFailed(string? errorMessage) => new(false, errorMessage, true);
    }
}
