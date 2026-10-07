// 作者：xxx
using System.Text.Json;
using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Runtime;

public sealed partial class CodingAgentJavaScriptExtensionRuntime
{
    /// <summary>【CodingAgent】【条目渲染】将原始自定义条目、展开状态和宽度送入其所属模块。</summary>
    /// <param name="filePath">注册模块路径。</param><param name="entry">完整原生条目。</param>
    /// <param name="expanded">是否展开。</param><param name="width">组件可用列数。</param>
    /// <returns>渲染文本或错误；空文本表示隐藏条目。</returns>
    public CodingAgentJavaScriptExtensionMessageRenderResult RenderEntry(string filePath, JsonElement entry, bool expanded = false, int width = 80)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        var arguments = JsonSerializer.SerializeToElement(new { entry, width });
        var execution = Execute(BuildPayload("renderEntry", filePath, _cwd, toolArgs: arguments, expanded: expanded));
        if (!execution.Success) return new(false, [], execution.Error);
        try
        {
            using var document = JsonDocument.Parse(execution.ResultJson);
            var root = document.RootElement;
            return ReadBool(root, "ok") ? new(true, ReadStringArray(root, "lines"), null)
                : new(false, [], ReadString(root, "error") ?? "javascript extension entry renderer failed");
        }
        catch (JsonException error) { return new(false, [], $"invalid node extension runtime output: {error.Message}"); }
    }
}

public sealed partial class CodingAgentExtensionCommandStore
{
    /// <summary>【CodingAgent】【条目渲染注册】按模块加载顺序返回去重后的条目渲染器。</summary>
    /// <returns>当前有效的渲染器列表。</returns>
    public IReadOnlyList<CodingAgentExtensionEntryRenderer> LoadEntryRenderers() => LoadStatus().EntryRenderers;

    /// <summary>【CodingAgent】【条目渲染】缺少或返回空组件时隐藏条目，渲染异常显示错误并继续会话。</summary>
    /// <param name="entry">完整原始条目。</param><param name="rendered">可显示正文或空值。</param>
    /// <param name="expanded">是否展开。</param><param name="width">组件列数。</param><returns>是否有可显示内容。</returns>
    public bool TryRenderCustomEntry(JsonElement entry, out string? rendered, bool expanded = false, int width = 80)
    {
        rendered = null;
        if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("customType", out var customType) ||
            customType.ValueKind != JsonValueKind.String) return false;
        var type = customType.GetString()!;
        var renderer = LoadEntryRenderers().FirstOrDefault(item => item.CustomType == type);
        if (renderer is null) return false;
        var result = _javaScriptRuntime.RenderEntry(renderer.FilePath, entry, expanded, width);
        rendered = result.Success ? string.Join("\n", result.Lines) : $"[{type}] renderer failed: {result.Error}";
        return !string.IsNullOrWhiteSpace(rendered);
    }
}

public sealed partial class RuntimeCodingAgentRunner
{
    /// <summary>【CodingAgent】【条目通知】持久化后排队通知宿主，离开同步 Node 操作通道后才调用渲染器。</summary>
    /// <param name="entry">实际保存的自定义条目。</param>
    internal void NotifyCustomEntryAppended(CodingAgentTreeSessionEntry entry)
    {
        var json = JsonSerializer.SerializeToElement(entry, CodingAgentTreeSessionJsonContext.Default.CodingAgentTreeSessionEntry);
        QueueStateNotification(JsonSerializer.SerializeToElement(new { type = "entry_appended", entry = json }), new CodingAgentEntryAppendedEvent(json));
    }
}

public sealed partial class CodingAgentHost
{
    private readonly Dictionary<string, JsonElement> _displayedCustomEntries = new(StringComparer.Ordinal);

    /// <summary>【CodingAgent】【条目显示】通过稳定条目标识更新显示，空组件移除旧显示，实时和恢复共用此入口。</summary>
    /// <param name="entry">已经持久化或位于历史快照中的原生条目。</param>
    private void RenderCustomEntry(JsonElement entry)
    {
        if (!entry.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) return;
        var key = "entry:" + id.GetString();
        _displayedCustomEntries[key] = entry.Clone();
        if (_extensionCommandStore?.TryRenderCustomEntry(entry, out var text, _toolOutputExpanded) == true)
            _ui.WriteCustomMessage(text!, key);
        else _ui.RemoveTranscriptEntry(TranscriptEntryKind.Custom, key);
    }
}
