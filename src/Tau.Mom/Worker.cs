namespace Tau.Mom;

public class Worker(
    ILogger<Worker> logger,
    MomLocalDelegationFlow flow,
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
            var result = await flow.ProcessOnceAsync(stoppingToken).ConfigureAwait(false);
            if (result.QueuedEvents > 0)
            {
                logger.LogInformation("Queued {Queued} due mom event(s).", result.QueuedEvents);
            }

            if (result.ProcessedRequests > 0)
            {
                logger.LogInformation("Processed {Processed} delegation request(s).", result.ProcessedRequests);
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.PollIntervalSeconds)), stoppingToken).ConfigureAwait(false);
        }
    }
}
