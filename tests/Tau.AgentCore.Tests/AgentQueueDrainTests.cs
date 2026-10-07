// 作者：xxx
using System.Collections.Concurrent;
using Tau.AgentCore.Runtime;
using Tau.Ai;

namespace Tau.AgentCore.Tests;

/// <summary>【AgentCore】【队列清空回归】验证完整快照及并发生产期间的消息所有权。</summary>
public sealed class AgentQueueDrainTests
{
    /// <summary>【AgentCore】【全部提取】完整提取不受调度模式影响，返回数组独立于后续入队。</summary>
    /// <param name="steeringMode">引导模式。</param><param name="followUpMode">跟进模式。</param>
    [Theory]
    [InlineData(AgentQueueMode.OneAtATime, AgentQueueMode.OneAtATime)]
    [InlineData(AgentQueueMode.OneAtATime, AgentQueueMode.All)]
    [InlineData(AgentQueueMode.All, AgentQueueMode.OneAtATime)]
    [InlineData(AgentQueueMode.All, AgentQueueMode.All)]
    public void DrainAll_IgnoresDeliveryModeAndPreservesSnapshots(AgentQueueMode steeringMode, AgentQueueMode followUpMode)
    {
        var runtime = new AgentRuntime { SteeringMode = steeringMode, FollowUpMode = followUpMode };
        ChatMessage[] steering = [new UserMessage("s1"), new UserMessage("s2")];
        ChatMessage[] followUp = [new UserMessage("f1"), new UserMessage("f2")];
        foreach (var message in steering) runtime.Steer(message);
        foreach (var message in followUp) runtime.FollowUp(message);
        var snapshot = runtime.GetQueuedMessages();
        Assert.Equal(steering, snapshot.Steering); Assert.Equal(followUp, snapshot.FollowUp);
        Assert.Equal(4, runtime.PendingMessageCount);
        Assert.IsType<ChatMessage[]>(snapshot.Steering)[0] = new UserMessage("mutated");
        var drained = runtime.DrainAllQueuedMessages();
        Assert.Equal(steering, drained.Steering); Assert.Equal(followUp, drained.FollowUp);
        Assert.False(runtime.HasQueuedMessages);
        runtime.Steer(new UserMessage("next")); runtime.FollowUp(new UserMessage("later"));
        Assert.Equal(steering, drained.Steering); Assert.Equal(followUp, drained.FollowUp);
        runtime.ClearAllQueues(); Assert.Equal(0, runtime.PendingMessageCount);
    }

    /// <summary>【AgentCore】【并发提取】并发入队和清空时，每条消息必须恰好属于一个已提取快照。</summary>
    /// <returns>并发生产和提取结束的任务。</returns>
    [Fact]
    public async Task DrainAll_WithConcurrentProducersLosesNoMessages()
    {
        var runtime = new AgentRuntime(); var collected = new ConcurrentBag<ChatMessage>();
        var messages = Enumerable.Range(0, 1000).Select(index => (ChatMessage)new UserMessage(index.ToString())).ToArray();
        var producers = Task.WhenAll(Enumerable.Range(0, 4).Select(part => Task.Run(() =>
        {
            for (var index = part; index < messages.Length; index += 4)
                if (part % 2 == 0) runtime.Steer(messages[index]); else runtime.FollowUp(messages[index]);
        })));
        do
        {
            var drained = runtime.DrainAllQueuedMessages();
            foreach (var message in drained.Steering.Concat(drained.FollowUp)) collected.Add(message);
            await Task.Yield();
        } while (!producers.IsCompleted || runtime.HasQueuedMessages);
        await producers;
        Assert.Equal(messages.Length, collected.Count);
        Assert.Equal(messages.Length, collected.Distinct(ReferenceEqualityComparer.Instance).Count());
        Assert.All(messages, message => Assert.Contains(message, collected));
    }
}
