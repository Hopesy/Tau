using Tau.AgentCore;
using Tau.AgentCore.Runtime;
using Tau.Ai;
using Tau.Ai.Auth;
using Tau.Ai.Auth.OAuth;
using Tau.Ai.Observability;
using Tau.Ai.Providers;
using Tau.Ai.Registry;
using Tau.CodingAgent.Runtime.Mcp;

namespace Tau.CodingAgent.Runtime;

public enum CodingAgentSdkNoToolsMode
{
    None,
    All,
    BuiltIn
}

public sealed record CodingAgentSdkScopedModel(Model Model, string? ThinkingLevel = null);

public sealed class CodingAgentSdkCreateSessionOptions
{
    public string? Cwd { get; init; }
    public string? AgentDirectory { get; init; }
    public string? SettingsPath { get; init; }
    public bool ResolveProjectTrust { get; init; }
    public bool? ProjectTrusted { get; init; }
    public Func<string, IReadOnlyList<string>, CancellationToken, Task<string?>>? ProjectTrustSelector { get; init; }
    public Action<string>? ProjectTrustError { get; init; }
    public Func<string, string?, CancellationToken, Task<string?>>? ProjectTrustInput { get; init; }
    public Action<string>? ProjectTrustNotify { get; init; }
    public string? SessionPath { get; init; }
    public bool NoSession { get; init; }
    public bool ContinueSession { get; init; }
    public string? ProviderId { get; init; }
    public string? ModelId { get; init; }
    public ThinkingLevel? ThinkingLevel { get; init; }
    public IReadOnlyList<CodingAgentSdkScopedModel>? ScopedModels { get; init; }
    public CodingAgentSdkNoToolsMode NoTools { get; init; }
    public IReadOnlyList<string>? Tools { get; init; }
    public IReadOnlyList<string>? ExcludeTools { get; init; }
    public IReadOnlyList<IAgentTool>? CustomTools { get; init; }
    public bool IncludeExtensions { get; init; } = true;
    /// <summary>【CodingAgent】【SDK MCP】显式启用文件及扩展注册的 MCP 服务器，默认不启动外部服务。</summary>
    public bool EnableMcp { get; init; }
    public CodingAgentMcpServiceOptions? McpOptions { get; init; }
    /// <summary>【CodingAgent】【SDK 工具搜索】注册内置工具搜索，MCP 启用时也会注册，是否声明由活动工具选择控制。</summary>
    public bool EnableToolSearch { get; init; }
    /// <summary>【CodingAgent】【SDK 脚本工具】注册 codemode，MCP 启用时也会注册，活动状态遵守工具选择及 MCP 配置。</summary>
    public bool EnableCodeMode { get; init; }
    public CodingAgentCodeModeOptions? CodeModeOptions { get; init; }
    /// <summary>【CodingAgent】【SDK 预热】可注入缓存预热时钟，用于可重复的离线调度验证。</summary>
    public TimeProvider? CacheWarmingTimeProvider { get; init; }
    public bool IncludeSkills { get; init; } = true;
    public bool IncludePromptTemplates { get; init; } = true;
    public bool IncludeContextFiles { get; init; } = true;
    public bool AutoResizeImages { get; init; } = true;
    public string? SystemPrompt { get; init; }
    public string? AppendSystemPrompt { get; init; }
    public string? ApiKey { get; init; }
    public ProviderRegistry? ProviderRegistry { get; init; }
    public ModelCatalog? ModelCatalog { get; init; }
    public ITauLogSink? LogSink { get; init; }
    public TauRuntimeLogContext? LogContext { get; init; }
}

public sealed class CodingAgentSdkSession : IDisposable, IAsyncDisposable
{
    internal CodingAgentSdkSession(
        RuntimeCodingAgentRunner runner,
        CodingAgentSettingsStore settingsStore,
        CodingAgentSessionStore? sessionStore,
        CodingAgentTreeSessionController? treeSessionController,
        CodingAgentPackageResourceState packageResourceState,
        CodingAgentExtensionCommandStore extensionCommandStore,
        CodingAgentExtensionStatus extensionStatus,
        CodingAgentPromptTemplateStore promptTemplateStore,
        CodingAgentSkillStore skillStore,
        CodingAgentContextFileStore contextFileStore,
        string? modelFallbackMessage)
    {
        Runner = runner;
        SettingsStore = settingsStore;
        SessionStore = sessionStore;
        _initialTreeSessionController = treeSessionController;
        PackageResourceState = packageResourceState;
        ExtensionCommandStore = extensionCommandStore;
        ExtensionStatus = extensionStatus;
        PromptTemplateStore = promptTemplateStore;
        SkillStore = skillStore;
        ContextFileStore = contextFileStore;
        ModelFallbackMessage = modelFallbackMessage;
        Runner.StartBackgroundDelivery();
    }

    public RuntimeCodingAgentRunner Runner { get; }
    public CodingAgentSettingsStore SettingsStore { get; }
    public CodingAgentSessionStore? SessionStore { get; }
    private readonly CodingAgentTreeSessionController? _initialTreeSessionController;
    public CodingAgentTreeSessionController? TreeSessionController => ExtensionCommandStore.CurrentTreeSessionController ?? _initialTreeSessionController;
    public CodingAgentPackageResourceState PackageResourceState { get; }
    public CodingAgentExtensionCommandStore ExtensionCommandStore { get; }
    public CodingAgentExtensionStatus ExtensionStatus { get; }
    public CodingAgentPromptTemplateStore PromptTemplateStore { get; }
    public CodingAgentSkillStore SkillStore { get; }
    public CodingAgentContextFileStore ContextFileStore { get; }
    public string? ModelFallbackMessage { get; }
    public CodingAgentMcpService? Mcp { get; init; }
    /// <summary>【CodingAgent】【扩展诊断】创建会话时的扩展启动错误，供 SDK 调用方展示或记录。</summary>
    public IReadOnlyList<CodingAgentExtensionLifecycleEventError> StartupExtensionErrors { get; init; } = [];

    public IReadOnlyList<ChatMessage> Messages => Runner.Messages;

    public IAsyncEnumerable<AgentEvent> RunAsync(string input, CancellationToken cancellationToken = default) =>
        Runner.RunAsync(input, cancellationToken);

    public IAsyncEnumerable<AgentEvent> RunAsync(
        IReadOnlyList<ContentBlock> input,
        CancellationToken cancellationToken = default) =>
        Runner.RunAsync(input, cancellationToken);

    /// <summary>【CodingAgent】【SDK保存】保存消息、模型、名称及思考等级，禁用会话时不写入文件。</summary>
    public void Save()
    {
        SessionStore?.Save(Runner.Messages, Runner.Model, Runner.SessionName, CodingAgentThinkingLevels.Format(Runner.ThinkingLevel));
        TreeSessionController?.SyncFromRunner(Runner);
    }

    /// <summary>【CodingAgent】【上下文编辑】修改模型上下文并同步运行器，原始会话条目保持不变。</summary>
    /// <param name="targetId">当前分支的消息条目标识。</param>
    /// <param name="replacement">包含 content 的替换对象；空值省略目标。</param>
    /// <returns>追加的上下文编辑条目标识。</returns>
    public string EditContext(string targetId, System.Text.Json.JsonElement? replacement)
    {
        if (Runner.IsStreaming || Runner.IsCompacting) throw new InvalidOperationException("Context can only be edited while the session is idle.");
        var tree = TreeSessionController ?? throw new InvalidOperationException("Context edits require a JSONL session.");
        lock (tree.Store.SyncRoot)
        {
            // 1. 【CodingAgent】【编辑保存】先提交已有消息，再追加编辑记录
            tree.SyncFromRunner(Runner);
            var id = tree.Store.AppendContextEdit(targetId, replacement);
            // 2. 【CodingAgent】【编辑应用】恢复投影并更新保存游标，避免把旧内容再次追加回历史
            Runner.RestoreSession(tree.LoadSnapshot().ToFlatSnapshot());
            Save();
            return id;
        }
    }

    /// <summary>【CodingAgent】【SDK会话】关闭会话持有的扩展运行时及其 Node 进程</summary>
    public void Dispose()
    {
        Runner.Abort();
        Runner.StopBackgroundDeliveryAsync().GetAwaiter().GetResult();
        Runner.WaitForIdleAsync(CancellationToken.None).GetAwaiter().GetResult();
        Runner.StopStateNotificationsAsync().GetAwaiter().GetResult();
        Mcp?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        ExtensionCommandStore.Dispose();
    }

    /// <summary>【CodingAgent】【SDK关闭】等待后台模型及事件回调退出，再释放扩展进程。</summary>
    /// <returns>会话关闭完成的任务。</returns>
    public async ValueTask DisposeAsync()
    {
        Runner.Abort();
        await Runner.StopBackgroundDeliveryAsync().ConfigureAwait(false);
        await Runner.WaitForIdleAsync(CancellationToken.None).ConfigureAwait(false);
        await Runner.StopStateNotificationsAsync().ConfigureAwait(false);
        if (Mcp is not null) await Mcp.DisposeAsync().ConfigureAwait(false);
        ExtensionCommandStore.Dispose();
    }
}

public static class CodingAgentSdk
{
    /// <summary>【CodingAgent】【SDK会话】按独立工作目录加载资源并创建会话</summary>
    /// <param name="options">会话创建选项；为空时使用默认值</param>
    /// <param name="cancellationToken">取消信号</param>
    /// <returns>已创建的会话；调用方应使用 using 或 Dispose 释放扩展进程</returns>
    public static async Task<CodingAgentSdkSession> CreateSessionAsync(
        CodingAgentSdkCreateSessionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new CodingAgentSdkCreateSessionOptions();
        // 1. 【CodingAgent】【SDK 参数校验】在迁移文件和启动外部服务前拒绝无效的脚本选项
        if (options.EnableCodeMode || options.EnableMcp) options.CodeModeOptions?.Validate();

        var cwd = ResolveDirectory(options.Cwd, Environment.CurrentDirectory);
        var agentDir = ResolveDirectory(options.AgentDirectory, GetDefaultAgentDirectory());
        CodingAgentMigrations.Run(new CodingAgentMigrationOptions(
            AgentDirectory: agentDir,
            Cwd: cwd,
            AuthPath: Path.Combine(agentDir, "auth.json"),
            KeybindingsPath: Path.Combine(agentDir, "coding-agent-keybindings.json"),
            BinDirectory: Path.Combine(agentDir, "bin")));

        var settingsPath = ResolvePath(options.SettingsPath, Path.Combine(cwd, ".tau", "coding-agent-settings.json"));
        var resolveTrust = options.ResolveProjectTrust || options.ProjectTrusted is not null || options.ProjectTrustSelector is not null;
        var settingsStore = CodingAgentSettingsStore.CreateLayered(Path.Combine(agentDir, "coding-agent-settings.json"), settingsPath, projectTrusted: !resolveTrust);
        var settings = settingsStore.Load();

        // 1. 【CodingAgent】【SDK会话】默认每次创建独立文件，继续会话限定到独立 Agent 目录和 cwd
        var sessionTarget = CodingAgentSessionTarget.Resolve(
            options.SessionPath, options.ContinueSession,
            sessionDirectory: CodingAgentSessionTarget.GetDefaultSessionDirectory(cwd, agentDir),
            noSession: options.NoSession, workingDirectory: cwd, agentDirectory: agentDir);
        var treeSessionController = sessionTarget.TreeSessionController;
        // 2. 【CodingAgent】【SDK会话】保留 SDK 的平面快照接口，副本跟随独立 JSONL 路径而不复用旧文件
        var sessionStore = sessionTarget.SessionStore ?? (treeSessionController is null
            ? null : new CodingAgentSessionStore(Path.ChangeExtension(treeSessionController.Path, ".json")));
        var sessionSnapshot = sessionTarget.LoadInitialSnapshot();

        var packageManager = new CodingAgentPackageManager(
            cwd,
            userSettingsPath: Path.Combine(agentDir, "coding-agent-settings.json"),
            projectSettingsPath: settingsPath) { IsProjectTrusted = !resolveTrust };
        var packageResourceState = new CodingAgentPackageResourceState(packageManager.ResolveResources());
        var extensionCommandStore = new CodingAgentExtensionCommandStore(
            cwd,
            userExtensionsDirectory: Path.Combine(agentDir, "extensions"),
            additionalPathsProvider: () => packageResourceState.ExtensionPaths,
            includeDefaults: options.IncludeExtensions,
            sourceInfosProvider: () => packageResourceState.SourceInfos)
        {
            IsProjectTrusted = !resolveTrust,
            AutoResourceFilterProvider = () => packageManager.CreateAutomaticResourceFilter("extensions"),
            RefreshResourcePaths = () => packageResourceState.Update(packageManager.ResolveResources())
        };
        CodingAgentMcpService? mcp = null;
        try
        {
            var modelCatalog = options.ModelCatalog?.CreateSessionCopy() ?? CreateModelCatalog(cwd, agentDir, options);
            extensionCommandStore.ConfigureModelCatalog(modelCatalog, new FileModelsStore(Path.Combine(agentDir, "models-store.json")));
            extensionCommandStore.SetExtensionUiBridge(null, "print");
            if (resolveTrust)
            {
                await CodingAgentProjectTrustBootstrap.ResolveAsync(cwd, agentDir, settingsStore, packageManager, packageResourceState,
                    extensionCommandStore, options.ProjectTrusted, options.ProjectTrustSelector, options.ProjectTrustError, cancellationToken,
                    options.ProjectTrustInput, options.ProjectTrustNotify).ConfigureAwait(false);
                settings = settingsStore.Load();
            }
            var extensionStatus = extensionCommandStore.LoadStatus();
            await extensionCommandStore.InitializeProviderModelsAsync(cancellationToken).ConfigureAwait(false);
            var extensionResources = new CodingAgentExtensionResourceState(extensionStatus.Resources);
            var promptFiles = new CodingAgentSystemPromptFiles(cwd, agentDir, () => settingsStore.IsProjectTrusted,
                options.SystemPrompt, options.AppendSystemPrompt is null ? null : [options.AppendSystemPrompt]);
            var loadedPromptFiles = promptFiles.Load();

            var promptTemplateStore = new CodingAgentPromptTemplateStore(
                cwd,
                userPromptsDirectory: Path.Combine(agentDir, "prompts"),
                additionalPathsProvider: () => CombineResourcePaths(packageResourceState.PromptPaths, extensionResources.PromptPaths),
                includeDefaults: options.IncludePromptTemplates) { IsProjectTrusted = settingsStore.IsProjectTrusted };
            var skillStore = new CodingAgentSkillStore(
                cwd,
                userSkillsDirectory: Path.Combine(agentDir, "skills"),
                additionalPathsProvider: () => CombineResourcePaths(packageResourceState.SkillPaths, extensionResources.SkillPaths),
                includeDefaults: options.IncludeSkills) { IsProjectTrusted = settingsStore.IsProjectTrusted };
            var contextFileStore = new CodingAgentContextFileStore(
                cwd,
                userContextDirectory: agentDir,
                includeDefaults: options.IncludeContextFiles);
            promptTemplateStore.SourceInfosProvider = () => CodingAgentSourceInfo.Combine(packageResourceState.SourceInfos, extensionResources.SourceInfos);
            skillStore.SourceInfosProvider = () => CodingAgentSourceInfo.Combine(packageResourceState.SourceInfos, extensionResources.SourceInfos);
            promptTemplateStore.AutoResourceFilterProvider = () => packageManager.CreateAutomaticResourceFilter("prompts");
            skillStore.AutoResourceFilterProvider = () => packageManager.CreateAutomaticResourceFilter("skills");

            // 1. 【CodingAgent】【会话配置】模型目录、请求选项和认证共用会话目录，不修改进程全局环境
            var (providerId, modelId, modelFallbackMessage) = ResolveModelSelection(options, settings, sessionSnapshot, modelCatalog);
            var allTools = RuntimeCodingAgentRunner.CreateDefaultTools(options.AutoResizeImages,
                MergeTools(extensionCommandStore.LoadTools(), options.CustomTools), workingDirectory: cwd);
            var selection = CodingAgentToolSelection.Create(allTools, options.Tools, options.ExcludeTools, settings.DefaultTools, options.NoTools);

            var runner = RuntimeCodingAgentRunner.Create(
                providerId,
                modelId,
                sessionSnapshot.Messages,
                selection.Registered,
                loadedPromptFiles.SystemPrompt,
                skillStore.Load(),
                contextFileStore.Load(),
                options.LogSink,
                options.LogContext,
                options.ProviderRegistry,
                modelCatalog,
                options.AutoResizeImages,
                extensionCommandStore.LoadToolInterceptors(),
                extensionCommandStore.LoadLifecycleEventSink(),
                loadedPromptFiles.AppendSystemPrompt,
                options.ApiKey,
                workingDirectory: cwd, initialActiveToolNames: options.Tools is null && options.NoTools == CodingAgentSdkNoToolsMode.None &&
                    sessionSnapshot.Messages.Any(message => message is SystemMessage) ? null : selection.Active);
            runner.SessionName = sessionSnapshot.Name;
            runner.ConfigureSessionSettings(settingsStore);
            runner.ConfigureCacheWarmingClock(options.CacheWarmingTimeProvider);
            runner.SetScopedModels(options.ScopedModels);
            runner.ConfigureInputResources(skillStore, promptTemplateStore);
            runner.ConfigureResourceReload(() => runner.RefreshSystemPromptResources(skillStore.Load(), contextFileStore.Load(), promptFiles.Load()));
            extensionCommandStore.ResourcesChanged += resources =>
            {
                extensionResources.Update(resources);
                runner.RefreshSystemPromptResources(skillStore.Load(), contextFileStore.Load(), promptFiles.Load());
            };
            runner.ConfigureToolSelection(selection);
            runner.SteeringMode = CodingAgentQueueModes.ToAgentQueueMode(settings.SteeringMode);
            runner.FollowUpMode = CodingAgentQueueModes.ToAgentQueueMode(settings.FollowUpMode);
            runner.ThinkingLevel = CodingAgentThinkingLevels.ResolveStartup(runner.Model,
                CodingAgentThinkingLevels.FormatRaw(options.ThinkingLevel), sessionSnapshot.ThinkingLevel, settings.DefaultThinkingLevel);
            runner.InstallTelemetryEnabled = CodingAgentTelemetry.IsInstallTelemetryEnabled(
                settings,
                Environment.GetEnvironmentVariable("PI_TELEMETRY"));
            extensionCommandStore.BindSession(runner, treeSessionController, sessionStore);
            extensionCommandStore.SetExtensionUiBridge(null, "print");
            var startupErrors = await extensionCommandStore.EnsureSessionStartedAsync(cancellationToken).ConfigureAwait(false);
            extensionStatus = extensionCommandStore.LoadStatus();
            if (options.EnableMcp)
            {
                var mcpOptions = options.McpOptions ?? new();
                mcp = new(agentDir, cwd, () => settingsStore.IsProjectTrusted, extensionCommandStore.McpServers,
                    mcpOptions with { ProviderToken = mcpOptions.ProviderToken ?? runner.ResolveMcpProviderTokenAsync });
                await mcp.ReloadAsync(cancellationToken).ConfigureAwait(false);
                runner.AttachMcpService(mcp);
            }
            if (options.EnableToolSearch || options.EnableMcp) runner.EnableToolSearch();
            if (options.EnableCodeMode || options.EnableMcp) runner.EnableCodeMode(extensionCommandStore, options.CodeModeOptions);

            return new CodingAgentSdkSession(
                runner,
                settingsStore,
                sessionStore,
                treeSessionController,
                packageResourceState,
                extensionCommandStore,
                extensionStatus,
                promptTemplateStore,
                skillStore,
                contextFileStore,
                modelFallbackMessage) { StartupExtensionErrors = startupErrors, Mcp = mcp };
        }
        catch
        {
            // 1. 【CodingAgent】【SDK会话】创建失败时及时释放已经启动的扩展进程
            if (mcp is not null) await mcp.DisposeAsync().ConfigureAwait(false);
            extensionCommandStore.Dispose();
            throw;
        }
    }

    /// <summary>【CodingAgent】【会话配置】按显式 AgentDirectory 或会话 Cwd 定位模型和认证文件。</summary>
    /// <param name="cwd">会话工作目录。</param>
    /// <param name="agentDir">会话资源目录。</param>
    /// <param name="options">SDK 创建选项。</param>
    /// <returns>绑定会话配置及认证的模型目录。</returns>
    private static ModelCatalog CreateModelCatalog(string cwd, string agentDir, CodingAgentSdkCreateSessionOptions options)
    {
        var explicitDirectory = !string.IsNullOrWhiteSpace(options.AgentDirectory);
        var configuration = new ModelConfigurationStore(GetConfigurationPaths("models.json", "TAU_MODELS_FILE", cwd, agentDir, explicitDirectory));
        var credentials = new OAuthCredentialStore(GetConfigurationPaths("auth.json", "TAU_AUTH_FILE", cwd, agentDir, explicitDirectory));
        var auth = new ProviderAuthResolver(credentialStore: credentials, logSink: options.LogSink, configurationStore: configuration);
        return new ModelCatalog(auth, configuration);
    }

    /// <summary>【CodingAgent】【会话配置】显式独立目录不回退；默认模式保留环境覆盖、项目和用户配置顺序。</summary>
    /// <param name="fileName">配置文件名。</param>
    /// <param name="variable">显式文件路径的环境变量。</param>
    /// <param name="cwd">会话工作目录。</param>
    /// <param name="agentDir">资源目录。</param>
    /// <param name="explicitDirectory">是否明确指定独立目录。</param>
    /// <returns>有序配置路径。</returns>
    private static IEnumerable<string> GetConfigurationPaths(string fileName, string variable, string cwd, string agentDir, bool explicitDirectory)
    {
        if (!explicitDirectory)
        {
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } configured) yield return Path.GetFullPath(configured, cwd);
            yield return Path.Combine(cwd, ".tau", fileName);
        }
        yield return Path.Combine(agentDir, fileName);
    }

    private static (string? ProviderId, string? ModelId, string? FallbackMessage) ResolveModelSelection(
        CodingAgentSdkCreateSessionOptions options,
        CodingAgentSettingsSnapshot settings,
        CodingAgentSessionSnapshot sessionSnapshot,
        ModelCatalog modelCatalog)
    {
        if (!string.IsNullOrWhiteSpace(options.ProviderId) || !string.IsNullOrWhiteSpace(options.ModelId))
        {
            return (options.ProviderId, options.ModelId, null);
        }

        var branchSelection = CodingAgentBranchModelSelection.Resolve(sessionSnapshot, modelCatalog);
        if (!string.IsNullOrWhiteSpace(branchSelection.Provider) || !string.IsNullOrWhiteSpace(branchSelection.Model))
        {
            try
            {
                var restored = modelCatalog.ResolveSelection(branchSelection.Provider, branchSelection.Model);
                return (restored.Provider, restored.ModelId, null);
            }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
            {
                var fallbackMessage = $"Could not restore model {sessionSnapshot.Provider}/{sessionSnapshot.Model}";
                return (settings.DefaultProvider, settings.DefaultModel, fallbackMessage);
            }
        }

        return (settings.DefaultProvider, settings.DefaultModel, null);
    }

    private static IReadOnlyList<IAgentTool> MergeTools(
        IReadOnlyList<IAgentTool> extensionTools,
        IReadOnlyList<IAgentTool>? customTools)
    {
        if (customTools is not { Count: > 0 })
        {
            return extensionTools;
        }

        return extensionTools.Concat(customTools).ToArray();
    }

    private static IReadOnlyList<string> CombineResourcePaths(
        IReadOnlyList<string> first,
        IReadOnlyList<string> second)
    {
        if (first.Count == 0)
        {
            return second;
        }

        if (second.Count == 0)
        {
            return first;
        }

        return first.Concat(second).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string GetDefaultAgentDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home)
            ? Path.Combine(Environment.CurrentDirectory, ".tau")
            : Path.Combine(home, ".tau");
    }

    private static string ResolvePath(string? path, string fallback) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? fallback : path);

    private static string ResolveDirectory(string? path, string fallback) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? fallback : path);
}
