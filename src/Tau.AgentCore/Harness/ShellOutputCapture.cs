using System.Text;

namespace Tau.AgentCore.Harness;

public sealed record ShellOutputCaptureOptions(
    int MaxLines = ToolOutputTruncator.DefaultMaxLines,
    int MaxBytes = ToolOutputTruncator.DefaultMaxBytes,
    int RetainedOutputChars = ToolOutputTruncator.DefaultMaxBytes * 2,
    string TempFilePrefix = "bash-",
    string TempFileSuffix = ".log",
    string? FullOutputPath = null);

public sealed record ShellOutputCaptureResult(
    string Output,
    int? ExitCode,
    bool Cancelled,
    bool Truncated,
    string? FullOutputPath = null);

public sealed class ShellOutputCapture
{
    private readonly object _gate = new();
    private readonly ShellOutputCaptureOptions _options;
    private readonly List<string> _outputChunks = [];
    private int _retainedOutputChars;
    private int _totalBytes;
    private string? _fullOutputPath;

    public ShellOutputCapture(ShellOutputCaptureOptions? options = null)
    {
        _options = options ?? new ShellOutputCaptureOptions();
        if (_options.MaxLines <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxLines must be greater than zero.");
        if (_options.MaxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxBytes must be greater than zero.");
        if (_options.RetainedOutputChars <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "RetainedOutputChars must be greater than zero.");
    }

    public string? FullOutputPath
    {
        get
        {
            lock (_gate)
            {
                return _fullOutputPath;
            }
        }
    }

    public string AppendChunk(string chunk)
    {
        if (chunk.Length == 0)
            return string.Empty;

        var rawBytes = Encoding.UTF8.GetByteCount(chunk);
        var text = SanitizeBinaryOutput(chunk).Replace("\r", string.Empty, StringComparison.Ordinal);

        lock (_gate)
        {
            _totalBytes += rawBytes;
            if (_totalBytes > _options.MaxBytes && _fullOutputPath is null)
                EnsureFullOutputFile(GetBufferedOutput() + text);
            else
                AppendFullOutput(text);

            if (text.Length > 0)
            {
                _outputChunks.Add(text);
                _retainedOutputChars += text.Length;
                while (_retainedOutputChars > _options.RetainedOutputChars && _outputChunks.Count > 1)
                {
                    var removed = _outputChunks[0];
                    _outputChunks.RemoveAt(0);
                    _retainedOutputChars -= removed.Length;
                }
            }
        }

        return text;
    }

    public ShellOutputCaptureResult Complete(int? exitCode, bool cancelled)
    {
        lock (_gate)
        {
            var tailOutput = GetBufferedOutput();
            var truncation = ToolOutputTruncator.TruncateTail(
                tailOutput,
                _options.MaxLines,
                _options.MaxBytes);
            if (truncation.Truncated && _fullOutputPath is null)
                EnsureFullOutputFile(tailOutput);

            return new ShellOutputCaptureResult(
                truncation.Truncated ? truncation.Content : tailOutput,
                cancelled ? null : exitCode,
                cancelled,
                truncation.Truncated,
                _fullOutputPath);
        }
    }

    public static string SanitizeBinaryOutput(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            var code = (int)character;
            if (code is 0x09 or 0x0a or 0x0d)
            {
                builder.Append(character);
                continue;
            }

            if (code <= 0x1f)
                continue;
            if (code >= 0xfff9 && code <= 0xfffb)
                continue;

            builder.Append(character);
        }

        return builder.ToString();
    }

    private string GetBufferedOutput() => string.Concat(_outputChunks);

    private void EnsureFullOutputFile(string initialContent)
    {
        if (_fullOutputPath is not null)
            return;

        var path = _options.FullOutputPath ?? Path.Combine(
            Path.GetTempPath(),
            $"{_options.TempFilePrefix}{Guid.NewGuid():N}{_options.TempFileSuffix}");
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, initialContent, Encoding.UTF8);
        _fullOutputPath = path;
    }

    private void AppendFullOutput(string text)
    {
        if (_fullOutputPath is null || text.Length == 0)
            return;

        File.AppendAllText(_fullOutputPath, text, Encoding.UTF8);
    }
}
