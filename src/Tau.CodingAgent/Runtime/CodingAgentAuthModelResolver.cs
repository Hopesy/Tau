// 作者：xxx
using System.Text.RegularExpressions;
using Tau.Ai;
using Tau.Ai.Providers;
using Tau.Ai.Registry;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【认证模型选择】解析明确提供方、规范引用、字面斜杠 ID、别名及推理后缀。</summary>
internal static class CodingAgentAuthModelResolver
{
    /// <summary>【CodingAgent】【模型匹配】按上游 CLI 顺序匹配模型，裸 ID 重名时仅选唯一已认证提供方。</summary>
    /// <param name="models">统一模型集合。</param><param name="providerId">可选提供方。</param><param name="reference">模型模式。</param>
    /// <param name="thinking">显式推理等级。</param><param name="token">取消信号。</param><returns>模型及是否由自定义 ID 回退生成。</returns>
    public static async Task<(Model Model, bool Custom)> ResolveAsync(Models models, string? providerId, string reference, string? thinking, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var all = models.GetModels();
        if (all.Count == 0) throw new CodingAgentAuthCommandException("No models available. Check your installation or add models to models.json.");
        var provider = providerId is null ? null : all.FirstOrDefault(model => Same(model.Provider, providerId))?.Provider;
        if (providerId is not null && provider is null) throw new CodingAgentAuthCommandException($"Unknown provider \"{providerId}\". Use --list-models to see available providers/models.");
        var pattern = reference; var inferred = false;
        if (provider is null && reference.IndexOf('/') is var slash && slash >= 0)
        {
            provider = all.FirstOrDefault(model => Same(model.Provider, reference[..slash]))?.Provider;
            if (provider is not null) { pattern = reference[(slash + 1)..]; inferred = true; }
        }
        // 1. 【CodingAgent】【重名选择】裸 ID 不能由目录顺序静默选择未认证的提供方
        if (provider is null)
        {
            var exact = all.Where(model => Same(model.Id, reference) || Same(model.Provider + "/" + model.Id, reference)).ToArray();
            if (exact.Length == 1) return (exact[0], false);
            if (exact.Length > 1)
            {
                var authenticated = new List<Model>();
                foreach (var model in exact) if ((await models.CheckAuthAsync(model.Provider, cancellationToken: token).ConfigureAwait(false))?.IsConfigured == true) authenticated.Add(model);
                if (authenticated.Count == 1) return (authenticated[0], false);
                throw new CodingAgentAuthCommandException($"Model \"{reference}\" is ambiguous across providers: {string.Join(", ", exact.Select(model => model.Provider + "/" + model.Id).Order(StringComparer.Ordinal))}. "
                    + (authenticated.Count == 0 ? "No matching provider is authenticated." : "More than one matching provider is authenticated.") + " Use --provider or provider/model.");
            }
        }
        if (providerId is not null && reference.StartsWith(provider + "/", StringComparison.OrdinalIgnoreCase)) pattern = reference[(provider!.Length + 1)..];
        var candidates = provider is null ? all : all.Where(model => model.Provider == provider).ToArray();
        var selected = MatchPattern(pattern, candidates);
        if (selected is not null)
        {
            // 2. 【CodingAgent】【斜杠 ID】推断提供方未认证时，唯一已认证的完整字面 ID 可以优先
            if (inferred)
            {
                var raw = all.Where(model => Same(model.Id, reference) && !(Same(model.Provider, selected.Provider) && Same(model.Id, selected.Id))).ToArray();
                if (raw.Length > 0 && (await models.CheckAuthAsync(selected.Provider, cancellationToken: token).ConfigureAwait(false))?.IsConfigured != true)
                {
                    var authenticated = new List<Model>();
                    foreach (var model in raw) if ((await models.CheckAuthAsync(model.Provider, cancellationToken: token).ConfigureAwait(false))?.IsConfigured == true) authenticated.Add(model);
                    if (authenticated.Count == 1) return (authenticated[0], false);
                }
            }
            return (selected, false);
        }
        if (inferred)
        {
            var fallback = all.FirstOrDefault(model => Same(model.Id, reference) || Same(model.Provider + "/" + model.Id, reference)) ?? MatchPattern(reference, all);
            if (fallback is not null) return (fallback, false);
        }
        // 3. 【CodingAgent】【自定义模型】明确提供方可继承已有协议元数据，打印命令的跨提供方搜索会排除此回退
        if (provider is not null && candidates.Count > 0)
        {
            var suffix = pattern.LastIndexOf(':'); string? fallbackThinking = null;
            if (thinking is null && suffix >= 0 && IsThinking(pattern[(suffix + 1)..])) { fallbackThinking = pattern[(suffix + 1)..]; pattern = pattern[..suffix]; }
            var basis = candidates.FirstOrDefault(model => model.Id == ModelCatalog.GetDefaultModelId(provider)) ?? candidates[0];
            return (basis with { Id = pattern, Name = pattern, Reasoning = (thinking ?? fallbackThinking) is { } level && level != "off" || basis.Reasoning }, true);
        }
        throw new CodingAgentAuthCommandException($"Model \"{(provider is null ? reference : provider + "/" + pattern)}\" not found. Use --list-models to see available models.");
    }

    /// <summary>【CodingAgent】【模型模式】完整匹配优先，其次部分名称；只在未匹配时剥离有效推理后缀。</summary>
    /// <param name="pattern">模式。</param><param name="models">候选模型。</param><returns>选中模型或空值。</returns>
    private static Model? MatchPattern(string pattern, IReadOnlyList<Model> models)
    {
        var canonical = models.Where(model => Same(model.Provider + "/" + model.Id, pattern.Trim())).ToArray();
        if (canonical.Length == 1) return canonical[0];
        var exact = models.Where(model => Same(model.Id, pattern.Trim())).ToArray();
        if (canonical.Length == 0 && exact.Length == 1) return exact[0];
        var partial = models.Where(model => model.Id.Contains(pattern, StringComparison.OrdinalIgnoreCase) || model.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (partial.Length > 0)
        {
            var aliases = partial.Where(model => !Regex.IsMatch(model.Id, "-[0-9]{8}$")).ToArray();
            return (aliases.Length > 0 ? aliases : partial).OrderByDescending(model => model.Id, StringComparer.Ordinal).First();
        }
        var colon = pattern.LastIndexOf(':');
        return colon >= 0 && IsThinking(pattern[(colon + 1)..]) ? MatchPattern(pattern[..colon], models) : null;
    }

    /// <summary>【CodingAgent】【推理后缀】只接受上游 CLI 的原生等级标识。</summary><param name="value">后缀。</param><returns>是否有效。</returns>
    private static bool IsThinking(string value) => value is "off" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max";
    /// <summary>【CodingAgent】【标识比较】按不区分大小写的 CLI 规则比较。</summary><param name="left">左值。</param><param name="right">右值。</param><returns>是否相同。</returns>
    private static bool Same(string left, string right) => left.Equals(right, StringComparison.OrdinalIgnoreCase);
}
