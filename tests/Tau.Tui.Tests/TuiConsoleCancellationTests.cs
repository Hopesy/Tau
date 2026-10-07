// 作者：xxx
using System.Collections.Concurrent;
using Tau.Tui.Abstractions;
using Tau.Tui.Runtime;

namespace Tau.Tui.Tests;

/// <summary>【TUI】【控制台取消测试】验证物理读取不会因提示取消而丢行，按键等待也不会阻塞登录完成。</summary>
public sealed class TuiConsoleCancellationTests
{
    /// <summary>【TUI】【保留下一行】取消等待后，后续提示复用同一个物理读取，不丢失下一次输入。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task RedirectedInput_CancelledWaitDoesNotStealNextLine()
    {
        using var input = new BlockingReader(); using var source = new CancellationTokenSource();
        var abandoned = TuiCancelableTextReader.ReadLineAsync(input, source.Token);
        await input.Started.Task.WaitAsync(TimeSpan.FromSeconds(2)); source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned.WaitAsync(TimeSpan.FromSeconds(2)));
        var next = TuiCancelableTextReader.ReadLineAsync(input); Assert.Equal(1, input.Reads);
        input.Lines.Add(() => "next prompt value"); Assert.Equal("next prompt value", await next.WaitAsync(TimeSpan.FromSeconds(2)));
        input.Lines.Add(() => "second value"); Assert.Equal("second value", await TuiCancelableTextReader.ReadLineAsync(input)); Assert.Equal(2, input.Reads);
    }

    /// <summary>【TUI】【串行读取】并发提示依次消费各自一行，底层没有并发 ReadLine。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task RedirectedInput_SerializesConcurrentPrompts()
    {
        using var input = new BlockingReader(); input.Lines.Add(() => "one"); input.Lines.Add(() => "two");
        var first = TuiCancelableTextReader.ReadLineAsync(input); var second = TuiCancelableTextReader.ReadLineAsync(input);
        Assert.Equal(new[] { "one", "two" }, await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, input.MaximumConcurrentReads);
    }

    /// <summary>【TUI】【取消排队】等待串行锁的提示取消不会消耗正在读取或下一行。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task RedirectedInput_CancelsQueuedPromptWithoutReading()
    {
        using var input = new BlockingReader(); using var source = new CancellationTokenSource();
        var first = TuiCancelableTextReader.ReadLineAsync(input); await input.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var queued = TuiCancelableTextReader.ReadLineAsync(input, source.Token); source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued); Assert.Equal(1, input.Reads);
        input.Lines.Add(() => "first"); Assert.Equal("first", await first.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    /// <summary>【TUI】【已取消输入】调用前取消不启动后台读取。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task RedirectedInput_PreCancelledTokenDoesNotStartRead()
    {
        using var input = new BlockingReader(); using var source = new CancellationTokenSource(); source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TuiCancelableTextReader.ReadLineAsync(input, source.Token)); Assert.Equal(0, input.Reads);
    }

    /// <summary>【TUI】【输入错误】底层错误传给当前提示，随后可读下一行及 EOF。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task RedirectedInput_PropagatesFailureAndEof()
    {
        using var input = new BlockingReader(); input.Lines.Add(() => throw new IOException("read failure"));
        Assert.Equal("read failure", (await Assert.ThrowsAsync<IOException>(() => TuiCancelableTextReader.ReadLineAsync(input))).Message);
        input.Lines.Add(() => "after failure"); Assert.Equal("after failure", await TuiCancelableTextReader.ReadLineAsync(input));
        input.Lines.Add(() => null); Assert.Null(await TuiCancelableTextReader.ReadLineAsync(input));
    }

    /// <summary>【TUI】【原生按键取消】没有键时等待可取消，取消不调用 ReadKey，下一次读取仍获得新按键。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task ConsoleKeyReader_CancellationLeavesFutureKeyAvailable()
    {
        var keys = new ConcurrentQueue<ConsoleKeyInfo>(); var reads = 0;
        using var reader = new SystemConsoleKeyReader(() => !keys.IsEmpty, () => { reads++; return keys.TryDequeue(out var key) ? key : throw new InvalidOperationException(); });
        using var source = new CancellationTokenSource(); var waiting = reader.ReadKeyAsync(source.Token).AsTask();
        Assert.False(waiting.IsCompleted); source.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(0, reads); keys.Enqueue(new('x', ConsoleKey.X, false, false, false));
        Assert.Equal('x', (await reader.ReadKeyAsync()).KeyChar); Assert.Equal(1, reads);
    }

    /// <summary>【TUI】【按键取消竞态】就绪检查期间取消时仍不消费按键。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task ConsoleKeyReader_ChecksCancellationAfterAvailability()
    {
        using var source = new CancellationTokenSource(); var reads = 0;
        using var reader = new SystemConsoleKeyReader(() => { source.Cancel(); return true; }, () => { reads++; return default; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadKeyAsync(source.Token).AsTask()); Assert.Equal(0, reads);
    }

    /// <summary>【TUI】【秘密字素】退格删除完整 emoji 与组合字符，读取不需要 renderer 或输入历史。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task SecretInput_BackspaceDeletesWholeGrapheme()
    {
        var keys = "pa🔐e\u0301".Select(character => new ConsoleKeyInfo(character, 0, false, false, false)).ToList();
        keys.AddRange([new('\b', ConsoleKey.Backspace, false, false, false), new('\b', ConsoleKey.Backspace, false, false, false),
            new('s', ConsoleKey.S, false, false, false), new('\r', ConsoleKey.Enter, false, false, false)]);
        Assert.Equal("pas", await TuiConsoleInput.ReadSecretAsync(new Keys(keys), default));
    }

    /// <summary>【TUI】【秘密取消】Escape 和带修饰键 Ctrl+C 都取消秘密输入。</summary><param name="escape">是否 Escape。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SecretInput_CancelsWithoutSubmission(bool escape)
    {
        var key = escape ? new ConsoleKeyInfo('\u001b', ConsoleKey.Escape, false, false, false) : new ConsoleKeyInfo('\u0003', ConsoleKey.C, true, false, true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TuiConsoleInput.ReadSecretAsync(new Keys([key]), default));
    }

    /// <summary>【TUI】【按键夹具】按队列返回按键。</summary><param name="keys">队列内容。</param>
    private sealed class Keys(IEnumerable<ConsoleKeyInfo> keys) : IConsoleKeyReader
    {
        private readonly Queue<ConsoleKeyInfo> _keys = new(keys);
        /// <inheritdoc />
        public ValueTask<ConsoleKeyInfo> ReadKeyAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(_keys.Dequeue());
    }

    /// <summary>【TUI】【同步阻塞夹具】模拟 Console.In 的同步读取，显式释放每一行。</summary>
    private sealed class BlockingReader : TextReader
    {
        private int _reads;
        private int _active;
        public int Reads => Volatile.Read(ref _reads);
        public int MaximumConcurrentReads { get; private set; }
        public BlockingCollection<Func<string?>> Lines { get; } = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <inheritdoc />
        public override string? ReadLine()
        {
            Interlocked.Increment(ref _reads); MaximumConcurrentReads = Math.Max(MaximumConcurrentReads, Interlocked.Increment(ref _active)); Started.TrySetResult();
            try { return Lines.Take()(); } finally { Interlocked.Decrement(ref _active); }
        }
        /// <inheritdoc />
        protected override void Dispose(bool disposing) { if (disposing) Lines.Dispose(); base.Dispose(disposing); }
    }
}
