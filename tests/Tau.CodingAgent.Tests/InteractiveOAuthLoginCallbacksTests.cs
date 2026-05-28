using Tau.CodingAgent.Runtime;
using Tau.Tui.Abstractions;
using Tau.Tui.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed class InteractiveOAuthLoginCallbacksTests
{
    [Fact]
    public async Task OnPromptAsync_UsesOAuthPromptAndReturnsInput()
    {
        var terminal = new FakeTerminal();
        terminal.QueueInput("redirect-url");
        var session = new InteractiveConsoleSession(terminal);
        var callbacks = new InteractiveOAuthLoginCallbacks(session);

        var input = await callbacks.OnPromptAsync("Paste the authorization code:");

        Assert.Equal("redirect-url", input);
        Assert.Contains(terminal.Writes, write => write.Text == "oauth> " && write.Color == ConsoleColor.Cyan && !write.IsLine);
        Assert.Contains("status> Paste the authorization code:", terminal.FlattenedText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnManualCodeInputAsync_CancelReturnsEmptyString()
    {
        var terminal = new BlockingPromptTerminal();
        var session = new InteractiveConsoleSession(terminal);
        var callbacks = new InteractiveOAuthLoginCallbacks(session);

        var task = callbacks.OnManualCodeInputAsync();
        Assert.NotNull(task);

        callbacks.CancelManualCodeInput();
        var result = await task!;

        Assert.Equal(string.Empty, result);
        Assert.Equal("oauth> ", terminal.LastPrompt);
        Assert.Equal(ConsoleColor.Cyan, terminal.LastPromptColor);
    }

    [Fact]
    public async Task OnManualCodeInputAsync_UserCancelThrowsOperationCanceled()
    {
        var terminal = new FakeTerminal();
        var keyReader = new ScriptedKeyReader();
        keyReader.EnqueueRaw(new ConsoleKeyInfo('\x03', ConsoleKey.C, shift: false, alt: false, control: true));
        var session = new InteractiveConsoleSession(terminal, new InteractiveInputEditor(keyReader, new CapturingRenderer()));
        var callbacks = new InteractiveOAuthLoginCallbacks(session);

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await callbacks.OnManualCodeInputAsync()!);
    }

    private sealed class BlockingPromptTerminal : ITerminal
    {
        public string? LastPrompt { get; private set; }
        public ConsoleColor? LastPromptColor { get; private set; }

        public Task<string?> PromptAsync(string prompt, ConsoleColor? color = null, CancellationToken cancellationToken = default)
        {
            LastPrompt = prompt;
            LastPromptColor = color;

            var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            return tcs.Task;
        }

        public void Write(string text, ConsoleColor? color = null)
        {
        }

        public void WriteLine(string? text = null, ConsoleColor? color = null)
        {
        }
    }

    private sealed class ScriptedKeyReader : IConsoleKeyReader
    {
        private readonly Queue<ConsoleKeyInfo> _keys = new();

        public void EnqueueRaw(ConsoleKeyInfo key) => _keys.Enqueue(key);

        public ValueTask<ConsoleKeyInfo> ReadKeyAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_keys.Dequeue());
    }

    private sealed class CapturingRenderer : IInteractiveRenderer
    {
        public int WindowWidth => 80;
        public void WritePrompt(string prompt, ConsoleColor? color = null)
        {
        }

        public void Render(string buffer, int cursorIndex)
        {
        }

        public void RenderSearch(string pattern, string? match, int cursorInMatch)
        {
        }

        public void Commit()
        {
        }

        public void Cancel()
        {
        }
    }
}
