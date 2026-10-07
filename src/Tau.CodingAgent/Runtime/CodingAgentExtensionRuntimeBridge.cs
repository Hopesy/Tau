// 作者：xxx
using System.Text;
using System.Text.Json;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【扩展运行】为模型、工具和上下文 API 提供宿主状态及调用通道。</summary>
internal sealed partial class CodingAgentExtensionSessionBridge
{
    private IReadOnlyList<Model>? _extensionModels;

    /// <summary>【CodingAgent】【扩展目录】读取当前会话模型目录，保持同一次绑定的目录快照。</summary>
    /// <returns>全部聊天模型。</returns>
    private IReadOnlyList<Model> GetExtensionModels() => _extensionModels ??=
        _runner.GetProviders().SelectMany(_runner.GetModels).ToArray();

    /// <summary>【CodingAgent】【扩展读取】序列化模型、工具和运行状态，不携带认证密钥。</summary>
    /// <param name="writer">写入当前 JSON 对象的写入器。</param>
    public void WriteRuntimeSnapshot(Utf8JsonWriter writer)
    {
        lock (_gate)
        {
            var control = _runner as ICodingAgentExtensionRuntimeControl;
            writer.WriteStartObject();
            writer.WriteBoolean("projectTrusted", (_runner as RuntimeCodingAgentRunner)?.IsProjectTrusted ?? true);
            _extensions?.WriteProviderBaselines(writer);
            var registryErrors = new List<string>();
            if ((_runner as RuntimeCodingAgentRunner)?.ModelConfigurationError is { } configurationError) registryErrors.Add(configurationError);
            if (_extensions?.ProviderRefreshDiagnostics is { Count: > 0 } providerErrors) registryErrors.AddRange(providerErrors.Select(error => error.Message));
            if (registryErrors.Count > 0) writer.WriteString("modelRegistryError", string.Join("\n", registryErrors));
            WriteCommandsSnapshot(writer);
            (_runner as RuntimeCodingAgentRunner)?.WriteToolRegistrationPolicy(writer);
            var availableProviders = (_runner as RuntimeCodingAgentRunner)?.WriteRegistryAuthMetadata(writer);
            (_runner as RuntimeCodingAgentRunner)?.WriteBuiltInModelPermissions(writer);
            // 1. 【CodingAgent】【提供方名称】只传基础名称，扩展注册与注销在 Node 内立即覆盖，避免回退到陈旧注册名
            writer.WriteStartObject("providerDisplayNames");
            foreach (var (provider, name) in Tau.Ai.Providers.BuiltInProviderNames.All) writer.WriteString(provider, name);
            writer.WriteEndObject();
            writer.WritePropertyName("model");
            WriteExtensionModel(writer, _runner.Model);
            writer.WriteString("thinkingLevel", CodingAgentThinkingLevels.Format(_runner.ThinkingLevel));
            writer.WritePropertyName("scopedModels");
            writer.WriteStartArray();
            foreach (var scoped in (_runner as RuntimeCodingAgentRunner)?.GetScopedModels() ?? [])
            {
                writer.WriteStartObject();
                writer.WritePropertyName("model");
                WriteExtensionModel(writer, scoped.Model);
                if (scoped.ThinkingLevel is not null) writer.WriteString("thinkingLevel", scoped.ThinkingLevel);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WritePropertyName("systemPromptOptions");
            if (_runner is RuntimeCodingAgentRunner runtime)
                JsonSerializer.Serialize(writer, runtime.GetSystemPromptOptions(), CodingAgentTreeSessionJsonContext.Default.CodingAgentSystemPromptOptions);
            else writer.WriteNullValue();
            writer.WritePropertyName("thinkingLevels");
            writer.WriteStartArray();
            foreach (var level in CodingAgentThinkingLevels.AvailableForModel(_runner.Model)) writer.WriteStringValue(level);
            writer.WriteEndArray();
            writer.WriteBoolean("isIdle", !_runner.IsStreaming && !_runner.IsCompacting);
            writer.WriteBoolean("hasPendingMessages", _runner.PendingMessageCount > 0);
            writer.WriteString("systemPrompt", control?.GetSystemPrompt() ?? Transcript.GetCurrentSystemMessage(_runner.Messages)?.Content ?? "");
            // 1. 【CodingAgent】【扩展目录】模型与可用认证状态分开传输，查询目录不会泄漏密钥
            writer.WritePropertyName("models");
            writer.WriteStartArray();
            foreach (var model in GetExtensionModels()) WriteExtensionModel(writer, model);
            writer.WriteEndArray();
            writer.WriteStartArray("allModels");
            foreach (var model in (_runner as RuntimeCodingAgentRunner)?.GetAllRegistryModels() ?? GetExtensionModels()) WriteExtensionModel(writer, model);
            writer.WriteEndArray();
            if (GetExtensionModels().Any(model => model.Api == "pi-virtual") && _runner is RuntimeCodingAgentRunner virtualRunner)
            {
                writer.WriteStartArray("physicalModels");
                foreach (var model in virtualRunner.GetPhysicalRegistryModels()) WriteExtensionModel(writer, model);
                writer.WriteEndArray();
            }
            writer.WritePropertyName("availableProviders");
            writer.WriteStartArray();
            foreach (var provider in availableProviders ?? GetExtensionModels().Select(model => model.Provider).Distinct(StringComparer.Ordinal).Where(provider => _runner.GetAuthStatus(provider).IsConfigured).ToArray())
                writer.WriteStringValue(provider);
            writer.WriteEndArray();
            writer.WritePropertyName("activeTools");
            writer.WriteStartArray();
            foreach (var name in control?.GetActiveToolNames() ?? []) writer.WriteStringValue(name);
            writer.WriteEndArray();
            writer.WritePropertyName("tools");
            writer.WriteStartArray();
            foreach (var tool in control?.GetRegisteredTools() ?? [])
            {
                writer.WriteStartObject();
                writer.WriteString("name", tool.Name);
                writer.WriteString("label", tool.Label);
                writer.WriteString("description", tool.Description);
                writer.WritePropertyName("sourceInfo");
                CodingAgentSourceInfo.ForTool(tool).WriteTo(writer);
                if (tool is CodingAgentExtensionToolAdapter adapter) writer.WriteString("extensionPath", adapter.FilePath);
                writer.WriteString("exposure", RuntimeCodingAgentRunner.GetToolExposure(tool));
                writer.WriteString("executionMode", tool.ExecutionMode.ToString().ToLowerInvariant());
                if (tool.OutputSchema is { } outputSchema) { writer.WritePropertyName("outputSchema"); outputSchema.WriteTo(writer); }
                if (tool.ConstrainedSampling is { } sampling)
                { writer.WritePropertyName("constrainedSampling"); JsonSerializer.Serialize(writer, sampling, CodingAgentTreeSessionJsonContext.Default.ConstrainedSamplingConfig); }
                if (tool is ICodingAgentToolDefinition definition)
                {
                    if (definition.DefaultActive is { } enabled) writer.WriteBoolean("defaultActive", enabled);
                    if (definition.Namespace is { } group) { writer.WritePropertyName("namespace"); group.WriteTo(writer); }
                    if (definition.Annotations is { } annotations) { writer.WritePropertyName("annotations"); annotations.WriteTo(writer); }
                }
                if (tool.PromptSnippet is not null) writer.WriteString("promptSnippet", tool.PromptSnippet);
                writer.WritePropertyName("promptGuidelines");
                writer.WriteStartArray();
                foreach (var guideline in tool.PromptGuidelines) writer.WriteStringValue(guideline);
                writer.WriteEndArray();
                writer.WritePropertyName("parameters");
                tool.ParameterSchema.WriteTo(writer);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            // 2. 【CodingAgent】【上下文用量】使用当前投影与有效 usage，压缩后尚无新响应时保留未知值
            writer.WritePropertyName("callableTools");
            writer.WriteStartArray();
            foreach (var tool in (_runner as RuntimeCodingAgentRunner)?.GetCallableTools() ?? []) writer.WriteStringValue(tool.Name);
            writer.WriteEndArray();
            writer.WritePropertyName("contextUsage");
            var contextUsage = _runner.GetSessionStats().ContextUsage;
            if (contextUsage is null) writer.WriteNullValue();
            else
            {
                writer.WriteStartObject();
                if (contextUsage.Tokens is { } tokens) writer.WriteNumber("tokens", tokens); else writer.WriteNull("tokens");
                writer.WriteNumber("contextWindow", contextUsage.ContextWindow);
                if (contextUsage.Percent is { } percent) writer.WriteNumber("percent", percent);
                else writer.WriteNull("percent");
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
    }

    /// <summary>【CodingAgent】【模型契约】按上游字段名称传输模型定义。</summary>
    /// <param name="writer">JSON 写入器。</param>
    /// <param name="model">目标模型。</param>
    private static void WriteExtensionModel(Utf8JsonWriter writer, Model model)
    {
        var value = JsonSerializer.SerializeToElement(model, CodingAgentTreeSessionJsonContext.Default.Model);
        writer.WriteStartObject();
        foreach (var property in value.EnumerateObject())
        {
            writer.WritePropertyName(property.Name switch { "inputModalities" => "input", "outputModalities" => "output", "maxOutputTokens" => "maxTokens", _ => property.Name });
            if (property.Name == "api") writer.WriteStringValue(model.Api switch
            { "openai-chat-completions" => "openai-completions", "google-generative-language" => "google-generative-ai", _ => model.Api });
            else if (property.Name == "cost" && property.Value.ValueKind == JsonValueKind.Object) WriteNativeModelCost(writer, property.Value);
            else property.Value.WriteTo(writer);
        }
        if (model is ImagesModel images)
        {
            writer.WriteStartArray("output");
            foreach (var modality in images.OutputModalities) writer.WriteStringValue(modality);
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }

    /// <summary>【CodingAgent】【模型费用】将每百万 token 费率与分层费用转换成原生字段。</summary>
    /// <param name="writer">JSON 写入器。</param><param name="cost">费用对象。</param>
    private static void WriteNativeModelCost(Utf8JsonWriter writer, JsonElement cost)
    {
        writer.WriteStartObject();
        foreach (var property in cost.EnumerateObject())
        {
            writer.WritePropertyName(property.Name switch { "inputPerMillion" => "input", "outputPerMillion" => "output",
                "cacheReadPerMillion" => "cacheRead", "cacheWritePerMillion" => "cacheWrite", _ => property.Name });
            if (property.Name == "tiers" && property.Value.ValueKind == JsonValueKind.Array)
            {
                writer.WriteStartArray();
                foreach (var tier in property.Value.EnumerateArray()) WriteNativeModelCost(writer, tier);
                writer.WriteEndArray();
            }
            else property.Value.WriteTo(writer);
        }
        writer.WriteEndObject();
    }

    /// <summary>【CodingAgent】【模型协议】将模型序列化为事件与上下文共用的原生字段。</summary>
    /// <param name="model">待序列化模型。</param><returns>独立 JSON 对象。</returns>
    internal static JsonElement SerializeExtensionModel(Model model)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteExtensionModel(writer, model);
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    /// <summary>【CodingAgent】【扩展控制】执行同步控制操作，失败由所属扩展调用报告。</summary>
    /// <param name="operation">操作名称。</param>
    /// <param name="action">参数对象。</param>
    private void ApplyRuntimeOperation(string operation, JsonElement action)
    {
        switch (operation)
        {
            case "registerMcpServers":
                if (_extensions is null) throw new InvalidOperationException("The runner does not support MCP registrations.");
                _extensions.ApplyMcpServers(action.GetProperty("servers"));
                if (_runner is RuntimeCodingAgentRunner mcpRunner) mcpRunner.NotifyMcpServersChanged(action.GetProperty("servers"));
                break;
            case "registerProviders":
                if (_extensions is null) throw new InvalidOperationException("The runner does not support dynamic providers.");
                _extensions.ApplyProviderRegistrations(action.GetProperty("providers"));
                break;
            case "registerTool":
                if (_runner is not RuntimeCodingAgentRunner dynamicRunner || _extensions is null)
                    throw new InvalidOperationException("The runner does not support dynamic tools.");
                var registration = action.GetProperty("tool");
                var prepared = action.GetProperty("changes");
                dynamicRunner.RegisterExtensionTool(_extensions.CreateDynamicTool(action.GetProperty("filePath").GetString()!, registration),
                    CodingAgentJavaScriptExtensionRuntime.ReadLoadoutChanges(prepared));
                dynamicRunner.ReportLoadoutErrors(prepared);
                break;
            case "setActiveTools":
                var names = action.GetProperty("names").EnumerateArray().Select(value => value.GetString()!).ToArray();
                if (_runner is RuntimeCodingAgentRunner loadoutRunner)
                {
                    loadoutRunner.SetActiveToolLoadout(names, action.TryGetProperty("changes", out var changes)
                        ? CodingAgentJavaScriptExtensionRuntime.ReadLoadoutChanges(changes) : new());
                    loadoutRunner.ReportLoadoutErrors(changes);
                }
                else RequireRuntimeControl().SetActiveTools(names);
                break;
            case "setThinkingLevel":
                if (!CodingAgentThinkingLevels.TryParse(action.GetProperty("level").GetString(), out var level))
                    throw new InvalidOperationException("Invalid thinking level.");
                _runner.ThinkingLevel = CodingAgentThinkingLevels.ClampForModel(_runner.Model, level ?? ThinkingLevel.Off);
                break;
            case "abort": RequireRuntimeControl().Abort(); break;
            case "shutdown":
                if (_runner is not RuntimeCodingAgentRunner runtime) throw new InvalidOperationException("The runner does not support extension shutdown.");
                runtime.RequestShutdown();
                break;
            default: throw new InvalidOperationException("Unknown extension runtime operation: " + operation);
        }
    }

    /// <summary>【CodingAgent】【扩展控制】取得支持真实操作的运行器，拒绝假成功。</summary>
    /// <returns>真实运行器控制接口。</returns>
    private ICodingAgentExtensionRuntimeControl RequireRuntimeControl() => _runner as ICodingAgentExtensionRuntimeControl
        ?? throw new InvalidOperationException("The runner does not support extension runtime control.");

    /// <summary>【CodingAgent】【扩展调用】执行需要返回结果的宿主操作，并回传最新快照。</summary>
    /// <param name="request">包含会话标识和操作参数的请求。</param>
    /// <param name="token">工作进程退出时取消的信号。</param>
    /// <param name="onProgress">嵌套调用的实时结果帧接收器。</param>
    /// <returns>带结果及状态快照的 JSON 响应。</returns>
    public async Task<string> HandleRuntimeRequestAsync(JsonElement request, CancellationToken token, Action<JsonElement>? onProgress = null)
    {
        object? value = null;
        try
        {
            // 1. 【CodingAgent】【扩展调用】操作前验证绑定，长时间等待不能持有会话锁
            if (request.GetProperty("sessionId").GetString() != Snapshot().Header.Id)
                throw new InvalidOperationException("Extension session changed while the request was running.");
            var operation = request.GetProperty("operation").GetString();
            if (operation == "providerCallback")
            {
                var callback = await (_extensions ?? throw new InvalidOperationException("Extension runtime is unavailable.")).HandleProviderCallbackAsync(request).ConfigureAwait(false);
                return JsonSerializer.Serialize(new { id = request.GetProperty("id").GetString(), ok = true, value = callback });
            }
            if (operation == "executeNestedTool")
            {
                if (_runner is not RuntimeCodingAgentRunner toolRunner) throw new InvalidOperationException("The runner does not support nested tools.");
                var result = await toolRunner.ExecuteNestedToolAsync(request.GetProperty("callerId").GetString()!,
                    request.GetProperty("name").GetString()!, request.GetProperty("args"), token,
                    update => { onProgress?.Invoke(CodingAgentJavaScriptExtensionRuntime.SerializeToolResult(update.ToPartialResult())); return Task.CompletedTask; },
                    () => onProgress?.Invoke(JsonSerializer.SerializeToElement(new { nestedCallStarted = true }))).ConfigureAwait(false);
                value = new { toolCall = new { type = "toolCall", id = result.ToolCall.Id, name = result.ToolCall.Name,
                    arguments = JsonSerializer.Deserialize<JsonElement>(result.ToolCall.Arguments) },
                    result = CodingAgentJavaScriptExtensionRuntime.SerializeToolResult(result.Result), isError = result.IsError };
            }
            else if (operation == "previewBoundary")
            {
                if (_runner is not RuntimeCodingAgentRunner boundaryRunner) throw new InvalidOperationException("Session boundaries are unavailable.");
                foreach (var delivery in CodingAgentJavaScriptExtensionRuntime.ReadMessageActions(request, request.GetProperty("filePath").GetString()!))
                    boundaryRunner.EnqueueExtensionMessage(delivery);
                boundaryRunner.PrepareBoundaryPreview(token);
                value = PreviewBoundary(request.GetProperty("entries"), request.GetProperty("boundary").GetString()!);
            }
            else if (operation == "waitForIdle") await RequireRuntimeControl().WaitForIdleAsync(token).ConfigureAwait(false);
            else if (operation == "compact") value = await CompactSessionAsync(request, token).ConfigureAwait(false);
            else if (operation is "newSession" or "fork" or "switchSession") value = await ReplaceSessionAsync(operation, request, token).ConfigureAwait(false);
            else if (operation == "refreshSessionContext") RefreshReplacementContext();
            else if (operation == "navigateTree") value = await NavigateTreeAsync(request, token).ConfigureAwait(false);
            else if (operation == "reload") await ReloadSessionAsync(token).ConfigureAwait(false);
            else if (operation == "refresh") value = _extensions is null ? null : await _extensions.RefreshProviderModelsAsync(
                request.TryGetProperty("providers", out var filter) && filter.ValueKind == JsonValueKind.Array ? filter.EnumerateArray().Select(item => item.GetString()!).ToArray() : null,
                !request.TryGetProperty("allowNetwork", out var network) || network.ValueKind != JsonValueKind.False,
                request.TryGetProperty("force", out var force) && force.ValueKind == JsonValueKind.True, token).ConfigureAwait(false);
            else if (operation == "getProviderAuth")
                value = (_runner as RuntimeCodingAgentRunner)?.ResolveRegistryAuth(request.GetProperty("provider").GetString()!,
                    request.TryGetProperty("modelId", out var authModel) ? authModel.GetString() : null, token,
                    request.TryGetProperty("modelType", out var authType) ? authType.GetString() ?? ModelTypes.Chat : ModelTypes.Chat);
            else if (operation == "modelRegistryStream")
            {
                if (_runner is not RuntimeCodingAgentRunner modelRunner || _extensions is null) throw new InvalidOperationException("Model registry streaming is unavailable.");
                await modelRunner.StreamRegistryModelAsync(request, _extensions, onProgress, token).ConfigureAwait(false);
            }
            else if (operation == "modelRegistryCapability")
            {
                if (_runner is not RuntimeCodingAgentRunner capabilityRunner || _extensions is null) throw new InvalidOperationException("Model registry capabilities are unavailable.");
                value = await capabilityRunner.CallRegistryCapabilityAsync(request, _extensions, token).ConfigureAwait(false);
            }
            else if (operation == "modelRegistryAvailable")
            {
                if (_runner is not RuntimeCodingAgentRunner availableRunner || _extensions is null) throw new InvalidOperationException("Model registry availability is unavailable.");
                value = await availableRunner.GetAvailableRegistryModelsAsync(request, _extensions, token).ConfigureAwait(false);
            }
            else if (operation == "deliverReplacedMessage")
            {
                if (_runner is not RuntimeCodingAgentRunner deliveryRunner) throw new InvalidOperationException("The runner does not support message delivery.");
                await deliveryRunner.DeliverReplacementMessagesAsync(CodingAgentJavaScriptExtensionRuntime.ReadMessageActions(request,
                    request.GetProperty("filePath").GetString()!), token).ConfigureAwait(false);
            }
            else if (operation == "setModel")
            {
                var provider = request.GetProperty("provider").GetString()!;
                var id = request.GetProperty("modelId").GetString()!;
                var model = GetExtensionModels().FirstOrDefault(candidate => candidate.Provider == provider && candidate.Id == id);
                value = model is not null && _runner.GetAuthStatus(provider).IsConfigured;
                if (value is true)
                {
                    // 2. 【CodingAgent】【模型事件】处理器可能保存会话或再次切换模型，执行期间不持有桥接锁
                    if (_runner is RuntimeCodingAgentRunner modelRunner) await modelRunner.SelectModelWithSourceAsync(provider, id, "set", token).ConfigureAwait(false);
                    else _runner.SelectModel(provider, id);
                    _runner.ThinkingLevel = CodingAgentThinkingLevels.ClampForModel(_runner.Model, _runner.ThinkingLevel);
                    lock (_gate)
                    {
                        _tree?.SyncFromRunner(_runner);
                        _flat?.Save(_runner.Messages, _runner.Model, _runner.SessionName, CodingAgentThinkingLevels.Format(_runner.ThinkingLevel));
                    }
                }
            }
            else if (operation == "getApiKey")
            {
                // 2. 【CodingAgent】【认证重入】认证回调可能再次查询宿主，等待期间不得持有会话锁
                var selected = ((_runner as RuntimeCodingAgentRunner)?.GetAllRegistryModels() ?? GetExtensionModels()).FirstOrDefault(candidate => candidate.Provider == request.GetProperty("provider").GetString()
                    && candidate.Id == request.GetProperty("modelId").GetString() && ModelTypes.GetModelType(candidate) ==
                    (request.TryGetProperty("modelType", out var keyType) ? keyType.GetString() ?? ModelTypes.Chat : ModelTypes.Chat));
                value = selected is null ? null : RequireRuntimeControl().ResolveModelApiKey(selected);
            }
            else throw new InvalidOperationException("Unknown extension host request.");
            // 3. 【CodingAgent】【扩展调用】宿主响应携带实际状态，避免异步操作成功后仍看到调用开始时的模型
            if (operation is not ("executeNestedTool" or "refresh" or "modelRegistryStream" or "modelRegistryCapability")) token.ThrowIfCancellationRequested();
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("id", request.GetProperty("id").GetString());
                writer.WriteBoolean("ok", true);
                writer.WriteNumber("extensionGeneration", _extensions?.ResetGeneration ?? 0);
                writer.WritePropertyName("value");
                JsonSerializer.Serialize(writer, value);
                writer.WritePropertyName("runtime");
                WriteRuntimeSnapshot(writer);
                writer.WritePropertyName("session");
                JsonSerializer.Serialize(writer, Snapshot(), CodingAgentTreeSessionJsonContext.Default.CodingAgentExtensionSessionSnapshot);
                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
        {
            var message = ReadOperation(request) == "getProviderAuth" && ex is Tau.Ai.Auth.ProviderAuthException { InnerException: { } cause } ? cause.Message : ex.Message;
            return JsonSerializer.Serialize(new { id = request.GetProperty("id").GetString(), ok = false, error = message });
        }
    }

    /// <summary>【CodingAgent】【宿主错误】读取操作名称供认证错误解包，不触发其他运行状态读取。</summary>
    /// <param name="request">请求。</param><returns>操作名称。</returns>
    private static string? ReadOperation(JsonElement request) => request.TryGetProperty("operation", out var value) ? value.GetString() : null;

    /// <summary>【CodingAgent】【扩展压缩】生成真实摘要并在同一运行事务内提交到持久树或内存会话。</summary>
    /// <param name="request">包含可选摘要要求的宿主请求。</param>
    /// <param name="token">扩展进程生命周期取消信号。</param>
    /// <returns>供 onComplete 使用的摘要、保留边界及用量结果。</returns>
    private async Task<object> CompactSessionAsync(JsonElement request, CancellationToken token)
    {
        if (_runner is not RuntimeCodingAgentRunner runner) throw new InvalidOperationException("The runner does not support extension compaction.");
        var instructions = request.TryGetProperty("customInstructions", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
        var result = await runner.CompactAsync(instructions, token).ConfigureAwait(false);
        return new { summary = result.Summary, firstKeptEntryId = result.FirstKeptEntryId, tokensBefore = result.TokensBefore,
            estimatedTokensAfter = result.EstimatedTokensAfter, usage = result.Usage, details = result.Details };
    }
}
