using System.Text;
using Tau.Tui.Runtime;

namespace Tau.Tui.Tests;

[Collection(TuiTestCollections.TuiKeyDecoderState)]
public sealed class TuiProcessTerminalTests
{
    [Fact]
    public void ProcessTerminal_StartEnablesRawInputBracketedPasteAndKittyQuery()
    {
        var transport = new FakeProcessTerminalTransport();
        var timer = new ManualTerminalTimer();
        var terminal = new TuiProcessTerminal(transport, timer);
        var resizeCount = 0;

        terminal.Start(_ => { }, () => resizeCount++);
        transport.EmitResize();

        Assert.True(transport.RawMode);
        Assert.Equal([true], transport.RawModeHistory);
        Assert.Equal(Encoding.UTF8, transport.InputEncoding);
        Assert.Equal(1, transport.ResumeCalls);
        Assert.Equal(1, transport.RefreshDimensionsCalls);
        Assert.Equal(0, transport.EnableVirtualTerminalInputCalls);
        Assert.Equal(1, resizeCount);
        Assert.Equal(
            [
                TuiProcessTerminal.EnableBracketedPaste,
                TuiProcessTerminal.QueryKittyKeyboardProtocol,
            ],
            transport.Writes);
    }

    [Fact]
    public void ProcessTerminal_StartOnWindowsEnablesVirtualTerminalInput()
    {
        var transport = new FakeProcessTerminalTransport { IsWindows = true };
        var terminal = new TuiProcessTerminal(transport, new ManualTerminalTimer());

        terminal.Start(_ => { }, () => { });

        Assert.Equal(1, transport.EnableVirtualTerminalInputCalls);
        Assert.Equal(0, transport.RefreshDimensionsCalls);
    }

    [Fact]
    public void ProcessTerminal_InputUsesSequenceBufferAndRewrapsPaste()
    {
        var transport = new FakeProcessTerminalTransport();
        var terminal = new TuiProcessTerminal(transport, new ManualTerminalTimer());
        var input = new List<string>();

        terminal.Start(input.Add, () => { });
        transport.EmitInput("ab\u001b[A");
        transport.EmitInput("\u001b[200~hello world\u001b[201~z");

        Assert.Equal(
            [
                "a",
                "b",
                "\u001b[A",
                "\u001b[200~hello world\u001b[201~",
                "z",
            ],
            input);
    }

    [Fact]
    public void ProcessTerminal_ConsumesKittyProtocolResponseAndSkipsModifyOtherKeysFallback()
    {
        var transport = new FakeProcessTerminalTransport();
        var timer = new ManualTerminalTimer();
        var terminal = new TuiProcessTerminal(transport, timer);
        var input = new List<string>();

        terminal.Start(input.Add, () => { });
        transport.EmitInput("\u001b[?1u");
        timer.FireAll();
        transport.EmitInput("x");

        Assert.True(terminal.KittyProtocolActive);
        Assert.True(TuiKeyDecoder.IsKittyProtocolActive());
        Assert.False(terminal.ModifyOtherKeysActive);
        Assert.Equal(["x"], input);
        Assert.Contains(TuiProcessTerminal.EnableKittyKeyboardProtocol, transport.Writes);
        Assert.DoesNotContain(TuiProcessTerminal.EnableModifyOtherKeys, transport.Writes);

        terminal.Stop();
        Assert.False(TuiKeyDecoder.IsKittyProtocolActive());
    }

    [Fact]
    public void ProcessTerminal_ModifyOtherKeysFallbackIsDisabledOnStop()
    {
        var transport = new FakeProcessTerminalTransport();
        var timer = new ManualTerminalTimer();
        var terminal = new TuiProcessTerminal(transport, timer);
        var input = new List<string>();
        var resizeCount = 0;

        terminal.Start(input.Add, () => resizeCount++);
        timer.FireAll();
        terminal.Stop();
        transport.EmitInput("x");
        transport.EmitResize();

        Assert.False(terminal.ModifyOtherKeysActive);
        Assert.False(transport.RawMode);
        Assert.Equal([true, false], transport.RawModeHistory);
        Assert.Equal(1, transport.PauseCalls);
        Assert.Empty(input);
        Assert.Equal(0, resizeCount);
        Assert.Contains(TuiProcessTerminal.EnableModifyOtherKeys, transport.Writes);
        Assert.Contains(TuiProcessTerminal.DisableBracketedPaste, transport.Writes);
        Assert.Contains(TuiProcessTerminal.DisableModifyOtherKeys, transport.Writes);
    }

    [Fact]
    public async Task ProcessTerminal_DrainInputDisablesKittyAndRestoresInputHandler()
    {
        var transport = new FakeProcessTerminalTransport();
        var terminal = new TuiProcessTerminal(transport, new ManualTerminalTimer());
        var input = new List<string>();

        terminal.Start(input.Add, () => { });
        transport.EmitInput("\u001b[?1u");

        await terminal.DrainInputAsync(
            max: TimeSpan.FromMilliseconds(5),
            idle: TimeSpan.FromMilliseconds(1));
        transport.EmitInput("z");

        Assert.False(terminal.KittyProtocolActive);
        Assert.False(TuiKeyDecoder.IsKittyProtocolActive());
        Assert.Equal(["z"], input);
        Assert.Contains(TuiProcessTerminal.DisableKittyKeyboardProtocol, transport.Writes);
    }

    [Fact]
    public async Task ProcessTerminal_DrainInputSuppressesInputAndDisablesModifyOtherKeys()
    {
        var transport = new FakeProcessTerminalTransport();
        var timer = new ManualTerminalTimer();
        var terminal = new TuiProcessTerminal(transport, timer);
        var input = new List<string>();

        terminal.Start(input.Add, () => { });
        timer.FireAll();

        var drain = terminal.DrainInputAsync(
            max: TimeSpan.FromMilliseconds(40),
            idle: TimeSpan.FromMilliseconds(10));
        transport.EmitInput("x");
        await drain;
        transport.EmitInput("y");

        Assert.False(terminal.ModifyOtherKeysActive);
        Assert.Equal(["y"], input);
        Assert.Contains(TuiProcessTerminal.DisableModifyOtherKeys, transport.Writes);
    }

    [Fact]
    public void ProcessTerminal_OperationsWriteAnsiSequencesAndClampDimensions()
    {
        var transport = new FakeProcessTerminalTransport { Columns = 0, Rows = -1 };
        var terminal = new TuiProcessTerminal(transport, new ManualTerminalTimer());

        terminal.Write("hello");
        terminal.MoveBy(2);
        terminal.MoveBy(-3);
        terminal.MoveBy(0);
        terminal.HideCursor();
        terminal.ShowCursor();
        terminal.ClearLine();
        terminal.ClearFromCursor();
        terminal.ClearScreen();
        terminal.SetTitle("Tau");

        Assert.Equal(1, terminal.Columns);
        Assert.Equal(1, terminal.Rows);
        Assert.Equal(
            [
                "hello",
                "\u001b[2B",
                "\u001b[3A",
                TuiProcessTerminal.HideCursorSequence,
                TuiProcessTerminal.ShowCursorSequence,
                TuiProcessTerminal.ClearLineSequence,
                TuiProcessTerminal.ClearFromCursorSequence,
                TuiProcessTerminal.ClearScreenSequence,
                "\u001b]0;Tau\u0007",
            ],
            transport.Writes);
    }

    [Fact]
    public void ProcessTerminal_WriteAppendsOptionalDiagnosticLog()
    {
        var transport = new FakeProcessTerminalTransport();
        var logPath = Path.Combine(Path.GetTempPath(), $"tau-tui-write-{Guid.NewGuid():N}.log");
        var log = TuiTerminalWriteLog.FromPath(logPath);
        var terminal = new TuiProcessTerminal(transport, new ManualTerminalTimer(), writeLog: log);

        try
        {
            terminal.Write("alpha");
            terminal.Write("beta");

            Assert.Equal(["alpha", "beta"], transport.Writes);
            Assert.Equal("alphabeta", File.ReadAllText(logPath, Encoding.UTF8));
        }
        finally
        {
            File.Delete(logPath);
        }
    }

    [Fact]
    public void TerminalWriteLog_ResolvesConfiguredDirectoryToTimestampedFile()
    {
        var directory = Directory.CreateTempSubdirectory("tau-tui-write-log-");
        var now = new DateTimeOffset(2026, 6, 6, 12, 34, 56, TimeSpan.Zero);
        var log = TuiTerminalWriteLog.FromPath(directory.FullName, () => now, processId: 42);

        try
        {
            Assert.NotNull(log);
            log.Append("hello");

            var file = Assert.Single(Directory.GetFiles(directory.FullName));
            Assert.EndsWith("tui-2026-06-06_12-34-56-42.log", file);
            Assert.Equal("hello", File.ReadAllText(file, Encoding.UTF8));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void TerminalWriteLog_PrefersTauEnvironmentVariable()
    {
        var log = TuiTerminalWriteLog.FromEnvironment(name =>
            name switch
            {
                "TAU_TUI_WRITE_LOG" => "tau.log",
                "PI_TUI_WRITE_LOG" => "pi.log",
                _ => null,
            });

        Assert.NotNull(log);
        Assert.Equal("tau.log", log.FilePath);
    }

    [Fact]
    public void ProcessTerminal_RejectsDoubleStart()
    {
        var terminal = new TuiProcessTerminal(
            new FakeProcessTerminalTransport(),
            new ManualTerminalTimer());

        terminal.Start(_ => { }, () => { });

        Assert.Throws<InvalidOperationException>(() => terminal.Start(_ => { }, () => { }));
    }

    private sealed class ManualTerminalTimer : ITuiTerminalTimer
    {
        private readonly List<ScheduledCallback> _callbacks = [];

        public IDisposable Schedule(TimeSpan dueTime, Action callback)
        {
            var scheduled = new ScheduledCallback(callback);
            _callbacks.Add(scheduled);
            return scheduled;
        }

        public void FireAll()
        {
            foreach (var callback in _callbacks.ToArray())
            {
                callback.Fire();
            }
        }

        private sealed class ScheduledCallback(Action callback) : IDisposable
        {
            private bool _disposed;

            public void Fire()
            {
                if (!_disposed)
                {
                    _disposed = true;
                    callback();
                }
            }

            public void Dispose() => _disposed = true;
        }
    }

    private sealed class FakeProcessTerminalTransport : ITuiProcessTerminalTransport
    {
        private readonly List<Action<string>> _inputHandlers = [];
        private readonly List<Action> _resizeHandlers = [];

        public bool RawMode { get; private set; }
        public bool IsRawMode => RawMode;
        public bool IsWindows { get; set; }
        public int Columns { get; set; } = 80;
        public int Rows { get; set; } = 24;
        public Encoding? InputEncoding { get; private set; }
        public List<string> Writes { get; } = [];
        public List<bool> RawModeHistory { get; } = [];
        public int ResumeCalls { get; private set; }
        public int PauseCalls { get; private set; }
        public int RefreshDimensionsCalls { get; private set; }
        public int EnableVirtualTerminalInputCalls { get; private set; }

        public void SetRawMode(bool enabled)
        {
            RawMode = enabled;
            RawModeHistory.Add(enabled);
        }

        public void SetInputEncoding(Encoding encoding) => InputEncoding = encoding;

        public void ResumeInput() => ResumeCalls++;

        public void PauseInput() => PauseCalls++;

        public void RefreshDimensions() => RefreshDimensionsCalls++;

        public void EnableVirtualTerminalInput() => EnableVirtualTerminalInputCalls++;

        public IDisposable SubscribeInput(Action<string> handler)
        {
            _inputHandlers.Add(handler);
            return new Subscription(() => _inputHandlers.Remove(handler));
        }

        public IDisposable SubscribeResize(Action handler)
        {
            _resizeHandlers.Add(handler);
            return new Subscription(() => _resizeHandlers.Remove(handler));
        }

        public void Write(string data) => Writes.Add(data);

        public void EmitInput(string data)
        {
            foreach (var handler in _inputHandlers.ToArray())
            {
                handler(data);
            }
        }

        public void EmitResize()
        {
            foreach (var handler in _resizeHandlers.ToArray())
            {
                handler();
            }
        }

        private sealed class Subscription(Action dispose) : IDisposable
        {
            private bool _disposed;

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                dispose();
            }
        }
    }
}
