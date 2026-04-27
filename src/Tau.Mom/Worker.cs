namespace Tau.Mom;

public class Worker(
    ILogger<Worker> logger,
    FileDelegationProcessor processor,
    MomOptions options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Tau.Mom worker started. inbox={InboxPath} outbox={OutboxPath} archive={ArchivePath}",
            options.InboxPath,
            options.OutboxPath,
            options.ArchivePath);

        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = await processor.ProcessPendingAsync(stoppingToken).ConfigureAwait(false);
            if (processed > 0)
            {
                logger.LogInformation("Processed {Processed} delegation request(s).", processed);
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.PollIntervalSeconds)), stoppingToken).ConfigureAwait(false);
        }
    }
}
