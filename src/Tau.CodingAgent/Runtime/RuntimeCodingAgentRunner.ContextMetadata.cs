// 作者：xxx
namespace Tau.CodingAgent.Runtime;

public sealed partial class RuntimeCodingAgentRunner
{
    private IReadOnlyList<CodingAgentSdkScopedModel>? _scopedModelOverrides;
    private IReadOnlyList<string>? _scopedModelPatterns;

    /// <summary>【CodingAgent】【基础提示】读取独立的基础构建选项，当前回合的扩展临时修改不会污染返回结果。</summary>
    /// <returns>可以安全修改的基础提示配置副本。</returns>
    public CodingAgentSystemPromptOptions GetSystemPromptOptions() => CodingAgentSystemPrompt.Normalize(FilterHiddenToolSnippets(
        _baseSystemPromptOptions ?? new() { Cwd = _workingDirectory, CustomPrompt = _initialSystemPrompt,
            SelectedTools = GetActiveToolNames().ToList() }));

    /// <summary>【CodingAgent】【SDK模型范围】设置显式模型及思考级别；空值重新采用模式或设置文件。</summary>
    /// <param name="models">SDK 提供的模型范围。</param>
    public void SetScopedModels(IReadOnlyList<CodingAgentSdkScopedModel>? models) => _scopedModelOverrides = models?.ToArray();

    /// <summary>【CodingAgent】【CLI模型范围】设置命令行模式；空值重新采用设置文件。</summary>
    /// <param name="patterns">模型通配符或精确标识。</param>
    internal void ConfigureScopedModelPatterns(IReadOnlyList<string>? patterns)
    {
        if (patterns is not null) _scopedModelPatterns = patterns.ToArray();
    }

    /// <summary>【CodingAgent】【扩展模型范围】返回会话范围快照，未配置时为空，不隐式扩展为全部模型。</summary>
    /// <returns>保留每个模型思考等级的独立集合。</returns>
    public IReadOnlyList<CodingAgentSdkScopedModel> GetScopedModels()
    {
        if (_scopedModelOverrides is { } explicitModels) return explicitModels.ToArray();
        var patterns = _scopedModelPatterns ?? _sessionSettings?.Load().EnabledModels;
        if (patterns is not { Count: > 0 }) return [];
        var models = GetProviders().SelectMany(GetModels).ToArray();
        var result = new List<CodingAgentSdkScopedModel>();
        // 1. 【CodingAgent】【范围解析】失效条目不抹掉其他有效条目，同一模型只保留第一次出现的等级
        foreach (var pattern in patterns)
            if (CodingAgentModelAvailability.TryResolveScopedModelEntries([pattern], models, out var entries, out _))
                foreach (var entry in entries)
                    if (!result.Any(existing => existing.Model.Provider == entry.Model.Provider && existing.Model.Id == entry.Model.Id))
                        result.Add(new(entry.Model, entry.ThinkingLevel));
        return result;
    }
}
