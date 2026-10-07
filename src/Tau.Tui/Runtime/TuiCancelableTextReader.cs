// 作者：xxx
using System.Runtime.CompilerServices;

namespace Tau.Tui.Runtime;

/// <summary>【TUI】【管道输入】对同一 TextReader 共用一次底层读取，取消等待后仍把下一行交给后续提示。</summary>
public static class TuiCancelableTextReader
{
    private static readonly ConditionalWeakTable<TextReader, PendingReader> Readers = new();

    /// <summary>【TUI】【管道输入】取消只结束当前等待，不再启动会争抢下一行的后台读取。</summary>
    /// <param name="input">输入流，调用方拥有其生命周期。</param><param name="token">本次等待取消。</param><returns>下一行，EOF 为空。</returns>
    public static Task<string?> ReadLineAsync(TextReader input, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        return Readers.GetValue(input, static reader => new(reader)).ReadAsync(token);
    }

    /// <summary>【TUI】【输入串行化】每个输入源最多只有一次正在进行的物理读取。</summary><param name="input">底层读入器。</param>
    private sealed class PendingReader(TextReader input)
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private Task<string?>? _pending;

        /// <summary>【TUI】【输入串行化】取消时保留尚未消费的行或错误，下次调用继续等待同一个任务。</summary>
        /// <param name="token">调用方取消。</param><returns>一行或 EOF。</returns>
        internal async Task<string?> ReadAsync(CancellationToken token)
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();
                if (_pending is null)
                {
                    // 1. 【TUI】【同步输入兼容】Console.In 的同步包装即使调用 ReadLineAsync 也可能阻塞调用线程
                    _pending = Task.Run(input.ReadLine);
                    _ = _pending.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
                try
                {
                    var line = await _pending.WaitAsync(token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    _pending = null;
                    return line;
                }
                catch (Exception) when (!token.IsCancellationRequested) { _pending = null; throw; }
            }
            finally { _gate.Release(); }
        }
    }
}
