using Tau.AgentCore;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

public sealed class CodingAgentPrintMode
{
    private readonly ICodingAgentRunner _runner;
    private readonly TextWriter _output;
    private readonly TextWriter _error;
    private readonly bool _jsonMode;
    private readonly CodingAgentSessionStore? _sessionStore;
    private readonly CodingAgentTreeSessionController? _initialTreeSessionController;
    private CodingAgentTreeSessionController? _treeSessionController => (_runner as RuntimeCodingAgentRunner)?.CurrentTreeSessionController ?? _initialTreeSessionController;

    /// <summary>【CodingAgent】【命令输出】创建文本打印入口。</summary>
    /// <param name="runner">共享多轮上下文的运行器。</param>
    /// <param name="output">标准输出。</param>
    /// <param name="error">错误输出。</param>
    public CodingAgentPrintMode(ICodingAgentRunner runner, TextWriter output, TextWriter error)
        : this(runner, output, error, jsonMode: false)
    {
    }

    /// <summary>【CodingAgent】【命令输出】创建文本或 JSON 打印入口，保留原有构造签名。</summary>
    /// <param name="runner">共享多轮上下文的运行器。</param>
    /// <param name="output">标准输出。</param>
    /// <param name="error">错误输出。</param>
    /// <param name="jsonMode">是否发布完整 JSON 事件。</param>
    public CodingAgentPrintMode(ICodingAgentRunner runner, TextWriter output, TextWriter error, bool jsonMode)
        : this(runner, output, error, jsonMode, null, null)
    {
    }

    /// <summary>【CodingAgent】【命令输出】创建可保存会话的文本或 JSON 打印入口。</summary>
    /// <param name="runner">共享多轮上下文的运行器。</param>
    /// <param name="output">标准输出。</param>
    /// <param name="error">错误输出。</param>
    /// <param name="jsonMode">是否发布完整 JSON 事件。</param>
    /// <param name="sessionStore">可选平面会话存储，未指定时不保存。</param>
    /// <param name="treeSessionController">可选树会话控制器，未指定时不保存。</param>
    public CodingAgentPrintMode(
        ICodingAgentRunner runner, TextWriter output, TextWriter error, bool jsonMode,
        CodingAgentSessionStore? sessionStore = null,
        CodingAgentTreeSessionController? treeSessionController = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        _runner = runner;
        _output = output;
        _error = error;
        _jsonMode = jsonMode;
        _sessionStore = sessionStore;
        _initialTreeSessionController = treeSessionController;
        if (runner is RuntimeCodingAgentRunner runtime) runtime.EnsureSessionContext(treeSessionController, sessionStore);
    }

    /// <summary>【CodingAgent】【命令输出】执行单条文本提示。</summary>
    /// <param name="prompt">非空文本提示。</param>
    /// <param name="cancellationToken">调用方取消信号。</param>
    /// <returns>成功为 0，错误或取消为 1。</returns>
    public async Task<int> RunAsync(string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        return await RunCoreAsync(
                [new CodingAgentInitialPrompt(prompt, [])],
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>【CodingAgent】【命令输出】执行含文本或图片的首条提示。</summary>
    /// <param name="prompt">包含附件的初始提示。</param>
    /// <param name="cancellationToken">调用方取消信号。</param>
    /// <returns>成功为 0，错误或取消为 1。</returns>
    public Task<int> RunAsync(
        CodingAgentInitialPrompt prompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        if (string.IsNullOrWhiteSpace(prompt.Text) && !prompt.HasImages)
        {
            throw new ArgumentException("Prompt content must not be empty.", nameof(prompt));
        }

        return RunCoreAsync([prompt], cancellationToken);
    }

    /// <summary>【CodingAgent】【提示序列】依次执行初始提示和剩余位置参数，附件只用于首轮。</summary>
    /// <param name="initialPrompt">已合并 stdin、附件及第一个位置参数的提示，可为空。</param>
    /// <param name="messages">尚未消费的位置参数，每项对应独立一轮。</param>
    /// <param name="cancellationToken">调用方取消信号。</param>
    /// <returns>成功为 0，错误或取消为 1。</returns>
    public Task<int> RunAsync(
        CodingAgentInitialPrompt? initialPrompt,
        IReadOnlyList<string> messages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var prompts = new List<CodingAgentInitialPrompt>();
        if (initialPrompt is not null) prompts.Add(initialPrompt);
        foreach (var message in messages) prompts.Add(new CodingAgentInitialPrompt(message, []));
        if (prompts.Count == 0) throw new ArgumentException("At least one prompt is required.", nameof(messages));
        return RunCoreAsync(prompts, cancellationToken);
    }

    /// <summary>【CodingAgent】【命令输出】JSON 模式发布全部轮次事件，文本模式只提交最后一轮助手正文。</summary>
    /// <param name="prompts">按执行顺序排列的提示。</param>
    /// <param name="cancellationToken">调用方取消信号。</param>
    /// <returns>成功为 0，错误或取消为 1。</returns>
    private async Task<int> RunCoreAsync(
        IReadOnlyList<CodingAgentInitialPrompt> prompts,
        CancellationToken cancellationToken)
    {
        var background = _runner as RuntimeCodingAgentRunner;
        string? errorMessage = null;
        ChatMessage? lastMessage = null;
        /// <summary>【CodingAgent】【打印后台】把自动回合合并到当前打印调用的输出和最终回复。</summary>
        /// <param name="evt">后台运行事件。</param>
        /// <param name="token">后台取消信号。</param>
        /// <returns>输出完成的任务。</returns>
        Task HandleBackgroundEventAsync(AgentEvent evt, CancellationToken token)
        {
            if (evt is MessageEndEvent messageEnd) lastMessage = messageEnd.Message;
            if (evt is AgentEndEvent end)
            {
                errorMessage = end.ErrorMessage;
                if (end.Messages.Count > 0) lastMessage = end.Messages[^1];
            }
            if (evt is MessageEndEvent or AgentEndEvent) SaveSession();
            if (_jsonMode) _output.WriteLine(CodingAgentRpcHost.SerializeEventLine(evt));
            return Task.CompletedTask;
        }
        if (background is not null)
        {
            background.BackgroundEvent += HandleBackgroundEventAsync;
            background.StartBackgroundDelivery();
        }
        try
        {
            foreach (var prompt in prompts)
            {
                if (background?.IsShutdownRequested == true) break;
                cancellationToken.ThrowIfCancellationRequested();
                lastMessage = null;
                errorMessage = null;
                try
                {
                    var events = prompt.HasImages
                        ? _runner.RunAsync(prompt.ToContentBlocks(), cancellationToken)
                        : _runner.RunAsync(prompt.Text, cancellationToken);
                    await foreach (var evt in events.ConfigureAwait(false))
                    {
                        if (!_jsonMode && evt is CodingAgentExtensionErrorEvent extensionError) _error.WriteLine(extensionError.Error);
                        // 1. 【CodingAgent】【命令输出】完成消息及 agent_end 快照优先于流式草稿
                        if (evt is MessageEndEvent messageEnd)
                        {
                            lastMessage = messageEnd.Message;
                            SaveSession();
                        }
                        if (evt is AgentEndEvent end)
                        {
                            errorMessage = end.ErrorMessage;
                            if (end.Messages.Count > 0) lastMessage = end.Messages[^1];
                        }
                        if (_jsonMode) _output.WriteLine(CodingAgentRpcHost.SerializeEventLine(evt));
                    }
                }
                finally
                {
                    // 2. 【CodingAgent】【会话保存】枚举器释放后补存最终状态，取消和异常也保留已提交消息
                    SaveSession();
                }
                lastMessage ??= _runner.Messages.LastOrDefault();
            }

            if (background is not null) await background.WaitForIdleAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (lastMessage is AssistantMessage { StopReason: StopReason.Error or StopReason.Aborted } failed)
            {
                errorMessage ??= failed.ErrorMessage ?? (failed.StopReason == StopReason.Aborted ? "Request aborted" : "Request error");
            }

            if (errorMessage is not null)
            {
                if (!_jsonMode) _error.WriteLine(errorMessage);
                _output.Flush();
                return 1;
            }

            // 3. 【CodingAgent】【命令输出】所有提示执行完毕后仅输出最终文本块，保留块间换行
            if (!_jsonMode && lastMessage is AssistantMessage assistant)
            {
                foreach (var content in assistant.Content.OfType<TextContent>()) _output.WriteLine(content.Text);
            }
            _output.Flush();
            return 0;
        }
        catch (OperationCanceledException)
        {
            _error.WriteLine("Cancelled.");
            return 1;
        }
        catch (Exception ex)
        {
            _error.WriteLine(ex.Message);
            return 1;
        }
        finally
        {
            if (background is not null)
            {
                await background.StopBackgroundDeliveryAsync().ConfigureAwait(false);
                background.BackgroundEvent -= HandleBackgroundEventAsync;
            }
        }
    }

    /// <summary>【CodingAgent】【会话保存】同步运行器已提交的历史，不保存流式草稿。</summary>
    private void SaveSession()
    {
        _sessionStore?.Save(_runner.Messages, _runner.Model, _runner.SessionName, CodingAgentThinkingLevels.Format(_runner.ThinkingLevel));
        _treeSessionController?.SyncFromRunner(_runner);
    }
}
