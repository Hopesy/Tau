// 作者：xxx
using System.Text.Json;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentLayeredSettingsTests
{
    /// <summary>【CodingAgent】【设置分层】对象递归合并，纯修饰列表继承工具，数组和未知嵌套字段完整保留。</summary>
    [Fact]
    public void Load_MergesNestedObjectsAndPreservesScope()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Global, """
            {"defaultModel":"global-model","defaultTools":["read","bash"],
             "terminal":{"showImages":true,"hyperlinks":"auto","nested":{"a":1}},
             "retry":{"provider":{"timeoutMs":5000,"maxRetries":4}},"custom":{"list":[1,2],"keep":true}}
            """);
        File.WriteAllText(fixture.Project, """
            {"defaultModel":"project-model","defaultTools":["-bash","+grep"],
             "terminal":{"clearOnShrink":true,"nested":{"b":2}},"retry":{"provider":{"maxRetries":1}},
             "custom":{"list":[],"added":true}}
            """);
        var store = fixture.CreateStore();
        var settings = store.Load();
        Assert.Equal("project-model", settings.DefaultModel);
        Assert.Equal(["read", "bash", "-bash", "+grep"], settings.DefaultTools);
        Assert.Equal(["read", "grep"], CodingAgentToolSelection.ResolveDefaults(settings.DefaultTools));
        Assert.True(settings.TerminalShowImages);
        Assert.True(settings.TerminalClearOnShrink);
        Assert.Equal(5000, settings.Retry!.Value.GetProperty("provider").GetProperty("timeoutMs").GetInt32());
        Assert.Equal(1, settings.Retry.Value.GetProperty("provider").GetProperty("maxRetries").GetInt32());
        Assert.Equal(0, settings.AdditionalSettings!["custom"].GetProperty("list").GetArrayLength());
        Assert.True(settings.AdditionalSettings["custom"].GetProperty("keep").GetBoolean());
        Assert.Equal("global-model", store.LoadGlobal().DefaultModel);
        Assert.Equal("project-model", store.LoadProject().DefaultModel);
        store.SetProjectTrusted(false);
        Assert.Equal("global-model", store.Load().DefaultModel);
        Assert.Null(store.LoadProject().DefaultModel);
        store.SetProjectTrusted(true);
        Assert.Equal("project-model", store.Load().DefaultModel);
    }

    /// <summary>【CodingAgent】【差量保存】修改全局叶字段时不复制项目覆盖，不抹掉外部写入和未知字段。</summary>
    [Fact]
    public void Save_PreservesConcurrentFieldsAndDoesNotCopyProjectValues()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Global, """{"theme":"global","terminal":{"showImages":true,"hyperlinks":"auto"}}""");
        var projectText = """{"theme":"project","terminal":{"clearOnShrink":true,"projectOnly":42}}""";
        File.WriteAllText(fixture.Project, projectText);
        var store = fixture.CreateStore();
        var original = store.Load();
        File.WriteAllText(fixture.Global, """{"theme":"external","terminal":{"showImages":true,"hyperlinks":"auto","external":7},"outside":true}""");
        store.Save(original with { TerminalShowImages = false, DefaultThinkingLevel = "high" });
        using var saved = JsonDocument.Parse(File.ReadAllText(fixture.Global));
        Assert.Equal("external", saved.RootElement.GetProperty("theme").GetString());
        var terminal = saved.RootElement.GetProperty("terminal");
        Assert.False(terminal.GetProperty("showImages").GetBoolean());
        Assert.Equal("auto", terminal.GetProperty("hyperlinks").GetString());
        Assert.Equal(7, terminal.GetProperty("external").GetInt32());
        Assert.False(terminal.TryGetProperty("projectOnly", out _));
        Assert.False(terminal.TryGetProperty("clearOnShrink", out _));
        Assert.True(saved.RootElement.GetProperty("outside").GetBoolean());
        Assert.Equal("high", store.Load().DefaultThinkingLevel);
        Assert.Equal("project", store.Load().Theme);
        Assert.Equal(projectText, File.ReadAllText(fixture.Project));
    }

    /// <summary>【CodingAgent】【设置保真】单文件设置更新保留 terminal、images、markdown 中未识别的字段。</summary>
    [Fact]
    public void Save_SingleFilePreservesUnknownNestedFields()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Global, """
            {"terminal":{"showImages":true,"images":"kitty"},"images":{"autoResize":true,"quality":90},
             "markdown":{"codeBlockIndent":"  ","mermaid":"final"}}
            """);
        var store = new CodingAgentSettingsStore(fixture.Global);
        var original = store.Load();
        store.Save(original with { TerminalShowImages = null, ImagesAutoResize = false, MarkdownCodeBlockIndent = "    " });
        using var saved = JsonDocument.Parse(File.ReadAllText(fixture.Global));
        Assert.False(saved.RootElement.GetProperty("terminal").TryGetProperty("showImages", out _));
        Assert.Equal("kitty", saved.RootElement.GetProperty("terminal").GetProperty("images").GetString());
        Assert.Equal(90, saved.RootElement.GetProperty("images").GetProperty("quality").GetInt32());
        Assert.Equal("final", saved.RootElement.GetProperty("markdown").GetProperty("mermaid").GetString());
    }

    /// <summary>【CodingAgent】【损坏隔离】损坏全局层不阻断项目读取，保存不能覆盖损坏文件。</summary>
    [Fact]
    public void Load_IsolatesCorruptLayerAndSaveRefusesToOverwriteIt()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Global, "{broken");
        File.WriteAllText(fixture.Project, """{"defaultModel":"valid"}""");
        var store = fixture.CreateStore();
        var snapshot = store.Load();
        Assert.Equal("valid", snapshot.DefaultModel);
        Assert.Equal("global", Assert.Single(store.LoadErrors).Scope);
        Assert.ThrowsAny<JsonException>(() => store.Save(snapshot with { Theme = "changed" }));
        Assert.Equal("{broken", File.ReadAllText(fixture.Global));
        store.SetProjectTrusted(false);
        Assert.Null(store.Load().DefaultModel);
    }

    /// <summary>【CodingAgent】【并发设置】多个独立实例从同一旧快照修改不同字段时，不发生丢失更新。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task Save_IndependentInstancesRetainConcurrentChanges()
    {
        using var fixture = new Fixture();
        var snapshots = Enumerable.Range(0, 6).Select(index =>
        {
            var store = fixture.CreateStore();
            var snapshot = store.Load();
            return (store, snapshot: snapshot with
            {
                AdditionalSettings = new Dictionary<string, JsonElement> { ["field" + index] = JsonDocument.Parse(index.ToString()).RootElement.Clone() }
            });
        }).ToArray();
        await Task.WhenAll(snapshots.Select(item => Task.Run(() => item.store.Save(item.snapshot))));
        var result = fixture.CreateStore().Load();
        for (var index = 0; index < 6; index++) Assert.Equal(index, result.AdditionalSettings!["field" + index].GetInt32());
    }

    /// <summary>【CodingAgent】【工具修饰】普通名称替换默认集合，增减项按顺序生效，空名称和重复添加不产生额外工具。</summary>
    /// <param name="entries">JSON 工具列表。</param><param name="expected">预期活动名称。</param>
    [Theory]
    [InlineData("[]", "")]
    [InlineData("[\"+grep\",\"-bash\"]", "read,edit,write,grep")]
    [InlineData("[\"read\",\"+grep\",\"+grep\",\"-read\",\"+read\"]", "grep,read")]
    [InlineData("[\"find\",\"+\",\"-\"]", "find")]
    public void DefaultTools_ResolvesOrderedModifiers(string entries, string expected)
    {
        using var json = JsonDocument.Parse(entries);
        var names = json.RootElement.EnumerateArray().Select(value => value.GetString()!).ToArray();
        Assert.Equal(expected.Split(',', StringSplitOptions.RemoveEmptyEntries), CodingAgentToolSelection.ResolveDefaults(names));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tau-layered-" + Guid.NewGuid().ToString("N"));
        public string Global => System.IO.Path.Combine(_root, "global.json");
        public string Project => System.IO.Path.Combine(_root, "project.json");
        /// <summary>【CodingAgent】【设置测试】创建隔离设置目录。</summary>
        public Fixture() => Directory.CreateDirectory(_root);
        /// <summary>【CodingAgent】【设置测试】构造独立的全局和项目设置实例。</summary><returns>测试设置实例。</returns>
        public CodingAgentSettingsStore CreateStore() => CodingAgentSettingsStore.CreateLayered(Global, Project);
        /// <summary>【CodingAgent】【设置测试】删除已确认的隔离目录。</summary>
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
