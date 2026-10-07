// 作者：xxx
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentKeybindingsTests
{
    /// <summary>【CodingAgent】【平台键位】Windows 和 WSL 使用兼容键位，撤销和挂起仍保持平台区别。</summary>
    /// <param name="platform">平台。</param><param name="wsl">WSL 标识。</param><param name="windows">兼容键位。</param><param name="undo">撤销键。</param>
    [Theory]
    [InlineData("win32", false, true, "ctrl+z")]
    [InlineData("linux", false, false, "ctrl+-")]
    [InlineData("linux", true, true, "alt+z")]
    [InlineData("darwin", true, false, "ctrl+-")]
    public void Definitions_UsePlatformDefaults(string platform, bool wsl, bool windows, string undo)
    {
        var env = new Dictionary<string, string> { ["WSL_INTEROP"] = wsl ? "fixture" : "" };
        var manager = new CodingAgentKeybindings(platform: platform, environment: env);
        Assert.Equal(windows, CodingAgentKeybindings.UseWindowsKeybindings(platform, env));
        Assert.Equal([undo], manager.GetKeys("tui.editor.undo"));
        Assert.Equal([windows ? "alt+p" : "shift+ctrl+p"], manager.GetKeys("app.model.cycleBackward"));
        Assert.Equal([windows ? "ctrl+q" : "alt+enter"], manager.GetKeys("app.message.followUp"));
        Assert.Equal([windows ? "alt+q" : "alt+up"], manager.GetKeys("app.message.dequeue"));
        Assert.Equal([windows ? "alt+v" : "ctrl+v"], manager.GetKeys("app.clipboard.pasteImage"));
        Assert.Equal([windows ? "ctrl+f" : "ctrl+shift+f"], manager.GetKeys("tui.altScreen.search"));
        Assert.Equal(platform == "win32" ? [] : new[] { "ctrl+z" }, manager.GetKeys("app.suspend"));
        Assert.Equal(platform == "darwin" ? ["alt+left", "ctrl+left"] : new[] { "ctrl+left", "alt+left" }, manager.GetKeys("app.tree.foldOrUp"));
    }

    /// <summary>【CodingAgent】【旧名称迁移】新名称无论是否有效都优先，未知名称和禁用数组保留。</summary>
    [Fact]
    public void Configuration_MigratesLegacyNamesWithNativePrecedence()
    {
        var manager = new CodingAgentKeybindings(CodingAgentKeybindings.ParseUserConfiguration("""
            {"selectUp":"ctrl+p","selectDown":"ctrl+n","tui.select.down":[],"selectConfirm":"ctrl+x",
            "tui.select.confirm":null,"cycleModelBackward":"alt+m","unknown":"ctrl+q"}
            """));
        Assert.Equal(["ctrl+p"], manager.GetKeys("tui.select.up"));
        Assert.Empty(manager.GetKeys("tui.select.down"));
        Assert.Equal(["enter"], manager.GetKeys("tui.select.confirm"));
        Assert.Equal(["alt+m"], manager.GetKeys("app.model.cycleBackward"));
        Assert.Equal(["ctrl+q"], manager.GetUserBindings()["unknown"]);
    }

    /// <summary>【CodingAgent】【配置重载】重新加载同一文件，无效或删除的文件恢复默认，UTF-8 BOM 可正常读取。</summary>
    [Fact]
    public void Reload_UsesCurrentFileAndRecoversFromInvalidContent()
    {
        var path = Path.Combine(Path.GetTempPath(), "tau-keybindings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "\ufeff" + """{"tui.select.confirm":"ctrl+x"}""");
            var manager = CodingAgentKeybindings.LoadOrDefault(path);
            Assert.Equal(["ctrl+x"], manager.GetKeys("tui.select.confirm"));
            File.WriteAllText(path, """{"tui.select.confirm":[]}""");
            manager.Reload(); Assert.Empty(manager.GetKeys("tui.select.confirm"));
            File.WriteAllText(path, "{"); manager.Reload(); Assert.Equal(["enter"], manager.GetKeys("tui.select.confirm"));
            File.Delete(path); manager.Reload(); Assert.Equal(["enter"], manager.GetKeys("tui.select.confirm"));
        }
        finally { File.Delete(path); }
    }
}
