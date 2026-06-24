using Tau.AgentCore.Harness;

namespace Tau.AgentCore.Tests;

public sealed class ShellOutputCaptureTests
{
    [Fact]
    public void SanitizeBinaryOutput_RemovesBinaryControlsAndKeepsTabsAndLineBreaks()
    {
        var sanitized = ShellOutputCapture.SanitizeBinaryOutput("a\0b\tc\n\rd\u001fe\ufff9f\ufffbg");

        Assert.Equal("ab\tc\n\rdefg", sanitized);
    }

    [Fact]
    public void AppendChunk_RemovesCarriageReturnsAndReturnsSanitizedText()
    {
        var capture = new ShellOutputCapture(new ShellOutputCaptureOptions(MaxBytes: 100, RetainedOutputChars: 100));

        var chunk = capture.AppendChunk("a\rb\0c");
        var result = capture.Complete(exitCode: 0, cancelled: false);

        Assert.Equal("abc", chunk);
        Assert.Equal("abc", result.Output);
        Assert.Equal(0, result.ExitCode);
        Assert.False(result.Cancelled);
        Assert.False(result.Truncated);
        Assert.Null(result.FullOutputPath);
    }

    [Fact]
    public void Complete_TruncatesTailByBytesAndWritesFullOutput()
    {
        using var temp = TempDirectory.Create();
        var fullOutputPath = Path.Combine(temp.Path, "full.log");
        var capture = new ShellOutputCapture(new ShellOutputCaptureOptions(
            MaxLines: 10,
            MaxBytes: 5,
            RetainedOutputChars: 100,
            FullOutputPath: fullOutputPath));

        capture.AppendChunk("0123456789");

        var result = capture.Complete(exitCode: 7, cancelled: false);

        Assert.Equal("56789", result.Output);
        Assert.Equal(7, result.ExitCode);
        Assert.True(result.Truncated);
        Assert.Equal(fullOutputPath, result.FullOutputPath);
        Assert.Equal("0123456789", File.ReadAllText(fullOutputPath));
    }

    [Fact]
    public void Complete_TruncatesTailByLinesAndWritesFullOutput()
    {
        using var temp = TempDirectory.Create();
        var fullOutputPath = Path.Combine(temp.Path, "full.log");
        var capture = new ShellOutputCapture(new ShellOutputCaptureOptions(
            MaxLines: 2,
            MaxBytes: 1000,
            RetainedOutputChars: 100,
            FullOutputPath: fullOutputPath));

        capture.AppendChunk("one\ntwo\nthree\n");

        var result = capture.Complete(exitCode: 0, cancelled: false);

        Assert.Equal("two\nthree", result.Output);
        Assert.True(result.Truncated);
        Assert.Equal(fullOutputPath, result.FullOutputPath);
        Assert.Equal("one\ntwo\nthree\n", File.ReadAllText(fullOutputPath));
    }

    [Fact]
    public void Complete_WhenCancelledSuppressesExitCode()
    {
        var capture = new ShellOutputCapture(new ShellOutputCaptureOptions(MaxBytes: 100, RetainedOutputChars: 100));

        capture.AppendChunk("partial");

        var result = capture.Complete(exitCode: 1, cancelled: true);

        Assert.Equal("partial", result.Output);
        Assert.Null(result.ExitCode);
        Assert.True(result.Cancelled);
        Assert.False(result.Truncated);
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path) => Path = path;

        public string Path { get; }

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "tau-agentcore-shell-output-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
