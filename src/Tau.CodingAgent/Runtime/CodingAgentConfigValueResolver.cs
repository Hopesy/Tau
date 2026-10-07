// 作者：xxx
using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Tau.CodingAgent.Tools;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【配置引用】解析环境模板、转义和有缓存的命令引用，不在诊断中输出解析后的凭据。</summary>
public sealed class CodingAgentConfigValueResolver
{
    private static readonly ConcurrentDictionary<string, Lazy<Task<string?>>> ProcessCommands = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _commands;
    private readonly Func<string, CancellationToken, Task<string?>> _execute;

    /// <summary>【CodingAgent】【配置解析器】捕获命令目录；注入执行器使用独立缓存，默认命令结果保留到进程退出。</summary>
    /// <param name="cwd">命令工作目录。</param><param name="execute">可选命令执行器。</param>
    public CodingAgentConfigValueResolver(string cwd, Func<string, CancellationToken, Task<string?>>? execute = null)
    {
        var directory = Path.GetFullPath(cwd);
        _commands = execute is null ? ProcessCommands : new(StringComparer.Ordinal);
        _execute = execute ?? ((command, token) => ExecuteCommandAsync(directory, command, token));
    }

    /// <summary>【CodingAgent】【环境引用】只读取模板中有效且不重复的环境变量名称，不运行命令。</summary>
    /// <param name="config">原始配置值。</param><returns>按出现顺序去重的变量名。</returns>
    public static IReadOnlyList<string> GetEnvironmentNames(string config) => config.StartsWith('!') ? [] :
        ParseTemplate(config).Where(part => part.Environment).Select(part => part.Value).Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>【CodingAgent】【配置解析】环境覆盖优先于进程环境，缺少变量返回空值，字面量保持原空白。</summary>
    /// <param name="config">模板或命令引用。</param><param name="environment">环境覆盖。</param><param name="token">调用方取消信号。</param><returns>解析值，无法解析时为空。</returns>
    public async Task<string?> ResolveAsync(string config, IReadOnlyDictionary<string, string>? environment = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (config.StartsWith('!')) return await _commands.GetOrAdd(config, key => new(() => ResolveCommandAsync(key[1..]), LazyThreadSafetyMode.ExecutionAndPublication)).Value.WaitAsync(token).ConfigureAwait(false);
        var result = new StringBuilder();
        foreach (var part in ParseTemplate(config))
        {
            if (!part.Environment) { result.Append(part.Value); continue; }
            var value = EnvironmentValue(part.Value, environment);
            if (value is null) return null;
            result.Append(value);
        }
        return result.ToString();
    }

    /// <summary>【CodingAgent】【必需配置】缺失引用时提供变量名诊断，不把空配置静默当作有效凭据。</summary>
    /// <param name="config">原始配置。</param><param name="description">调用方提供的非敏感字段说明。</param>
    /// <param name="environment">环境覆盖。</param><param name="token">取消信号。</param><returns>非空解析值。</returns>
    public async Task<string> ResolveOrThrowAsync(string config, string description, IReadOnlyDictionary<string, string>? environment = null, CancellationToken token = default)
    {
        var value = await ResolveAsync(config, environment, token).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(value)) return value;
        var names = GetEnvironmentNames(config).Where(name => EnvironmentValue(name, environment) is null).ToArray();
        throw new InvalidOperationException("Failed to resolve " + description + (config.StartsWith('!') ? " from shell command" :
            names.Length > 0 ? " from environment variable" + (names.Length > 1 ? "s" : "") + ": " + string.Join(", ", names) : ""));
    }

    /// <summary>【CodingAgent】【命令缓存】限制命令为十秒，失败和空输出也进入缓存。</summary>
    /// <param name="command">已移除感叹号的命令。</param><returns>裁剪后的 stdout 或空值。</returns>
    private async Task<string?> ResolveCommandAsync(string command)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { var value = (await _execute(command, deadline.Token).ConfigureAwait(false))?.Trim(); return string.IsNullOrEmpty(value) ? null : value; }
        catch { return null; }
    }

    /// <summary>【CodingAgent】【配置命令】复用平台 shell 和进程树清理，仅采集 stdout，输出上限为一 MiB。</summary>
    /// <param name="cwd">工作目录。</param><param name="command">命令文本。</param><param name="token">超时信号。</param><returns>成功时的 stdout。</returns>
    private static async Task<string?> ExecuteCommandAsync(string cwd, string command, CancellationToken token)
    {
        var output = new StringBuilder();
        var exit = await new LocalCodingAgentShellOperations(false, null).ExecuteWithStreamsAsync(new(command, cwd, ShellTool.CreateEnvironment(null)),
            (stream, text) =>
            {
                if (stream == "stdout")
                {
                    if (output.Length + text.Length > 1024 * 1024) throw new IOException("Configuration command output is too large.");
                    output.Append(text);
                }
                return Task.CompletedTask;
            }, token).ConfigureAwait(false);
        return exit == 0 ? output.ToString() : null;
    }

    /// <summary>【CodingAgent】【环境值】空覆盖仍回退进程值，与上游配置引用保持一致。</summary>
    /// <param name="name">变量名。</param><param name="environment">覆盖。</param><returns>非空值或空引用。</returns>
    private static string? EnvironmentValue(string name, IReadOnlyDictionary<string, string>? environment)
    {
        var value = environment?.GetValueOrDefault(name);
        if (string.IsNullOrEmpty(value)) value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>【CodingAgent】【模板扫描】识别 $NAME、${NAME}、$$ 和 $!，无效引用保留为字面量。</summary>
    /// <param name="config">配置模板。</param><returns>按顺序排列的文本与环境引用。</returns>
    private static IReadOnlyList<Part> ParseTemplate(string config)
    {
        var parts = new List<Part>(); var literal = new StringBuilder();
        for (var index = 0; index < config.Length;)
        {
            if (config[index] != '$') { literal.Append(config[index++]); continue; }
            var next = index + 1 < config.Length ? config[index + 1] : '\0';
            if (next is '$' or '!') { literal.Append(next); index += 2; continue; }
            string? name = null; var end = index + 1;
            if (next == '{')
            {
                var close = config.IndexOf('}', index + 2);
                if (close >= 0)
                {
                    var candidate = config[(index + 2)..close];
                    if (Regex.IsMatch(candidate, "\\A[A-Za-z_][A-Za-z0-9_]*\\z", RegexOptions.CultureInvariant)) { name = candidate; end = close + 1; }
                    else { literal.Append(config[index..(close + 1)]); index = close + 1; continue; }
                }
            }
            else
            {
                var match = Regex.Match(config[(index + 1)..], "\\A[A-Za-z_][A-Za-z0-9_]*", RegexOptions.CultureInvariant);
                if (match.Success) { name = match.Value; end = index + match.Length + 1; }
            }
            if (name is null) { literal.Append('$'); index++; continue; }
            if (literal.Length > 0) { parts.Add(new(literal.ToString(), false)); literal.Clear(); }
            parts.Add(new(name, true)); index = end;
        }
        if (literal.Length > 0) parts.Add(new(literal.ToString(), false));
        return parts;
    }

    /// <summary>【CodingAgent】【模板片段】一个字面量或环境引用。</summary>
    private readonly record struct Part(string Value, bool Environment);
}
