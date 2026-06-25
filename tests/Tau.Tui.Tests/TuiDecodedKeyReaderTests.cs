using Tau.Tui.Abstractions;
using Tau.Tui.Runtime;

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
