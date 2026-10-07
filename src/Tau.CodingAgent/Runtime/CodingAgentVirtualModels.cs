// 作者：xxx
using System.Text.Json;
using System.Text.Json.Nodes;
using Tau.Ai;
using Tau.Ai.Registry;
using Tau.AgentCore.Runtime;

namespace Tau.CodingAgent.Runtime;

public sealed partial class CodingAgentJavaScriptExtensionRuntime
{
    /// <summary>【CodingAgent】【虚拟模型】读取由 Node 验证的原生目录定义，不要求物理请求地址。</summary>
    /// <param name="value">原生虚拟模型。</param><returns>宿主目录项。</returns>
    private static Model ReadVirtualModel(JsonElement value) => new()
    {
        Id = value.GetProperty("id").GetString()!, Provider = value.GetProperty("provider").GetString()!,
        Name = ReadString(value, "name") ?? value.GetProperty("id").GetString()!, Api = "pi-virtual", BaseUrl = string.Empty,
        Reasoning = ReadBool(value, "reasoning"), ContextWindow = ReadInt(value, "contextWindow"), MaxOutputTokens = ReadInt(value, "maxTokens"),
        InputModalities = value.GetProperty("input").EnumerateArray().Select(item => item.GetString()!).ToArray(),
        ThinkingLevelMap = value.GetProperty("thinkingLevelMap").EnumerateObject().ToDictionary(field => field.Name, field => field.Value.GetString()),
        Cost = new(0, 0, 0, 0)
    };

    /// <summary>【CodingAgent】【虚拟路由】调用注册时的路由闭包，绑定会话上下文及取消生命周期。</summary>
    /// <param name="model">虚拟选择。</param><param name="fields">路由参数。</param><param name="token">取消信号。</param><returns>物理选择与可选状态。</returns>
    internal async Task<(JsonElement Value, string? StateSessionId)> RouteVirtualModelAsync(Model model, JsonObject fields, CancellationToken token)
    {
        var session = fields["reason"]?.GetValue<string>() != "direct" ? _sessionBridge?.ReadVirtualModelState(model) : null;
        if (session?.State is { } state) fields["state"] = JsonNode.Parse(state.GetRawText());
        JsonElement registration = default;
        long generation;
        lock (_providerRegistrationGate)
        {
            if (_providerRegistrations is { } providers)
                foreach (var provider in providers.EnumerateArray())
                    if (ReadString(provider, "id") == model.Provider && provider.TryGetProperty("virtualModels", out var routes))
                        registration = routes.EnumerateArray().FirstOrDefault(route => ReadString(route.GetProperty("model"), "id") == model.Id).Clone();
            generation = ResetGeneration;
        }
        if (registration.ValueKind != JsonValueKind.Object) throw new InvalidOperationException($"Virtual model {model.Provider}/{model.Id} is not registered.");
        fields["model"] = JsonNode.Parse(CodingAgentExtensionSessionBridge.SerializeExtensionModel(model).GetRawText());
        fields["version"] = ReadInt(registration, "version");
        var result = await ExecuteAsync(BuildPayload("routeVirtualModel", registration.GetProperty("filePath").GetString()!, _cwd,
            toolArgs: JsonSerializer.SerializeToElement(fields)), token, expectedGeneration: generation, timeout: Timeout.InfiniteTimeSpan).ConfigureAwait(false);
        if (!result.Success) throw new InvalidOperationException(result.Error);
        using var document = JsonDocument.Parse(result.ResultJson);
        if (!ReadBool(document.RootElement, "ok")) throw new InvalidOperationException(ReadString(document.RootElement, "error"));
        var value = document.RootElement.GetProperty("value").Clone();
        return (value, session is not null && ReadBool(document.RootElement, "stateChanged") ? session.Value.SessionId : null);
    }

    /// <summary>【CodingAgent】【路由提交】仅在物理目标及认证验证成功后保存新的分支状态。</summary>
    /// <param name="model">虚拟选择。</param><param name="state">新状态。</param><param name="sessionId">原会话身份。</param>
    internal void CommitVirtualModelState(Model model, JsonElement state, string sessionId) => _sessionBridge?.CommitVirtualModelState(model, state, sessionId);
}

public sealed partial class RuntimeCodingAgentRunner
{
    private CodingAgentJavaScriptExtensionRuntime? _virtualModelRuntime;
    private AssistantMessage? _failedVirtualResponse;

    /// <summary>【CodingAgent】【有效模型】虚拟选择使用最近成功响应的物理模型限制，失败检查可显式传入本次响应。</summary>
    /// <param name="response">可选待检查响应。</param><returns>具有有效上下文和输出限制的模型。</returns>
    internal Model GetEffectiveContextModel(AssistantMessage? response = null)
    {
        if (Model.Api != "pi-virtual") return Model;
        response ??= Messages.OfType<AssistantMessage>().LastOrDefault(message => message.StopReason is not (StopReason.Error or StopReason.Aborted));
        return response?.Provider is { } provider && response.Model is { } id && _modelCatalog.TryGetModel(provider, id) is { Api: not "pi-virtual" } physical ? physical : Model;
    }

    /// <summary>【CodingAgent】【虚拟回合】只替换请求模型，路由原因和状态跟随当前会话分支。</summary>
    /// <param name="update">前置钩子的请求修改。</param><param name="request">当前请求状态。</param><param name="options">当前流选项。</param>
    /// <param name="prepareAgain">压缩后重新执行原准备钩子。</param><param name="token">取消信号。</param><returns>路由后的请求更新。</returns>
    private async Task<AgentRequestUpdate> RouteAgentRequestAsync(AgentRequestUpdate update, AgentPrepareRequestContext request, SimpleStreamOptions options,
        Func<IReadOnlyList<ChatMessage>, Task<AgentRequestUpdate>> prepareAgain, CancellationToken token)
    {
        var selected = update.Model ?? _config.Model;
        if (selected.Api != "pi-virtual") { _failedVirtualResponse = null; return update; }
        if (_virtualModelRuntime is null) throw new InvalidOperationException("Virtual model runtime is unavailable.");
        var messages = update.Context ?? request.Context;
        var failed = _failedVirtualResponse;
        _failedVirtualResponse = null;
        var lastAssistant = messages.Select((message, index) => (message, index)).LastOrDefault(pair => pair.message is AssistantMessage).index;
        var reason = failed is not null ? "retry" : messages.Skip(messages.Any(message => message is AssistantMessage) ? lastAssistant + 1 : 0).Any(message => message is UserMessage) ? "user" : "continuation";
        var route = await ResolveVirtualModelAsync(_virtualModelRuntime, selected, messages, options.Reasoning, reason, token, failed).ConfigureAwait(false);
        // 1. 【CodingAgent】【路由压缩】按已选定物理窗口压缩，保留本次路由决策及已提交状态
        if (await CompactBeforeRoutedRequestAsync(route.Model, token).ConfigureAwait(false))
        {
            update = await prepareAgain(Messages.ToArray()).ConfigureAwait(false);
            options = update.StreamOptions!;
        }
        token.ThrowIfCancellationRequested();
        var crossProvider = selected.Provider != route.Model.Provider;
        options = options with { Reasoning = route.Reasoning,
            ApiKey = crossProvider ? null : options.ApiKey,
            Headers = CodingAgentProviderAttribution.MergeAttributionHeaders(route.Model, InstallTelemetryEnabled,
                SessionId ?? options.SessionId, crossProvider ? null : AsReadOnly(options.Headers)), Env = crossProvider ? null : options.Env,
            MaxTokens = options.MaxTokens is { } maximum && route.Model.MaxOutputTokens is > 0 ? Math.Min(maximum, route.Model.MaxOutputTokens.Value) : options.MaxTokens };
        return update with { Model = route.Model, StreamOptions = options, Reasoning = route.Reasoning ?? Tau.Ai.ThinkingLevel.Off, PreserveModelSelection = true };
    }

    /// <summary>【CodingAgent】【路由记录】将已持久化的状态记录合并进当前事件序列。</summary>
    /// <param name="entry">保存后的状态条目。</param>
    internal void NotifyVirtualModelState(CodingAgentTreeSessionEntry entry) => _nestedToolEvents?.TryWrite(new CodingAgentEntryAppendedEvent(
        JsonSerializer.SerializeToElement(entry, CodingAgentTreeSessionJsonContext.Default.CodingAgentTreeSessionEntry)));

    /// <summary>【CodingAgent】【虚拟路由】验证路由结果为已配置认证的物理模型，并钳制其推理等级。</summary>
    /// <param name="extensions">扩展运行时。</param><param name="model">虚拟选择。</param><param name="messages">当前请求消息。</param>
    /// <param name="thinkingLevel">所选推理等级。</param><param name="reason">路由原因。</param><param name="token">取消信号。</param>
    /// <param name="failed">重试前已省略的失败响应。</param><returns>实际模型、推理等级和路由原始返回值。</returns>
    private async Task<(Model Model, ThinkingLevel? Reasoning, JsonElement Result)> ResolveVirtualModelAsync(
        CodingAgentJavaScriptExtensionRuntime extensions, Model model, IReadOnlyList<ChatMessage> messages, ThinkingLevel? thinkingLevel, string reason, CancellationToken token, AssistantMessage? failed = null)
    {
        var serialized = new JsonArray(messages.Select(message => JsonSerializer.SerializeToNode(CodingAgentSessionStore.FromMessage(message),
            CodingAgentTreeSessionJsonContext.Default.CodingAgentSessionMessage)).ToArray());
        var fields = new JsonObject { ["thinkingLevel"] = CodingAgentThinkingLevels.Format(thinkingLevel), ["reason"] = reason, ["messages"] = serialized };
        var latest = messages.OfType<AssistantMessage>().LastOrDefault(message => message.StopReason is not (StopReason.Error or StopReason.Aborted));
        if (latest?.Provider is { } previousProvider && latest.Model is { } previousId && _modelCatalog.TryGetModel(previousProvider, previousId) is { Api: not "pi-virtual" } previous)
        {
            fields["previous"] = new JsonObject { ["model"] = JsonNode.Parse(CodingAgentExtensionSessionBridge.SerializeExtensionModel(previous).GetRawText()),
                ["thinkingLevel"] = latest.ThinkingLevel };
            if (latest.ThinkingLevel is null) fields["previous"]!.AsObject().Remove("thinkingLevel");
        }
        if (failed?.Provider is { } failedProvider && failed.Model is { } failedId && _modelCatalog.TryGetModel(failedProvider, failedId) is { Api: not "pi-virtual" } failedModel)
        {
            fields["failed"] = new JsonObject { ["model"] = JsonNode.Parse(CodingAgentExtensionSessionBridge.SerializeExtensionModel(failedModel).GetRawText()),
                ["thinkingLevel"] = failed.ThinkingLevel,
                ["message"] = JsonSerializer.SerializeToNode(CodingAgentSessionStore.FromMessage(failed), CodingAgentTreeSessionJsonContext.Default.CodingAgentSessionMessage) };
            if (failed.ThinkingLevel is null) fields["failed"]!.AsObject().Remove("thinkingLevel");
        }
        var route = await extensions.RouteVirtualModelAsync(model, fields, token).ConfigureAwait(false);
        var result = route.Value;
        token.ThrowIfCancellationRequested();
        var selected = result.GetProperty("model");
        var provider = selected.GetProperty("provider").GetString()!;
        var id = selected.GetProperty("id").GetString()!;
        var routed = $"Virtual model {model.Provider}/{model.Id} routed to {provider}/{id}";
        var target = _modelCatalog.TryGetModel(provider, id);
        if (target is null || target.Api == "pi-virtual") throw new InvalidOperationException(routed + ", which is not a physical model.");
        if (!_authResolver.GetStatus(target, provider == Model.Provider ? _config.StreamOptions?.ApiKey : null).IsConfigured)
            throw new InvalidOperationException(routed + ", which has no credentials.");
        var level = ModelCatalog.ClampThinkingLevel(target, result.GetProperty("thinkingLevel").GetString()!);
        if (!CodingAgentThinkingLevels.TryParse(level, out var reasoning)) throw new InvalidOperationException("Unsupported routed thinking level: " + level);
        if (route.StateSessionId is { } sessionId && result.TryGetProperty("state", out var state))
            extensions.CommitVirtualModelState(model, state, sessionId);
        return (target, reasoning, result);
    }
}

internal sealed partial class CodingAgentExtensionSessionBridge
{
    /// <summary>【CodingAgent】【路由状态】沿当前分支寻找指定虚拟模型最近的状态。</summary>
    /// <param name="model">虚拟模型。</param><returns>会话身份和路由状态。</returns>
    internal (string SessionId, JsonElement? State) ReadVirtualModelState(Model model)
    {
        lock (_gate)
        {
            var snapshot = Snapshot();
            foreach (var entry in GetSnapshotBranch(snapshot).Reverse())
                if (entry.Type == "custom" && entry.CustomType == "pi.virtual-model-state" && entry.Data is { ValueKind: JsonValueKind.Object } data &&
                    data.TryGetProperty("provider", out var provider) && provider.ValueKind == JsonValueKind.String && provider.GetString() == model.Provider &&
                    data.TryGetProperty("modelId", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() == model.Id)
                    return (snapshot.Header.Id, data.TryGetProperty("state", out var state) ? state.Clone() : null);
            return (snapshot.Header.Id, null);
        }
    }

    /// <summary>【CodingAgent】【路由状态】向当前分支追加自定义记录，保留取消和会话替换边界。</summary>
    /// <param name="model">虚拟模型。</param><param name="state">JSON 状态。</param><param name="sessionId">路由开始时的会话。</param>
    internal void CommitVirtualModelState(Model model, JsonElement state, string sessionId)
    {
        lock (_gate)
        {
            var snapshot = Snapshot();
            if (snapshot.Header.Id != sessionId) throw new InvalidOperationException("Session changed during virtual model routing.");
            var entry = new CodingAgentTreeSessionEntry { Id = Guid.NewGuid().ToString("N"), ParentId = snapshot.LeafId, Type = "custom",
                Timestamp = DateTimeOffset.UtcNow, CustomType = "pi.virtual-model-state",
                Data = JsonSerializer.SerializeToElement(new { provider = model.Provider, modelId = model.Id, state }) };
            if (_tree is not null) { _tree.Store.AppendExtensionEntry(entry); _tree.LoadSnapshot(); }
            else AppendMemoryEntry(entry);
            (_runner as RuntimeCodingAgentRunner)?.NotifyVirtualModelState(entry);
        }
    }
}
