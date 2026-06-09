using System.Collections.Concurrent;
using Tau.WebUi.Contracts;

namespace Tau.WebUi.Services;

public sealed class WebArtifactService
{
    private static readonly char[] PortableInvalidFileNameChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ConcurrentQueue<string>>> _htmlArtifactLogs = new(StringComparer.OrdinalIgnoreCase);
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

        ClearHtmlArtifactLogs(sessionId, normalized);
        Persist();
        return artifact;
    }

    public bool DeleteArtifact(string sessionId, string fileName)
    {
        var normalized = NormalizeFileName(fileName);
        var removed = GetSessionArtifacts(sessionId).TryRemove(normalized, out _);
        if (removed)
        {
            ClearHtmlArtifactLogs(sessionId, normalized);
            Persist();
        }

        return removed;
    }

    public void DeleteSession(string sessionId)
    {
        var removedArtifacts = _sessions.TryRemove(sessionId, out _);
        _htmlArtifactLogs.TryRemove(sessionId, out _);
        if (removedArtifacts)
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
            "console" => HandleConsoleMessage(sessionId, message),
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
                "create" => HandleRuntimeCreate(sessionId, message),
                "update" => HandleRuntimeUpdate(sessionId, message),
                "rewrite" => HandleRuntimeRewrite(sessionId, message),
                "createOrUpdate" => HandleRuntimeCreateOrUpdate(sessionId, message),
                "delete" => HandleRuntimeDelete(sessionId, message),
                "logs" or "htmlArtifactLogs" => HandleRuntimeHtmlArtifactLogs(sessionId, message),
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

    private IDictionary<string, object?> HandleRuntimeCreate(string sessionId, WebRuntimeMessageRequest message)
    {
        var fileName = RuntimeFileName(message);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return CreateRuntimeResponse(message, success: false, error: "Artifact filename is required.");
        }

        if (string.IsNullOrEmpty(message.Content))
        {
            return CreateRuntimeResponse(message, success: false, error: "create command requires filename and content");
        }

        var normalized = NormalizeFileName(fileName);
        var artifacts = GetSessionArtifacts(sessionId);
        if (artifacts.ContainsKey(normalized))
        {
            return CreateRuntimeResponse(message, success: false, error: $"File {normalized} already exists");
        }

        var now = DateTimeOffset.UtcNow;
        var content = message.Content;
        var artifact = new WebArtifactDto(
            normalized,
            content,
            NormalizeMimeType(message.MimeType, normalized),
            EstimateUtf8Size(content),
            now,
            now);
        if (!artifacts.TryAdd(normalized, artifact))
        {
            return CreateRuntimeResponse(message, success: false, error: $"File {normalized} already exists");
        }

        ClearHtmlArtifactLogs(sessionId, normalized);
        Persist();
        return CreateRuntimeResponse(message, success: true, result: artifact.FileName);
    }

    private IDictionary<string, object?> HandleRuntimeUpdate(string sessionId, WebRuntimeMessageRequest message)
    {
        var existing = TryGetRuntimeArtifact(sessionId, message, out var fileName, out var notFound);
        if (existing is null)
        {
            return notFound;
        }

        if (string.IsNullOrEmpty(message.OldString) || message.NewString is null)
        {
            return CreateRuntimeResponse(message, success: false, error: "update command requires old_str and new_str");
        }

        var index = existing.Content.IndexOf(message.OldString, StringComparison.Ordinal);
        if (index < 0)
        {
            return CreateRuntimeResponse(
                message,
                success: false,
                error: $"String not found in file. Here is the full content:\n\n{existing.Content}");
        }

        var updatedContent =
            existing.Content[..index] +
            message.NewString +
            existing.Content[(index + message.OldString.Length)..];
        var artifact = ReplaceArtifact(sessionId, fileName!, existing, updatedContent, message.MimeType);
        return CreateRuntimeResponse(message, success: true, result: artifact.FileName);
    }

    private IDictionary<string, object?> HandleRuntimeRewrite(string sessionId, WebRuntimeMessageRequest message)
    {
        var existing = TryGetRuntimeArtifact(sessionId, message, out var fileName, out var notFound);
        if (existing is null)
        {
            return notFound;
        }

        if (string.IsNullOrEmpty(message.Content))
        {
            return CreateRuntimeResponse(message, success: false, error: "rewrite command requires content");
        }

        var artifact = ReplaceArtifact(sessionId, fileName!, existing, message.Content, message.MimeType);
        return CreateRuntimeResponse(message, success: true, result: artifact.FileName);
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

    private IDictionary<string, object?> HandleRuntimeHtmlArtifactLogs(string sessionId, WebRuntimeMessageRequest message)
    {
        var existing = TryGetRuntimeArtifact(sessionId, message, out var fileName, out var notFound);
        if (existing is null)
        {
            return notFound;
        }

        if (!IsHtmlArtifact(existing))
        {
            return CreateRuntimeResponse(
                message,
                success: false,
                error: $"File {fileName} is not an HTML file. Logs are only available for HTML files.");
        }

        var logs = GetHtmlArtifactLogs(sessionId, fileName!);
        return CreateRuntimeResponse(
            message,
            success: true,
            result: logs.Count == 0
                ? $"No logs for {fileName}"
                : string.Join(Environment.NewLine, logs));
    }

    private IDictionary<string, object?> HandleConsoleMessage(string sessionId, WebRuntimeMessageRequest message)
    {
        var fileName = ResolveArtifactFileNameFromSandboxId(sessionId, message.SandboxId);
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            var method = string.IsNullOrWhiteSpace(message.Method) ? "log" : message.Method.Trim();
            var text = message.Text ?? string.Empty;
            var line = $"[{method}] {text}";
            var sessionLogs = _htmlArtifactLogs.GetOrAdd(
                sessionId,
                _ => new ConcurrentDictionary<string, ConcurrentQueue<string>>(StringComparer.OrdinalIgnoreCase));
            var artifactLogs = sessionLogs.GetOrAdd(fileName, _ => new ConcurrentQueue<string>());
            artifactLogs.Enqueue(line);
            while (artifactLogs.Count > 200 && artifactLogs.TryDequeue(out _))
            {
                // Keep runtime logs bounded; upstream keeps these logs in the live artifact element.
            }
        }

        return CreateRuntimeResponse(message, success: true);
    }

    private WebArtifactDto? TryGetRuntimeArtifact(
        string sessionId,
        WebRuntimeMessageRequest message,
        out string? fileName,
        out IDictionary<string, object?> notFound)
    {
        fileName = RuntimeFileName(message);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            notFound = CreateRuntimeResponse(message, success: false, error: "Artifact filename is required.");
            return null;
        }

        var normalized = NormalizeFileName(fileName);
        var artifacts = GetSessionArtifacts(sessionId);
        if (artifacts.TryGetValue(normalized, out var artifact))
        {
            fileName = normalized;
            notFound = new Dictionary<string, object?>(StringComparer.Ordinal);
            return artifact;
        }

        var files = artifacts.Keys.OrderBy(static item => item, StringComparer.OrdinalIgnoreCase).ToArray();
        var error = files.Length == 0
            ? $"File {normalized} not found. No files have been created yet."
            : $"File {normalized} not found. Available files: {string.Join(", ", files)}";
        fileName = normalized;
        notFound = CreateRuntimeResponse(message, success: false, error: error);
        return null;
    }

    private WebArtifactDto ReplaceArtifact(
        string sessionId,
        string fileName,
        WebArtifactDto existing,
        string content,
        string? mimeType)
    {
        var artifacts = GetSessionArtifacts(sessionId);
        var now = DateTimeOffset.UtcNow;
        var artifact = existing with
        {
            Content = content,
            MimeType = string.IsNullOrWhiteSpace(mimeType) ? existing.MimeType : NormalizeMimeType(mimeType, fileName),
            Size = EstimateUtf8Size(content),
            UpdatedAt = now
        };
        artifacts[fileName] = artifact;
        ClearHtmlArtifactLogs(sessionId, fileName);
        Persist();
        return artifact;
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

    private static string? ResolveArtifactFileNameFromSandboxId(string sessionId, string? sandboxId)
    {
        var prefix = $"artifact-{sessionId}-";
        return !string.IsNullOrWhiteSpace(sandboxId) &&
               sandboxId.StartsWith(prefix, StringComparison.Ordinal)
            ? sandboxId[prefix.Length..]
            : null;
    }

    private IReadOnlyList<string> GetHtmlArtifactLogs(string sessionId, string fileName)
    {
        return _htmlArtifactLogs.TryGetValue(sessionId, out var sessionLogs) &&
               sessionLogs.TryGetValue(fileName, out var logs)
            ? logs.ToArray()
            : [];
    }

    private void ClearHtmlArtifactLogs(string sessionId, string fileName)
    {
        if (_htmlArtifactLogs.TryGetValue(sessionId, out var sessionLogs))
        {
            sessionLogs.TryRemove(fileName, out _);
        }
    }

    private static bool IsHtmlArtifact(WebArtifactDto artifact) =>
        artifact.MimeType.Contains("html", StringComparison.OrdinalIgnoreCase) ||
        artifact.FileName.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
        artifact.FileName.EndsWith(".htm", StringComparison.OrdinalIgnoreCase);

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
