// 作者：xxx
using Tau.AgentCore;
using Tau.Ai;
using Tau.Ai.Providers;
using Tau.Ai.Registry;
using Tau.Ai.Streaming;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

/// <summary>【CodingAgent】【工具目录】验证 SDK 会话的工具与提示词使用各自工作目录</summary>
public sealed class CodingAgentWorkingDirectoryTests
{
    /// <summary>验证并存会话的同名文件操作、搜索及子进程目录相互隔离</summary>
    /// <param name="scenario">待验证的工具场景</param>
    /// <returns>异步验证任务</returns>
    [Theory]
    [InlineData("read")]
    [InlineData("write")]
    [InlineData("edit")]
    [InlineData("find")]
    [InlineData("grep")]
    [InlineData("ls")]
    [InlineData("bash")]
    [InlineData("bash-relative")]
    public async Task SdkSessions_ResolveToolsAgainstTheirOwnDirectory(string scenario)
    {
        var root = Path.Combine(Path.GetTempPath(), "tau-cwd-tests-" + Guid.NewGuid().ToString("N"));
        var processDirectory = Environment.CurrentDirectory;
        Directory.CreateDirectory(root);
        try
        {
            var projects = new[] { Path.Combine(root, "project A"), Path.Combine(root, "project B") };
            for (var index = 0; index < projects.Length; index++)
            {
                Directory.CreateDirectory(Path.Combine(projects[index], "sub directory"));
                await File.WriteAllTextAsync(Path.Combine(projects[index], "input.txt"), $"marker-{index}");
                await File.WriteAllTextAsync(Path.Combine(projects[index], $"marker-{index}.flag"), "flag");
            }

            // 1. 【CodingAgent】【工具目录】并行驱动两个会话，验证没有依赖全局目录切换
            var results = await Task.WhenAll(projects.Select(project => RunSessionAsync(project, scenario)));
            Assert.Equal(processDirectory, Environment.CurrentDirectory);
            for (var index = 0; index < projects.Length; index++)
            {
                var (text, prompt) = results[index];
                Assert.Contains(projects[index].Replace('\\', '/'), prompt);
                if (scenario == "write")
                {
                    Assert.Equal("written", await File.ReadAllTextAsync(Path.Combine(projects[index], "output.txt")));
                }
                else if (scenario == "edit")
                {
                    Assert.Equal($"edited-{index}", await File.ReadAllTextAsync(Path.Combine(projects[index], "input.txt")));
                }
                else if (scenario.StartsWith("bash", StringComparison.Ordinal))
                {
                    var expected = scenario == "bash-relative" ? Path.Combine(projects[index], "sub directory") : projects[index];
                    Assert.Contains(expected.Replace('\\', '/'), text.Replace('\\', '/'));
                }
                else
                {
                    Assert.Contains($"marker-{index}", text);
                    Assert.DoesNotContain($"marker-{1 - index}", text);
                }
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>创建独立 SDK 会话，刷新提示词后通过模拟模型调用指定工具</summary>
    /// <param name="cwd">项目工作目录</param>
    /// <param name="scenario">工具场景</param>
    /// <returns>工具文本和最终系统提示词</returns>
    private static async Task<(string Text, string Prompt)> RunSessionAsync(string cwd, string scenario)
    {
        var toolName = scenario.StartsWith("bash", StringComparison.Ordinal) ? "bash" : scenario;
        var arguments = scenario switch
        {
            "read" => """{"path":"input.txt"}""",
            "write" => """{"path":"output.txt","content":"written"}""",
            "edit" => """{"path":"input.txt","old_string":"marker","new_string":"edited"}""",
            "find" => """{"pattern":"*.flag"}""",
            "grep" => """{"pattern":"marker-","path":"input.txt","include_content":true}""",
            "ls" => "{}",
            "bash-relative" => OperatingSystem.IsWindows()
                ? """{"command":"pwd -W","working_directory":"sub directory"}"""
                : """{"command":"pwd","working_directory":"sub directory"}""",
            _ => OperatingSystem.IsWindows() ? """{"command":"pwd -W"}""" : """{"command":"pwd"}"""
        };
        var provider = new ToolRequestProvider(toolName, arguments);
        var registry = new ProviderRegistry();
        registry.Register(provider.Api, provider);
        var catalog = new ModelCatalog();
        catalog.RegisterModel(new Model { Id = "cwd-test", Name = "Cwd test", Provider = "cwd-test", Api = provider.Api });
        using var session = await CodingAgentSdk.CreateSessionAsync(new CodingAgentSdkCreateSessionOptions
        {
            Cwd = cwd,
            AgentDirectory = Path.Combine(cwd, ".tau"),
            NoSession = true,
            Tools = [toolName],
            IncludeExtensions = false,
            IncludeSkills = false,
            IncludePromptTemplates = false,
            IncludeContextFiles = false,
            ProviderId = "cwd-test",
            ModelId = "cwd-test",
            ApiKey = "test-key",
            ProviderRegistry = registry,
            ModelCatalog = catalog
        });
        session.Runner.RefreshSystemPromptResources([], []);
        await foreach (var evt in session.Runner.RunAsync("run directory probe")) { }
        var result = Assert.Single(session.Runner.Messages.OfType<ToolResultMessage>());
        Assert.False(result.IsError, string.Join("\n", result.Content.OfType<TextContent>().Select(content => content.Text)));
        return (string.Join("\n", result.Content.OfType<TextContent>().Select(content => content.Text)), provider.SystemPrompt!);
    }

    /// <summary>只产生一次工具调用，不访问外部模型服务</summary>
    private sealed class ToolRequestProvider(string toolName, string arguments) : IStreamProvider
    {
        private int _calls;
        public string Api => "cwd-test";
        public string? SystemPrompt { get; private set; }

        /// <summary>返回预设工具调用或最终文本</summary>
        /// <param name="model">模型信息</param>
        /// <param name="context">当前上下文</param>
        /// <param name="options">请求选项</param>
        /// <returns>已填充的响应流</returns>
        public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options)
        {
            SystemPrompt = context.SystemPrompt;
            var stream = new AssistantMessageStream();
            var content = _calls++ == 0
                ? new ContentBlock[] { new ToolCallContent("cwd-call", toolName, arguments) }
                : [new TextContent("done")];
            stream.Push(new DoneEvent(new AssistantMessage(content)));
            return stream;
        }

        /// <summary>使用简化选项返回预设响应</summary>
        /// <param name="model">模型信息</param>
        /// <param name="context">当前上下文</param>
        /// <param name="options">请求选项</param>
        /// <returns>已填充的响应流</returns>
        public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options) => Stream(model, context, options);
    }
}
