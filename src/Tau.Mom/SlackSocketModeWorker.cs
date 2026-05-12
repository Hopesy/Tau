namespace Tau.Mom;

public sealed class SlackSocketModeWorker(
    SlackSocketModeTransport transport,
    MomChannelQueueDispatcher dispatcher,
    SlackBackfillService backfill,
    MomOptions options,
    ILogger<SlackSocketModeWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.SlackSocketModeEnabled)
        {
            logger.LogInformation("Slack Socket Mode worker disabled.");
            return;
        }

        if (options.SlackBackfillEnabled)
        {
            try
            {
                var count = await backfill.BackfillExistingChannelsAsync(stoppingToken).ConfigureAwait(false);
                logger.LogInformation("Slack startup backfill wrote {Count} message(s).", count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Slack startup backfill failed; continuing Socket Mode worker.");
            }
        }

        logger.LogInformation("Slack Socket Mode worker started.");
        await foreach (var message in transport.ReadMessagesAsync(stoppingToken).ConfigureAwait(false))
        {
            var dispatch = dispatcher.Dispatch(message, transport.Responder, stoppingToken);
            if (!dispatch.Accepted)
            {
                logger.LogWarning(
                    "Discarded Slack channel message {ChannelId}/{Ts}: {Status}.",
                    message.ChannelId,
                    message.Ts,
                    dispatch.Status);
            }
        }
    }
}
