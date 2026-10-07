// 作者：xxx
using Tau.Ai;

namespace Tau.Ai.Tests;

public sealed class RecoverableLengthTests
{
    /// <summary>只有 length 且实际输出低于正的原始预算时才允许压缩恢复。</summary>
    /// <param name="reason">提供方停止原因。</param>
    /// <param name="output">已生成的 token 数。</param>
    /// <param name="desired">原始输出预算。</param>
    /// <param name="expected">预期判断。</param>
    [Theory]
    [InlineData(StopReason.MaxTokens, 10, 100, true)]
    [InlineData(StopReason.MaxTokens, 100, 100, false)]
    [InlineData(StopReason.MaxTokens, 110, 100, false)]
    [InlineData(StopReason.MaxTokens, 0, 0, false)]
    [InlineData(StopReason.Aborted, 10, 100, false)]
    [InlineData(StopReason.EndTurn, 10, 100, false)]
    public void DetectsOnlyPrematureLength(StopReason reason, int output, int desired, bool expected)
    {
        var message = new AssistantMessage([]) { StopReason = reason, Usage = new(20, output) };
        Assert.Equal(expected, ContextOverflowDetector.IsRecoverableLength(message, desired));
    }
}
