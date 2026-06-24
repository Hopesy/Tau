namespace Tau.Ai;

public sealed record DiagnosticErrorInfo
{
    public string? Name { get; init; }
    public required string Message { get; init; }
    public string? Stack { get; init; }
    public object? Code { get; init; }
}

public sealed record AssistantMessageDiagnostic
{
    public required string Type { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public DiagnosticErrorInfo? Error { get; init; }
    public IReadOnlyDictionary<string, object?>? Details { get; init; }
}

public static class AssistantMessageDiagnostics
{
    public static string FormatThrownValue(object? value)
    {
        if (value is Exception ex)
        {
            return string.IsNullOrEmpty(ex.Message) ? ex.GetType().Name : ex.Message;
        }

        if (value is string text)
        {
            return text;
        }

        return value?.ToString() ?? "null";
    }

    public static DiagnosticErrorInfo ExtractDiagnosticError(object? error)
    {
        if (error is not Exception ex)
        {
            return new DiagnosticErrorInfo
            {
                Name = "ThrownValue",
                Message = FormatThrownValue(error)
            };
        }

        var name = ex.GetType().Name;
        return new DiagnosticErrorInfo
        {
            Name = string.IsNullOrWhiteSpace(name) ? null : name,
            Message = string.IsNullOrEmpty(ex.Message) ? name : ex.Message,
            Stack = ex.StackTrace,
            Code = ExtractExceptionCode(ex)
        };
    }

    public static AssistantMessageDiagnostic CreateAssistantMessageDiagnostic(
        string type,
        object? error,
        IReadOnlyDictionary<string, object?>? details = null,
        DateTimeOffset? timestamp = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);

        return new AssistantMessageDiagnostic
        {
            Type = type,
            Timestamp = timestamp ?? DateTimeOffset.UtcNow,
            Error = ExtractDiagnosticError(error),
            Details = details
        };
    }

    public static AssistantMessage AppendAssistantMessageDiagnostic(
        AssistantMessage message,
        AssistantMessageDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(diagnostic);

        var diagnostics = message.Diagnostics?.ToList() ?? [];
        diagnostics.Add(diagnostic);
        return message with
        {
            Diagnostics = diagnostics
        };
    }

    private static object? ExtractExceptionCode(Exception ex)
    {
        if (ex.Data["code"] is string or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
        {
            return ex.Data["code"];
        }

        return null;
    }
}
