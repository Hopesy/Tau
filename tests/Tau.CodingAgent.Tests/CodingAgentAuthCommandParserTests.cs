// 作者：xxx
using Tau.Ai.Auth;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

/// <summary>【CodingAgent】【认证命令参数测试】验证帮助、专用选项、时长、通用参数及 bearer 提取。</summary>
public sealed class CodingAgentAuthCommandParserTests
{
    /// <summary>【CodingAgent】【帮助参数】帮助可以位于子命令后的任意位置。</summary><param name="line">参数文本。</param>
    [Theory]
    [InlineData("auth")]
    [InlineData("auth help")]
    [InlineData("auth check --provider test --help")]
    [InlineData("auth print-api-key -h")]
    public void Help_IsRecognized(string line) => Assert.True(CodingAgentAuthCommandParser.IsHelp(line.Split(' ')));

    /// <summary>【CodingAgent】【命令解析】专用检查标志不交给通用参数解析。</summary>
    [Fact]
    public void CheckFlags_AreSeparatedFromSharedArguments()
    {
        var command = CodingAgentAuthCommandParser.Parse(["auth", "check", "--json", "--provider", "test", "--credentials", "--no-refresh"])!;
        Assert.Equal(CodingAgentAuthCommandKind.Check, command.Kind); Assert.True(command.Json); Assert.True(command.Credentials); Assert.True(command.NoRefresh);
        Assert.Equal(new[] { "--provider", "test" }, command.Arguments); Assert.Null(command.MinimumExpiry);
        Assert.Null(CodingAgentAuthCommandParser.Parse(["prompt"]));
    }

    /// <summary>【CodingAgent】【时长解析】有效单位转换为毫秒，零值也被保留。</summary><param name="value">时长。</param><param name="milliseconds">预期毫秒。</param>
    [Theory]
    [InlineData("0ms", 0.0)]
    [InlineData("250ms", 250.0)]
    [InlineData("30s", 30000.0)]
    [InlineData("30m", 1800000.0)]
    [InlineData("1h", 3600000.0)]
    [InlineData("2H", 7200000.0)]
    public void Duration_ParsesUnits(string value, double milliseconds) => Assert.Equal(milliseconds,
        CodingAgentAuthCommandParser.Parse(["auth", "print-bearer-token", "--min-expiry", value])!.MinimumExpiry!.Value.TotalMilliseconds);

    /// <summary>【CodingAgent】【时长错误】缺单位、小数、负数和不可表达时长被拒绝。</summary><param name="value">无效时长。</param>
    [Theory]
    [InlineData("")]
    [InlineData("30")]
    [InlineData("1.5h")]
    [InlineData("-1s")]
    [InlineData("1d")]
    [InlineData("999999999999999999999999999999h")]
    public void InvalidDuration_IsRejected(string value) => Assert.Throws<CodingAgentAuthCommandException>(() => CodingAgentAuthCommandParser.Parse(["auth", "print-bearer-token", "--min-expiry", value]));

    /// <summary>【CodingAgent】【选项归属】专用选项不能用在其他认证命令。</summary><param name="command">子命令。</param><param name="option">选项。</param>
    [Theory]
    [InlineData("print-api-key", "--json")]
    [InlineData("print-bearer-token", "--credentials")]
    [InlineData("print-api-key", "--no-refresh")]
    [InlineData("check", "--min-expiry")]
    [InlineData("print-api-key", "--min-expiry")]
    public void WrongCommandOption_IsRejected(string command, string option) => Assert.Throws<CodingAgentAuthCommandException>(() => CodingAgentAuthCommandParser.Parse(["auth", command, option]));

    /// <summary>【CodingAgent】【通用校验】未知标志、消息、文件、显式密钥与缺少选择均无效。</summary><param name="line">剩余参数。</param>
    [Theory]
    [InlineData("")]
    [InlineData("--provider test hello")]
    [InlineData("--provider test @file.txt")]
    [InlineData("--provider test --api-key fixture")]
    [InlineData("--provider test --mystery value")]
    public void InvalidSharedArguments_AreRejected(string line)
    {
        var parsed = CodingAgentCliArguments.Parse(line.Length == 0 ? [] : line.Split(' '));
        Assert.Throws<CodingAgentAuthCommandException>(() => CodingAgentAuthCommandParser.Validate(parsed, CodingAgentAuthCommandKind.Check));
    }

    /// <summary>【CodingAgent】【选择规范化】提供方与模型去除两端空格，任一字段即可满足选择要求。</summary>
    [Fact]
    public void SharedArguments_AreTrimmed()
    {
        var result = CodingAgentAuthCommandParser.Validate(CodingAgentCliArguments.Parse(["--provider", " test ", "--model", " chat "]), CodingAgentAuthCommandKind.Check);
        Assert.Equal(("test", "chat"), result);
    }

    /// <summary>【CodingAgent】【认证值】仅提取 API key 或 Bearer，保留大小写和空白分隔兼容。</summary>
    /// <param name="key">密钥。</param><param name="authorization">认证头。</param><param name="expected">预期值。</param>
    [Theory]
    [InlineData("key", "Bearer token", "key")]
    [InlineData(null, "bEaReR token", "token")]
    [InlineData("", "Bearer\ttoken", "token")]
    [InlineData(null, "Basic value", null)]
    [InlineData(null, "Bearer", null)]
    [InlineData(null, null, null)]
    public void CredentialExtraction_UsesOnlySupportedValues(string? key, string? authorization, string? expected)
    {
        var auth = new ProviderAuthResult(key, authorization is null ? null : new Dictionary<string, string> { ["aUtHoRiZaTiOn"] = authorization });
        Assert.Equal(expected, CodingAgentAuthCommandParser.GetCredential(auth));
    }
}
