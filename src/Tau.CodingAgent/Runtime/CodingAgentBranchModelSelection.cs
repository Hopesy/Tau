// 作者：xxx
using Tau.Ai;
using Tau.Ai.Registry;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【模型恢复】结合分支历史和当前目录恢复模型选择。</summary>
internal static class CodingAgentBranchModelSelection
{
    /// <summary>【CodingAgent】【分支选择】有效虚拟声明保持选择，其他情况优先采用最后实际响应的模型。</summary>
    /// <param name="snapshot">包含完整分支或兼容平面历史的快照。</param>
    /// <param name="catalog">当前扩展注册后的模型目录。</param><returns>恢复的提供方和模型标识。</returns>
    internal static (string? Provider, string? Model) Resolve(CodingAgentSessionSnapshot snapshot, ModelCatalog catalog)
    {
        // 1. 【CodingAgent】【原生分支】只有响应之前最近一次模型变更能够保持虚拟选择
        if (snapshot.BranchEntries is { } branch)
        {
            for (var index = branch.Count - 1; index >= 0; index--)
            {
                var entry = branch[index];
                if (entry.Type == "model_change") return (entry.Provider, entry.Model);
                if (entry.Type != "message" || entry.Message is not { Role: "assistant", Api: not "pi-virtual", Provider: { } provider, Model: { } model }) continue;
                for (var previous = index - 1; previous >= 0; previous--)
                {
                    var change = branch[previous];
                    if (change.Type != "model_change") continue;
                    if (change.Provider is { } changedProvider && change.Model is { } changedModel &&
                        catalog.TryGetModel(changedProvider, changedModel) is { Api: "pi-virtual" })
                        return (changedProvider, changedModel);
                    break;
                }
                return (provider, model);
            }
        }
        // 2. 【CodingAgent】【兼容快照】没有变更时间线的旧快照保留现有选择，仅在目录移除后使用实际响应
        else if (snapshot.Provider is { } selectedProvider && snapshot.Model is { } selectedModel &&
            catalog.TryGetModel(selectedProvider, selectedModel) is null &&
            snapshot.Messages.OfType<AssistantMessage>().LastOrDefault(message => message.Api != "pi-virtual" &&
                message.Provider is not null && message.Model is not null) is { } response)
            return (response.Provider, response.Model);
        return (snapshot.Provider, snapshot.Model);
    }
}
