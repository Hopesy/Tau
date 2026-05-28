namespace Tau.Mom;

public static class MomSandboxValidator
{
    public static async Task<MomSandboxValidationResult> ValidateAsync(
        MomOptions options,
        IMomSandboxProcessRunner? processRunner = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var sandbox = options.Sandbox;
        MomSandboxConfig config;
        try
        {
            config = MomSandboxConfig.Parse(sandbox);
        }
        catch (ArgumentException ex)
        {
            return new MomSandboxValidationResult(
                string.IsNullOrWhiteSpace(sandbox) ? "host" : sandbox,
                Succeeded: false,
                Message: ex.Message);
        }

        try
        {
            await MomSandboxExecutorFactory.ValidateAsync(config, processRunner, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return new MomSandboxValidationResult(
                config.DisplayName,
                Succeeded: false,
                Message: ex.Message);
        }

        var message = config.Kind == MomSandboxKind.Host
            ? "Mom sandbox 'host' is valid. No Docker checks were required."
            : $"Mom sandbox '{config.DisplayName}' is valid. Docker is available and the container is running.";
        return new MomSandboxValidationResult(config.DisplayName, Succeeded: true, message);
    }
}
