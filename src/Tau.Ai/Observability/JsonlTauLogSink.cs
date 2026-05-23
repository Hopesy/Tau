using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Tau.Ai.Observability;

public sealed class JsonlTauLogSink : ITauLogSink, IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter _writer;
    private readonly TauSecretRedactor _redactor;
    private bool _disposed;

    public JsonlTauLogSink(string path, TauSecretRedactor? redactor = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream) { AutoFlush = true };
        _redactor = redactor ?? TauSecretRedactor.ForEnvironmentVariable(TauSecretRedactor.TauLogEnvironmentVariable);
        Path = path;
    }

    public string Path { get; }

    public void Log(TauLogEvent evt)
    {
        if (_disposed)
        {
            return;
        }

        var line = SerializeEvent(evt, _redactor);

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _writer.WriteLine(line);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _writer.Dispose();
        }
    }

    public static JsonlTauLogSink? FromEnvironment()
    {
        var explicitPath = Environment.GetEnvironmentVariable("TAU_LOG_FILE");
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return new JsonlTauLogSink(explicitPath);
        }

        var disabled = Environment.GetEnvironmentVariable("TAU_LOG_DISABLED");
        if (string.Equals(disabled, "1", StringComparison.Ordinal))
        {
            return null;
        }

        var defaultPath = System.IO.Path.Combine(".tau", "log.jsonl");
        return new JsonlTauLogSink(defaultPath);
    }

    internal static string SerializeEvent(TauLogEvent evt, TauSecretRedactor? redactor = null)
    {
        redactor ??= TauSecretRedactor.ForEnvironmentVariable(TauSecretRedactor.TauLogEnvironmentVariable);
        var builder = new StringBuilder();
        builder.Append('{');
        builder.Append("\"ts\":\"").Append(EscapeString(evt.Timestamp.ToString("O"))).Append("\",");
        builder.Append("\"category\":\"").Append(EscapeString(evt.Category)).Append("\",");
        builder.Append("\"event\":\"").Append(EscapeString(evt.Event)).Append("\",");
        builder.Append("\"fields\":{");

        var first = true;
        foreach (var pair in evt.Fields)
        {
            if (!first) builder.Append(',');
            first = false;
            builder.Append('"').Append(EscapeString(pair.Key)).Append("\":");
            if (pair.Value is null)
            {
                builder.Append("null");
            }
            else
            {
                builder.Append('"').Append(EscapeString(pair.Value)).Append('"');
            }
        }

        builder.Append("}}");
        return JsonlSecretRedactor.RedactLine(builder.ToString(), redactor);
    }

    private static string EscapeString(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\': builder.Append("\\\\"); break;
                case '"': builder.Append("\\\""); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (ch < 0x20)
                    {
                        builder.Append("\\u").Append(((int)ch).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(ch);
                    }
                    break;
            }
        }

        return builder.ToString();
    }
}
