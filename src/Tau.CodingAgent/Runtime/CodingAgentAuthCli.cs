using Tau.Ai.Auth.OAuth;
using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Runtime;

internal static class CodingAgentAuthCli
{
    public static async Task<int?> TryHandleAsync(
        IReadOnlyList<string> args,
        TextReader input,
        TextWriter output,
        TextWriter error,
        OAuthProviderRegistry? oauthProviders = null,
        OAuthCredentialStore? credentialStore = null,
        Func<IOAuthLoginCallbacks>? callbacksFactory = null,
        CancellationToken cancellationToken = default,
        CodingAgentSettingsStore? settingsStore = null)
    {
        if (args.Count == 0)
        {
            return null;
        }

        // 1. 【CodingAgent】【认证命令】新状态与凭据命令复用统一认证链路，旧登录命令继续使用原交互入口
        if (CodingAgentAuthCommands.IsCommand(args))
            return await CodingAgentAuthCommands.HandleAsync(args, output, error, token: cancellationToken,
                oauthProviders: oauthProviders, credentialStore: credentialStore).ConfigureAwait(false);

        oauthProviders ??= new OAuthProviderRegistry();
        credentialStore ??= new OAuthCredentialStore();
        callbacksFactory ??= () => new ConsoleOAuthLoginCallbacks(input, output);

        if (args[0].Equals("login", StringComparison.OrdinalIgnoreCase))
        {
            return await LoginAsync(
                    args.Skip(1).ToArray(),
                    input,
                    output,
                    error,
                    oauthProviders,
                    credentialStore,
                    callbacksFactory,
                    cancellationToken,
                    settingsStore)
                .ConfigureAwait(false);
        }

        if (!args[0].Equals("auth", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rest = args.Skip(1).ToArray();
        if (rest.Length == 0 ||
            rest[0].Equals("help", StringComparison.OrdinalIgnoreCase) ||
            rest[0].Equals("--help", StringComparison.OrdinalIgnoreCase) ||
            rest[0].Equals("-h", StringComparison.OrdinalIgnoreCase))
        {
            PrintHelp(output);
            return 0;
        }

        if (rest[0].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            if (rest.Length != 1)
            {
                error.WriteLine("Usage: tau auth list");
                return 1;
            }

            PrintProviders(output, oauthProviders);
            return 0;
        }

        if (rest[0].Equals("login", StringComparison.OrdinalIgnoreCase))
        {
            return await LoginAsync(
                    rest.Skip(1).ToArray(),
                    input,
                    output,
                    error,
                    oauthProviders,
                    credentialStore,
                    callbacksFactory,
                    cancellationToken,
                    settingsStore)
                .ConfigureAwait(false);
        }

        error.WriteLine($"Unknown auth command: {rest[0]}");
        error.WriteLine("Use 'tau auth --help' for usage.");
        return 1;
    }

    private static async Task<int> LoginAsync(
        IReadOnlyList<string> args,
        TextReader input,
        TextWriter output,
        TextWriter error,
        OAuthProviderRegistry oauthProviders,
        OAuthCredentialStore credentialStore,
        Func<IOAuthLoginCallbacks> callbacksFactory,
        CancellationToken cancellationToken,
        CodingAgentSettingsStore? settingsStore)
    {
        if (args.Count > 1)
        {
            error.WriteLine("Usage: tau auth login [provider]");
            return 1;
        }

        string? providerId;
        try { providerId = args.Count == 1 ? args[0] : await SelectProviderAsync(input, output, oauthProviders, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { error.WriteLine("Login cancelled."); return 1; }
        if (string.IsNullOrWhiteSpace(providerId))
        {
            error.WriteLine("Invalid selection");
            return 1;
        }

        var provider = oauthProviders.TryGet(providerId);
        if (provider is null)
        {
            error.WriteLine($"Unknown provider: {providerId}");
            error.WriteLine("Use 'tau auth list' to see available providers.");
            return 1;
        }

        output.WriteLine($"Logging in to {provider.Id}...");
        try
        {
            var options = new OAuthLoginOptions(() => (settingsStore ?? CodingAgentSettingsStore.ForInstallation()).GetOrCreateDeviceId());
            var credentials = await provider.LoginAsync(callbacksFactory(), options, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            credentialStore.Save(provider.Id, credentials);
            output.WriteLine("Credentials saved to auth.json.");
            return 0;
        }
        catch (OperationCanceledException)
        {
            error.WriteLine("Login cancelled.");
            return 1;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    /// <summary>【CodingAgent】【提供方选择】列出旧 CLI 登录提供方，等待期间接受 Ctrl+C 取消。</summary>
    /// <param name="input">输入。</param><param name="output">输出。</param><param name="oauthProviders">认证目录。</param>
    /// <param name="token">取消信号。</param><returns>提供方 ID 或无效选择。</returns>
    private static async Task<string?> SelectProviderAsync(
        TextReader input,
        TextWriter output,
        OAuthProviderRegistry oauthProviders,
        CancellationToken token)
    {
        var providers = oauthProviders.Providers
            .OrderBy(provider => provider.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (providers.Length == 0)
        {
            return null;
        }

        output.WriteLine("Select a provider:");
        output.WriteLine();
        for (var i = 0; i < providers.Length; i++)
        {
            output.WriteLine($"  {i + 1}. {providers[i].Name}");
        }

        output.WriteLine();
        output.Write($"Enter number (1-{providers.Length}): ");
        var choice = ReferenceEquals(input, Console.In)
            ? await TuiConsoleInput.ReadLineAsync(token: token).ConfigureAwait(false)
            : await TuiCancelableTextReader.ReadLineAsync(input, token).ConfigureAwait(false);
        if (!int.TryParse(choice, out var index) || index < 1 || index > providers.Length)
        {
            return null;
        }

        return providers[index - 1].Id;
    }

    private static void PrintProviders(TextWriter output, OAuthProviderRegistry oauthProviders)
    {
        output.WriteLine("Available OAuth providers:");
        output.WriteLine();
        foreach (var provider in oauthProviders.Providers.OrderBy(provider => provider.Id, StringComparer.OrdinalIgnoreCase))
        {
            output.WriteLine($"  {provider.Id.PadRight(20)} {provider.Name}");
        }
    }

    private static void PrintHelp(TextWriter output)
    {
        CodingAgentAuthCommands.PrintHelp(output);
        output.WriteLine();
        output.WriteLine(
            """
            Usage: tau auth <command> [provider]

            Commands:
              auth login [provider]  Login to an OAuth provider
              auth list              List available OAuth providers

            Aliases:
              login [provider]       Login to an OAuth provider

            Examples:
              tau auth login
              tau auth login anthropic
              tau auth list
            """);
    }
}
