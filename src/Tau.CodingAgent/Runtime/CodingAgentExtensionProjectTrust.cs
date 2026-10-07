// 作者：xxx
using System.Text.Json;

namespace Tau.CodingAgent.Runtime;

public sealed partial class CodingAgentExtensionCommandStore
{
    /// <summary>【CodingAgent】【启动信任】仅在项目扩展尚未加载时调用，首个明确扩展决定生效，错误隔离并上报。</summary>
    /// <param name="reportError">扩展错误接收器。</param><param name="token">取消信号。</param><returns>首个明确决定，没有决定时为空。</returns>
    internal async Task<CodingAgentProjectTrustDecision?> ResolveProjectTrustEventAsync(Action<string>? reportError, CancellationToken token)
    {
        var payload = JsonSerializer.SerializeToElement(new { type = "project_trust", cwd = _cwd });
        foreach (var module in LoadStatus().EventHandlers.Where(module => module.EventType == "project_trust").DistinctBy(module => module.FilePath))
        {
            var result = await _javaScriptRuntime.EmitEventAsync(module.FilePath, payload, token).ConfigureAwait(false);
            if (!result.Success) reportError?.Invoke($"【CodingAgent】【项目信任】{module.FilePath}: {result.Error}");
            foreach (var error in result.HandlerErrors) reportError?.Invoke($"【CodingAgent】【项目信任】{module.FilePath}: {error}");
            if (result.TransformedEvent is not { } evt || !evt.TryGetProperty("handlerResult", out var decision) || decision.ValueKind != JsonValueKind.Object) continue;
            var trusted = decision.TryGetProperty("trusted", out var value) ? value.GetString() : null;
            if (trusted is "yes" or "no") return new(trusted, decision.TryGetProperty("remember", out var remember) && remember.ValueKind == JsonValueKind.True);
        }
        return null;
    }
}

public static partial class CodingAgentProjectTrustBootstrap
{
    /// <summary>【CodingAgent】【信任引导】先加载全局资源并决策，再统一更新设置、扩展和包资源的项目访问权限。</summary>
    /// <param name="cwd">项目目录。</param><param name="agentDirectory">全局 Agent 目录。</param>
    /// <param name="settings">设置管理器。</param><param name="packages">包管理器。</param><param name="resources">可更新包资源。</param>
    /// <param name="extensions">扩展管理器。</param><param name="trustOverride">显式决定。</param>
    /// <param name="select">可选交互选择器。</param><param name="reportError">扩展错误接收器。</param><param name="token">取消信号。</param>
    /// <param name="input">可选文本输入器。</param><param name="notify">启动通知接收器。</param><param name="mode">宿主运行模式。</param>
    /// <returns>项目最终信任状态。</returns>
    public static async Task<bool> ResolveAsync(string cwd, string agentDirectory, CodingAgentSettingsStore settings,
        CodingAgentPackageManager packages, CodingAgentPackageResourceState resources, CodingAgentExtensionCommandStore extensions,
        bool? trustOverride = null, Func<string, IReadOnlyList<string>, CancellationToken, Task<string?>>? select = null,
        Action<string>? reportError = null, CancellationToken token = default,
        Func<string, string?, CancellationToken, Task<string?>>? input = null, Action<string>? notify = null, string mode = "print")
    {
        settings.SetProjectTrusted(false);
        packages.IsProjectTrusted = false;
        extensions.IsProjectTrusted = false;
        resources.Update(packages.ResolveResources());
        var global = settings.LoadGlobal();
        var policy = global.AdditionalSettings?.GetValueOrDefault("defaultProjectTrust");
        var bridge = select is null ? null : CreateUiBridge(select, input, notify);
        extensions.SetExtensionUiBridge(bridge, mode);
        bool trusted;
        try
        {
            trusted = await CodingAgentProjectTrust.ResolveAsync(new(cwd, new(agentDirectory))
            {
                Override = trustOverride,
                DefaultPolicy = policy is { ValueKind: JsonValueKind.String } ? policy.Value.GetString()! : "ask",
                ExtensionDecision = ct => extensions.ResolveProjectTrustEventAsync(reportError, ct),
                Select = select
            }, token).ConfigureAwait(false);
        }
        finally
        {
            bridge?.Close();
            extensions.SetExtensionUiBridge(null, mode);
        }
        settings.SetProjectTrusted(trusted);
        packages.IsProjectTrusted = trusted;
        extensions.IsProjectTrusted = trusted;
        resources.Update(packages.ResolveResources());
        return trusted;
    }

    /// <summary>【CodingAgent】【启动交互】为信任扩展提供 select、confirm、input、notify，决策完成后由宿主关闭桥接。</summary>
    /// <param name="select">选择器。</param><param name="input">文本输入器。</param><param name="notify">通知接收器。</param>
    /// <returns>已连接的启动交互桥接器。</returns>
    private static CodingAgentRpcExtensionUiBridge CreateUiBridge(Func<string, IReadOnlyList<string>, CancellationToken, Task<string?>> select,
        Func<string, string?, CancellationToken, Task<string?>>? input, Action<string>? notify)
    {
        var bridge = new CodingAgentRpcExtensionUiBridge();
        bridge.Attach(async (request, token) =>
        {
            var json = JsonSerializer.SerializeToElement(request);
            var method = json.GetProperty("method").GetString();
            if (method == "notify") { notify?.Invoke(json.GetProperty("message").GetString()!); return; }
            if (!json.TryGetProperty("id", out var id)) return;
            var title = json.GetProperty("title").GetString()!;
            string? value = null;
            if (method == "select") value = await select(title, json.GetProperty("options").EnumerateArray().Select(option => option.GetString()!).ToArray(), token).ConfigureAwait(false);
            else if (method == "confirm") value = await select(title + "\n" + json.GetProperty("message").GetString(), ["Yes", "No"], token).ConfigureAwait(false);
            else if (method == "input" && input is not null) value = await input(title,
                json.TryGetProperty("placeholder", out var placeholder) ? placeholder.GetString() : null, token).ConfigureAwait(false);
            bridge.TryHandleResponse(JsonSerializer.SerializeToElement(new { id = id.GetString(), value, confirmed = value == "Yes", cancelled = value is null }));
        });
        return bridge;
    }
}
