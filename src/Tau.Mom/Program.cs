using System.Text.Json;
using Microsoft.Extensions.Options;
using Tau.Ai.Observability;
using Tau.Mom;

var commandLine = MomCommandLine.Parse(args);
var builder = Host.CreateApplicationBuilder(commandLine.HostArgs);
builder.Services.Configure<MomOptions>(builder.Configuration.GetSection("Mom"));
builder.Services.AddSingleton(sp =>
{
    var env = sp.GetRequiredService<IHostEnvironment>();
    var configured = sp.GetRequiredService<IOptions<MomOptions>>().Value;
    var root = Directory.GetParent(env.ContentRootPath)?.Parent?.FullName ?? env.ContentRootPath;

    return new MomOptions
    {
        InboxPath = Path.GetFullPath(configured.InboxPath, root),
        OutboxPath = Path.GetFullPath(configured.OutboxPath, root),
        ArchivePath = Path.GetFullPath(configured.ArchivePath, root),
        EventsPath = Path.GetFullPath(configured.EventsPath, root),
        DefaultWorkingDirectory = Path.GetFullPath(configured.DefaultWorkingDirectory, root),
        Sandbox = configured.Sandbox,
        DefaultProvider = configured.DefaultProvider,
        DefaultModel = configured.DefaultModel,
        SlackSocketModeEnabled = configured.SlackSocketModeEnabled,
        SlackAppToken = configured.SlackAppToken,
        SlackBotToken = configured.SlackBotToken,
        SlackApiBaseUrl = configured.SlackApiBaseUrl,
        SlackBackfillEnabled = configured.SlackBackfillEnabled,
        SlackBackfillMaxPages = configured.SlackBackfillMaxPages,
        SlackBackfillPageSize = configured.SlackBackfillPageSize,
        SlackChannelQueueLimit = configured.SlackChannelQueueLimit,
        SlackSocketModeReconnectDelaySeconds = configured.SlackSocketModeReconnectDelaySeconds,
        PollIntervalSeconds = configured.PollIntervalSeconds,
        MaxFilesPerPoll = configured.MaxFilesPerPoll,
        RunningStatusStaleAfterMinutes = configured.RunningStatusStaleAfterMinutes
    };
});
builder.Services.AddSingleton<ITauLogSink>(_ => JsonlTauLogSink.FromEnvironment() is { } sink ? sink : NullTauLogSink.Instance);
builder.Services.AddSingleton<IDelegationAgentRunner, RuntimeDelegationAgentRunner>();
builder.Services.AddSingleton<ChannelStatusStore>();
builder.Services.AddSingleton<MomEventProcessor>();
builder.Services.AddSingleton<FileDelegationProcessor>();
builder.Services.AddSingleton<MomLocalDelegationFlow>();
builder.Services.AddSingleton<MomChannelRunRegistry>();
builder.Services.AddSingleton<MomChannelMessageProcessor>();
builder.Services.AddSingleton<MomChannelQueueDispatcher>();
builder.Services.AddSingleton<SlackAttachmentDownloader>();
builder.Services.AddSingleton<SlackBackfillService>();
builder.Services.AddSingleton<SlackChannelHistoryDownloadService>();
builder.Services.AddSingleton<SlackWebApiResponder>();
builder.Services.AddSingleton<SlackSocketModeTransport>();

if (!commandLine.RunOnce && !commandLine.ValidateSandbox && !commandLine.ValidateSlack && !commandLine.HasDownload)
{
    builder.Services.AddHostedService<Worker>();
    builder.Services.AddHostedService<SlackSocketModeWorker>();
}

var host = builder.Build();

if (commandLine.HasDownload)
{
    var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Tau.Mom.Download");
    if (string.IsNullOrWhiteSpace(commandLine.DownloadChannelId))
    {
        Console.Error.WriteLine("Usage: Tau.Mom --download <channel-id>");
        Environment.ExitCode = 1;
        return;
    }

    try
    {
        var downloader = host.Services.GetRequiredService<SlackChannelHistoryDownloadService>();
        await downloader.DownloadAsync(commandLine.DownloadChannelId, Console.Out, Console.Error)
            .ConfigureAwait(false);
        return;
    }
    catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or JsonException)
    {
        logger.LogError("{Message}", ex.Message);
        Environment.ExitCode = 1;
        return;
    }
}

if (commandLine.ValidateSandbox)
{
    var options = host.Services.GetRequiredService<MomOptions>();
    var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Tau.Mom.ValidateSandbox");
    var result = await MomSandboxValidator.ValidateAsync(options).ConfigureAwait(false);
    if (commandLine.JsonOutput)
    {
        WriteValidationJson(ToSandboxJsonResult(result), result.Succeeded);
        return;
    }

    if (result.Succeeded)
    {
        logger.LogInformation("{Message}", result.Message);
        return;
    }

    logger.LogError("{Message}", result.Message);
    Environment.ExitCode = 1;
    return;
}

if (commandLine.ValidateSlack)
{
    var validator = host.Services.GetRequiredService<SlackSocketModeTransport>();
    var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Tau.Mom.ValidateSlack");
    var result = await validator.ValidateAsync().ConfigureAwait(false);
    if (commandLine.JsonOutput)
    {
        WriteValidationJson(ToSlackJsonResult(result), result.Succeeded);
        return;
    }

    if (result.Succeeded)
    {
        logger.LogInformation("{Message}", result.Message);
        return;
    }

    logger.LogError("{Message}", result.Message);
    Environment.ExitCode = 1;
    return;
}

if (commandLine.RunOnce)
{
    var flow = host.Services.GetRequiredService<MomLocalDelegationFlow>();
    var options = host.Services.GetRequiredService<MomOptions>();
    var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Tau.Mom.RunOnce");
    logger.LogInformation(
        "Processing local delegation inbox once. inbox={InboxPath} events={EventsPath} outbox={OutboxPath} provider={Provider} model={Model} workdir={WorkingDirectory}",
        options.InboxPath,
        options.EventsPath,
        options.OutboxPath,
        options.DefaultProvider,
        options.DefaultModel ?? "<default>",
        options.DefaultWorkingDirectory);
    var result = await flow.ProcessOnceAsync().ConfigureAwait(false);
    logger.LogInformation("Queued {Count} due event(s).", result.QueuedEvents);
    logger.LogInformation("Processed {Count} file(s).", result.ProcessedRequests);
    return;
}

await host.RunAsync().ConfigureAwait(false);

static void WriteValidationJson<TPayload>(TPayload payload, bool succeeded)
{
    Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    if (!succeeded)
    {
        Environment.ExitCode = 1;
    }
}

static MomSandboxValidationJsonResult ToSandboxJsonResult(MomSandboxValidationResult result) =>
    new(
        result.Succeeded,
        result.Message,
        result.Sandbox,
        result.Succeeded ? null : result.Message);

static MomSlackValidationJsonResult ToSlackJsonResult(MomSlackValidationResult result) =>
    new(
        result.Succeeded,
        result.Message,
        result.SlackSocketModeEnabled,
        result.BotUserId,
        result.SocketHost,
        result.Error);
