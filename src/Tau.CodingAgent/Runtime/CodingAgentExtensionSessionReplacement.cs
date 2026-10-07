// 作者：xxx
using System.Text.Json;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

public sealed partial class CodingAgentJavaScriptExtensionRuntime
{
    /// <summary>【CodingAgent】【实例替换】在同一工作进程中卸载旧注册表，保留正在等待替换结果的命令调用。</summary>
    /// <param name="token">替换取消信号。</param>
    internal void ReplaceSessionModules(CancellationToken token)
    {
        SessionCommands?.RefreshResourcePaths?.Invoke();
        Interlocked.Increment(ref _resetGeneration);
        var result = Execute(BuildPayload("resetModules", _cwd, _cwd), token);
        if (!result.Success) throw new InvalidOperationException(result.Error);
        using var document = JsonDocument.Parse(result.ResultJson);
        if (!ReadBool(document.RootElement, "ok")) throw new InvalidOperationException(ReadString(document.RootElement, "error"));
        ApplyProviderRegistrations(null);
        SessionCommands?.ResetDiscoveredResources();
    }
}

internal sealed partial class CodingAgentExtensionSessionBridge
{
    private readonly SemaphoreSlim _replacementGate = new(1, 1);

    /// <summary>【CodingAgent】【扩展重载】保存原会话，关闭旧实例后重建注册信息和资源，再发布重载启动事件。</summary>
    /// <param name="token">命令取消信号。</param>
    /// <returns>重载完成任务。</returns>
    private async Task ReloadSessionAsync(CancellationToken token)
    {
        if (_runner is not RuntimeCodingAgentRunner runner || _extensions is null) throw new InvalidOperationException("The runner does not support reload.");
        await _replacementGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await runner.ReplaceSessionAsync(async () =>
            {
                var commands = _extensions.SessionCommands;
                if (commands is not null) runner.ReportSessionHookErrors(await commands.PublishSessionShutdownAsync(token, "reload").ConfigureAwait(false));
                Snapshot();
                _extensions.ReplaceSessionModules(token);
                if (commands is not null)
                {
                    runner.RefreshExtensionBindings(commands);
                    await commands.InitializeProviderModelsAsync(token).ConfigureAwait(false);
                    runner.ReportSessionHookErrors(await commands.PublishSessionStartAsync("reload", token).ConfigureAwait(false));
                }
            }, token).ConfigureAwait(false);
        }
        finally { _replacementGate.Release(); }
    }

    /// <summary>【CodingAgent】【初始化投影】初始化回调追加消息后更新运行器，并同步保存游标避免重复追加。</summary>
    private void RefreshReplacementContext()
    {
        lock (_gate)
        {
            if (_tree is not null) _runner.RestoreSession(_tree.LoadSnapshot().ToFlatSnapshot());
            else
            {
                var branch = ReadReplacementBranch(_memoryEntries, _memoryLeafId);
                var projection = CodingAgentTreeSessionStore.ProjectBranch(branch, legacyMessages: true);
                _runner.RestoreSession(new(projection.Messages, projection.Model?.Provider, projection.Model?.ModelId, _runner.SessionName)
                    { ThinkingLevel = projection.ThinkingLevel, BranchEntries = branch });
                _memoryMessageCount = _runner.Messages.Count;
                _memoryFirstMessage = _runner.Messages.FirstOrDefault();
            }
        }
    }

    /// <summary>【CodingAgent】【会话命令】验证目标并运行取消钩子，随后完成旧回合、保存关闭状态和替换上下文。</summary>
    /// <param name="operation">newSession、fork 或 switchSession。</param>
    /// <param name="request">目标路径、分叉位置等参数。</param>
    /// <param name="token">命令取消信号。</param>
    /// <returns>取消标记以及分叉前选中的用户文本。</returns>
    private async Task<CodingAgentSessionReplacementResult> ReplaceSessionAsync(string operation, JsonElement request, CancellationToken token)
    {
        if (_runner is not RuntimeCodingAgentRunner runner)
            throw new InvalidOperationException("The runner does not support session replacement.");
        await _replacementGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var current = Snapshot();
            if (request.TryGetProperty("sessionId", out var requestSessionId) && requestSessionId.GetString() != current.Header.Id)
                throw new InvalidOperationException("Extension session changed while the request was running.");
            var reason = operation switch { "newSession" => "new", "fork" => "fork", _ => "resume" };
            var path = ReadReplacementString(request, "sessionPath");
            var id = ReadReplacementString(request, "entryId");
            var position = ReadReplacementString(request, "position") ?? "before";
            var commands = _extensions?.SessionCommands;
            // 1. 【CodingAgent】【切换取消】取消钩子先于目标验证和文件创建，取消不会改变任何运行状态
            if (commands is not null)
            {
                var before = operation == "fork"
                    ? JsonSerializer.SerializeToElement(new { type = "session_before_fork", entryId = id, position })
                    : JsonSerializer.SerializeToElement(new { type = "session_before_switch", reason, targetSessionFile = path });
                if (await commands.PublishBeforeSessionReplacementAsync(before, runner, token).ConfigureAwait(false)) return new(true);
            }
            current = Snapshot();
            CodingAgentTreeSessionStore? replacement = null;
            IReadOnlyList<CodingAgentTreeSessionEntry> memory = [];
            string? selectedText = null;
            if (operation == "switchSession")
            {
                if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Session path is required.");
                path = Path.GetFullPath(path, current.Header.Cwd);
                if (!File.Exists(path)) throw new FileNotFoundException("Session file does not exist.", path);
                if (CodingAgentTreeSessionStore.TryGetResumeSessionInfo(path) is null) throw new InvalidDataException("Invalid session file.");
                replacement = new(path, current.Header.Cwd);
                var target = replacement.ReadExtensionSnapshot();
                if (!Directory.Exists(target.Header.Cwd)) throw new DirectoryNotFoundException("Session working directory does not exist: " + target.Header.Cwd);
                if (!Path.GetFullPath(target.Header.Cwd).Equals(Path.GetFullPath(current.Header.Cwd), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new InvalidOperationException("Switching to a session in another working directory requires a new agent runtime.");
            }
            else if (operation == "fork")
            {
                if (position is not ("at" or "before")) throw new ArgumentException("Invalid fork position.");
                var entry = current.Entries.FirstOrDefault(entry => entry.Id == id) ?? throw new ArgumentException("Invalid entry ID for forking");
                if (position == "before")
                {
                    if (entry.Type != "message" || entry.Message?.Role != "user") throw new ArgumentException("Invalid entry ID for forking");
                    selectedText = (CodingAgentSessionStore.ToMessage(entry.Message) as UserMessage)?.Content.OfType<TextContent>().Select(text => text.Text).Aggregate("", (text, next) => text + next);
                }
                var leaf = position == "at" ? entry.Id : entry.ParentId;
                if (_tree is not null) replacement = leaf is null ? _tree.Store.CreateNewSession(current.SessionFile) : _tree.Store.CreateBranchedSession(leaf);
                else
                {
                    memory = CodingAgentTreeSessionStore.BuildReplacementBranch(current.Entries, ReadReplacementBranch(current.Entries, leaf));
                    if (_flat is not null) replacement = CodingAgentTreeSessionStore.CreateReplacementSession(
                        Path.GetDirectoryName(_flat.Path)!, current.Header.Cwd, current.SessionFile, memory);
                }
            }
            else if (_tree is not null) replacement = _tree.Store.CreateNewSession(ReadReplacementString(request, "parentSession"));
            else if (_flat is not null) replacement = CodingAgentTreeSessionStore.CreateReplacementSession(
                Path.GetDirectoryName(_flat.Path)!, current.Header.Cwd, ReadReplacementString(request, "parentSession"), []);
            var projection = replacement is null ? CodingAgentTreeSessionStore.ProjectBranch(memory, legacyMessages: true) : null;
            var snapshot = replacement?.LoadCurrentBranchSnapshot().ToFlatSnapshot() ??
                new CodingAgentSessionSnapshot(projection!.Messages, projection.Model?.Provider, projection.Model?.ModelId,
                    memory.LastOrDefault(entry => entry.Type == "session_info")?.Name)
                { ThinkingLevel = memory.Count == 0 ? CodingAgentThinkingLevels.Format(runner.ThinkingLevel) : projection.ThinkingLevel,
                    BranchEntries = memory };
            var prepared = runner.PrepareReplacementSnapshot(snapshot);
            var previousModel = runner.Model;
            // 2. 【CodingAgent】【替换事务】不持有桥接锁执行扩展事件，允许关闭处理器同步保存自己的元数据
            await runner.ReplaceSessionAsync(async () =>
            {
                Snapshot();
                if (commands is not null) runner.ReportSessionHookErrors(await commands.PublishSessionShutdownAsync(token, reason, replacement?.Path).ConfigureAwait(false));
                Snapshot();
                _flat?.Save(_runner.Messages, _runner.Model, _runner.SessionName, CodingAgentThinkingLevels.Format(_runner.ThinkingLevel));
                lock (_gate)
                {
                    if (replacement is not null)
                    {
                        if (_tree is null) _tree = new(replacement);
                        _tree.Adopt(replacement);
                        runner.RestoreReplacementSession(prepared.Snapshot);
                        _flat?.FollowTreePath(replacement.Path);
                        runner.SessionId = replacement.ReadExtensionSnapshot().Header.Id;
                    }
                    else
                    {
                        _memoryHeader = NewMemoryHeader(current.Header.Cwd, ReadReplacementString(request, "parentSession"));
                        _memoryEntries.Clear();
                        _memoryEntries.AddRange(memory);
                        _memoryLeafId = memory.LastOrDefault()?.Id;
                        runner.RestoreReplacementSession(prepared.Snapshot);
                        runner.SessionId = _memoryHeader.Id;
                        _memoryMessageCount = runner.Messages.Count;
                        _memoryFirstMessage = runner.Messages.FirstOrDefault();
                    }
                    _extensionModels = null;
                    runner.SessionModelFallbackMessage = prepared.Fallback;
                }
                _extensions?.ReplaceSessionModules(token);
                if (commands is not null)
                {
                    runner.RefreshExtensionBindings(commands);
                    await commands.InitializeProviderModelsAsync(token).ConfigureAwait(false);
                    runner.ReportSessionHookErrors(await commands.PublishSessionStartAsync(reason, token, current.SessionFile).ConfigureAwait(false));
                }
                await runner.PublishModelSelectionAsync(previousModel, "restore", token).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
            return new(false, selectedText);
        }
        finally { _replacementGate.Release(); }
    }

    /// <summary>【CodingAgent】【分叉父链】从指定叶节点沿父链收集根到叶条目。</summary>
    /// <param name="entries">完整树条目。</param>
    /// <param name="leaf">目标叶节点，空值表示空会话。</param>
    /// <returns>有序分支条目。</returns>
    private static IReadOnlyList<CodingAgentTreeSessionEntry> ReadReplacementBranch(IReadOnlyList<CodingAgentTreeSessionEntry> entries, string? leaf)
    {
        var byId = entries.ToDictionary(entry => entry.Id);
        var result = new List<CodingAgentTreeSessionEntry>();
        var visited = new HashSet<string>();
        while (leaf is not null && visited.Add(leaf) && byId.TryGetValue(leaf, out var entry)) { result.Add(entry); leaf = entry.ParentId; }
        result.Reverse();
        return result;
    }

    /// <summary>【CodingAgent】【替换参数】读取可选字符串并拒绝非字符串参数。</summary>
    /// <param name="request">请求对象。</param>
    /// <param name="name">字段名。</param>
    /// <returns>字段字符串或空值。</returns>
    private static string? ReadReplacementString(JsonElement request, string name) => request.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;
}
