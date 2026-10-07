using System.Runtime.CompilerServices;
using Tau.AgentCore;
using Tau.AgentCore.Harness;
using Tau.AgentCore.Harness.Session;
using Tau.AgentCore.Runtime;
using Tau.Ai;
using Tau.Ai.Auth;
using Tau.Ai.Auth.OAuth;
using Tau.Ai.Observability;
using Tau.Ai.Providers;
using Tau.Ai.Registry;
using Tau.CodingAgent.Tools;

namespace Tau.CodingAgent.Runtime;

public sealed partial class RuntimeCodingAgentRunner : ICodingAgentRunner, ICodingAgentToolResultDetailsProvider, ICodingAgentExtensionRuntimeControl
{
    private readonly AgentRuntime _runtime;
    private readonly ModelCatalog _modelCatalog;

    /// <summary>【CodingAgent】【配置诊断】读取当前模型文件的错误，供扩展目录查询。</summary>
    internal string? ModelConfigurationError => _modelCatalog.ConfigurationStore.Error;
    private readonly ProviderAuthResolver _authResolver;
    private readonly ITauLogSink _logSink;
    private CodingAgentExtensionLifecycleEventSink? _extensionLifecycleEventSink;
    private readonly Dictionary<string, object?> _toolResultDetailsByToolCallId = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _refreshesGeneratedSystemPrompt;
    private string? _appendSystemPrompt;
    private readonly string _workingDirectory;
    private AgentLoopConfig _config;
    private IReadOnlyList<IAgentTool> _registeredTools;
    private readonly IReadOnlyList<IAgentTool> _baseRegisteredTools;
    private IReadOnlyList<CodingAgentSkill> _currentSkills = [];
    private string? _initialSystemPrompt;
    private CodingAgentSystemPromptOptions? _baseSystemPromptOptions;
    private IReadOnlyList<CodingAgentContextFile> _contextFiles;
    private int _activeCompactionCount;

    /// <summary>【CodingAgent】【会话运行】创建绑定独立工作目录的运行器</summary>
    /// <param name="runtime">Agent 运行时</param>
    /// <param name="config">Agent 循环配置</param>
    /// <param name="modelCatalog">可用模型目录</param>
    /// <param name="authResolver">认证解析器</param>
    /// <param name="logSink">日志接收器</param>
    /// <param name="logContext">日志关联信息</param>
    /// <param name="refreshesGeneratedSystemPrompt">是否允许重新生成系统提示词</param>
    /// <param name="contextFiles">项目上下文文件</param>
    /// <param name="extensionLifecycleEventSink">扩展生命周期事件接收器</param>
    /// <param name="appendSystemPrompt">追加的系统提示词</param>
    /// <param name="workingDirectory">会话目录；为空时捕获当前进程目录</param>
    /// <param name="systemPromptOptions">结构化提示构建选项；为空时保留旧式完整提示配置</param>
    /// <param name="initialActiveToolNames">显式初始活动工具；为空时采用工具默认策略或恢复历史。</param>
    public RuntimeCodingAgentRunner(
        AgentRuntime runtime,
        AgentLoopConfig config,
        ModelCatalog modelCatalog,
        ProviderAuthResolver? authResolver = null,
        ITauLogSink? logSink = null,
        TauRuntimeLogContext? logContext = null,
        bool refreshesGeneratedSystemPrompt = false,
        IReadOnlyList<CodingAgentContextFile>? contextFiles = null,
        CodingAgentExtensionLifecycleEventSink? extensionLifecycleEventSink = null,
        string? appendSystemPrompt = null,
        string? workingDirectory = null,
        CodingAgentSystemPromptOptions? systemPromptOptions = null,
        IReadOnlyList<string>? initialActiveToolNames = null)
    {
        _runtime = runtime;
        var effectiveLogSink = logSink ?? config.LogSink;
        _authResolver = authResolver ?? config.AuthResolver ?? modelCatalog.AuthResolver;
        _config = config with
        {
            ProviderRegistry = config.ProviderRegistry.CreateSessionCopy(),
            Tools = config.Tools.Where(IsToolActiveOnRegistration).ToArray(),
            ConfigurationStore = config.ConfigurationStore ?? modelCatalog.ConfigurationStore,
            AuthResolver = _authResolver,
            SystemPrompt = null,
            SystemPromptFromTranscript = true,
            LogSink = effectiveLogSink,
            LogContext = logContext ?? config.LogContext
        };
        _modelCatalog = modelCatalog;
        _registeredTools = config.Tools.Select(tool => tool is ReadFileTool read
            ? read.WithSessionContext(GetEffectiveImageModel, () => AutoResizeInputImages) : tool).ToArray();
        if (initialActiveToolNames is not null)
            _config = _config with { Tools = initialActiveToolNames.Select(name => CodingAgentToolNames.ResolveRegistered(name, _registeredTools)).Distinct(StringComparer.Ordinal).Select(name => _registeredTools.FirstOrDefault(tool => tool.Name == name))
                .OfType<IAgentTool>().Where(tool => GetToolExposure(tool) != "hidden").ToArray() };
        _baseRegisteredTools = _registeredTools.Where(tool => tool is not CodingAgentExtensionToolAdapter).ToArray();
        _logSink = effectiveLogSink;
        _extensionLifecycleEventSink = extensionLifecycleEventSink;
        _refreshesGeneratedSystemPrompt = refreshesGeneratedSystemPrompt;
        _contextFiles = contextFiles ?? [];
        _appendSystemPrompt = string.IsNullOrWhiteSpace(appendSystemPrompt) ? null : appendSystemPrompt;
        _workingDirectory = CodingAgentToolPaths.CaptureWorkingDirectory(workingDirectory);
        BindShellTools();
        _initialSystemPrompt = config.SystemPrompt;
        _baseSystemPromptOptions = systemPromptOptions is null ? null : CodingAgentSystemPrompt.Normalize(systemPromptOptions);
        if (_baseSystemPromptOptions is not null)
            _baseSystemPromptOptions = _baseSystemPromptOptions with { SelectedTools = _config.Tools.Select(tool => tool.Name).ToList() };
        if (_baseSystemPromptOptions is null && extensionLifecycleEventSink?.HasBeforeAgentStartHandlers == true)
            _baseSystemPromptOptions = CreateSystemPromptOptions(_config.Tools, [], _contextFiles, _workingDirectory, _appendSystemPrompt, config.SystemPrompt);
        if (Messages.Count > 0 && initialActiveToolNames is null) RestoreToolsFromTranscript(Messages);
        else _config = _config with { Tools = ApplyToolLoadout(GetActiveToolNames()) };
        InitializeTranscript();
        InitializeQueuedInputs();
        InstallToolImageNormalization();
    }

    public IReadOnlyList<ChatMessage> Messages => _runtime.State.Messages;
    public Model Model => _config.Model;
    public string? SessionName { get; set; }

    /// <summary>
    /// 转发给 opencode 系列提供方的会话标识，用于生成会话亲和归因请求头。
    /// </summary>
    public string? SessionId { get; set; }

    /// <summary>
    /// 是否启用安装遥测；用于控制 OpenRouter、NVIDIA、Cloudflare、Vercel 模型的产品归因请求头。
    /// </summary>
    public bool InstallTelemetryEnabled { get; set; } = true;
    public IReadOnlyDictionary<string, object?> ToolResultDetailsByToolCallId => _toolResultDetailsByToolCallId;
    public bool IsStreaming => !Volatile.Read(ref _isEmittingAgentSettled) && (Volatile.Read(ref _currentRunCancellation) is not null || _runtime.State.IsStreaming);
    public bool IsCompacting => Volatile.Read(ref _activeCompactionCount) > 0 || Volatile.Read(ref _compactionRequested) > 0;

    public AgentQueueMode SteeringMode
    {
        get => _runtime.SteeringMode;
        set => _runtime.SteeringMode = value;
    }

    public AgentQueueMode FollowUpMode
    {
        get => _runtime.FollowUpMode;
        set => _runtime.FollowUpMode = value;
    }

    public ThinkingLevel? ThinkingLevel
    {
        get => _config.StreamOptions?.Reasoning;
        set
        {
            var previous = CodingAgentThinkingLevels.Format(_config.StreamOptions?.Reasoning);
            _config = _config with { StreamOptions = (_config.StreamOptions ?? new SimpleStreamOptions()) with { Reasoning = value } };
            var current = CodingAgentThinkingLevels.Format(value);
            if (previous != current) QueueStateNotification(System.Text.Json.JsonSerializer.SerializeToElement(new
                { type = "thinking_level_select", level = current, previousLevel = previous }), new CodingAgentThinkingLevelChangedEvent(current));
        }
    }

    public IReadOnlyList<string> GetProviders() => _modelCatalog.GetProviders();

    /// <summary>【CodingAgent】【认证目录】合并内置、扩展及非聊天提供方，使登录不依赖模型发现成功。</summary>
    /// <returns>去重排序后的认证提供方。</returns>
    public IReadOnlyList<string> GetAuthProviders() => _modelCatalog.GetAllProviders().Concat(_modelCatalog.GetRegisteredProviderIds())
        .Concat(_authResolver.GetAuthenticationProviderIds()).Concat(BuiltInProviderNames.All.Keys)
        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();

    public IReadOnlyList<Model> GetModels(string provider) => _modelCatalog.GetModels(provider);

    public Model SelectModel(string? providerId, string? modelId) => SelectModelWithSourceAsync(providerId, modelId, "set").GetAwaiter().GetResult();

    /// <summary>【CodingAgent】【模型状态】更新模型配置，事件由调用方在适当的会话边界发布。</summary>
    /// <param name="providerId">提供方名称。</param><param name="modelId">模型名称。</param><returns>新模型。</returns>
    private Model SelectModelCore(string? providerId, string? modelId)
    {
        var selection = _modelCatalog.ResolveSelection(providerId, modelId, defaultProvider: _config.Model.Provider);
        var model = _modelCatalog.GetModel(selection.Provider, selection.ModelId);
        if (model.Provider != Model.Provider || model.Id != Model.Id) CancelCacheWarming();
        _config = _config with { Model = model };
        return model;
    }

    /// <summary>【CodingAgent】【扩展工具】读取当前会话允许使用的全部工具。</summary>
    /// <returns>独立的工具注册表快照。</returns>
    public IReadOnlyList<IAgentTool> GetRegisteredTools() { SynchronizeMcpTools(); return _registeredTools.ToArray(); }

    /// <summary>【CodingAgent】【扩展工具】读取下一回合将声明的工具。</summary>
    /// <returns>活动工具名称列表。</returns>
    public IReadOnlyList<string> GetActiveToolNames() { SynchronizeMcpTools(); return _config.Tools.Select(tool => tool.Name).ToArray(); }

    /// <summary>【CodingAgent】【扩展工具】更新活动工具并刷新自动生成的系统提示词，未知名称忽略。</summary>
    /// <param name="names">按所需顺序排列的工具名称。</param>
    public void SetActiveTools(IReadOnlyList<string> names) => SetActiveToolLoadout(names, null);

    /// <summary>【CodingAgent】【工具组合】接收宿主或扩展准备的组合，不在 Node 输出管道内再次等待 Node。</summary>
    /// <param name="names">所需工具名称。</param>
    /// <param name="prepared">Node 已计算的呈现变更，宿主调用时为空。</param>
    internal void SetActiveToolLoadout(IReadOnlyList<string> names, CodingAgentToolLoadoutChanges? prepared)
    {
        var previous = GetActiveToolNames();
        // 1. 【CodingAgent】【扩展工具】只允许启用本会话注册的工具，保持名称大小写及去重语义
        var tools = ApplyToolLoadout(names, prepared);
        _config = _config with { Tools = tools };
        if (previous.Except(GetActiveToolNames(), StringComparer.Ordinal).Any()) _pendingToolNames.Clear();
        _pendingToolNames.ExceptWith(GetActiveToolNames());
        if (_baseSystemPromptOptions is { } options)
            _baseSystemPromptOptions = options with { SelectedTools = GetActiveToolNames().ToList() };
        // 2. 【CodingAgent】【扩展工具】新声明将在请求准备阶段发布到历史，自定义提示词保持调用方控制
        if (_refreshesGeneratedSystemPrompt) RefreshSystemPromptResources(_currentSkills, _contextFiles);
    }

    /// <summary>【CodingAgent】【扩展上下文】读取当前生效的系统提示词。</summary>
    /// <returns>合并系统声明后的提示词。</returns>
    public string GetSystemPrompt() => _runSystemPromptOptions is { } options ? CodingAgentSystemPrompt.Build(options) : _runtime.State.SystemPrompt;

    /// <summary>【CodingAgent】【扩展取消】请求取消当前 Agent 运行。</summary>
    public void Abort()
    {
        lock (_backgroundGate)
        {
            _currentRunCancellation?.Cancel();
            _compactionCancellation?.Cancel();
        }
        _runtime.Abort();
    }

    /// <summary>【CodingAgent】【扩展等待】等待当前 Agent 运行结束。</summary>
    /// <param name="cancellationToken">取消等待的信号。</param>
    /// <returns>运行结束时完成的任务。</returns>
    public async Task WaitForIdleAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task? background;
            lock (_backgroundGate) background = _backgroundTask;
            if (background is not null) await background.WaitAsync(cancellationToken).ConfigureAwait(false);
            await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            _runGate.Release();
            lock (_backgroundGate)
                if (_backgroundTask is null && (_backgroundLifetime is null || _extensionDeliveries.IsEmpty)) return;
        }
    }

    /// <summary>【CodingAgent】【扩展认证】通过会话认证来源解析指定模型的密钥。</summary>
    /// <param name="model">需要认证的模型。</param>
    /// <returns>可用密钥，缺少认证时为空。</returns>
    public string? ResolveModelApiKey(Model model) => _authResolver.ResolveApiKey(model.Provider,
        model.Provider == Model.Provider ? _config.StreamOptions?.ApiKey : null, _config.StreamOptions?.Env);

    /// <summary>【CodingAgent】【认证状态】使用会话认证来源检查模型，当前模型同时考虑显式密钥。</summary>
    /// <param name="providerId">待检查的提供方；为空时使用当前模型。</param>
    /// <returns>不含密钥值的认证状态。</returns>
    public ProviderAuthStatus GetAuthStatus(string? providerId = null)
    {
        var provider = string.IsNullOrWhiteSpace(providerId) ? _config.Model.Provider : providerId;
        var model = provider.Equals(_config.Model.Provider, StringComparison.OrdinalIgnoreCase)
            ? _config.Model
            : _modelCatalog.GetModels(provider).FirstOrDefault();
        return _authResolver.GetStatus(provider, model,
            provider.Equals(_config.Model.Provider, StringComparison.OrdinalIgnoreCase) ? _config.StreamOptions?.ApiKey : null,
            _config.StreamOptions?.Env);
    }

    public IOAuthProvider? GetOAuthProvider(string providerId) =>
        _authResolver.GetOAuthProvider(providerId);

    /// <summary>【CodingAgent】【提供方名称】优先使用注册名称，其次使用内置名称和兼容 OAuth 名称。</summary>
    /// <param name="providerId">提供方标识。</param><returns>当前显示名称。</returns>
    public string GetProviderDisplayName(string providerId) => _modelCatalog.GetRegisteredProviderName(providerId)
        ?? CodingAgentProviderDisplayNames.TryGetBuiltIn(providerId) ?? GetOAuthProvider(providerId)?.Name ?? providerId;

    /// <summary>【CodingAgent】【凭据目录】读取持久凭据元数据，保留已经卸载的扩展条目。</summary>
    /// <returns>不含秘密的凭据列表。</returns>
    public IReadOnlyList<ProviderCredentialInfo> ListStoredCredentials() => _authResolver.ListStoredCredentials();

    /// <summary>【CodingAgent】【OAuth 保存】保存凭据并等待本提供方的离线目录同步。</summary>
    /// <param name="providerId">提供方。</param><param name="credentials">OAuth 凭据。</param>
    public void SaveOAuthCredentials(string providerId, OAuthCredentials credentials) =>
        SaveOAuthCredentialsAsync(providerId, credentials).GetAwaiter().GetResult();

    /// <summary>【CodingAgent】【密钥登录】优先读取扩展定义，未被原生扩展接管时回退内置密钥和云凭据定义。</summary>
    /// <param name="providerId">提供方。</param><returns>可用认证定义。</returns>
    public ApiKeyAuthDefinition? GetApiKeyProvider(string providerId) => _authResolver.GetApiKeyProvider(providerId)
        ?? (_authResolver.HasNativeAuthentication(providerId) ? null : BuiltInProviders.GetApiKeyAuth(providerId, _authResolver));
    /// <summary>保存原生 API key 登录结果。</summary><param name="providerId">提供方。</param><param name="credential">凭据。</param>
    public void SaveApiKeyCredential(string providerId, ApiKeyCredential credential) => SaveApiKeyCredentialAsync(providerId, credential).GetAwaiter().GetResult();

    /// <summary>【CodingAgent】【凭据登出】删除凭据并等待本提供方的离线目录同步。</summary>
    /// <param name="providerId">提供方。</param><returns>是否删除了已有凭据。</returns>
    public bool Logout(string providerId) =>
        LogoutAsync(providerId).GetAwaiter().GetResult();

    public bool RefreshSkills(IReadOnlyList<CodingAgentSkill> skills)
    {
        return RefreshSystemPromptResources(skills, _contextFiles);
    }

    /// <summary>【CodingAgent】【上下文刷新】刷新技能和项目指令，保留当前系统提示文件内容。</summary>
    /// <param name="skills">有效技能。</param><param name="contextFiles">上下文文件。</param><returns>是否完成刷新。</returns>
    public bool RefreshSystemPromptResources(IReadOnlyList<CodingAgentSkill> skills, IReadOnlyList<CodingAgentContextFile> contextFiles) =>
        RefreshSystemPromptResources(skills, contextFiles, null);

    /// <summary>【CodingAgent】【系统提示词】刷新技能和上下文文件，并保持会话工作目录</summary>
    /// <param name="skills">更新后的技能列表</param>
    /// <param name="contextFiles">更新后的上下文文件</param>
    /// <param name="promptFiles">可选的新提示文件快照，空值保留当前提示来源。</param>
    /// <returns>已更新提示词时为 true；使用固定提示词时为 false</returns>
    public bool RefreshSystemPromptResources(
        IReadOnlyList<CodingAgentSkill> skills,
        IReadOnlyList<CodingAgentContextFile> contextFiles,
        CodingAgentSystemPromptFilesSnapshot? promptFiles)
    {
        if (!_refreshesGeneratedSystemPrompt)
        {
            return false;
        }

        _contextFiles = contextFiles;
        _currentSkills = skills.ToArray();
        if (promptFiles is not null) _appendSystemPrompt = promptFiles.AppendSystemPrompt;
        _baseSystemPromptOptions = CreateSystemPromptOptions(_registeredTools, skills, _contextFiles, _workingDirectory,
            _appendSystemPrompt, promptFiles is null ? _baseSystemPromptOptions?.CustomPrompt : promptFiles.SystemPrompt) with { SelectedTools = GetActiveToolNames().ToList() };
        _initialSystemPrompt = CodingAgentSystemPrompt.Build(FilterHiddenToolSnippets(_baseSystemPromptOptions));
        if (_runSystemPromptOptions is { } running)
            _runSystemPromptOptions = running with
            {
                SelectedTools = _baseSystemPromptOptions.SelectedTools,
                ToolSnippets = _baseSystemPromptOptions.ToolSnippets,
                ToolGuidelines = _baseSystemPromptOptions.ToolGuidelines,
                ContextFiles = _baseSystemPromptOptions.ContextFiles,
                Skills = _baseSystemPromptOptions.Skills
            };
        ApplyPromptSections(FilterHiddenToolSnippets(_runSystemPromptOptions ?? _baseSystemPromptOptions));
        return true;
    }

    public CodingAgentSessionStats GetSessionStats(string? sessionFile = null)
    {
        if (_readSessionStatistics is not null) return _readSessionStatistics(sessionFile);
        var messages = Messages;
        var limits = GetEffectiveContextModel();
        return new CodingAgentSessionStats(
            _config.Model.Provider,
            _config.Model.Id,
            messages.Count,
            messages.OfType<UserMessage>().Count(),
            messages.OfType<AssistantMessage>().Count(),
            messages.OfType<ToolResultMessage>().Count(),
            messages.OfType<AssistantMessage>().Sum(message => message.Content.OfType<ToolCallContent>().Count()),
            CodingAgentTokenEstimator.Estimate(messages),
            limits.ContextWindow,
            SessionName,
            sessionFile)
            { SessionId = SessionId, CacheWaste = GetCacheWaste(), UsageBreakdown = CodingAgentUsageAccounting.FromMessages(messages),
                ContextUsage = limits.ContextWindow is > 0 ? new CodingAgentContextUsage(
                AgentCompaction.EstimateContextTokens(messages).Tokens, limits.ContextWindow.Value,
                100.0 * AgentCompaction.EstimateContextTokens(messages).Tokens / limits.ContextWindow.Value) : null }
            .WithUsage(CodingAgentSessionUsageSummary.FromMessages(messages));
    }

    /// <summary>【CodingAgent】【会话压缩】生成摘要，成功后以当前系统声明和摘要替换对话。</summary>
    /// <param name="customInstructions">摘要附加要求。</param>
    /// <param name="cancellationToken">取消令牌；取消或生成失败时保留原会话。</param>
    /// <returns>摘要及压缩前后的消息和 token 统计。</returns>
    private async Task<CodingAgentCompactionResult> CompactCoreAsync(
        string? customInstructions = null,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _activeCompactionCount);
        try
        {
            // 1. 【CodingAgent】【会话压缩】系统声明独立保存，不参与摘要有效性判断或摘要文本生成
            var originalMessages = Messages.ToArray();
            var conversation = originalMessages.Where(message => message is not SystemMessage).ToArray();
            var system = Transcript.GetCurrentSystemMessage(originalMessages);
            if (conversation.Length == 0)
            {
                throw new InvalidOperationException("Nothing to compact (session is empty)");
            }

            if (conversation.Length == 1 && IsCompactionSummaryMessage(conversation[0]))
            {
                throw new InvalidOperationException("Already compacted");
            }

            if (conversation.Length < 2)
            {
                throw new InvalidOperationException("Nothing to compact (session too small)");
            }

            cancellationToken.ThrowIfCancellationRequested();

            var tokensBefore = CodingAgentTokenEstimator.Estimate(originalMessages);
            var previousSummary = ExtractPreviousSummary(conversation);
            var messagesToSummarize = previousSummary is null
                ? conversation
                : conversation.Skip(1).ToArray();
            var summaryOptions = (await CreateSummaryGenerationOptionsAsync(
                customInstructions,
                replaceInstructions: false,
                Math.Min(_config.StreamOptions?.MaxTokens ?? 16_384, 4_096),
                cancellationToken).ConfigureAwait(false)) with { RetryCallbacks = CreateSummaryRetryCallbacks("compaction", "manual") };
            var summary = previousSummary is null
                ? await AgentCompactionSummaries
                    .GenerateSummaryAsync(messagesToSummarize, summaryOptions)
                    .ConfigureAwait(false)
                : await AgentCompactionSummaries
                    .GenerateUpdatedSummaryAsync(messagesToSummarize, previousSummary, summaryOptions)
                    .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(summary))
            {
                throw new InvalidOperationException("Compaction produced no text summary");
            }

            var fileDetails = AgentCompaction.ComputeFileLists(CollectFileOperations(messagesToSummarize));
            summary += AgentCompaction.FormatFileOperations(fileDetails.ReadFiles, fileDetails.ModifiedFiles);

            // 2. 【CodingAgent】【会话压缩】全部生成工作完成后再修改状态，取消不提交半成品
            cancellationToken.ThrowIfCancellationRequested();
            var messagesBefore = originalMessages.Length;
            ResetRuntimeState();
            if (system is not null) _runtime.AddMessage(system with { Timestamp = DateTimeOffset.UtcNow });
            _runtime.AddMessage(CodingAgentCompactionMessages.CreateSummaryMessage(summary));
            Interlocked.Increment(ref _historyRevision);

            return new CodingAgentCompactionResult(summary, messagesBefore, Messages.Count, tokensBefore);
        }
        finally
        {
            Interlocked.Decrement(ref _activeCompactionCount);
        }
    }

    /// <summary>【CodingAgent】【分支摘要】总结分支对话，系统声明独立于摘要保留。</summary>
    /// <param name="messages">待总结的分支消息。</param>
    /// <param name="customInstructions">摘要要求。</param>
    /// <param name="replaceInstructions">是否替换默认要求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>摘要、普通对话统计与文件操作信息。</returns>
    public async Task<CodingAgentBranchSummaryResult> SummarizeBranchAsync(
        IReadOnlyList<ChatMessage> messages,
        string? customInstructions = null,
        bool replaceInstructions = false,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _activeCompactionCount);
        try
        {
            var conversation = messages.Where(message => message is not SystemMessage).ToArray();
            if (conversation.Length == 0)
            {
                throw new InvalidOperationException("No branch content to summarize");
            }

            var tokensBefore = CodingAgentTokenEstimator.Estimate(conversation);
            var summaryUsage = new List<Usage>();
            var branchSummary = await AgentBranchSummaries.GenerateBranchSummaryAsync(
                ToSessionEntries(conversation),
                (await CreateSummaryGenerationOptionsAsync(
                    customInstructions,
                    replaceInstructions,
                    Math.Min(_config.StreamOptions?.MaxTokens ?? 16_384, 2_048),
                    cancellationToken).ConfigureAwait(false)) with
                    {
                        ReserveTokens = _sessionSettings?.Load().GetBranchSummaryReserveTokens(),
                        OnSummaryResponse = response => { if (response.Usage is { } usage) summaryUsage.Add(usage); }
                    }).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var summary = branchSummary.Summary;
            if (string.IsNullOrWhiteSpace(summary))
            {
                throw new InvalidOperationException("Branch summarization produced no text summary");
            }

            return new CodingAgentBranchSummaryResult(
                summary,
                conversation.Length,
                tokensBefore,
                branchSummary.ReadFiles,
                branchSummary.ModifiedFiles) { Usage = SerializeSummaryUsage(summaryUsage) };
        }
        finally
        {
            Interlocked.Decrement(ref _activeCompactionCount);
        }
    }

    /// <summary>【CodingAgent】【摘要请求】把当前会话的配置、认证和基础选项传递给压缩及分支摘要。</summary>
    /// <param name="customInstructions">自定义摘要要求。</param>
    /// <param name="replaceInstructions">是否替换默认要求。</param>
    /// <param name="maxTokens">摘要输出预算。</param>
    /// <param name="cancellationToken">摘要取消信号。</param>
    /// <returns>完整摘要请求配置。</returns>
    private async Task<AgentSummaryGenerationOptions> CreateSummaryGenerationOptionsAsync(
        string? customInstructions,
        bool replaceInstructions,
        int maxTokens,
        CancellationToken cancellationToken)
    {
        var model = _config.Model;
        var options = ResolveProviderRequestOptions(_config.StreamOptions);
        var reasoning = options.Reasoning;
        if (model.Api == "pi-virtual")
        {
            if (_virtualModelRuntime is null) throw new InvalidOperationException("Virtual model runtime is unavailable.");
            var route = await ResolveVirtualModelAsync(_virtualModelRuntime, model, Messages, reasoning, "direct", cancellationToken).ConfigureAwait(false);
            if (model.Provider != route.Model.Provider) options = options with { ApiKey = null, Headers = null, Env = null };
            model = route.Model;
            reasoning = route.Reasoning;
            if (model.MaxOutputTokens is > 0) maxTokens = Math.Min(maxTokens, model.MaxOutputTokens.Value);
        }
        return new()
        {
            ProviderRegistry = _config.ProviderRegistry,
            ConfigurationStore = _config.ConfigurationStore,
            AuthResolver = _authResolver,
            StreamOptions = options,
            Model = model,
            CustomInstructions = string.IsNullOrWhiteSpace(customInstructions) ? null : customInstructions.Trim(),
            ReplaceInstructions = replaceInstructions,
            MaxTokens = maxTokens,
            RetryPolicy = SummaryRetryOptions.ToPolicy(),
            RetryCallbacks = CreateSummaryRetryCallbacks("branchSummary"),
            ThinkingLevel = reasoning,
            CancellationToken = cancellationToken
        };
    }

    private static AgentFileOperations CollectFileOperations(IEnumerable<ChatMessage> messages)
    {
        var fileOperations = AgentCompaction.CreateFileOperations();
        foreach (var message in messages)
            AgentCompaction.ExtractFileOperationsFromMessage(message, fileOperations);

        return fileOperations;
    }

    private static IReadOnlyList<SessionTreeEntry> ToSessionEntries(IReadOnlyList<ChatMessage> messages)
    {
        var entries = new SessionTreeEntry[messages.Count];
        string? parentId = null;
        var timestamp = DateTimeOffset.UtcNow;
        for (var i = 0; i < messages.Count; i++)
        {
            var id = $"message-{i}";
            entries[i] = new MessageSessionEntry(id, parentId, timestamp, messages[i]);
            parentId = id;
        }

        return entries;
    }

    private static string? ExtractPreviousSummary(IReadOnlyList<ChatMessage> messages)
    {
        if (messages.Count == 0) return null;
        var first = messages[0];
        if (!CodingAgentCompactionMessages.IsSummaryMessage(first)) return null;

        var text = ((TextContent)((UserMessage)first).Content[0]).Text;
        var startIndex = text.IndexOf(CodingAgentCompactionMessages.SummaryPrefix, StringComparison.Ordinal);
        if (startIndex < 0) return null;

        var contentStart = startIndex + CodingAgentCompactionMessages.SummaryPrefix.Length;
        var endIndex = text.IndexOf(CodingAgentCompactionMessages.SummarySuffix, contentStart, StringComparison.Ordinal);
        if (endIndex < 0) return text[contentStart..].Trim();

        return text[contentStart..endIndex].Trim();
    }

    /// <summary>【CodingAgent】【会话重置】清除对话、队列和名称，保留当前有效的系统声明。</summary>
    public void ResetSession()
    {
        ClearBashContext();
        CancelCacheWarming();
        ClearExtensionDeliveries();
        var system = Transcript.GetCurrentSystemMessage(Messages);
        ResetRuntimeState();
        if (system is not null) _runtime.AddMessage(system);
        _toolResultDetailsByToolCallId.Clear();
        SessionName = null;
        Interlocked.Increment(ref _historyRevision);
    }

    public void Steer(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return;
        }

        QueueInput(new UserMessage(input), "steer");
    }

    public void Steer(IReadOnlyList<ContentBlock> input)
    {
        if (input.Count == 0)
        {
            return;
        }

        QueueInput(new UserMessage(input), "steer");
    }

    /// <summary>
    /// 将一条完整会话消息排入 steering 队列。
    /// </summary>
    /// <param name="input">需要在当前 assistant turn 后尽快注入的消息。</param>
    public void Steer(ChatMessage input)
    {
        ArgumentNullException.ThrowIfNull(input);
        QueueInput(input, "steer");
    }

    public void FollowUp(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return;
        }

        QueueInput(new UserMessage(input), "followUp");
    }

    public void FollowUp(IReadOnlyList<ContentBlock> input)
    {
        if (input.Count == 0)
        {
            return;
        }

        QueueInput(new UserMessage(input), "followUp");
    }

    /// <summary>
    /// 将一条完整会话消息排入 follow-up 队列。
    /// </summary>
    /// <param name="input">需要在当前 agent 自然停止后注入的消息。</param>
    public void FollowUp(ChatMessage input)
    {
        ArgumentNullException.ThrowIfNull(input);
        QueueInput(input, "followUp");
    }

    /// <summary>
    /// 向当前运行时会话追加一条已经由宿主生成的消息。
    /// </summary>
    /// <param name="message">需要追加到会话状态的消息。</param>
    public void AppendMessage(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _runtime.AddMessage(message);
    }

    /// <summary>
    /// 向扩展 lifecycle sink 发布一条宿主生成的 agent event。
    /// </summary>
    /// <param name="agentEvent">需要发布给扩展事件处理器的事件。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task PublishLifecycleEventAsync(
        AgentEvent agentEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agentEvent);
        if (_extensionLifecycleEventSink is null)
        {
            return;
        }

        try
        {
            var logContext = CreateRunLogContext();
            var extensionErrors = await _extensionLifecycleEventSink
                .PublishAsync(agentEvent, cancellationToken)
                .ConfigureAwait(false);
            foreach (var extensionError in extensionErrors)
            {
                LogExtensionEventError(extensionError, logContext);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logSink.Log(new TauLogEvent(
                "extension",
                "event.error",
                DateTimeOffset.UtcNow,
                new Dictionary<string, string?>
                {
                    ["eventType"] = agentEvent.Type,
                    ["scope"] = "runtime",
                    ["runtime"] = "unknown",
                    ["error"] = ex.Message
                }.WithLogContext(CreateRunLogContext())));
        }
    }

    /// <summary>【CodingAgent】【会话恢复】加载分支声明；旧会话缺少开场声明时补齐当前配置基线。</summary>
    /// <param name="snapshot">目标会话快照，输入集合不会被修改。</param>
    public void RestoreSession(CodingAgentSessionSnapshot snapshot) => RestoreSessionCore(snapshot, false);

    /// <summary>【CodingAgent】【压缩恢复】恢复摘要和保留尾部，保留扩展钩子中排队的输入。</summary>
    /// <param name="snapshot">压缩后的上下文快照。</param>
    internal void RestoreCompactedSession(CodingAgentSessionSnapshot snapshot) => RestoreSessionCore(snapshot, true);

    /// <summary>【CodingAgent】【实例恢复】替换会话时先恢复数据，模型通知留到新扩展实例完成初始化。</summary>
    /// <param name="snapshot">目标会话。</param>
    internal void RestoreReplacementSession(CodingAgentSessionSnapshot snapshot) => RestoreSessionCore(snapshot, false, true);

    /// <summary>【CodingAgent】【上下文恢复】按恢复类型替换消息并同步模型、名称及思考级别。</summary>
    /// <param name="snapshot">目标快照。</param>
    /// <param name="preserveQueues">是否保留压缩期间的待处理输入。</param>
    /// <param name="deferModelNotification">是否由替换事务向新扩展实例发布模型事件。</param>
    private void RestoreSessionCore(CodingAgentSessionSnapshot snapshot, bool preserveQueues, bool deferModelNotification = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!preserveQueues) ClearBashContext();
        CancelCacheWarming();
        var previousModel = Model;
        if (!preserveQueues) ClearExtensionDeliveries();
        var messages = snapshot.Messages.ToArray();
        var selection = CodingAgentBranchModelSelection.Resolve(snapshot, _modelCatalog);
        if (!string.IsNullOrWhiteSpace(selection.Provider) || !string.IsNullOrWhiteSpace(selection.Model))
        {
            SelectModelCore(selection.Provider, selection.Model);
        }

        if (!preserveQueues) ResetRuntimeState();
        _runtime.ReplaceMessages(messages);
        if (!preserveQueues) RestoreToolsFromTranscript(messages);
        _toolResultDetailsByToolCallId.Clear();
        InitializeTranscript();
        SessionName = snapshot.Name;
        if (snapshot.ThinkingLevel is not null)
            ThinkingLevel = CodingAgentThinkingLevels.ResolveStartup(Model, null, snapshot.ThinkingLevel, null);
        if (!preserveQueues && !deferModelNotification && (previousModel.Provider != Model.Provider || previousModel.Id != Model.Id))
            QueueStateNotification(CreateModelSelectionEvent(previousModel, "restore"));
        Interlocked.Increment(ref _historyRevision);
    }

    public IAsyncEnumerable<AgentEvent> RunAsync(string input, CancellationToken cancellationToken = default) =>
        RunAsync(input, logContext: null, cancellationToken);

    public IAsyncEnumerable<AgentEvent> RunAsync(
        string input,
        TauRuntimeLogContext? logContext,
        CancellationToken cancellationToken = default)
    {
        var initialMessage = new UserMessage(input);
        var runLogContext = CreateRunLogContext(logContext);
        return RunWithCommandsAsync(
            [initialMessage],
            inputBytes: input?.Length ?? 0,
            runLogContext,
            cancellationToken);
    }

    public IAsyncEnumerable<AgentEvent> RunAsync(
        IReadOnlyList<ContentBlock> input,
        CancellationToken cancellationToken = default) =>
        RunAsync(input, logContext: null, cancellationToken);

    public IAsyncEnumerable<AgentEvent> RunAsync(
        IReadOnlyList<ContentBlock> input,
        TauRuntimeLogContext? logContext,
        CancellationToken cancellationToken = default)
    {
        if (input.Count == 0)
        {
            throw new ArgumentException("Input content must not be empty.", nameof(input));
        }

        var initialMessage = new UserMessage(input);
        var sizeHint = input.OfType<TextContent>().Sum(static block => block.Text.Length);
        var runLogContext = CreateRunLogContext(logContext);
        return RunWithCommandsAsync(
            [initialMessage],
            inputBytes: sizeHint,
            runLogContext,
            cancellationToken);
    }

    public IAsyncEnumerable<AgentEvent> RunAsync(
        ChatMessage input,
        CancellationToken cancellationToken = default) =>
        RunAsync(input, logContext: null, cancellationToken);

    /// <summary>
    /// 用一条完整会话消息启动 agent turn。
    /// </summary>
    /// <param name="input">作为 prompt 写入会话状态的消息，可为 custom/user 等上层消息。</param>
    /// <param name="logContext">本轮运行日志上下文。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>agent 运行期间产生的事件流。</returns>
    public IAsyncEnumerable<AgentEvent> RunAsync(
        ChatMessage input,
        TauRuntimeLogContext? logContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var initialMessage = input;
        var runLogContext = CreateRunLogContext(logContext);
        return RunWithCommandsAsync(
            [initialMessage],
            inputBytes: EstimateInputBytes(input),
            runLogContext,
            cancellationToken);
    }

    public IAsyncEnumerable<AgentEvent> RunAsync(
        IReadOnlyList<ChatMessage> input,
        CancellationToken cancellationToken = default) =>
        RunAsync(input, logContext: null, cancellationToken);

    /// <summary>
    /// 用一批完整会话消息启动 agent turn。
    /// </summary>
    /// <param name="input">作为 prompt 写入会话状态的消息集合。</param>
    /// <param name="logContext">本轮运行日志上下文。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>agent 运行期间产生的事件流。</returns>
    public IAsyncEnumerable<AgentEvent> RunAsync(
        IReadOnlyList<ChatMessage> input,
        TauRuntimeLogContext? logContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Count == 0)
        {
            throw new ArgumentException("Input messages must not be empty.", nameof(input));
        }

        var runLogContext = CreateRunLogContext(logContext);
        return RunWithCommandsAsync(
            input.ToArray(),
            inputBytes: input.Sum(EstimateInputBytes),
            runLogContext,
            cancellationToken);
    }

    /// <summary>
    /// 为单次运行生成带产品归因和会话请求头的循环配置；每次运行都会重新计算，保证会话内切换模型后归因仍然正确。
    /// </summary>
    private AgentLoopConfig WithAttributionHeaders(TauRuntimeLogContext runLogContext)
    {
        var baseOptions = ResolveProviderRequestOptions(_config.StreamOptions);
        var sessionId = string.IsNullOrWhiteSpace(SessionId) ? baseOptions.SessionId : SessionId;
        var merged = CodingAgentProviderAttribution.MergeAttributionHeaders(
            _config.Model,
            InstallTelemetryEnabled,
            sessionId,
            AsReadOnly(baseOptions.Headers));

        var previousPrepare = _config.PrepareRequestAsync;
        var previousFinish = _config.FinishTurnAsync;
        if (previousFinish is null && _config.ShouldStopAfterTurnAsync is { } previousStop)
            previousFinish = async (turn, token) => await previousStop(turn, token).ConfigureAwait(false) ? AgentTurnDecision.End : null;
        var config = _config with
        {
            StreamFunction = CreateCacheWarmingStreamFunction(),
            LogContext = runLogContext,
            FinishTurnAsync = _boundarySession is null || _extensionLifecycleEventSink?.HasBoundaryHandlers("turn_end") != true
                ? _config.FinishTurnAsync : (turn, token) => FinishBoundaryTurnAsync(previousFinish, turn, runLogContext, token),
            PrepareRequestAsync = async (request, token) =>
            {
                // 1. 【CodingAgent】【MCP 外部登录】每次模型请求前发现其他进程保存的凭据，并同步重连后的工具目录
                if (_mcpService is { } mcp)
                {
                    await mcp.ReconnectSignedInServersAsync(token).ConfigureAwait(false);
                    SynchronizeMcpTools();
                }
                var prepared = await PrepareAgentRequestAsync(previousPrepare, request, runLogContext, token).ConfigureAwait(false);
                return await RouteAgentRequestAsync(prepared, request, prepared.StreamOptions!,
                    context => PrepareAgentRequestAsync(previousPrepare, request with { Context = context }, runLogContext, token), token).ConfigureAwait(false);
            }
        };
        // 1. 【CodingAgent】【嵌套落盘】内置脚本也产生子调用，关闭扩展时仍须把调用记录附加到父消息
        var previousMessageEndTransform = config.MessageEndTransformAsync;
        config = config with { MessageEndTransformAsync = (message, token) =>
            TransformLifecycleMessageEndAsync(previousMessageEndTransform, message, runLogContext, token) };
        if (_extensionLifecycleEventSink is not null)
        {
            var previousContextTransform = config.TransformContextAsync;
            var previousSyncContextTransform = config.TransformContext;
            config = config with
            {
                TransformContextAsync = async (messages, token) =>
                {
                    var current = previousContextTransform is null
                        ? previousSyncContextTransform?.Invoke(messages) ?? messages
                        : await previousContextTransform(messages, token).ConfigureAwait(false);
                    return _extensionLifecycleEventSink.TransformRequestContext(current,
                        error => LogExtensionEventError(error, runLogContext), token);
                }
            };
        }

        // 2. 【CodingAgent】【工具呈现】在扩展上下文变换之后过滤声明，保留历史中的完整工具集合
        var contextTransform = config.TransformContextAsync;
        var syncContextTransform = config.TransformContext;
        config = config with
        {
            TransformContextAsync = async (messages, token) => ProjectHiddenDeclarations(contextTransform is null
                ? syncContextTransform?.Invoke(messages) ?? messages
                : await contextTransform(messages, token).ConfigureAwait(false))
        };
        if (merged is null)
        {
            return config;
        }

        return config with
        {
            StreamOptions = baseOptions with { Headers = merged }
        };
    }

    /// <summary>【CodingAgent】【动态配置】读取当前选择并执行准备钩子；压缩后可重建上下文而不重复路由。</summary>
    /// <param name="prepare">原请求准备钩子。</param><param name="request">当前会话消息。</param>
    /// <param name="logContext">日志关联信息。</param><param name="token">请求取消信号。</param><returns>尚未虚拟路由的请求更新。</returns>
    private async Task<AgentRequestUpdate> PrepareAgentRequestAsync(
        Func<AgentPrepareRequestContext, CancellationToken, Task<AgentRequestUpdate?>>? prepare,
        AgentPrepareRequestContext request, TauRuntimeLogContext logContext, CancellationToken token)
    {
        // 1. 【CodingAgent】【准备快照】上一请求的实际模型不得覆盖用户仍然选择的虚拟模型
        request = request with { Model = _config.Model, ThinkingLevel = ThinkingLevel ?? Tau.Ai.ThinkingLevel.Off, Tools = _config.Tools };
        var update = prepare is null ? null : await prepare(request, token).ConfigureAwait(false);
        var options = update?.StreamOptions ?? (_config.StreamOptions ?? new SimpleStreamOptions()) with
        {
            Headers = CodingAgentProviderAttribution.MergeAttributionHeaders(update?.Model ?? _config.Model, InstallTelemetryEnabled,
                SessionId ?? _config.StreamOptions?.SessionId, AsReadOnly(_config.StreamOptions?.Headers))
        };
        options = ResolveProviderRequestOptions(options);
        if (update?.ClearReasoning == true) options = options with { Reasoning = null };
        if (update?.Reasoning is { } level) options = options with { Reasoning = level == Tau.Ai.ThinkingLevel.Off ? null : level };
        return (update ?? new AgentRequestUpdate()) with
        {
            Context = update?.Context ?? request.Context, Model = update?.Model ?? _config.Model, Tools = update?.Tools ?? _config.Tools,
            StreamOptions = _extensionLifecycleEventSink?.WrapRequestOptions(options with { Signal = token }, error => LogExtensionEventError(error, logContext)) ?? options
        };
    }

    /// <summary>
    /// 发布 message_end lifecycle event，并把扩展返回的替换消息接入 agent 状态写入路径。
    /// </summary>
    /// <param name="previousTransform">已有的消息结束转换钩子。</param>
    /// <param name="message">准备结束并写入会话状态的消息。</param>
    /// <param name="logContext">本轮运行日志上下文。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>扩展替换后的最终消息；扩展失败时返回原消息。</returns>
    private async Task<ChatMessage> TransformLifecycleMessageEndAsync(
        Func<ChatMessage, CancellationToken, Task<ChatMessage>>? previousTransform,
        ChatMessage message,
        TauRuntimeLogContext logContext,
        CancellationToken cancellationToken)
    {
        var currentMessage = previousTransform is null
            ? message
            : await previousTransform(message, cancellationToken).ConfigureAwait(false);
        currentMessage = AttachNestedToolRecord(currentMessage);
        if (_extensionLifecycleEventSink is null)
        {
            return currentMessage;
        }

        try
        {
            var result = await _extensionLifecycleEventSink
                .TransformMessageEndAsync(currentMessage, cancellationToken)
                .ConfigureAwait(false);
            foreach (var extensionError in result.Errors)
            {
                LogExtensionEventError(extensionError, logContext);
            }

            return result.Message;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logSink.Log(new TauLogEvent(
                "extension",
                "event.error",
                DateTimeOffset.UtcNow,
                new Dictionary<string, string?>
                {
                    ["eventType"] = "message_end",
                    ["scope"] = "runtime",
                    ["runtime"] = "unknown",
                    ["error"] = ex.Message
                }.WithLogContext(logContext)));
            return currentMessage;
        }
    }

    private static IReadOnlyDictionary<string, string>? AsReadOnly(IDictionary<string, string>? headers) =>
        headers is null
            ? null
            : headers as IReadOnlyDictionary<string, string>
                ?? new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);

    private TauRuntimeLogContext CreateRunLogContext(TauRuntimeLogContext? logContext = null)
    {
        var baseContext = _config.LogContext;
        var effectiveContext = logContext is null || baseContext is null
            ? logContext ?? baseContext
            : new TauRuntimeLogContext(
                FirstNonWhiteSpace(logContext.CorrelationId, baseContext.CorrelationId),
                FirstNonWhiteSpace(logContext.SessionId, baseContext.SessionId),
                FirstNonWhiteSpace(logContext.MessageId, baseContext.MessageId));
        return (effectiveContext ?? new TauRuntimeLogContext()).EnsureCorrelationId();
    }

    /// <summary>【CodingAgent】【队列文本】保留输入中的缩进、换行和首尾空白，不使用显示层的修剪规则。</summary>
    /// <param name="message">待恢复的完整消息。</param><returns>文本内容；纯图片或其他消息为空文本。</returns>
    private static string QueuedMessageToText(ChatMessage message) =>
        message switch
        {
            UserMessage user => string.Concat(user.Content.OfType<TextContent>().Select(block => block.Text)),
            AgentCustomMessage custom => string.Concat(custom.Content.OfType<TextContent>().Select(block => block.Text)),
            _ => string.Empty
        };

    private static string ContentBlocksToText(IReadOnlyList<ContentBlock> content) =>
        string.Join(Environment.NewLine, content.OfType<TextContent>().Select(static block => block.Text)).Trim();

    private static int EstimateInputBytes(ChatMessage message) => message switch
    {
        UserMessage user => EstimateContentBytes(user.Content),
        AgentCustomMessage custom => EstimateContentBytes(custom.Content),
        _ => 0
    };

    private static int EstimateContentBytes(IEnumerable<ContentBlock> content) =>
        content.OfType<TextContent>().Sum(static block => block.Text.Length);

    private static string? FirstNonWhiteSpace(string? primary, string? fallback) =>
        string.IsNullOrWhiteSpace(primary) ? fallback : primary;

    /// <summary>【CodingAgent】【事件编排】执行已取得运行锁的回合及扩展后续回合。</summary>
    /// <param name="inner">模型事件序列。</param>
    /// <param name="inputBytes">输入大小。</param>
    /// <param name="logContext">日志上下文。</param>
    /// <param name="cancellationToken">取消信号。</param>
    /// <returns>按发生顺序发布的事件。</returns>
    private async IAsyncEnumerable<AgentEvent> InstrumentRunCore(
        IAsyncEnumerable<AgentEvent> inner,
        int inputBytes,
        TauRuntimeLogContext logContext,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        _logSink.Log(new TauLogEvent(
            "agent",
            "run.start",
            startedAt,
            new Dictionary<string, string?>
            {
                ["provider"] = _config.Model.Provider,
                ["model"] = _config.Model.Id,
                ["inputBytes"] = inputBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }.WithLogContext(logContext)));

        var hadError = false;
        var hadCancel = false;
        await using var enumerator = inner.GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    hadCancel = true;
                    throw;
                }
                catch (Exception ex)
                {
                    hadError = true;
                    LogRunError(ex, logContext);
                    throw;
                }

                if (!hasNext) break;
                if (enumerator.Current is AgentEndEvent) FlushPendingBashMessages();
                if (enumerator.Current is MessageStartEvent { Message: UserMessage input })
                {
                    ConsumeQueuedInput(input);
                    await WaitForQueueNotificationsAsync().ConfigureAwait(false);
                }
                if (enumerator.Current is TurnStartEvent turnStart) _boundaryTurnIndex = turnStart.TurnIndex;
                var boundaryPublished = false;
                if (enumerator.Current is TurnEndEvent { Message: AssistantMessage assistant } turnEnd && _boundarySession is not null)
                {
                    boundaryPublished = true;
                    if (!_boundaryDispatchedMessages.Remove(assistant))
                        await DispatchTurnBoundaryAsync(assistant, turnEnd.ToolResults, logContext).ConfigureAwait(false);
                }
                if (enumerator.Current is ToolExecutionEndEvent toolEnd)
                {
                    CaptureToolResultDetails(toolEnd);
                }

                var alreadyPublished = _publishedExtensionMessageEvents.Remove(enumerator.Current) || enumerator.Current switch
                {
                    ToolExecutionStartEvent { ParentToolCallId: not null } => true,
                    ToolExecutionUpdateEvent { ParentToolCallId: not null } => true,
                    ToolExecutionEndEvent { ParentToolCallId: not null } => true,
                    _ => false
                };
                if (_extensionLifecycleEventSink is not null && enumerator.Current is not MessageEndEvent && !alreadyPublished && !boundaryPublished)
                {
                    try
                    {
                        var extensionErrors = await _extensionLifecycleEventSink
                            .PublishAsync(enumerator.Current, enumerator.Current is AgentEndEvent ? CancellationToken.None : cancellationToken)
                            .ConfigureAwait(false);
                        foreach (var extensionError in extensionErrors)
                        {
                            LogExtensionEventError(extensionError, logContext);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        hadCancel = true;
                        throw;
                    }
                    catch (Exception ex)
                    {
                        hadError = true;
                        LogRunError(ex, logContext);
                        throw;
                    }
                }

                if (!cancellationToken.IsCancellationRequested)
                {
                    _extensionRunMessages.AddRange(DrainExtensionDeliveries(enumerator.Current is not AgentEndEvent, cancellationToken,
                        completedTurn: enumerator.Current is AgentEndEvent));
                    if (enumerator.Current is TurnEndEvent or AgentEndEvent) FlushDeferredExtensionMessages(cancellationToken);
                }
                yield return enumerator.Current;
                while (_extensionMessageEvents.TryDequeue(out var appended))
                {
                    _publishedExtensionMessageEvents.Remove(appended);
                    yield return appended;
                }
            }

            // 1. 【CodingAgent】【结束投递】agent_end 处理器可以触发下一次运行，等待当前枚举真正结束后再启动
            if (!IsShutdownRequested && _extensionRunMessages.Count > 0)
            {
                var next = _extensionRunMessages.ToArray();
                _extensionRunMessages.Clear();
                var nextContext = CreateRunLogContext();
                await foreach (var evt in InstrumentRunCore(RunPreparedAsync(next, nextContext, cancellationToken, inputPrepared: true),
                    next.Sum(EstimateInputBytes), nextContext, cancellationToken).ConfigureAwait(false)) yield return evt;
            }
        }
        finally
        {
            if (!hadError)
            {
                var endedAt = DateTimeOffset.UtcNow;
                _logSink.Log(new TauLogEvent(
                    "agent",
                    hadCancel ? "run.cancel" : "run.end",
                    endedAt,
                    new Dictionary<string, string?>
                    {
                        ["provider"] = _config.Model.Provider,
                        ["model"] = _config.Model.Id,
                        ["elapsedMs"] = ((long)(endedAt - startedAt).TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    }.WithLogContext(logContext)));
            }
        }
    }

    private void LogExtensionEventError(
        CodingAgentExtensionLifecycleEventError error,
        TauRuntimeLogContext logContext)
    {
        _logSink.Log(new TauLogEvent(
            "extension",
            "event.error",
            DateTimeOffset.UtcNow,
            new Dictionary<string, string?>
            {
                ["provider"] = _config.Model.Provider,
                ["model"] = _config.Model.Id,
                ["eventType"] = error.EventType,
                ["path"] = error.FilePath,
                ["scope"] = error.Scope,
                ["runtime"] = error.Runtime,
                ["error"] = error.Error
            }.WithLogContext(logContext)));
    }

    private void LogRunError(Exception ex, TauRuntimeLogContext logContext)
    {
        _logSink.Log(new TauLogEvent(
            "agent",
            "run.error",
            DateTimeOffset.UtcNow,
            new Dictionary<string, string?>
            {
                ["provider"] = _config.Model.Provider,
                ["model"] = _config.Model.Id,
                ["error"] = ex.GetType().Name,
                ["message"] = ex.Message
            }.WithLogContext(logContext)));
    }

    private void CaptureToolResultDetails(ToolExecutionEndEvent toolEnd)
    {
        if (string.IsNullOrWhiteSpace(toolEnd.ToolCallId))
        {
            return;
        }

        if (toolEnd.Result.Details is null)
        {
            _toolResultDetailsByToolCallId.Remove(toolEnd.ToolCallId);
            return;
        }

        _toolResultDetailsByToolCallId[toolEnd.ToolCallId] = toolEnd.Result.Details;
    }

    public static RuntimeCodingAgentRunner CreateDefault()
    {
        return Create();
    }

    /// <summary>【CodingAgent】【会话运行】按模型、工具及会话目录组装运行器</summary>
    /// <param name="providerId">提供方标识</param>
    /// <param name="modelId">模型标识</param>
    /// <param name="initialMessages">初始消息</param>
    /// <param name="toolsOverride">覆盖的工具列表</param>
    /// <param name="systemPromptOverride">覆盖的系统提示词</param>
    /// <param name="skills">会话技能</param>
    /// <param name="contextFiles">上下文文件</param>
    /// <param name="logSink">日志接收器</param>
    /// <param name="logContext">日志关联信息</param>
    /// <param name="providerRegistryOverride">覆盖的提供方注册表</param>
    /// <param name="modelCatalogOverride">覆盖的模型目录</param>
    /// <param name="autoResizeImages">是否自动缩放图片</param>
    /// <param name="interceptors">工具拦截器</param>
    /// <param name="extensionLifecycleEventSink">扩展事件接收器</param>
    /// <param name="appendSystemPrompt">追加的系统提示词</param>
    /// <param name="apiKey">显式认证密钥</param>
    /// <param name="workingDirectory">会话工作目录</param>
    /// <param name="initialActiveToolNames">可选初始活动工具，目录中的其他工具仍可由扩展启用。</param>
    /// <returns>已配置的运行器</returns>
    public static RuntimeCodingAgentRunner Create(
        string? providerId = null,
        string? modelId = null,
        IReadOnlyList<ChatMessage>? initialMessages = null,
        IReadOnlyList<IAgentTool>? toolsOverride = null,
        string? systemPromptOverride = null,
        IReadOnlyList<CodingAgentSkill>? skills = null,
        IReadOnlyList<CodingAgentContextFile>? contextFiles = null,
        ITauLogSink? logSink = null,
        TauRuntimeLogContext? logContext = null,
        ProviderRegistry? providerRegistryOverride = null,
        ModelCatalog? modelCatalogOverride = null,
        bool autoResizeImages = true,
        IReadOnlyList<IToolInterceptor>? interceptors = null,
        CodingAgentExtensionLifecycleEventSink? extensionLifecycleEventSink = null,
        string? appendSystemPrompt = null,
        string? apiKey = null,
        string? workingDirectory = null,
        IReadOnlyList<string>? initialActiveToolNames = null)
    {
        // 1. 【CodingAgent】【工具目录】统一捕获目录，工具和提示词共用同一会话上下文
        var cwd = CodingAgentToolPaths.CaptureWorkingDirectory(workingDirectory);
        var modelCatalog = modelCatalogOverride ?? new ModelCatalog();
        var registry = providerRegistryOverride ?? new ProviderRegistry();
        if (providerRegistryOverride is null)
        {
            BuiltInProviders.RegisterAll(registry, modelCatalog.ConfigurationStore);
        }

        var selection = modelCatalog.ResolveSelection(
            providerId,
            modelId,
            defaultProvider: Environment.GetEnvironmentVariable("TAU_PROVIDER"));

        var model = modelCatalog.GetModel(selection.Provider, selection.ModelId);
        var tools = toolsOverride ?? CreateDefaultTools(autoResizeImages, workingDirectory: cwd);
        var promptOptions = CreateSystemPromptOptions(tools, skills ?? [], contextFiles ?? [], cwd, appendSystemPrompt, systemPromptOverride);
        var config = new AgentLoopConfig
        {
            Model = model,
            ProviderRegistry = registry,
            Tools = tools,
            Interceptors = interceptors ?? [],
            LogSink = logSink ?? NullTauLogSink.Instance,
            LogContext = logContext,
            SystemPrompt = CodingAgentSystemPrompt.Build(promptOptions),
            ConvertToLlm = AgentHarnessMessages.ConvertToLlm,
            StreamOptions = new SimpleStreamOptions
            {
                ApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey
            }
        };

        var runtime = new AgentRuntime();
        if (initialMessages is not null)
        {
            foreach (var message in initialMessages)
            {
                runtime.AddMessage(message);
            }
        }

        return new RuntimeCodingAgentRunner(
            runtime,
            config,
            modelCatalog,
            logSink: logSink,
            logContext: logContext,
            refreshesGeneratedSystemPrompt: true,
            contextFiles: contextFiles ?? [],
            extensionLifecycleEventSink: extensionLifecycleEventSink,
            appendSystemPrompt: appendSystemPrompt,
            workingDirectory: cwd,
            systemPromptOptions: promptOptions,
            initialActiveToolNames: initialActiveToolNames ?? (toolsOverride is null ? CodingAgentToolSelection.DefaultNames : null)) { _currentSkills = skills ?? [], _autoResizeImages = autoResizeImages };
    }

    public static string GetDefaultProviderId() => ModelCatalog.GetDefaultProviderId();

    public static string GetDefaultModelId(string providerId)
    {
        return ModelCatalog.GetDefaultModelId(providerId);
    }

    /// <summary>【CodingAgent】【工具目录】创建绑定会话目录的内置工具并合并扩展工具</summary>
    /// <param name="autoResizeImages">是否自动缩放图片</param>
    /// <param name="extensionTools">待合并的扩展工具</param>
    /// <param name="selectedBuiltInToolNames">启用的内置工具名称；为空时启用全部</param>
    /// <param name="workingDirectory">会话工作目录</param>
    /// <returns>可供 Agent 调用的工具数组</returns>
    public static IAgentTool[] CreateDefaultTools(
        bool autoResizeImages = true,
        IReadOnlyList<IAgentTool>? extensionTools = null,
        IReadOnlyList<string>? selectedBuiltInToolNames = null,
        string? workingDirectory = null)
    {
        var cwd = CodingAgentToolPaths.CaptureWorkingDirectory(workingDirectory);
        IAgentTool[] allBuiltInTools =
        [
            new ReadFileTool(autoResizeImages, cwd),
            new WriteFileTool(cwd),
            new EditFileTool(cwd),
            new ShellTool(cwd),
            new PowerShellTool(cwd),
            new GlobTool(cwd),
            new GrepTool(cwd),
            new ListDirectoryTool(cwd)
        ];

        // 1. 【CodingAgent】【工具目录】空值返回完整内置目录，宿主使用独立的工具选择策略确定初始活动集合
        var selectedNames = CodingAgentToolSelection.ResolveNames(selectedBuiltInToolNames);
        IAgentTool[] builtInTools = selectedNames is null
            ? allBuiltInTools
            : allBuiltInTools
                .Where(tool => selectedNames.Contains(tool.Name, StringComparer.Ordinal))
                .ToArray();

        if (extensionTools is not { Count: > 0 })
        {
            return builtInTools;
        }

        var toolsByName = new Dictionary<string, IAgentTool>(StringComparer.Ordinal);
        foreach (var tool in builtInTools)
        {
            toolsByName[tool.Name] = tool;
        }

        foreach (var tool in extensionTools)
        {
            if (!string.IsNullOrWhiteSpace(tool.Name))
            {
                toolsByName[tool.Name] = tool;
            }
        }

        return toolsByName.Values.ToArray();
    }

    /// <summary>
    /// Appends <paramref name="appendSystemPrompt"/> to <paramref name="basePrompt"/> separated by a
    /// blank line, mirroring upstream <c>buildSystemPrompt</c>'s <c>appendSection</c> (<c>\n\n</c>).
    /// </summary>
    internal static string AppendToSystemPrompt(string basePrompt, string? appendSystemPrompt)
    {
        return string.IsNullOrWhiteSpace(appendSystemPrompt)
            ? basePrompt
            : basePrompt + "\n\n" + appendSystemPrompt;
    }

    private static bool IsCompactionSummaryMessage(ChatMessage message)
    {
        return CodingAgentCompactionMessages.IsSummaryMessage(message);
    }
}

file static class RuntimeLogFieldExtensions
{
    public static Dictionary<string, string?> WithLogContext(
        this Dictionary<string, string?> fields,
        TauRuntimeLogContext logContext)
    {
        logContext.AddTo(fields);
        return fields;
    }
}
