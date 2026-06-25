using System.Text;
using Tau.CodingAgent.Runtime;
using Tau.Tui.Rendering;

namespace Tau.CodingAgent.Tests;

public sealed class SystemCodingAgentClipboardTests
{
    [Theory]
    [InlineData("image/png", "png")]
    [InlineData("image/jpeg; charset=binary", "jpg")]
    [InlineData("image/webp", "webp")]
    [InlineData("image/gif", "gif")]
    [InlineData("image/bmp", null)]
    public void ExtensionForImageMimeType_ReturnsExpectedExtension(string mimeType, string? expected) =>
        Assert.Equal(expected, SystemCodingAgentClipboard.ExtensionForImageMimeType(mimeType));

    [Fact]
    public async Task ReadImageAsync_OnWaylandReadsPreferredWlPasteImageType()
    {
        var pngBytes = ImageTestData.CreatePng(3, 5);
        var runner = new FakeClipboardCommandRunner();
        runner.Enqueue("wl-paste", ["--list-types"], Encoding.UTF8.GetBytes("text/plain\nimage/webp\nimage/png\n"));
        runner.Enqueue("wl-paste", ["--type", "image/png", "--no-newline"], pngBytes);
        var clipboard = CreateClipboard(
            runner,
            new Dictionary<string, string?> { ["WAYLAND_DISPLAY"] = "wayland-0" },
            CodingAgentClipboardPlatform.Linux);

        var image = await clipboard.ReadImageAsync();

        Assert.NotNull(image);
        Assert.Equal("image/png", image!.MimeType);
        Assert.Equal(pngBytes, image.Bytes);
    }

    [Fact]
    public async Task ReadImageAsync_OnXclipFallsBackThroughSupportedTypes()
    {
        var jpegBytes = ImageTestData.CreateJpeg(11, 7);
        var runner = new FakeClipboardCommandRunner();
        runner.Enqueue("xclip", ["-selection", "clipboard", "-t", "TARGETS", "-o"], Encoding.UTF8.GetBytes("text/plain\n"));
        runner.EnqueueFailure("xclip", ["-selection", "clipboard", "-t", "image/png", "-o"]);
        runner.Enqueue("xclip", ["-selection", "clipboard", "-t", "image/jpeg", "-o"], jpegBytes);
        var clipboard = CreateClipboard(runner, new Dictionary<string, string?>(), CodingAgentClipboardPlatform.Linux);

        var image = await clipboard.ReadImageAsync();

        Assert.NotNull(image);
        Assert.Equal("image/jpeg", image!.MimeType);
        Assert.Equal(jpegBytes, image.Bytes);
    }

    [Fact]
    public async Task ReadImageAsync_ConvertsUnsupportedImageMimeTypeToPng()
    {
        var jpegBytes = ImageTestData.CreateJpeg(13, 17);
        var runner = new FakeClipboardCommandRunner();
        runner.Enqueue("wl-paste", ["--list-types"], Encoding.UTF8.GetBytes("image/bmp\n"));
        runner.Enqueue("wl-paste", ["--type", "image/bmp", "--no-newline"], jpegBytes);
        var clipboard = CreateClipboard(
            runner,
            new Dictionary<string, string?> { ["WAYLAND_DISPLAY"] = "wayland-0" },
            CodingAgentClipboardPlatform.Linux);

        var image = await clipboard.ReadImageAsync();

        Assert.NotNull(image);
        Assert.Equal("image/png", image!.MimeType);
        Assert.Equal(new TuiImageDimensions(13, 17), TuiTerminalImage.GetPngDimensions(Convert.ToBase64String(image.Bytes)));
    }

    [Fact]
    public async Task ReadImageAsync_OnWindowsUsesPowerShellTempPng()
    {
        var tmpFile = Path.Combine(Path.GetTempPath(), $"tau-clipboard-test-{Guid.NewGuid():N}.png");
        var pngBytes = ImageTestData.CreatePng(7, 9);
        var runner = new FakeClipboardCommandRunner
        {
            OnRun = command =>
            {
                if (command.FileName == "powershell.exe")
                {
                    File.WriteAllBytes(tmpFile, pngBytes);
                }
            }
        };
        runner.Enqueue("powershell.exe", ["-NoProfile", "-STA", "-Command", "*"], Encoding.UTF8.GetBytes("ok\n"), wildcardArguments: true);
        var clipboard = CreateClipboard(runner, new Dictionary<string, string?>(), CodingAgentClipboardPlatform.Windows, () => tmpFile);

        var image = await clipboard.ReadImageAsync();

        Assert.NotNull(image);
        Assert.Equal("image/png", image!.MimeType);
        Assert.Equal(pngBytes, image.Bytes);
        Assert.False(File.Exists(tmpFile));
    }

    [Fact]
    public async Task ReadImageAsync_WhenTermuxReturnsNullWithoutCommands()
    {
        var runner = new FakeClipboardCommandRunner();
        var clipboard = CreateClipboard(
            runner,
            new Dictionary<string, string?> { ["TERMUX_VERSION"] = "1" },
            CodingAgentClipboardPlatform.Linux);

        var image = await clipboard.ReadImageAsync();

        Assert.Null(image);
        Assert.Empty(runner.Commands);
    }

    private static SystemCodingAgentClipboard CreateClipboard(
        FakeClipboardCommandRunner runner,
        IReadOnlyDictionary<string, string?> env,
        CodingAgentClipboardPlatform platform,
        Func<string>? tempPngPathFactory = null) =>
        new(runner, env, platform, tempPngPathFactory);

    private sealed class FakeClipboardCommandRunner : ICodingAgentClipboardCommandRunner
    {
        private readonly Queue<FakeCommandResponse> _responses = [];

        public List<FakeCommand> Commands { get; } = [];
        public Action<FakeCommand>? OnRun { get; init; }

        public void Enqueue(string fileName, IReadOnlyList<string> arguments, byte[] stdout, bool wildcardArguments = false) =>
            _responses.Enqueue(new FakeCommandResponse(fileName, arguments, wildcardArguments, true, stdout));

        public void EnqueueFailure(string fileName, IReadOnlyList<string> arguments, bool wildcardArguments = false) =>
            _responses.Enqueue(new FakeCommandResponse(fileName, arguments, wildcardArguments, false, []));

        public Task<CodingAgentClipboardCommandResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            byte[]? stdin,
            int timeoutMs,
            int maxBufferBytes,
            CancellationToken cancellationToken)
        {
            var command = new FakeCommand(fileName, arguments.ToArray());
            Commands.Add(command);
            OnRun?.Invoke(command);
            var response = Assert.Single(_responses.Take(1));
            _responses.Dequeue();
            Assert.Equal(response.FileName, fileName);
            if (!response.WildcardArguments)
            {
                Assert.Equal(response.Arguments, arguments);
            }

            return Task.FromResult(new CodingAgentClipboardCommandResult(response.Ok, response.Stdout, string.Empty));
        }
    }

    private sealed record FakeCommand(string FileName, IReadOnlyList<string> Arguments);

    private sealed record FakeCommandResponse(
        string FileName,
        IReadOnlyList<string> Arguments,
        bool WildcardArguments,
        bool Ok,
        byte[] Stdout);
}
