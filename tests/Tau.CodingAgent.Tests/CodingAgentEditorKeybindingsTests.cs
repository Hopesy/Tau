// 作者：xxx
using Tau.CodingAgent.Runtime;
using Tau.Tui.Abstractions;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentEditorKeybindingsTests
{
    /// <summary>【CodingAgent】【输入平台键位】Windows 平台采用兼容默认值，用户覆盖和禁用立即生效。</summary>
    [Fact]
    public void EditorBindings_UsePlatformAndUserActions()
    {
        var manager = new CodingAgentKeybindings(platform: "win32");
        var map = CodingAgentKeybindings.CreateEditorBindings(manager);
        Assert.Equal(EditorAction.CycleModelBackward, map.Resolve(new('p', ConsoleKey.P, false, true, false)));
        Assert.Equal(EditorAction.PasteImage, map.Resolve(new('v', ConsoleKey.V, false, true, false)));
        Assert.Equal(EditorAction.QueueFollowUpMessage, map.Resolve(new('\u0011', ConsoleKey.Q, false, false, true)));
        Assert.Equal(EditorAction.Undo, map.Resolve(new('\u001a', ConsoleKey.Z, false, false, true)));
        manager.SetUserBindings(CodingAgentKeybindings.ParseUserConfiguration("""{"app.tools.expand":"f3","app.clear":[],"app.model.cycleBackward":[]}"""));
        Assert.Equal(EditorAction.ToggleToolOutputExpansion, map.Resolve(new('\0', ConsoleKey.F3, false, false, false)));
        Assert.Equal(EditorAction.None, map.Resolve(new('\u000f', ConsoleKey.O, false, false, true)));
        Assert.Equal(EditorAction.None, map.Resolve(new('\u0003', ConsoleKey.C, false, false, true)));
        Assert.Equal(EditorAction.None, map.Resolve(new('p', ConsoleKey.P, false, true, false)));
    }

    /// <summary>【CodingAgent】【应用优先级】应用动作优先于编辑动作，界面提示使用同一个最终动作映射。</summary>
    [Fact]
    public void EditorBindings_KeepApplicationPriorityAndHints()
    {
        var manager = new CodingAgentKeybindings(CodingAgentKeybindings.ParseUserConfiguration("""
            {"app.model.select":"ctrl+x","tui.input.submit":"ctrl+x","tui.select.confirm":"ctrl+x"}
            """));
        var map = CodingAgentKeybindings.CreateEditorBindings(manager);
        Assert.Equal(EditorAction.SelectModel, map.Resolve(new('\u0018', ConsoleKey.X, false, false, true)));
        Assert.Equal(EditorAction.SelectModel, map.Bindings[new(ConsoleKey.X, ConsoleModifiers.Control)]);
        Assert.False(map.UseLegacyShortcuts);
    }

    /// <summary>【CodingAgent】【文件格式兼容】原生动作配置与 Tau 旧 bindings 数组都能驱动输入编辑器。</summary>
    /// <param name="legacy">是否旧格式。</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EditorBindings_LoadBothConfigurationFormats(bool legacy)
    {
        var path = Path.Combine(Path.GetTempPath(), "tau-editor-keys-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, legacy ? """{"bindings":[{"key":"F2","action":"Submit"}]}""" : """{"submit":"f2"}""");
            var map = CodingAgentKeybindings.LoadEditorOrDefault(path);
            Assert.Equal(EditorAction.Submit, map.Resolve(new('\0', ConsoleKey.F2, false, false, false)));
            Assert.Equal(legacy, map.UseLegacyShortcuts);
            Assert.Equal(legacy ? EditorAction.Submit : EditorAction.None, map.Resolve(new('\r', ConsoleKey.Enter, false, false, false)));
        }
        finally { File.Delete(path); }
    }
}
