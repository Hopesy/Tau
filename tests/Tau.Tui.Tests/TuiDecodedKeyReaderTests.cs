using Tau.Tui.Abstractions;
using Tau.Tui.Runtime;
using System.Text;

namespace Tau.Tui.Tests;

[Collection(TuiTestCollections.TuiKeyDecoderState)]
public sealed class TuiDecodedKeyReaderTests : IDisposable
{
    public void Dispose() => TuiKeyDecoder.SetKittyProtocolActive(false);

    [Fact]
    public void TryMapInput_MapsKittyCsiUToConsoleKeyInfo()
    {
        TuiKeyDecoder.SetKittyProtocolActive(true);

        Assert.True(TuiConsoleKeyInfoMapper.TryMapInput("\u001b[99;5u", out var ctrlC));
        Assert.Equal(ConsoleKey.C, ctrlC.Key);
        Assert.Equal(ConsoleModifiers.Control, ctrlC.Modifiers);
        Assert.Equal('\x03', ctrlC.KeyChar);

        Assert.True(TuiConsoleKeyInfoMapper.TryMapInput("\u001b[1;5D", out var ctrlLeft));
        Assert.Equal(ConsoleKey.LeftArrow, ctrlLeft.Key);
        Assert.Equal(ConsoleModifiers.Control, ctrlLeft.Modifiers);
    }

    [Fact]
    public void TryMapInput_MapsPrintableKittyAndModifyOtherKeysSequences()
    {
        TuiKeyDecoder.SetKittyProtocolActive(true);

        Assert.True(TuiConsoleKeyInfoMapper.TryMapInput("\u001b[97:65;2u", out var kittyShiftA));
        Assert.Equal('A', kittyShiftA.KeyChar);
        Assert.Equal(ConsoleKey.A, kittyShiftA.Key);

        Assert.True(TuiConsoleKeyInfoMapper.TryMapInput("\u001b[27;2;65~", out var modifyOtherKeysA));
        Assert.Equal('A', modifyOtherKeysA.KeyChar);
        Assert.Equal(ConsoleKey.A, modifyOtherKeysA.Key);
    }

    [Fact]
    public void TryMapInput_IgnoresKittyReleaseEvents()
    {
        TuiKeyDecoder.SetKittyProtocolActive(true);

        Assert.False(TuiConsoleKeyInfoMapper.TryMapInput("\u001b[97;1:3u", out _));
    }

    [Fact]
    public async Task DecodedKeyReader_FeedsRawSequencesIntoInteractiveInputEditor()
    {
        TuiKeyDecoder.SetKittyProtocolActive(true);
        var rawReader = new FakeRawInputReader();
        rawReader.Enqueue("\u001b[97:65;2u");
        rawReader.Enqueue("\u001b[98;1u");
        rawReader.Enqueue("\u001b[D");
        rawReader.Enqueue("\u001b[33;1u");
        rawReader.Enqueue("\r");

        var renderer = new FakeRenderer();
        var editor = new InteractiveInputEditor(new TuiDecodedKeyReader(rawReader), renderer);

        var result = await editor.ReadLineAsync("> ");

        Assert.Equal(InputResultKind.Submitted, result.Kind);
        Assert.Equal("A!b", result.Text);
    }

    [Fact]
    public async Task StreamRawInputReader_SplitsByteStreamIntoCompleteTerminalSequences()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("x\u001b[A\u001b[97:65;2u"));
        using var reader = new TuiStreamRawInputReader(stream, sequenceTimeout: TimeSpan.FromMilliseconds(1));

        Assert.Equal("x", await reader.ReadInputAsync());
        Assert.Equal("\u001b[A", await reader.ReadInputAsync());
        Assert.Equal("\u001b[97:65;2u", await reader.ReadInputAsync());

        await Assert.ThrowsAsync<EndOfStreamException>(async () => await reader.ReadInputAsync());
    }

    [Fact]
    public async Task SystemConsoleKeyReader_CreateRawUsesDecodedInputPath()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("\u001b[99;5u"));
        using var reader = SystemConsoleKeyReader.CreateRaw(stream, sequenceTimeout: TimeSpan.FromMilliseconds(1));

        var key = await reader.ReadKeyAsync();

        Assert.Equal(ConsoleKey.C, key.Key);
        Assert.Equal(ConsoleModifiers.Control, key.Modifiers);
        Assert.Equal('\x03', key.KeyChar);
    }

    [Fact]
    public void SystemConsoleKeyReader_CreateRawRestoresRawModeOnDispose()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("x"));
        var rawMode = new FakeRawModeController();

        using (SystemConsoleKeyReader.CreateRaw(
                   stream,
                   sequenceTimeout: TimeSpan.FromMilliseconds(1),
                   rawModeController: rawMode))
        {
            Assert.Equal(1, rawMode.EnterCalls);
            Assert.Equal(0, rawMode.Scope.DisposeCalls);
        }

        Assert.Equal(1, rawMode.Scope.DisposeCalls);
    }

    private sealed class FakeRawInputReader : ITuiRawInputReader
    {
        private readonly Queue<string> _inputs = new();

        public void Enqueue(string input) => _inputs.Enqueue(input);

        public ValueTask<string> ReadInputAsync(CancellationToken cancellationToken = default)
        {
            if (_inputs.Count == 0)
            {
                throw new InvalidOperationException("No more queued input.");
            }

            return ValueTask.FromResult(_inputs.Dequeue());
        }
    }

    private sealed class FakeRawModeController : ITuiConsoleRawModeController
    {
        public int EnterCalls { get; private set; }
        public FakeRawModeScope Scope { get; } = new();

        public IDisposable EnterRawMode()
        {
            EnterCalls++;
            return Scope;
        }
    }

    private sealed class FakeRawModeScope : IDisposable
    {
        public int DisposeCalls { get; private set; }

        public void Dispose() => DisposeCalls++;
    }

    private sealed class FakeRenderer : IInteractiveRenderer
    {
        public int WindowWidth => 80;
        public void WritePrompt(string prompt, ConsoleColor? color = null) { }
        public void Render(string buffer, int cursorIndex) { }
        public void RenderSearch(string pattern, string? match, int cursorInMatch) { }
        public void Commit() { }
        public void Cancel() { }
    }
}
