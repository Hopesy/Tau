// 作者：xxx
using System.Text;
using System.Text.Json;
using Tau.Ai;
using Tau.Ai.Auth;
using Tau.Ai.Auth.OAuth;
using Tau.Ai.Providers;
using Tau.Ai.Registry;

namespace Tau.CodingAgent.Runtime;

/// <summary>【CodingAgent】【认证运行时】独立于聊天会话的模型、凭据及配置错误。</summary>
/// <param name="Models">模型集合。</param><param name="Credentials">统一凭据存储。</param><param name="Error">模型配置错误。</param>
internal sealed record CodingAgentAuthRuntime(Models Models, IProviderCredentialStore Credentials, string? Error = null);

/// <summary>【CodingAgent】【认证检查结果】只保存可公开状态，秘密值由显式输出选项独立管理。</summary>
/// <param name="Status">ready、not_ready 或 invalid。</param><param name="Provider">提供方。</param><param name="Reason">失败原因。</param><param name="AuthType">认证类型。</param>
internal sealed record CodingAgentAuthCheckResult(string Status, string Provider, string? Reason = null, string? AuthType = null);

/// <summary>【CodingAgent】【认证命令执行】执行状态检查、API key 打印及带最低有效期的 OAuth bearer 打印。</summary>
internal static class CodingAgentAuthCommands
{
    /// <summary>【CodingAgent】【命令分派】识别新认证子命令，其他命令交给现有 CLI。</summary><param name="args">参数。</param><returns>是否属于本入口。</returns>
    public static bool IsCommand(IReadOnlyList<string> args) => args.Count > 1 && args[0] == "auth" && args[1] is "check" or "print-api-key" or "print-bearer-token";

    /// <summary>【CodingAgent】【命令执行】解析并执行认证命令，状态检查返回 0、1、2，打印失败返回 1。</summary>
    /// <param name="args">原始参数。</param><param name="output">标准输出。</param><param name="error">错误输出。</param>
    /// <param name="runtimeFactory">可注入离线模型与凭据运行时。</param><param name="token">调用方取消。</param>
    /// <param name="oauthProviders">可选兼容 OAuth 注册表。</param><param name="credentialStore">可选 auth.json 存储。</param><returns>退出码。</returns>
    public static async Task<int> HandleAsync(IReadOnlyList<string> args, TextWriter output, TextWriter error,
        Func<CancellationToken, Task<CodingAgentAuthRuntime>>? runtimeFactory = null, CancellationToken token = default,
        OAuthProviderRegistry? oauthProviders = null, OAuthCredentialStore? credentialStore = null)
    {
        if (CodingAgentAuthCommandParser.IsHelp(args)) { PrintHelp(output); return 0; }
        CodingAgentAuthCommand command;
        try { command = CodingAgentAuthCommandParser.Parse(args) ?? throw new CodingAgentAuthCommandException("Expected an auth command"); }
        catch (CodingAgentAuthCommandException exception) { error.WriteLine("Error: " + exception.Message); return 1; }

        try
        {
            CodingAgentCliArguments parsed;
            try { parsed = CodingAgentCliArguments.Parse(command.Arguments); }
            catch (ArgumentException exception) { throw new CodingAgentAuthCommandException(exception.Message); }
            var requested = CodingAgentAuthCommandParser.Validate(parsed, command.Kind);
            // 1. 【CodingAgent】【凭据打印】整体 15 秒时限覆盖初始化、目录恢复及实际认证解析
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(token);
            if (command.Kind != CodingAgentAuthCommandKind.Check) operation.CancelAfter(TimeSpan.FromSeconds(15));
            var signal = operation.Token; signal.ThrowIfCancellationRequested();
            if (command.Kind != CodingAgentAuthCommandKind.Check)
            {
                var runtime = await CreateRuntimeAsync(runtimeFactory, oauthProviders, credentialStore, signal).ConfigureAwait(false);
                if (runtime.Error is not null) throw new InvalidOperationException("Invalid model configuration");
                await runtime.Models.RefreshAsync(allowNetwork: false, cancellationToken: signal).ConfigureAwait(false);
                var value = await ResolveForPrintAsync(command, parsed, requested, runtime, signal).ConfigureAwait(false);
                signal.ThrowIfCancellationRequested(); output.WriteLine(value); return 0;
            }

            // 2. 【CodingAgent】【状态检查】运行时或凭据错误只输出统一状态，不泄露底层异常中的秘密内容
            CodingAgentAuthCheckResult result; string? credential = null;
            try
            {
                var runtime = await CreateRuntimeAsync(runtimeFactory, oauthProviders, credentialStore, signal).ConfigureAwait(false);
                var provider = requested.Model is null ? requested.Provider!
                    : (await CodingAgentAuthModelResolver.ResolveAsync(runtime.Models, requested.Provider, requested.Model, parsed.Thinking, signal).ConfigureAwait(false)).Model.Provider;
                result = await CheckAsync(provider, runtime, !command.NoRefresh, signal).ConfigureAwait(false);
                if (command.Credentials && result.Status == "ready")
                {
                    var stored = await runtime.Credentials.ReadAsync(provider, signal).WaitAsync(signal).ConfigureAwait(false);
                    credential = command.NoRefresh && stored is ProviderCredential.OAuth oauth ? oauth.Value.Access
                        : CodingAgentAuthCommandParser.GetCredential(await GetAuthAsync(runtime.Models, provider, null, null, signal).ConfigureAwait(false));
                    if (string.IsNullOrEmpty(credential)) result = new("not_ready", provider, "credential_not_available");
                }
            }
            catch { result = new("invalid", requested.Provider ?? requested.Model!, "invalid_state"); credential = null; }
            output.WriteLine(command.Json ? ToJson(result, credential) : credential ?? result.Status);
            return result.Status switch { "ready" => 0, "not_ready" => 1, _ => 2 };
        }
        catch (CodingAgentAuthCommandException exception)
        {
            error.WriteLine("Error: " + exception.Message);
            if (exception.UnknownOption) { error.WriteLine($"Use \"tau --help\" or \"{CodingAgentAuthCommandParser.Usage(command.Kind)}\"."); return 1; }
            return command.Kind == CodingAgentAuthCommandKind.Check ? 2 : 1;
        }
        catch { error.WriteLine("Error: Failed to resolve credential"); return command.Kind == CodingAgentAuthCommandKind.Check ? 2 : 1; }
    }

    /// <summary>【CodingAgent】【状态检查】已存 OAuth 可以只检查配置，也可按普通请求策略刷新。</summary>
    /// <param name="provider">提供方。</param><param name="runtime">运行时。</param><param name="refresh">是否解析并刷新认证。</param><param name="token">取消信号。</param><returns>非秘密状态。</returns>
    private static async Task<CodingAgentAuthCheckResult> CheckAsync(string provider, CodingAgentAuthRuntime runtime, bool refresh, CancellationToken token)
    {
        if (runtime.Error is not null) return new("invalid", provider, "invalid_state");
        if (runtime.Models.GetProvider(provider) is null) return new("not_ready", provider, "provider_not_found");
        try
        {
            var status = await runtime.Models.CheckAuthAsync(provider, cancellationToken: token).ConfigureAwait(false);
            if (status?.IsConfigured != true || refresh && await GetAuthAsync(runtime.Models, provider, null, null, token).ConfigureAwait(false) is null)
                return new("not_ready", provider, "credentials_not_configured");
            return new("ready", provider, AuthType: status.UsesOAuth ? "oauth" : "api_key");
        }
        catch { return new("invalid", provider, "invalid_state"); }
    }

    /// <summary>【CodingAgent】【凭据选择】按保存类型过滤提供方，只在唯一匹配时输出实际认证值。</summary>
    /// <param name="command">输出种类与最低有效期。</param><param name="parsed">通用参数。</param><param name="requested">规范选择。</param>
    /// <param name="runtime">模型与凭据。</param><param name="token">总时限信号。</param><returns>唯一凭据值。</returns>
    private static async Task<string> ResolveForPrintAsync(CodingAgentAuthCommand command, CodingAgentCliArguments parsed,
        (string? Provider, string? Model) requested, CodingAgentAuthRuntime runtime, CancellationToken token)
    {
        var types = (await runtime.Credentials.ListAsync(token).WaitAsync(token).ConfigureAwait(false)).ToDictionary(item => item.ProviderId, item => item.Type, StringComparer.OrdinalIgnoreCase);
        var candidates = new List<(string Id, Model? Model)>();
        if (requested.Provider is { } id)
        {
            var provider = runtime.Models.GetProvider(id) ?? throw new CodingAgentAuthCommandException($"Unknown provider \"{id}\". Use --list-models to see available providers.");
            candidates.Add((provider.Id, requested.Model is null ? null
                : (await CodingAgentAuthModelResolver.ResolveAsync(runtime.Models, provider.Id, requested.Model, parsed.Thinking, token).ConfigureAwait(false)).Model));
        }
        else
        {
            foreach (var provider in runtime.Models.GetProviders())
            {
                if (!types.ContainsKey(provider.Id)) continue;
                try
                {
                    var resolved = await CodingAgentAuthModelResolver.ResolveAsync(runtime.Models, provider.Id, requested.Model!, parsed.Thinking, token).ConfigureAwait(false);
                    if (!resolved.Custom) candidates.Add((provider.Id, resolved.Model));
                }
                catch (CodingAgentAuthCommandException) { }
            }
            if (candidates.Count == 0) throw new CodingAgentAuthCommandException($"Model \"{requested.Model}\" not found. Use --list-models to see available models.");
        }
        var values = new List<(string Provider, string Value)>();
        foreach (var candidate in candidates)
        {
            var type = types.GetValueOrDefault(candidate.Id);
            if (command.Kind == CodingAgentAuthCommandKind.ApiKey && type == "oauth" || command.Kind == CodingAgentAuthCommandKind.BearerToken && type != "oauth") continue;
            var minimum = command.Kind == CodingAgentAuthCommandKind.BearerToken ? command.MinimumExpiry ?? TimeSpan.FromMinutes(30) : (TimeSpan?)null;
            var value = CodingAgentAuthCommandParser.GetCredential(await GetAuthAsync(runtime.Models, candidate.Id, candidate.Model, minimum, token).ConfigureAwait(false));
            if (!string.IsNullOrEmpty(value)) values.Add((candidate.Id, value));
        }
        if (values.Count == 1) return values[0].Value;
        if (values.Count > 1) throw new CodingAgentAuthCommandException($"Multiple configured providers matched ({string.Join(", ", values.Select(value => value.Provider))}). Specify --provider.");
        var selected = candidates.FirstOrDefault().Id;
        if (requested.Provider is not null && command.Kind == CodingAgentAuthCommandKind.ApiKey && types.GetValueOrDefault(selected) == "oauth")
            throw new CodingAgentAuthCommandException($"Provider \"{selected}\" is configured with OAuth, not an API key");
        if (requested.Provider is not null && command.Kind == CodingAgentAuthCommandKind.BearerToken && types.GetValueOrDefault(selected) != "oauth")
            throw new CodingAgentAuthCommandException($"Provider \"{selected}\" is not configured with an OAuth bearer token");
        throw new CodingAgentAuthCommandException($"No usable {(command.Kind == CodingAgentAuthCommandKind.ApiKey ? "API key" : "OAuth bearer token")} is configured");
    }

    /// <summary>【CodingAgent】【认证解析】模型选择时保留模型级请求元数据，仅提供方选择时使用空模型标识。</summary>
    /// <param name="models">集合。</param><param name="provider">提供方。</param><param name="model">可选模型。</param><param name="minimum">最低 OAuth 有效期。</param><param name="token">取消。</param><returns>实际认证。</returns>
    private static Task<ProviderAuthResult?> GetAuthAsync(Models models, string provider, Model? model, TimeSpan? minimum, CancellationToken token) =>
        models.ResolveAuthAsync(model ?? new Model { Id = "", Name = provider, Provider = provider, Api = "" }, cancellationToken: token, minimumOAuthValidity: minimum);

    /// <summary>【CodingAgent】【独立认证运行时】不创建聊天会话，使用统一文件凭据和本地模型配置。</summary>
    /// <param name="factory">测试或宿主注入工厂。</param><param name="providers">OAuth 注册表。</param><param name="store">文件存储。</param><param name="token">取消。</param><returns>认证运行时。</returns>
    private static async Task<CodingAgentAuthRuntime> CreateRuntimeAsync(Func<CancellationToken, Task<CodingAgentAuthRuntime>>? factory,
        OAuthProviderRegistry? providers, OAuthCredentialStore? store, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (factory is not null) return await factory(token).WaitAsync(token).ConfigureAwait(false);
        store ??= new OAuthCredentialStore(); var configuration = new ModelConfigurationStore();
        var resolver = new ProviderAuthResolver(providers, store, configurationStore: configuration);
        var models = BuiltInProviders.CreateBuiltInModels(configuration, resolver, credentialStore: store);
        return new(models, store, configuration.Error);
    }

    /// <summary>【CodingAgent】【状态 JSON】固定字段名并省略空字段，只有明确请求且成功时附带秘密值。</summary>
    /// <param name="result">状态。</param><param name="credential">显式请求的可选凭据。</param><returns>单行 JSON。</returns>
    private static string ToJson(CodingAgentAuthCheckResult result, string? credential)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WriteString("status", result.Status); writer.WriteString("provider", result.Provider);
            if (result.Reason is not null) writer.WriteString("reason", result.Reason);
            if (result.AuthType is not null) writer.WriteString("authType", result.AuthType);
            if (!string.IsNullOrEmpty(credential)) writer.WriteString("credentials", credential);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>【CodingAgent】【认证帮助】展示新命令及刷新、凭据输出规则。</summary><param name="output">输出目标。</param>
    public static void PrintHelp(TextWriter output) => output.WriteLine("""
        Usage:
          tau auth print-api-key [--provider <provider>] [--model <model>]
          tau auth print-bearer-token [--provider <provider>] [--model <model>] [--min-expiry <duration>]
          tau auth check [--provider <provider>] [--model <model>] [--json] [--credentials] [--no-refresh]

        Auth commands require at least one of --provider or --model. Checks refresh expired OAuth credentials by default; --no-refresh prevents this. --credentials emits the credential, or includes it in JSON output.
        """);
}
