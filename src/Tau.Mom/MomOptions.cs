namespace Tau.Mom;

public sealed class MomOptions
{
    public string InboxPath { get; set; } = "mom/inbox";
    public string OutboxPath { get; set; } = "mom/outbox";
    public string ArchivePath { get; set; } = "mom/archive";
    public string EventsPath { get; set; } = "mom/events";
    public string DefaultWorkingDirectory { get; set; } = ".";
    public string DefaultProvider { get; set; } = "openai";
    public string? DefaultModel { get; set; }
    public int PollIntervalSeconds { get; set; } = 5;
    public int MaxFilesPerPoll { get; set; } = 8;
    public int RunningStatusStaleAfterMinutes { get; set; } = 60;
}
