using System.Text.Json.Serialization;

namespace Tau.Ai;

public record ImagesModel : Model
{
    public IReadOnlyList<string> OutputModalities { get; init; } = ["image"];
}

public record struct ImagesContext(IReadOnlyList<ContentBlock> Input);

public record ImagesOptions
{
    public string? ApiKey { get; init; }
    [JsonIgnore]
    public CancellationToken Signal { get; init; }
    [JsonIgnore]
    public Func<ProviderResponse, ImagesModel, ValueTask>? OnResponse { get; init; }
    [JsonIgnore]
    public Func<object, ImagesModel, ValueTask<object?>>? OnPayload { get; init; }
    public IDictionary<string, string>? Headers { get; init; }
    public TimeSpan? Timeout { get; init; }
    public TimeSpan? MaxRetryDelay { get; init; }
    public int? MaxRetries { get; init; }
    public IDictionary<string, object>? Metadata { get; init; }
    public IReadOnlyDictionary<string, string>? Env { get; init; }
}

public sealed record AssistantImages
{
    public required string Api { get; init; }
    public required string Provider { get; init; }
    public required string Model { get; init; }
    public IReadOnlyList<ContentBlock> Output { get; init; } = [];
    public string? ResponseId { get; init; }
    public Usage? Usage { get; init; }
    public ImagesStopReason StopReason { get; init; } = ImagesStopReason.Stop;
    public string? ErrorMessage { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

public enum ImagesStopReason
{
    Stop,
    Error,
    Aborted
}
