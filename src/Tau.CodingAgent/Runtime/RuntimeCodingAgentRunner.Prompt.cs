// 作者：xxx
using Tau.AgentCore;
using Tau.Ai;
using Tau.Ai.Observability;
using System.Runtime.CompilerServices;

namespace Tau.CodingAgent.Runtime;

public sealed partial class RuntimeCodingAgentRunner
{
    private CodingAgentSystemPromptOptions? _runSystemPromptOptions;
    /// <summary>【CodingAgent】【启动准备】执行启动前钩子，发布提示增量和自定义消息，并安装本轮强制提示投影。</summary>
    /// <param name="input">本轮调用方提供的消息。</param>
    /// <param name="logContext">本轮日志关联信息。</param>
    /// <param name="token">本轮取消信号。</param>
    /// <param name="inputPrepared">输入是否已经按扩展来源处理，避免后续自动回合重复触发 input。</param>
    /// <param name="deliveries">取得运行锁后提交的命令消息动作。</param>
    /// <param name="preflightResult">可选的单次接收回调。</param>
    /// <returns>完成准备后 Agent 的事件流。</returns>
    private async IAsyncEnumerable<AgentEvent> RunPreparedAsync(IReadOnlyList<ChatMessage> input, TauRuntimeLogContext logContext,
        [EnumeratorCancellation] CancellationToken token, bool inputPrepared = false, IReadOnlyList<CodingAgentExtensionMessageDelivery>? deliveries = null,
        Func<CodingAgentPromptDisposition, Task>? preflightResult = null)
    {
        if (IsShutdownRequested)
        {
            if (preflightResult is not null) await preflightResult(CodingAgentPromptDisposition.Handled).ConfigureAwait(false);
            yield break;
        }
        if (deliveries is not null) foreach (var delivery in deliveries) EnqueueExtensionMessage(delivery);
        var pending = inputPrepared ? input.ToList() : input.Select(message => PrepareInputMessage(message, null, token)).OfType<ChatMessage>().ToList();
        if (pending.Count == 0 && preflightResult is not null) await preflightResult(CodingAgentPromptDisposition.Handled).ConfigureAwait(false);
        pending.AddRange(DrainExtensionDeliveries(running: false, token));
        if (pending.Count == 0)
        {
            while (_extensionMessageEvents.TryDequeue(out var appended)) yield return appended;
            yield break;
        }
        await ValidatePromptAuthenticationAsync(token).ConfigureAwait(false);
        while (_nextTurnExtensionMessages.TryDequeue(out var nextTurn)) pending.Add(nextTurn);
        if (_mcpService is { } mcp) await mcp.WaitForDirectServersAsync(token).ConfigureAwait(false);
        SynchronizeMcpTools();
        var recovery = new RecoveryState();
        await foreach (var evt in CheckAutomaticCompactionAsync(Messages.OfType<AssistantMessage>().LastOrDefault(), [], recovery, token,
            skipAborted: false).ConfigureAwait(false)) yield return evt;
        token.ThrowIfCancellationRequested();
        string? forced = null;
        var baseline = _baseSystemPromptOptions ?? (_mcpService is null ? null :
            new CodingAgentSystemPromptOptions { Cwd = _workingDirectory, CustomPrompt = _initialSystemPrompt });
        if (baseline is not null)
        {
            var options = CodingAgentSystemPrompt.Normalize(baseline) with { SelectedTools = GetActiveToolNames().ToList() };
            // 1. 【CodingAgent】【MCP 动态提示】每轮重新生成目录，连接说明和策略变化通过结构化提示增量进入会话
            if (_mcpService is { } promptMcp)
            {
                if (promptMcp.GetServersPrompt() is { } section) options.Sections[Mcp.CodingAgentMcpPrompt.SectionName] = section;
                else options.Sections.Remove(Mcp.CodingAgentMcpPrompt.SectionName);
            }
            var selectedBefore = options.SelectedTools.ToArray();
            var blocks = pending.OfType<UserMessage>().SelectMany(message => message.Content).ToArray();
            if (_extensionLifecycleEventSink is not null)
            {
                var result = _extensionLifecycleEventSink.BeforeAgentStart(
                    string.Join("\n", blocks.OfType<TextContent>().Select(block => block.Text)), blocks.OfType<ImageContent>().ToArray(),
                    options, error => LogExtensionEventError(error, logContext), token);
                options = result.Options;
                pending.AddRange(result.Messages);
            }

            // 1. 【CodingAgent】【工具优先级】显式修改 selectedTools 优先，否则采用处理器调用 setActiveTools 后的活动集合
            if (options.SelectedTools.SequenceEqual(selectedBefore)) options = options with { SelectedTools = GetActiveToolNames().ToList() };
            var tools = ApplyToolLoadout(options.SelectedTools);
            _config = _config with { Tools = tools };
            options = FilterHiddenToolSnippets(options with { SelectedTools = tools.Select(tool => tool.Name).ToList() });
            if (BuildPromptPatch(options) is { } patch) pending.Insert(0, patch);
            forced = options.ForceSystemPrompt;
            _runSystemPromptOptions = options;
        }
        else _config = _config with { Tools = ApplyToolLoadout(GetActiveToolNames()) };

        pending.AddRange(DrainExtensionDeliveries(running: false, token));
        await NormalizePromptImagesAsync(pending, token).ConfigureAwait(false);
        while (_extensionMessageEvents.TryDequeue(out var appended)) yield return appended;
        _pendingToolNames.Clear();
        var config = WithAttributionHeaders(logContext) with { InitialMessages = pending };
        if (forced is not null)
        {
            var transform = config.TransformContextAsync;
            var syncTransform = config.TransformContext;
            // 2. 【CodingAgent】【强制提示】在所有上下文钩子之后折叠系统消息，仅改变发送副本，不记录强制文本
            config = config with
            {
                TransformContextAsync = async (messages, cancellation) =>
                {
                    var current = transform is null ? syncTransform?.Invoke(messages) ?? messages : await transform(messages, cancellation).ConfigureAwait(false);
                    var system = Transcript.GetCurrentSystemMessage(current);
                    return [new SystemMessage(forced) { ToolsAdded = system?.ToolsAdded, Timestamp = system?.Timestamp ?? DateTimeOffset.UtcNow },
                        .. current.Where(message => message is not SystemMessage)];
                }
            };
        }
        _settlementContinuationConfig = config;
        _settlementRecovery = recovery;
        await foreach (var evt in RunRecoverableAsync(config, recovery, token).ConfigureAwait(false)) yield return evt;
    }

    /// <summary>【CodingAgent】【提示配置】从工具元数据和项目资源建立独立的构建选项。</summary>
    /// <param name="tools">当前活动工具。</param>
    /// <param name="skills">加载的技能。</param>
    /// <param name="contextFiles">项目上下文文件。</param>
    /// <param name="cwd">会话工作目录。</param>
    /// <param name="append">附加指令。</param>
    /// <param name="custom">自定义前言。</param>
    /// <returns>包含所有集合的提示构建选项。</returns>
    private static CodingAgentSystemPromptOptions CreateSystemPromptOptions(IReadOnlyList<IAgentTool> tools,
        IReadOnlyList<CodingAgentSkill> skills, IReadOnlyList<CodingAgentContextFile> contextFiles, string cwd, string? append, string? custom = null) => new()
    {
        Cwd = cwd,
        CustomPrompt = custom,
        AppendSystemPrompt = append ?? "",
        SelectedTools = tools.Select(tool => tool.Name).ToList(),
        ToolSnippets = tools.Where(tool => !string.IsNullOrWhiteSpace(tool.PromptSnippet)).ToDictionary(tool => tool.Name,
            tool => string.Join(" ", tool.PromptSnippet!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), StringComparer.Ordinal),
        ToolGuidelines = tools.ToDictionary(tool => tool.Name, tool => tool.PromptGuidelines.ToList(), StringComparer.Ordinal),
        ContextFiles = contextFiles.Select(file => new CodingAgentPromptContextFile(file.FilePath, file.Content)).ToList(),
        Skills = skills.ToList()
    };

    /// <summary>【CodingAgent】【提示基线】新会话保存段落，恢复已有会话时保持历史系统声明。</summary>
    private void InitializeTranscript()
    {
        if (_baseSystemPromptOptions is null) _runtime.InitializeTranscript(_initialSystemPrompt, _config.Model, _config.Tools);
        else _runtime.InitializeStructuredTranscript(CodingAgentSystemPrompt.BuildState(FilterHiddenToolSnippets(_baseSystemPromptOptions)), _config.Model, _config.Tools);
    }

    /// <summary>【CodingAgent】【提示增量】根据历史重放结果建立段落更新，强制提示不写入历史。</summary>
    /// <param name="options">当前所需的提示构建选项。</param>
    /// <returns>只含变化段落的系统消息；未变化时为空。</returns>
    private SystemMessage? BuildPromptPatch(CodingAgentSystemPromptOptions options)
    {
        var current = Transcript.GetCurrentSystemMessage(Messages);
        var patch = CodingAgentSystemPrompt.DiffSections(current?.Sections ?? new Dictionary<string, string?>(),
            CodingAgentSystemPrompt.BuildSections(options));
        return patch is null ? null : new SystemMessage("") { Sections = patch };
    }

    /// <summary>【CodingAgent】【资源刷新】只追加变化段落，避免重写历史中的系统提示。</summary>
    /// <param name="options">刷新后的完整提示构建选项。</param>
    private void ApplyPromptSections(CodingAgentSystemPromptOptions options)
    {
        if (BuildPromptPatch(options) is { } patch) _runtime.AddMessage(patch);
    }
}
