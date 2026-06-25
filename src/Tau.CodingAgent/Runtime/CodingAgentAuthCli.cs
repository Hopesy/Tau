using Tau.Ai.Auth.OAuth;

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
        CancellationToken cancellationToken = default)
    {
        if (args.Count == 0)
        {
            return null;
        }

        oauthProviders ??= new OAuthProviderRegistry();
        credentialStore ??= new OAuthCredentialStore();
        callbacksFactory ??= static () => new ConsoleOAuthLoginCallbacks();

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
                    cancellationToken)
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
                    cancellationToken)
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
        CancellationToken cancellationToken)
    {
        if (args.Count > 1)
        {
            error.WriteLine("Usage: tau auth login [provider]");
            return 1;
        }

        var providerId = args.Count == 1 ? args[0] : await SelectProviderAsync(input, output, oauthProviders).ConfigureAwait(false);
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
            var credentials = await provider.LoginAsync(callbacksFactory(), cancellationToken).ConfigureAwait(false);
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

    private static async Task<string?> SelectProviderAsync(
        TextReader input,
        TextWriter output,
        OAuthProviderRegistry oauthProviders)
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
        var choice = await input.ReadLineAsync().ConfigureAwait(false);
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
