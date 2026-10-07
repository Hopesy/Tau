// 作者：xxx
using System.Text.Json;
using Tau.Tui.Abstractions;
using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【快捷键配置】提供应用与终端的动作定义，读取原生命名配置并支持重新加载。</summary>
public sealed class CodingAgentKeybindings : TuiKeybindingsManager
{
    private readonly string? _configPath;

    /// <summary>【CodingAgent】【快捷键初始化】按目标平台构建默认动作，再应用完整用户覆盖。</summary>
    /// <param name="bindings">用户覆盖。</param><param name="configPath">重新加载路径。</param><param name="platform">win32、linux 或 darwin，缺省使用当前平台。</param>
    /// <param name="environment">平台判断环境，缺省读取进程环境。</param>
    public CodingAgentKeybindings(IReadOnlyDictionary<string, IReadOnlyList<string>>? bindings = null, string? configPath = null,
        string? platform = null, IReadOnlyDictionary<string, string>? environment = null)
        : base(CreateDefinitions(platform, environment), bindings) => _configPath = configPath;

    /// <summary>【CodingAgent】【Windows 键位】Windows 和具有 WSL 标识的 Linux 使用终端兼容键位。</summary>
    /// <param name="platform">Node 风格平台名称。</param><param name="environment">可选环境覆盖。</param><returns>是否使用 Windows 兼容键位。</returns>
    public static bool UseWindowsKeybindings(string? platform = null, IReadOnlyDictionary<string, string>? environment = null)
    {
        platform ??= CurrentPlatform();
        return platform == "win32" || platform == "linux" &&
            (!string.IsNullOrEmpty(environment is null ? Environment.GetEnvironmentVariable("WSL_DISTRO_NAME") : environment.GetValueOrDefault("WSL_DISTRO_NAME")) ||
             !string.IsNullOrEmpty(environment is null ? Environment.GetEnvironmentVariable("WSL_INTEROP") : environment.GetValueOrDefault("WSL_INTEROP")));
    }

    /// <summary>【CodingAgent】【快捷键读取】读取指定配置；缺失、无效或不可访问时使用平台默认值。</summary>
    /// <param name="path">配置路径。</param><returns>保留重新加载路径的管理器。</returns>
    public static CodingAgentKeybindings LoadOrDefault(string? path) => new(ReadConfiguration(path), path);

    /// <summary>【CodingAgent】【编辑器配置】读取动作名格式，已有 bindings 数组继续使用旧版覆盖语义。</summary>
    /// <param name="path">可选快捷键文件。</param><returns>编辑器与宿主提示共用的绑定映射。</returns>
    public static IKeyBindingMap LoadEditorOrDefault(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            try
            {
                var text = File.ReadAllText(path).TrimStart('\ufeff');
                using var document = JsonDocument.Parse(text);
                if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("bindings", out var legacy) && legacy.ValueKind == JsonValueKind.Array)
                    return KeyBindingFileStore.Parse(text);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return CreateEditorBindings(LoadOrDefault(path));
    }

    /// <summary>【CodingAgent】【输入动作】接入当前宿主支持的应用动作，编辑动作由终端层按优先级解析。</summary>
    /// <param name="manager">当前命名快捷键配置。</param><returns>可随配置重载更新的映射。</returns>
    public static IKeyBindingMap CreateEditorBindings(CodingAgentKeybindings manager) => new TuiEditorKeyBindingMap(manager,
    [
        new("app.clipboard.pasteImage", EditorAction.PasteImage),
        new("app.interrupt", EditorAction.Interrupt),
        new("app.exit", EditorAction.ExitIfEmpty),
        new("tui.editor.historyPrevious", EditorAction.PromptHistoryPrevious),
        new("tui.editor.historyNext", EditorAction.PromptHistoryNext),
        new("app.clear", EditorAction.ClearEditor),
        new("app.thinking.cycle", EditorAction.CycleThinkingLevel),
        new("app.model.cycleForward", EditorAction.CycleModelForward),
        new("app.model.cycleBackward", EditorAction.CycleModelBackward),
        new("app.model.select", EditorAction.SelectModel),
        new("app.tools.expand", EditorAction.ToggleToolOutputExpansion),
        new("app.thinking.toggle", EditorAction.ToggleThinkingBlock),
        new("app.editor.external", EditorAction.OpenExternalEditor),
        new("app.message.copy", EditorAction.CopyLastMessage),
        new("app.message.followUp", EditorAction.QueueFollowUpMessage),
        new("app.message.dequeue", EditorAction.RestoreQueuedMessages),
        new("app.session.new", EditorAction.NewSession),
        new("app.session.tree", EditorAction.OpenSessionTree),
        new("app.session.fork", EditorAction.ForkSession),
        new("app.session.resume", EditorAction.ResumeSession)
    ]);

    /// <summary>【CodingAgent】【快捷键重载】重新读取原文件，文件被删除时恢复平台默认值。</summary>
    public void Reload()
    {
        if (_configPath is not null) SetUserBindings(ReadConfiguration(_configPath));
    }

    /// <summary>【CodingAgent】【配置解析】迁移旧动作名，显式的新名称优先于旧名称。</summary>
    /// <param name="json">原生格式配置 JSON。</param><returns>迁移后的覆盖。</returns>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ParseUserConfiguration(string json)
    {
        var values = ParseConfiguration(json);
        using var document = JsonDocument.Parse(json.TrimStart('\ufeff'));
        if (document.RootElement.ValueKind != JsonValueKind.Object) return values;
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (id, keys) in values)
        {
            var migrated = CodingAgentMigrations.KeybindingNameMigrations.GetValueOrDefault(id) ?? id;
            if (id != migrated && document.RootElement.TryGetProperty(migrated, out _)) continue;
            result[migrated] = keys;
        }
        return result;
    }

    /// <summary>【CodingAgent】【配置文件】仅接受字符串或全字符串数组，解析失败使用空覆盖。</summary>
    /// <param name="path">可选文件路径。</param><returns>用户覆盖。</returns>
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ReadConfiguration(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return new Dictionary<string, IReadOnlyList<string>>();
        try { return ParseUserConfiguration(File.ReadAllText(path)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { return new Dictionary<string, IReadOnlyList<string>>(); }
    }

    /// <summary>【CodingAgent】【平台名称】转换当前运行平台为上游使用的名称。</summary>
    /// <returns>Node 风格平台名称。</returns>
    private static string CurrentPlatform() => OperatingSystem.IsWindows() ? "win32" : OperatingSystem.IsMacOS() ? "darwin" : "linux";

    /// <summary>【CodingAgent】【动作目录】复制终端默认值后补齐应用动作与平台差异。</summary>
    /// <param name="platform">可选平台名称。</param><param name="environment">可选环境变量。</param><returns>完整默认定义。</returns>
    private static IReadOnlyDictionary<string, TuiKeybindingDefinition> CreateDefinitions(string? platform, IReadOnlyDictionary<string, string>? environment)
    {
        platform ??= CurrentPlatform();
        var windows = UseWindowsKeybindings(platform, environment);
        var definitions = TuiKeybindingDefinitions.All.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        definitions["app.interrupt"] = new(["escape"], "Cancel or abort");
        definitions["app.clear"] = new(["ctrl+c"], "Clear editor");
        definitions["app.exit"] = new(["ctrl+d"], "Exit when editor is empty");
        definitions["app.thinking.cycle"] = new(["shift+tab"], "Cycle thinking level");
        definitions["app.thinking.save"] = new(["ctrl+s"], "Save thinking level");
        definitions["app.model.cycleForward"] = new(["ctrl+p"], "Cycle to next model");
        definitions["app.model.select"] = new(["ctrl+l"], "Open model selector");
        definitions["app.tools.expand"] = new(["ctrl+o"], "Toggle tool output");
        definitions["app.thinking.toggle"] = new(["ctrl+t"], "Toggle thinking blocks");
        definitions["app.session.toggleNamedFilter"] = new(["ctrl+n"], "Toggle named session filter");
        definitions["app.editor.external"] = new(["ctrl+g"], "Open external editor");
        definitions["app.message.copy"] = new(["ctrl+x"], "Copy selection or last assistant message");
        definitions["app.session.new"] = new([], "Start a new session");
        definitions["app.session.tree"] = new([], "Open session tree");
        definitions["app.session.fork"] = new([], "Fork current session");
        definitions["app.session.resume"] = new([], "Resume a session");
        definitions["app.tree.editLabel"] = new(["shift+l"], "Edit tree label");
        definitions["app.tree.toggleLabelTimestamp"] = new(["shift+t"], "Toggle tree label timestamps");
        definitions["app.session.togglePath"] = new(["ctrl+p"], "Toggle session path display");
        definitions["app.session.toggleSort"] = new(["ctrl+s"], "Toggle session sort mode");
        definitions["app.session.rename"] = new(["ctrl+r"], "Rename session");
        definitions["app.session.delete"] = new(["ctrl+d"], "Delete session");
        definitions["app.session.deleteNoninvasive"] = new(["ctrl+backspace"], "Delete session when query is empty");
        definitions["app.models.save"] = new(["ctrl+s"], "Save model selection");
        definitions["app.models.enableAll"] = new(["ctrl+a"], "Enable all models");
        definitions["app.models.clearAll"] = new(["ctrl+x"], "Clear all models");
        definitions["app.models.toggleProvider"] = new(["ctrl+p"], "Toggle all models for provider");
        definitions["app.models.reorderUp"] = new(["alt+up"], "Move model up in order");
        definitions["app.models.reorderDown"] = new(["alt+down"], "Move model down in order");
        definitions["app.tree.filter.default"] = new(["ctrl+d"], "Tree filter: default view");
        definitions["app.tree.filter.noTools"] = new(["ctrl+t"], "Tree filter: hide tool results");
        definitions["app.tree.filter.userOnly"] = new(["ctrl+u"], "Tree filter: user messages only");
        definitions["app.tree.filter.labeledOnly"] = new(["ctrl+l"], "Tree filter: labeled entries only");
        definitions["app.tree.filter.all"] = new(["ctrl+a"], "Tree filter: show all entries");
        definitions["app.tree.filter.cycleForward"] = new(["ctrl+o"], "Tree filter: cycle forward");
        definitions["app.tree.filter.cycleBackward"] = new(["shift+ctrl+o"], "Tree filter: cycle backward");
        // 1. 【CodingAgent】【终端差异】WSL 保留挂起键，Windows 原生控制台把 Ctrl+Z 用于撤销
        definitions["tui.editor.undo"] = new([platform == "win32" ? "ctrl+z" : windows ? "alt+z" : "ctrl+-"], "Undo");
        definitions["tui.altScreen.previousPrompt"] = new(windows ? ["ctrl+up"] : ["ctrl+shift+up", "ctrl+up"], "Jump to previous semantic prompt");
        definitions["tui.altScreen.nextPrompt"] = new(windows ? ["ctrl+down"] : ["ctrl+shift+down", "ctrl+down"], "Jump to next semantic prompt");
        definitions["tui.altScreen.search"] = new([windows ? "ctrl+f" : "ctrl+shift+f"], "Search the primary scroll view");
        definitions["app.suspend"] = new(platform == "win32" ? [] : ["ctrl+z"], "Suspend to background");
        definitions["app.model.cycleBackward"] = new([windows ? "alt+p" : "shift+ctrl+p"], "Cycle to previous model");
        definitions["app.message.followUp"] = new([windows ? "ctrl+q" : "alt+enter"], "Queue follow-up message");
        definitions["app.message.dequeue"] = new([windows ? "alt+q" : "alt+up"], "Restore queued messages");
        definitions["app.clipboard.pasteImage"] = new([windows ? "alt+v" : "ctrl+v"], "Paste files on macOS, images, or text from clipboard");
        definitions["app.tree.foldOrUp"] = new(platform == "darwin" ? ["alt+left", "ctrl+left"] : ["ctrl+left", "alt+left"], "Fold tree branch or move up");
        definitions["app.tree.unfoldOrDown"] = new(platform == "darwin" ? ["alt+right", "ctrl+right"] : ["ctrl+right", "alt+right"], "Unfold tree branch or move down");
        return definitions;
    }
}
