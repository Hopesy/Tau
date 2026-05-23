using Tau.CodingAgent.Runtime;

namespace Tau.Mom;

public sealed class ChannelSessionStore
{
    public const string ContextFileName = "context.json";

    private readonly string _path;
    private readonly ILogger? _logger;

    public ChannelSessionStore(string workingDirectory, ILogger? logger = null)
    {
        _path = System.IO.Path.Combine(System.IO.Path.GetFullPath(workingDirectory), ContextFileName);
        _logger = logger;
    }

    public string Path => _path;

    public ChannelSessionMetadata LoadMetadata()
    {
        var snapshot = new CodingAgentSessionStore(_path).Load();
        return new ChannelSessionMetadata(
            NormalizeOptional(snapshot.Provider),
            NormalizeOptional(snapshot.Model),
            NormalizeOptional(snapshot.Name),
            snapshot.Messages.Count);
    }

    public CodingAgentSessionSnapshot Load(string provider, string model, string? sessionName)
    {
        var store = new CodingAgentSessionStore(_path);
        var snapshot = store.Load();
        var name = string.IsNullOrWhiteSpace(sessionName) ? snapshot.Name : sessionName.Trim();
        return new CodingAgentSessionSnapshot(snapshot.Messages, provider, model, name);
    }

    public void Save(ICodingAgentRunner runner)
    {
        try
        {
            var store = new CodingAgentSessionStore(_path);
            store.Save(runner.Messages, runner.Model, runner.SessionName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "Failed to save mom channel context {Path}.", _path);
        }
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

public readonly record struct ChannelSessionMetadata(
    string? Provider,
    string? Model,
    string? SessionName,
    int MessageCount)
{
    public bool HasModelSelection =>
        !string.IsNullOrWhiteSpace(Provider) ||
        !string.IsNullOrWhiteSpace(Model);
}
