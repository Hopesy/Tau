using Microsoft.Extensions.Options;
using Tau.Mom;

var builder = Host.CreateApplicationBuilder(args);
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
        DefaultWorkingDirectory = Path.GetFullPath(configured.DefaultWorkingDirectory, root),
        DefaultProvider = configured.DefaultProvider,
        DefaultModel = configured.DefaultModel,
        PollIntervalSeconds = configured.PollIntervalSeconds,
        MaxFilesPerPoll = configured.MaxFilesPerPoll
    };
});
builder.Services.AddSingleton<IDelegationAgentRunner, RuntimeDelegationAgentRunner>();
builder.Services.AddSingleton<FileDelegationProcessor>();

var runOnce = args.Any(arg => string.Equals(arg, "--once", StringComparison.OrdinalIgnoreCase));
if (!runOnce)
{
    builder.Services.AddHostedService<Worker>();
}

var host = builder.Build();

if (runOnce)
{
    var processor = host.Services.GetRequiredService<FileDelegationProcessor>();
    var options = host.Services.GetRequiredService<MomOptions>();
    var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Tau.Mom.RunOnce");
    logger.LogInformation(
        "Processing local delegation inbox once. inbox={InboxPath} outbox={OutboxPath} provider={Provider} model={Model} workdir={WorkingDirectory}",
        options.InboxPath,
        options.OutboxPath,
        options.DefaultProvider,
        options.DefaultModel ?? "<default>",
        options.DefaultWorkingDirectory);
    var count = await processor.ProcessPendingAsync().ConfigureAwait(false);
    logger.LogInformation("Processed {Count} file(s).", count);
    return;
}

await host.RunAsync().ConfigureAwait(false);
