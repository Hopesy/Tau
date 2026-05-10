namespace Tau.Mom;

public class Worker(
    ILogger<Worker> logger,
    MomEventProcessor eventProcessor,
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
            var queued = await eventProcessor.ProcessDueEventsAsync(stoppingToken).ConfigureAwait(false);
            if (queued > 0)
            {
                logger.LogInformation("Queued {Queued} due mom event(s).", queued);
            }

            var processed = await processor.ProcessPendingAsync(stoppingToken).ConfigureAwait(false);
            if (processed > 0)
            {
                logger.LogInformation("Processed {Processed} delegation request(s).", processed);
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.PollIntervalSeconds)), stoppingToken).ConfigureAwait(false);
        }
    }
}
