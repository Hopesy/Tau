using System.Collections.Concurrent;
using Tau.WebUi.Contracts;

namespace Tau.WebUi.Services;

public sealed class WebArtifactService
{
    private static readonly char[] PortableInvalidFileNameChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, WebArtifactDto>> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly WebArtifactStore _store;

    public WebArtifactService(WebArtifactStore store)
    {
        _store = store;
        foreach (var session in _store.Load())
        {
            var artifacts = new ConcurrentDictionary<string, WebArtifactDto>(StringComparer.OrdinalIgnoreCase);
            foreach (var artifact in session.Artifacts)
            {
                artifacts[artifact.FileName] = artifact;
            }

            _sessions[session.SessionId] = artifacts;
        }
    }

    public IReadOnlyList<WebArtifactSummaryDto> ListArtifacts(string sessionId) =>
        GetSessionArtifacts(sessionId)
            .Values
            .OrderBy(artifact => artifact.FileName, StringComparer.OrdinalIgnoreCase)
            .Select(static artifact => new WebArtifactSummaryDto(
                artifact.FileName,
                artifact.MimeType,
                artifact.Size,
                artifact.CreatedAt,
                artifact.UpdatedAt))
            .ToArray();

    public WebArtifactDto? GetArtifact(string sessionId, string fileName)
    {
        var normalized = NormalizeFileName(fileName);
        return GetSessionArtifacts(sessionId).TryGetValue(normalized, out var artifact)
            ? artifact
            : null;
    }

    public WebArtifactDto UpsertArtifact(string sessionId, string fileName, UpsertWebArtifactRequest request)
    {
        if (request.Content is null)
        {
            throw new ArgumentException("Artifact content is required.", nameof(request));
        }

        var normalized = NormalizeFileName(fileName);
        var artifacts = GetSessionArtifacts(sessionId);
        var now = DateTimeOffset.UtcNow;
        var content = request.Content;
        var mimeType = NormalizeMimeType(request.MimeType, normalized);
        var size = EstimateUtf8Size(content);
        var artifact = artifacts.AddOrUpdate(
            normalized,
            _ => new WebArtifactDto(normalized, content, mimeType, size, now, now),
            (_, existing) => existing with
            {
                Content = content,
                MimeType = mimeType,
                Size = size,
                UpdatedAt = now
            });

        Persist();
        return artifact;
    }

    public bool DeleteArtifact(string sessionId, string fileName)
    {
        var normalized = NormalizeFileName(fileName);
        var removed = GetSessionArtifacts(sessionId).TryRemove(normalized, out _);
        if (removed)
        {
            Persist();
        }

        return removed;
    }

    public void DeleteSession(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out _))
        {
            Persist();
        }
    }

    public IDictionary<string, object?> HandleRuntimeMessage(string sessionId, WebRuntimeMessageRequest message)
    {
        var type = message.Type ?? string.Empty;
        return type switch
        {
            "artifact-operation" => HandleArtifactOperation(sessionId, message),
            "console" => CreateRuntimeResponse(message, success: true),
            "file-returned" => CreateRuntimeResponse(message, success: true, result: new
            {
                fileName = RuntimeFileName(message),
                mimeType = message.MimeType
            }),
            "execution-complete" => CreateRuntimeResponse(message, success: true),
            "execution-error" => CreateRuntimeResponse(message, success: false, error: message.Text ?? "Sandbox execution failed."),
            _ => CreateRuntimeResponse(message, success: false, error: $"Unknown runtime message type: {type}")
        };
    }

    private IDictionary<string, object?> HandleArtifactOperation(string sessionId, WebRuntimeMessageRequest message)
    {
        try
        {
            return (message.Action ?? string.Empty) switch
            {
                "list" => CreateRuntimeResponse(
                    message,
                    success: true,
                    result: ListArtifacts(sessionId).Select(static artifact => artifact.FileName).ToArray()),
                "get" => HandleRuntimeGet(sessionId, message),
                "createOrUpdate" => HandleRuntimeCreateOrUpdate(sessionId, message),
                "delete" => HandleRuntimeDelete(sessionId, message),
                _ => CreateRuntimeResponse(message, success: false, error: $"Unknown artifact action: {message.Action}")
            };
        }
        catch (Exception ex)
        {
            return CreateRuntimeResponse(message, success: false, error: ex.Message);
        }
    }

    private IDictionary<string, object?> HandleRuntimeGet(string sessionId, WebRuntimeMessageRequest message)
    {
        var fileName = RuntimeFileName(message);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return CreateRuntimeResponse(message, success: false, error: "Artifact filename is required.");
        }

        var artifact = GetArtifact(sessionId, fileName);
        return artifact is null
            ? CreateRuntimeResponse(message, success: false, error: $"Artifact not found: {fileName}")
            : CreateRuntimeResponse(message, success: true, result: artifact.Content);
    }

    private IDictionary<string, object?> HandleRuntimeCreateOrUpdate(string sessionId, WebRuntimeMessageRequest message)
    {
        var fileName = RuntimeFileName(message);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return CreateRuntimeResponse(message, success: false, error: "Artifact filename is required.");
        }

        var artifact = UpsertArtifact(
            sessionId,
            fileName,
            new UpsertWebArtifactRequest(message.Content ?? string.Empty, message.MimeType));
        return CreateRuntimeResponse(message, success: true, result: artifact.FileName);
    }

    private IDictionary<string, object?> HandleRuntimeDelete(string sessionId, WebRuntimeMessageRequest message)
    {
        var fileName = RuntimeFileName(message);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return CreateRuntimeResponse(message, success: false, error: "Artifact filename is required.");
        }

        return DeleteArtifact(sessionId, fileName)
            ? CreateRuntimeResponse(message, success: true)
            : CreateRuntimeResponse(message, success: false, error: $"Artifact not found: {fileName}");
    }

    private ConcurrentDictionary<string, WebArtifactDto> GetSessionArtifacts(string sessionId) =>
        _sessions.GetOrAdd(sessionId, _ => new ConcurrentDictionary<string, WebArtifactDto>(StringComparer.OrdinalIgnoreCase));

    private void Persist()
    {
        var sessions = _sessions
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new WebArtifactSessionDocument(
                pair.Key,
                pair.Value.Values
                    .OrderBy(artifact => artifact.FileName, StringComparer.OrdinalIgnoreCase)
                    .ToArray()))
            .Where(session => session.Artifacts.Count > 0)
            .ToArray();
        _store.Save(sessions);
    }

    private static IDictionary<string, object?> CreateRuntimeResponse(
        WebRuntimeMessageRequest message,
        bool success,
        object? result = null,
        string? error = null) =>
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "runtime-response",
            ["messageId"] = message.MessageId,
            ["sandboxId"] = message.SandboxId,
            ["success"] = success,
            ["result"] = result,
            ["error"] = error
        };

    private static string? RuntimeFileName(WebRuntimeMessageRequest message) =>
        message.Filename;

    private static string NormalizeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("Artifact filename is required.", nameof(fileName));
        }

        var trimmed = fileName.Trim();
        if (trimmed.Length > 180)
        {
            throw new ArgumentException("Artifact filename is too long.", nameof(fileName));
        }

        if (trimmed.Any(static ch => char.IsControl(ch)) ||
            trimmed.IndexOfAny(PortableInvalidFileNameChars) >= 0 ||
            trimmed is "." or "..")
        {
            throw new ArgumentException($"Artifact filename is not portable: {trimmed}", nameof(fileName));
        }

        return trimmed;
    }

    private static string NormalizeMimeType(string? mimeType, string fileName)
    {
        if (!string.IsNullOrWhiteSpace(mimeType))
        {
            return mimeType.Trim();
        }

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension switch
        {
            ".html" or ".htm" => "text/html",
            ".svg" => "image/svg+xml",
            ".md" or ".markdown" => "text/markdown",
            ".json" => "application/json",
            ".js" => "text/javascript",
            ".css" => "text/css",
            ".csv" => "text/csv",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".txt" => "text/plain",
            _ => "text/plain"
        };
    }

    private static long EstimateUtf8Size(string content) =>
        System.Text.Encoding.UTF8.GetByteCount(content);
}
