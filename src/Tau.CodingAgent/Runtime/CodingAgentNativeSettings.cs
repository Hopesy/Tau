// 作者：xxx
using System.Text.Json;
using System.Text.Json.Nodes;
using Tau.AgentCore.Harness;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【原生设置】读取 pi 嵌套配置，保留模型覆盖和尚未消费的字段。</summary>
public static class CodingAgentNativeSettings
{
    /// <summary>【CodingAgent】【模型压缩配置】按精确 provider/model 键、通用设置及默认值逐项解析保留窗口。</summary>
    /// <param name="settings">会话设置。</param>
    /// <param name="model">当前模型，可为空。</param>
    /// <returns>已验证的压缩设置。</returns>
    public static AgentCompactionSettings GetCompactionSettings(this CodingAgentSettingsSnapshot settings, Model? model = null)
    {
        var ordinaryReserve = ReadInteger(settings.Compaction, "reserveTokens", "compaction");
        var ordinaryKeep = ReadInteger(settings.Compaction, "keepRecentTokens", "compaction");
        JsonElement? modelOverride = null;
        var key = model is null ? null : model.Provider + "/" + model.Id;
        if (key is not null && settings.Compaction is { ValueKind: JsonValueKind.Object } compaction
            && compaction.TryGetProperty("modelOverrides", out var overrides) && overrides.ValueKind == JsonValueKind.Object
            && overrides.TryGetProperty(key, out var candidate))
        {
            if (candidate.ValueKind != JsonValueKind.Object) throw new InvalidOperationException($"Invalid compaction.modelOverrides[\"{key}\"] setting: expected an object.");
            modelOverride = candidate;
        }
        return new(settings.AutoCompactionEnabled ?? ReadBoolean(settings.Compaction, "enabled", "compaction") ?? true,
            ReadInteger(modelOverride, "reserveTokens", $"compaction.modelOverrides[\"{key}\"]") ?? ordinaryReserve ?? 16384,
            ReadInteger(modelOverride, "keepRecentTokens", $"compaction.modelOverrides[\"{key}\"]") ?? ordinaryKeep ?? 20000);
    }

    /// <summary>【CodingAgent】【分支预算】读取分支摘要的输入与输出保留预算。</summary>
    /// <param name="settings">会话设置。</param>
    /// <returns>保留 token 数。</returns>
    public static int GetBranchSummaryReserveTokens(this CodingAgentSettingsSnapshot settings) => ReadInteger(settings.BranchSummary, "reserveTokens", "branchSummary") ?? 16384;

    /// <summary>【CodingAgent】【摘要询问】读取是否跳过分支摘要询问。</summary>
    /// <param name="settings">会话设置。</param>
    /// <returns>是否跳过询问。</returns>
    public static bool GetBranchSummarySkipPrompt(this CodingAgentSettingsSnapshot settings) => ReadBoolean(settings.BranchSummary, "skipPrompt", "branchSummary") ?? false;

    /// <summary>【CodingAgent】【重试兼容】原生 enabled 和 maxRetries 优先于旧平面字段。</summary>
    /// <param name="retry">原生重试对象。</param>
    /// <param name="legacy">旧额外重试次数。</param>
    /// <returns>有效次数，未配置时为空。</returns>
    internal static int? ReadRetryCount(JsonElement? retry, int? legacy)
    {
        var enabled = ReadBoolean(retry, "enabled", "retry");
        var count = ReadInteger(retry, "maxRetries", "retry");
        return enabled == false ? 0 : count ?? (enabled == true && legacy is 0 ? 3 : legacy);
    }

    /// <summary>【CodingAgent】【配置整数】拒绝负数、小数和错误类型，避免无声使用错误预算。</summary>
    /// <param name="container">设置对象。</param>
    /// <param name="field">字段名称。</param>
    /// <param name="path">诊断路径。</param>
    /// <returns>非负整数或未配置。</returns>
    internal static int? ReadInteger(JsonElement? container, string field, string path)
    {
        if (container is null) return null;
        if (container.Value.ValueKind != JsonValueKind.Object) throw new InvalidOperationException($"Invalid {path} setting: expected an object.");
        if (!container.Value.TryGetProperty(field, out var value)) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < 0)
            throw new InvalidOperationException($"Invalid {path}.{field} setting: expected a non-negative integer.");
        return number;
    }

    /// <summary>【CodingAgent】【配置开关】读取可选布尔字段，错误类型明确报告。</summary>
    /// <param name="container">设置对象。</param>
    /// <param name="field">字段名称。</param>
    /// <param name="path">诊断路径。</param>
    /// <returns>布尔值或未配置。</returns>
    internal static bool? ReadBoolean(JsonElement? container, string field, string path)
    {
        if (container is null) return null;
        if (container.Value.ValueKind != JsonValueKind.Object) throw new InvalidOperationException($"Invalid {path} setting: expected an object.");
        if (!container.Value.TryGetProperty(field, out var value)) return null;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidOperationException($"Invalid {path}.{field} setting: expected a boolean.");
        return value.GetBoolean();
    }

    /// <summary>【CodingAgent】【压缩保存】同步旧开关到原生对象，保留模型覆盖和其他字段。</summary>
    /// <param name="settings">待保存设置。</param>
    /// <returns>合并后的 JSON 对象。</returns>
    internal static JsonElement? MergeCompaction(CodingAgentSettingsSnapshot settings)
    {
        if (settings.Compaction is null && settings.AutoCompactionEnabled is null) return null;
        var value = settings.Compaction is { } original ? JsonNode.Parse(original.GetRawText())!.AsObject() : new JsonObject();
        if (settings.AutoCompactionEnabled is { } enabled) value["enabled"] = enabled;
        return JsonSerializer.SerializeToElement(value);
    }

    /// <summary>【CodingAgent】【重试保存】同步旧次数及等待字段，保留 provider 配置与原生延迟上限。</summary>
    /// <param name="settings">待保存设置。</param>
    /// <returns>合并后的 JSON 对象。</returns>
    internal static JsonElement? MergeRetry(CodingAgentSettingsSnapshot settings)
    {
        if (settings.Retry is null && settings.RetryMaxAttempts is null && settings.RetryBaseDelayMilliseconds is null) return null;
        var value = settings.Retry is { } original ? JsonNode.Parse(original.GetRawText())!.AsObject() : new JsonObject();
        if (settings.RetryMaxAttempts is { } count)
        {
            value["enabled"] = count > 0;
            if (count > 0 || value["maxRetries"] is null) value["maxRetries"] = Math.Max(0, count);
        }
        if (settings.RetryBaseDelayMilliseconds is { } delay) value["baseDelayMs"] = Math.Max(0, delay);
        return JsonSerializer.SerializeToElement(value);
    }

    /// <summary>【CodingAgent】【设置更新】按字段合并原生设置对象，显式空值删除对应覆盖。</summary>
    /// <param name="original">原对象，可为空。</param>
    /// <param name="patch">待合并对象。</param>
    /// <returns>独立的合并结果。</returns>
    internal static JsonElement MergeObject(JsonElement? original, JsonElement patch)
    {
        if (patch.ValueKind != JsonValueKind.Object) throw new ArgumentException("Settings update must be an object.");
        var value = original is { ValueKind: JsonValueKind.Object } previous ? JsonNode.Parse(previous.GetRawText())!.AsObject() : new JsonObject();
        foreach (var property in patch.EnumerateObject())
            if (property.Value.ValueKind == JsonValueKind.Null) value.Remove(property.Name);
            else value[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        return JsonSerializer.SerializeToElement(value);
    }
}

public sealed partial class RuntimeCodingAgentRunner
{
    private CodingAgentSettingsStore? _sessionSettings;
    public bool IsProjectTrusted => _sessionSettings?.IsProjectTrusted ?? true;

    /// <summary>【CodingAgent】【摘要询问】是否按原生设置跳过可选分支摘要询问。</summary>
    internal bool BranchSummarySkipPrompt => _sessionSettings?.Load().GetBranchSummarySkipPrompt() == true;

    /// <summary>【CodingAgent】【配置绑定】绑定会话设置，压缩时根据当前模型重新解析覆盖项。</summary>
    /// <param name="store">会话设置存储。</param>
    public void ConfigureSessionSettings(CodingAgentSettingsStore store)
    {
        _sessionSettings = store;
        SummaryRetryOptions = CodingAgentRetryOptions.FromSettingsOrEnvironment(store.Load());
    }

    /// <summary>【CodingAgent】【提供方重试】将原生 provider 超时和重试配置带入每次模型请求，显式调用选项优先。</summary>
    /// <param name="options">调用方请求选项。</param>
    /// <returns>补充会话重试和超时的选项。</returns>
    private SimpleStreamOptions ResolveProviderRequestOptions(SimpleStreamOptions? options)
    {
        options ??= new();
        var settings = _sessionSettings?.Load();
        if (settings is null) return options;
        JsonElement? provider = settings.Retry is { ValueKind: JsonValueKind.Object } retry && retry.TryGetProperty("provider", out var value) ? value : null;
        var timeout = CodingAgentNativeSettings.ReadInteger(provider, "timeoutMs", "retry.provider");
        var retries = CodingAgentNativeSettings.ReadInteger(provider, "maxRetries", "retry.provider");
        var maximum = CodingAgentNativeSettings.ReadInteger(provider, "maxRetryDelayMs", "retry.provider") ?? 60000;
        return options with
        {
            Timeout = options.Timeout ?? (timeout is { } milliseconds ? TimeSpan.FromMilliseconds(milliseconds) : null),
            MaxRetries = options.MaxRetries ?? retries,
            MaxRetryDelay = options.MaxRetryDelay ?? TimeSpan.FromMilliseconds(maximum)
        };
    }
}
