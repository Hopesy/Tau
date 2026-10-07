// 作者：xxx
using System.Text.Json;
using Tau.CodingAgent.Runtime.Mcp;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentMcpWindowsCommandTests
{
    /// <summary>【CodingAgent】【Windows MCP 实测】真实包装器和 shebang 进程保留空参数、Unicode、引号、反斜杠及 shell 元字符。</summary>
    /// <param name="mode">包装器类型。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData("cmd")]
    [InlineData("npm")]
    [InlineData("shebang")]
    public async Task McpWindowsCommandsPreserveArgumentsAcrossWrappers(string mode)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "tau-mcp-windows-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, mode == "npm" ? "node_modules/.bin" : "space & symbols");
        Directory.CreateDirectory(directory);
        try
        {
            var server = """
                // 作者：xxx
                const readline=require('node:readline');
                readline.createInterface({input:process.stdin}).on('line',line=>{
                  const m=JSON.parse(line);if(!('id' in m))return;
                  const result=m.method==='initialize'?{protocolVersion:m.params.protocolVersion,serverInfo:{name:'argv',version:'1'},capabilities:{}}:
                    {args:process.argv.slice(2),cwd:process.cwd()};
                  process.stdout.write(JSON.stringify({jsonrpc:'2.0',id:m.id,result})+'\n');
                });
                """;
            File.WriteAllText(Path.Combine(directory, "server.cjs"), server);
            string command;
            if (mode == "shebang")
            {
                command = "fixture";
                File.WriteAllText(Path.Combine(directory, command), "#!/usr/bin/env node --no-warnings\n" + server);
            }
            else if (mode == "npm")
            {
                command = "fixture";
                File.WriteAllText(Path.Combine(directory, command + ".cmd"), """
                    @ECHO off
                    GOTO start
                    :find_dp0
                    SET dp0=%~dp0
                    EXIT /b
                    :start
                    SETLOCAL
                    CALL :find_dp0
                    SET "_prog=node"
                    endLocal & goto #_undefined_# 2>NUL || title %COMSPEC% & "%_prog%" "%dp0%\server.cjs" %*
                    """.Replace("\n", "\r\n", StringComparison.Ordinal));
            }
            else
            {
                command = Path.Combine(directory, "fixture wrapper.cmd");
                File.WriteAllText(command, "@echo off\r\nnode \"%~dp0server.cjs\" %*\r\n");
            }
            string[] args = ["", "hello world", "中文", "quote\"value", @"backslash\", @"\\\""", "&|<>^!;,()[]*?", "%TAU_MCP_ARG_TEST%", new string('\\', 2000) + "\"tail"];
            var environment = new Dictionary<string, string>
            {
                ["PATH"] = "\"" + directory + "\";" + Environment.GetEnvironmentVariable("PATH"),
                ["PATHEXT"] = ".COM;.EXE;.BAT;.CMD", ["TAU_MCP_ARG_TEST"] = "must-not-expand"
            };
            await using var transport = new CodingAgentMcpStdioTransport(command, args, root, environment);
            await using var client = new CodingAgentMcpClient(timeout: TimeSpan.FromSeconds(5));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await client.ConnectAsync(transport, deadline.Token);
            var result = await client.RequestAsync("argv", token: deadline.Token);
            Assert.Equal(args, result.GetProperty("args").EnumerateArray().Select(value => value.GetString()));
            Assert.Equal(root, result.GetProperty("cwd").GetString());
        }
        finally { Directory.Delete(root, true); }
    }
}
