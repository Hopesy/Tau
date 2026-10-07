// 作者：xxx
using Tau.Ai;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

/// <summary>【CodingAgent】【结构化提示】验证提示段落、强制替换、增量更新和输入隔离。</summary>
public sealed class CodingAgentSystemPromptTests
{
    /// <summary>工具只显示显式简介，规则按活动工具顺序去重并去除空白。</summary>
    [Fact]
    public void Sections_RespectToolVisibilityAndRuleOrder()
    {
        var sections = CodingAgentSystemPrompt.BuildSections(new()
        {
            Cwd = @"C:\project",
            SelectedTools = ["read", "bash", "hidden"],
            ToolSnippets = new() { ["read"] = "Read files", ["inactive"] = "Do not show" },
            ToolGuidelines = new() { ["read"] = [" First rule ", "", "First rule"], ["bash"] = ["Second rule"], ["inactive"] = ["Ignored"] },
            PromptGuidelines = ["First rule", "Extra rule"]
        });
        Assert.Equal(["preamble", "tools", "rules", "docs", "cwd"], sections.Keys);
        Assert.Contains("- read: Read files", sections["tools"]);
        Assert.DoesNotContain("hidden:", sections["tools"]);
        Assert.DoesNotContain("inactive", sections["tools"]);
        Assert.Equal("<cwd>\nC:/project\n</cwd>", sections["cwd"]);
        var rules = sections["rules"];
        Assert.Contains("Use bash for file operations", rules);
        Assert.Contains("- First rule\n- Second rule\n- Extra rule", rules);
        Assert.DoesNotContain("Ignored", rules);
    }

    /// <summary>自定义前言只替换默认说明，附加指令、项目文件、技能和工作目录继续保留。</summary>
    [Fact]
    public void CustomPrompt_PreservesConfiguredResources()
    {
        var options = new CodingAgentSystemPromptOptions
        {
            Cwd = "/project", CustomPrompt = "Custom", AppendSystemPrompt = "Append", SelectedTools = ["read"],
            ContextFiles = [new("/project/AGENTS.md", "Rules")],
            Skills = [new("skill", "Description", "", "/skill/SKILL.md", "/skill", "test", false)]
        };
        var sections = CodingAgentSystemPrompt.BuildSections(options);
        Assert.Equal(["preamble", "addendum", "project_context", "skills", "cwd"], sections.Keys);
        Assert.Equal("Custom", sections["preamble"]);
        Assert.Contains("<project_instructions path=\"/project/AGENTS.md\">\nRules\n</project_instructions>", sections["project_context"]);
        Assert.Contains("Use the read tool", sections["skills"]);
        Assert.Equal("", CodingAgentSystemPrompt.BuildState(options).Content);
        Assert.Equal(string.Join("\n\n", sections.Values), CodingAgentSystemPrompt.Build(options));
    }

    /// <summary>强制提示包括空字符串在内均原样输出，同时不影响结构化段落的构建。</summary>
    /// <param name="forced">强制提示。</param>
    [Theory]
    [InlineData("")]
    [InlineData("  exact\ntext  ")]
    public void ForcePrompt_IsOpaqueAndDoesNotChangeSections(string forced)
    {
        var options = new CodingAgentSystemPromptOptions { Cwd = "/cwd", ForceSystemPrompt = forced };
        var state = CodingAgentSystemPrompt.BuildState(options);
        Assert.Equal(forced, state.Content);
        Assert.Null(state.Sections);
        Assert.Equal(forced, CodingAgentSystemPrompt.Build(options));
        Assert.Contains("cwd", CodingAgentSystemPrompt.BuildSections(options).Keys);
    }

    /// <summary>拒绝保留名称和不合法 XML 标签，防止生成不可识别的段落更新。</summary>
    /// <param name="name">非法段落名。</param>
    [Theory]
    [InlineData("preamble")]
    [InlineData("Upper")]
    [InlineData("a b")]
    [InlineData("1name")]
    [InlineData("a>")]
    public void InvalidSections_AreRejected(string name) => Assert.Throws<ArgumentException>(() =>
        CodingAgentSystemPrompt.BuildSections(new() { Cwd = "/cwd", Sections = new() { [name] = "bad" } }));

    /// <summary>自定义段落覆盖默认值，空字符串不删除默认值，并按插入顺序添加新段落。</summary>
    [Fact]
    public void CustomSections_OverrideWithoutReordering()
    {
        var sections = CodingAgentSystemPrompt.BuildSections(new()
        {
            Cwd = "/cwd", Sections = new() { ["tools"] = "Only these", ["rules"] = "", ["task_1-extra"] = "Task" }
        });
        Assert.Equal("<tools>\nOnly these\n</tools>", sections["tools"]);
        Assert.Contains("Be concise", sections["rules"]);
        Assert.Equal("task_1-extra", sections.Keys.Last());
    }

    /// <summary>没有读取工具或全部技能禁止模型调用时不生成技能段落。</summary>
    [Fact]
    public void Skills_RequireAvailableReadToolAndVisibleSkill()
    {
        var skill = new CodingAgentSkill("<&", "\"desc\"", "", "/a&b/SKILL.md", "/a&b", "test", false);
        var options = new CodingAgentSystemPromptOptions { Cwd = "/cwd", SelectedTools = [], Skills = [skill] };
        Assert.DoesNotContain("skills", CodingAgentSystemPrompt.BuildSections(options).Keys);
        var sections = CodingAgentSystemPrompt.BuildSections(options with { SelectedTools = ["bash"] });
        Assert.Contains("Use bash to load", sections["skills"]);
        Assert.Contains("&lt;&amp;", sections["skills"]);
        Assert.DoesNotContain("skills", CodingAgentSystemPrompt.BuildSections(options with { SelectedTools = ["read"], Skills = [skill with { DisableModelInvocation = true }] }).Keys);
    }

    /// <summary>规范化复制嵌套集合，保证处理器本轮修改不改变下一轮默认配置。</summary>
    [Fact]
    public void Normalize_IsolatesAllMutableCollections()
    {
        var original = new CodingAgentSystemPromptOptions { Cwd = "/cwd", ToolGuidelines = new() { ["read"] = ["rule"] } };
        var copied = CodingAgentSystemPrompt.Normalize(original);
        copied.SelectedTools.Clear();
        copied.ToolGuidelines["read"].Add("changed");
        copied.ToolSnippets["read"] = "changed";
        copied.Sections["extra"] = "changed";
        copied.ContextFiles.Add(new("path", "content"));
        copied.PromptGuidelines.Add("changed");
        Assert.Equal(4, original.SelectedTools.Count);
        Assert.Single(original.ToolGuidelines["read"]);
        Assert.Empty(original.ToolSnippets);
        Assert.Empty(original.Sections);
        Assert.Empty(original.ContextFiles);
        Assert.Empty(original.PromptGuidelines);
    }

    /// <summary>段落差异可由真实会话重放，删除只输出 null，不重复持久化未变更段落。</summary>
    [Fact]
    public void Diff_ReplaysReplacementsAndDeletions()
    {
        Dictionary<string, string?> old = new() { ["preamble"] = "Base", ["old"] = "Old", ["same"] = "Same" };
        Dictionary<string, string> current = new() { ["preamble"] = "Base", ["same"] = "Same", ["new"] = "New" };
        var patch = CodingAgentSystemPrompt.DiffSections(old, current)!;
        Assert.Equal(2, patch.Count);
        Assert.Null(patch["old"]);
        Assert.Equal("New", patch["new"]);
        var replayed = Transcript.GetCurrentSystemMessage([new SystemMessage("") { Sections = old }, new SystemMessage("") { Sections = patch }])!;
        Assert.Equal(["preamble", "same", "new"], replayed.Sections!.Keys);
        Assert.Null(CodingAgentSystemPrompt.DiffSections(replayed.Sections, current));
    }
}
