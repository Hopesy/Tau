// 作者：xxx
using System.Globalization;
using System.Text.RegularExpressions;
using Tau.Ai.Auth;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【认证命令】上游支持的认证检查与凭据输出种类。</summary>
internal enum CodingAgentAuthCommandKind { Check, ApiKey, BearerToken }

/// <summary>【CodingAgent】【认证命令】已分离认证专用选项的命令参数。</summary>
/// <param name="Kind">命令种类。</param><param name="Arguments">交给通用解析器的剩余参数。</param><param name="Json">输出 JSON 状态。</param>
/// <param name="Credentials">检查成功时输出凭据。</param><param name="NoRefresh">禁止检查时轮换 OAuth。</param><param name="MinimumExpiry">要求的最低 OAuth 剩余期限。</param>
internal sealed record CodingAgentAuthCommand(CodingAgentAuthCommandKind Kind, IReadOnlyList<string> Arguments, bool Json,
    bool Credentials, bool NoRefresh, TimeSpan? MinimumExpiry);

/// <summary>【CodingAgent】【认证命令错误】可向 CLI 用户展示的参数或选择错误。</summary>
/// <param name="message">不含凭据的诊断。</param><param name="unknownOption">是否为未知选项错误。</param>
internal sealed class CodingAgentAuthCommandException(string message, bool unknownOption = false) : Exception(message)
{
    public bool UnknownOption { get; } = unknownOption;
}

/// <summary>【CodingAgent】【认证命令解析】对齐 auth-command.ts 的参数校验与认证值提取。</summary>
internal static class CodingAgentAuthCommandParser
{
    /// <summary>【CodingAgent】【命令帮助】auth 无子命令、help 或任意位置帮助选项时展示用法。</summary><param name="args">原始参数。</param><returns>是否帮助请求。</returns>
    public static bool IsHelp(IReadOnlyList<string> args) => args.Count > 0 && args[0] == "auth"
        && (args.Count == 1 || args[1] == "help" || args.Contains("--help") || args.Contains("-h"));

    /// <summary>【CodingAgent】【命令名称】返回种类对应的子命令。</summary><param name="kind">命令种类。</param><returns>显示名称。</returns>
    public static string Name(CodingAgentAuthCommandKind kind) => kind switch
    { CodingAgentAuthCommandKind.Check => "auth check", CodingAgentAuthCommandKind.ApiKey => "auth print-api-key", _ => "auth print-bearer-token" };

    /// <summary>【CodingAgent】【命令用法】返回参数错误后的具体用法提示。</summary><param name="kind">命令种类。</param><returns>用法。</returns>
    public static string Usage(CodingAgentAuthCommandKind kind) => $"tau {Name(kind)} --provider <provider>" + (kind switch
    { CodingAgentAuthCommandKind.Check => " [--json] [--credentials] [--no-refresh]", CodingAgentAuthCommandKind.ApiKey => " [--model <model>]", _ => " [--model <model>] [--min-expiry <duration>]" });

    /// <summary>【CodingAgent】【专用选项】解析认证子命令，其他顶层命令返回空值，错误组合立即报告。</summary>
    /// <param name="args">原始 CLI 参数。</param><returns>认证命令或空值。</returns>
    public static CodingAgentAuthCommand? Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0] != "auth") return null;
        var name = args.Count > 1 ? args[1] : "";
        var kind = name switch
        {
            "check" => CodingAgentAuthCommandKind.Check, "print-api-key" => CodingAgentAuthCommandKind.ApiKey,
            "print-bearer-token" => CodingAgentAuthCommandKind.BearerToken,
            _ => throw new CodingAgentAuthCommandException($"Unknown auth command \"{name}\". Use \"tau auth print-api-key\", \"tau auth print-bearer-token\", or \"tau auth check\".")
        };
        var remaining = new List<string>(); var json = false; var credentials = false; var noRefresh = false; TimeSpan? minimum = null;
        for (var index = 2; index < args.Count; index++)
        {
            var arg = args[index];
            if (arg == "--min-expiry")
            {
                if (kind != CodingAgentAuthCommandKind.BearerToken) throw new CodingAgentAuthCommandException("--min-expiry is only supported by print-bearer-token");
                minimum = ParseDuration(++index < args.Count ? args[index] : null); continue;
            }
            if (arg is "--json" or "--credentials" or "--no-refresh")
            {
                if (kind != CodingAgentAuthCommandKind.Check) throw new CodingAgentAuthCommandException($"{arg} is only supported by auth check");
                if (arg == "--json") json = true; else if (arg == "--credentials") credentials = true; else noRefresh = true;
                continue;
            }
            remaining.Add(arg);
        }
        return new(kind, remaining, json, credentials, noRefresh, minimum);
    }

    /// <summary>【CodingAgent】【认证参数校验】拒绝消息、文件、显式密钥及未知标志，要求至少指定提供方或模型。</summary>
    /// <param name="args">通用 CLI 解析结果。</param><param name="kind">认证命令种类。</param><returns>规范化的提供方和模型。</returns>
    public static (string? Provider, string? Model) Validate(CodingAgentCliArguments args, CodingAgentAuthCommandKind kind)
    {
        if (args.ExtensionFlags.Count > 0) throw new CodingAgentAuthCommandException($"Unknown option --{args.ExtensionFlags.Keys.First()} for \"{Name(kind)}\".", true);
        if (args.Diagnostics.Count > 0) throw new CodingAgentAuthCommandException(string.Join("\n", args.Diagnostics.Select(diagnostic => diagnostic.Message)));
        if (args.ApiKey is not null || args.Messages.Count > 0 || args.FileArguments.Count > 0)
            throw new CodingAgentAuthCommandException("Auth commands only accept --provider and --model");
        var provider = string.IsNullOrWhiteSpace(args.Provider) ? null : args.Provider.Trim();
        var model = string.IsNullOrWhiteSpace(args.Model) ? null : args.Model.Trim();
        if (provider is null && model is null) throw new CodingAgentAuthCommandException(kind == CodingAgentAuthCommandKind.Check
            ? "Auth checks require --provider <provider> or --model <model>" : "Credential printing requires --provider <provider> or --model <model>");
        return (provider, model);
    }

    /// <summary>【CodingAgent】【认证值提取】API key 优先，否则只接受大小写不敏感的 Authorization Bearer 头。</summary>
    /// <param name="auth">已解析认证。</param><returns>密钥或 bearer 值；其他认证方式为空。</returns>
    public static string? GetCredential(ProviderAuthResult? auth)
    {
        if (!string.IsNullOrEmpty(auth?.ApiKey)) return auth.ApiKey;
        var authorization = auth?.Headers?.FirstOrDefault(pair => pair.Key.Equals("authorization", StringComparison.OrdinalIgnoreCase)).Value;
        if (authorization is null) return null;
        var match = Regex.Match(authorization, "^Bearer\\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>【CodingAgent】【令牌有效期】读取整数 ms、s、m 或 h 时长，拒绝超出目标时间类型范围的值。</summary>
    /// <param name="value">时长文本。</param><returns>可表达的时长。</returns>
    private static TimeSpan ParseDuration(string? value)
    {
        var match = Regex.Match(value ?? "", "^([0-9]+)(ms|s|m|h)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) throw new CodingAgentAuthCommandException("--min-expiry must use a duration such as 30m or 1h");
        var amount = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var milliseconds = amount * (match.Groups[2].Value.ToLowerInvariant() switch { "ms" => 1, "s" => 1000, "m" => 60000, _ => 3600000 });
        if (!double.IsFinite(milliseconds) || milliseconds >= TimeSpan.MaxValue.TotalMilliseconds)
            throw new CodingAgentAuthCommandException("--min-expiry exceeds the supported duration range");
        return TimeSpan.FromMilliseconds(milliseconds);
    }
}
