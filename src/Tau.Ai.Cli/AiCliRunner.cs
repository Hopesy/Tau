using Tau.Ai.Auth;
using Tau.Ai.Auth.OAuth;

namespace Tau.Ai.Cli;

internal sealed class AiCliRunner
{
    private const string CommandNameEnvironmentVariable = "TAU_AI_CLI_COMMAND_NAME";

    private readonly OAuthProviderRegistry _providers;
    private readonly IAiCliConsole _console;
    private readonly Func<IReadOnlyList<string>, IAiCliCredentialStore> _credentialStoreFactory;
    private readonly Func<IReadOnlyList<string>> _defaultAuthSearchPathsFactory;
    private readonly string _commandName;

    public AiCliRunner(
        OAuthProviderRegistry providers,
        IAiCliConsole console,
        Func<IReadOnlyList<string>, IAiCliCredentialStore> credentialStoreFactory,
        Func<IReadOnlyList<string>>? defaultAuthSearchPathsFactory = null,
        string commandName = "tau-ai")
    {
        _providers = providers;
        _console = console;
        _credentialStoreFactory = credentialStoreFactory;
        _defaultAuthSearchPathsFactory = defaultAuthSearchPathsFactory ?? GetDefaultAuthSearchPaths;
        _commandName = commandName;
    }

    public static AiCliRunner CreateDefault(string? commandName = null) =>
        new(
            new OAuthProviderRegistry(),
            new SystemAiCliConsole(),
            paths => new OAuthCredentialStoreWriter(paths),
            commandName: commandName ?? GetDefaultCommandName());

    internal static string GetDefaultCommandName()
    {
        var commandName = Environment.GetEnvironmentVariable(CommandNameEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(commandName))
        {
            return commandName.Trim();
        }

        var toolCommandName = GetToolCommandName();
        return string.IsNullOrWhiteSpace(toolCommandName) ? "tau-ai" : toolCommandName;
    }

    private static string? GetToolCommandName()
    {
        var commandPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(commandPath))
        {
            return null;
        }

        var fileName = Path.GetFileNameWithoutExtension(commandPath.Trim().Trim('"'));
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        if (fileName.Equals("tau-ai", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("pi-ai", StringComparison.OrdinalIgnoreCase))
        {
            return fileName;
        }

        return null;
    }

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        var parse = AiCliArguments.Parse(args);
        if (parse.Error is not null)
        {
            _console.WriteErrorLine(parse.Error);
            _console.WriteErrorLine($"Use '{_commandName} --help' for usage.");
            return 1;
        }

        var commandArgs = parse.CommandArgs;
        var command = commandArgs.Count == 0 ? null : commandArgs[0];
        if (command is null ||
            command.Equals("help", StringComparison.OrdinalIgnoreCase) ||
            command.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
            command.Equals("-h", StringComparison.OrdinalIgnoreCase))
        {
            WriteUsage();
            return 0;
        }

        if (command.Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            WriteProviderList();
            return 0;
        }

        if (command.Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            return RunStatus(commandArgs.Skip(1).ToArray(), parse.AuthFile);
        }

        if (command.Equals("login", StringComparison.OrdinalIgnoreCase))
        {
            return await RunLoginAsync(commandArgs.Skip(1).ToArray(), parse.AuthFile, cancellationToken)
                .ConfigureAwait(false);
        }

        _console.WriteErrorLine($"Unknown command: {command}");
        _console.WriteErrorLine($"Use '{_commandName} --help' for usage.");
        return 1;
    }

    private int RunStatus(IReadOnlyList<string> args, string? authFile)
    {
        if (args.Count > 1)
        {
            _console.WriteErrorLine("status accepts at most one provider.");
            _console.WriteErrorLine($"Use '{_commandName} --help' for usage.");
            return 1;
        }

        var authPaths = string.IsNullOrWhiteSpace(authFile)
            ? _defaultAuthSearchPathsFactory()
            : [authFile!];
        var resolver = new ProviderAuthResolver(
            _providers,
            new OAuthCredentialStore(authPaths));

        if (args.Count == 1)
        {
            WriteProviderStatus([resolver.GetStatus(args[0])]);
            return 0;
        }

        var providers = GetProviders();
        if (providers.Count == 0)
        {
            _console.WriteErrorLine("No OAuth providers are registered.");
            return 1;
        }

        WriteProviderStatus(providers.Select(provider => resolver.GetStatus(provider.Id)).ToArray());
        return 0;
    }

    private async Task<int> RunLoginAsync(
        IReadOnlyList<string> args,
        string? authFile,
        CancellationToken cancellationToken)
    {
        string? providerId = null;
        if (args.Count > 0)
        {
            providerId = args[0];
        }

        var providers = GetProviders();
        if (providerId is null)
        {
            if (providers.Count == 0)
            {
                _console.WriteErrorLine("No OAuth providers are registered.");
                return 1;
            }

            _console.WriteLine("Select a provider:");
            _console.WriteLine();
            for (var i = 0; i < providers.Count; i++)
            {
                _console.WriteLine($"  {i + 1}. {providers[i].Name}");
            }
            _console.WriteLine();

            var choice = await _console
                .ReadLineAsync($"Enter number (1-{providers.Count}): ", cancellationToken)
                .ConfigureAwait(false);
            if (!int.TryParse(choice, out var selected) || selected < 1 || selected > providers.Count)
            {
                _console.WriteErrorLine("Invalid selection");
                return 1;
            }

            providerId = providers[selected - 1].Id;
        }

        var provider = _providers.TryGet(providerId);
        if (provider is null)
        {
            _console.WriteErrorLine($"Unknown provider: {providerId}");
            _console.WriteErrorLine($"Use '{_commandName} list' to see available providers.");
            return 1;
        }

        _console.WriteLine($"Logging in to {provider.Id}...");

        try
        {
            var callbacks = new AiCliOAuthLoginCallbacks(_console, cancellationToken);
            var credentials = await provider.LoginAsync(callbacks, cancellationToken).ConfigureAwait(false);
            var authPaths = string.IsNullOrWhiteSpace(authFile)
                ? _defaultAuthSearchPathsFactory()
                : [authFile!];
            var store = _credentialStoreFactory(authPaths);
            store.Save(provider.Id, credentials);
            _console.WriteLine();
            _console.WriteLine($"Credentials saved to {store.DisplayPath}");
            return 0;
        }
        catch (OperationCanceledException ex)
        {
            _console.WriteErrorLine(string.IsNullOrWhiteSpace(ex.Message) ? "Login cancelled." : ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            _console.WriteErrorLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private void WriteUsage()
    {
        var providerList = string.Join(
            Environment.NewLine,
            GetProviders().Select(static provider => $"  {provider.Id.PadRight(20)} {provider.Name}"));

        _console.WriteLine($"""
Usage: {_commandName} <command> [provider] [options]

Commands:
  login [provider]  Login to an OAuth provider
  status [provider] Show auth status without reading or printing secrets
  list              List available providers

Options:
  --auth-file PATH  Read/write credentials at PATH instead of the Tau default auth store

Providers:
{providerList}

Examples:
  {_commandName} login
  {_commandName} login anthropic
  {_commandName} --auth-file auth.json login anthropic
  {_commandName} --auth-file auth.json status anthropic
  {_commandName} list
""");
    }

    private void WriteProviderStatus(IReadOnlyList<ProviderAuthStatus> statuses)
    {
        _console.WriteLine("Provider authentication status:");
        _console.WriteLine();
        foreach (var status in statuses)
        {
            var oauthProvider = _providers.TryGet(status.Provider);
            _console.WriteLine(
                $"  {status.Provider.PadRight(20)} {FormatConfigured(status).PadRight(10)} source={status.Source}; oauth={FormatBoolean(status.UsesOAuth)}; login={FormatBoolean(status.CanLogin)}; callback={FormatCallbackMode(oauthProvider)}; {status.Message}");
        }
    }

    private void WriteProviderList()
    {
        _console.WriteLine("Available OAuth providers:");
        _console.WriteLine();
        foreach (var provider in GetProviders())
        {
            _console.WriteLine($"  {provider.Id.PadRight(20)} {provider.Name} (callback={FormatBoolean(provider.UsesCallbackServer)})");
        }
    }

    private IReadOnlyList<IOAuthProvider> GetProviders() => _providers.Providers.ToArray();

    private static string FormatConfigured(ProviderAuthStatus status) =>
        status.IsConfigured ? "configured" : "missing";

    private static string FormatBoolean(bool value) => value ? "yes" : "no";

    private static string FormatCallbackMode(IOAuthProvider? provider)
    {
        if (provider is null)
        {
            return "n/a";
        }

        return provider.UsesCallbackServer ? "yes" : "no";
    }

    private static string[] GetDefaultAuthSearchPaths()
    {
        var paths = new List<string>();
        var configured = Environment.GetEnvironmentVariable("TAU_AUTH_FILE");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            paths.Add(configured);
        }

        paths.Add(Path.Combine(Directory.GetCurrentDirectory(), ".tau", "auth.json"));

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            paths.Add(Path.Combine(home, ".tau", "auth.json"));
        }

        return paths.ToArray();
    }
}

internal sealed record AiCliArguments(IReadOnlyList<string> CommandArgs, string? AuthFile, string? Error)
{
    public static AiCliArguments Parse(IReadOnlyList<string> args)
    {
        var commandArgs = new List<string>();
        string? authFile = null;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.Equals("--auth-file", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Count)
                {
                    return new AiCliArguments([], null, "--auth-file requires a path.");
                }

                authFile = args[++i];
                continue;
            }

            if (arg.StartsWith("--auth-file=", StringComparison.OrdinalIgnoreCase))
            {
                authFile = arg["--auth-file=".Length..];
                if (string.IsNullOrWhiteSpace(authFile))
                {
                    return new AiCliArguments([], null, "--auth-file requires a path.");
                }

                continue;
            }

            commandArgs.Add(arg);
        }

        return new AiCliArguments(commandArgs, authFile, null);
    }
}

internal interface IAiCliCredentialStore
{
    string DisplayPath { get; }
    void Save(string providerId, OAuthCredentials credentials);
}

internal sealed class OAuthCredentialStoreWriter : IAiCliCredentialStore
{
    private readonly OAuthCredentialStore _store;

    public OAuthCredentialStoreWriter(IReadOnlyList<string> searchPaths)
    {
        if (searchPaths.Count == 0)
        {
            throw new ArgumentException("At least one auth search path is required.", nameof(searchPaths));
        }

        var paths = searchPaths.ToArray();
        DisplayPath = paths.FirstOrDefault(File.Exists) ?? paths[0];
        _store = new OAuthCredentialStore(paths);
    }

    public string DisplayPath { get; }

    public void Save(string providerId, OAuthCredentials credentials) => _store.Save(providerId, credentials);
}

internal interface IAiCliConsole
{
    void WriteLine(string message = "");
    void WriteErrorLine(string message);
    Task<string?> ReadLineAsync(string prompt, CancellationToken cancellationToken = default);
}

internal sealed class SystemAiCliConsole : IAiCliConsole
{
    public void WriteLine(string message = "") => Console.WriteLine(message);

    public void WriteErrorLine(string message) => Console.Error.WriteLine(message);

    public Task<string?> ReadLineAsync(string prompt, CancellationToken cancellationToken = default)
    {
        Console.Write(prompt);
        return Task.FromResult(Console.ReadLine());
    }
}

internal sealed class AiCliOAuthLoginCallbacks(
    IAiCliConsole console,
    CancellationToken cancellationToken) : IOAuthLoginCallbacks
{
    public void OnAuth(string url, string? instructions = null)
    {
        console.WriteLine();
        console.WriteLine("Open this URL in your browser:");
        console.WriteLine(url);
        if (!string.IsNullOrWhiteSpace(instructions))
        {
            console.WriteLine(instructions);
        }
        console.WriteLine();
    }

    public async Task<string> OnPromptAsync(string message, string? placeholder = null, bool allowEmpty = false)
    {
        var suffix = string.IsNullOrWhiteSpace(placeholder) ? ":" : $" ({placeholder}):";
        var input = await console.ReadLineAsync($"{message}{suffix} ", cancellationToken).ConfigureAwait(false)
                    ?? string.Empty;
        if (!allowEmpty && string.IsNullOrWhiteSpace(input))
        {
            throw new OperationCanceledException("Login cancelled (empty input).", cancellationToken);
        }

        return input;
    }

    public void OnProgress(string message) => console.WriteLine(message);

    public Task<string>? OnManualCodeInputAsync() => ReadManualCodeInputAsync();

    private async Task<string> ReadManualCodeInputAsync()
    {
        return await console.ReadLineAsync("Enter authorization code: ", cancellationToken).ConfigureAwait(false)
               ?? string.Empty;
    }
}
