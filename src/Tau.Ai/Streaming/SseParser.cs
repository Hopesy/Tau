using System.Runtime.CompilerServices;

namespace Tau.Ai.Streaming;

/// <summary>
/// Parses Server-Sent Events (SSE) from a stream.
/// Each event has optional fields: event, data, id, retry.
/// </summary>
public static class SseParser
{
    public static async IAsyncEnumerable<SseEvent> ParseAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var reader = new StreamReader(stream);
        string? eventType = null;
        var dataLines = new List<string>();

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);

            if (line is null)
            {
                if (dataLines.Count > 0)
                    yield return BuildEvent(eventType, dataLines);
                yield break;
            }

            if (line.Length == 0)
            {
                if (dataLines.Count > 0)
                {
                    yield return BuildEvent(eventType, dataLines);
                    eventType = null;
                    dataLines.Clear();
                }
                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var value = line.Length > 5 && line[5] == ' ' ? line[6..] : line[5..];
                dataLines.Add(value);
            }
            else if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventType = line.Length > 6 && line[6] == ' ' ? line[7..] : line[6..];
            }
            // id: and retry: are ignored for now
        }
    }

    private static SseEvent BuildEvent(string? eventType, List<string> dataLines)
    {
        var data = string.Join("\n", dataLines);
        return new SseEvent(eventType, data);
    }
}

public readonly record struct SseEvent(string? EventType, string Data);
