namespace Tau.Mom;

public sealed class MomOptions
{
    public string InboxPath { get; set; } = "mom/inbox";
    public string OutboxPath { get; set; } = "mom/outbox";
    public string ArchivePath { get; set; } = "mom/archive";
    public string EventsPath { get; set; } = "mom/events";
    public string DefaultWorkingDirectory { get; set; } = ".";
    public string Sandbox { get; set; } = "host";
    public string DefaultProvider { get; set; } = "openai";
    public string? DefaultModel { get; set; }
    public bool SlackSocketModeEnabled { get; set; }
    public string? SlackAppToken { get; set; }
    public string? SlackBotToken { get; set; }
    public string SlackApiBaseUrl { get; set; } = "https://slack.com/api/";
    public bool SlackBackfillEnabled { get; set; } = true;
    public int SlackBackfillMaxPages { get; set; } = 3;
    public int SlackBackfillPageSize { get; set; } = 1000;
    public int SlackChannelQueueLimit { get; set; } = 5;
    public int SlackSocketModeReconnectDelaySeconds { get; set; } = 5;
    public int PollIntervalSeconds { get; set; } = 5;
    public int MaxFilesPerPoll { get; set; } = 8;
    public int RunningStatusStaleAfterMinutes { get; set; } = 60;
}
