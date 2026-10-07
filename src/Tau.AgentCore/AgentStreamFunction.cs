// 作者：xxx
using Tau.Ai;
using Tau.Ai.Streaming;

namespace Tau.AgentCore;

/// <summary>【AgentCore】【流式函数】使用转换后的上下文启动模型流；可同步返回流或异步完成初始化</summary>
/// <param name="model">请求准备钩子选定的模型</param>
/// <param name="context">已完成上下文变换及消息转换的模型输入</param>
/// <param name="options">包含当前凭据与取消信号的请求选项</param>
/// <returns>遵守流式协议的响应流；模型请求失败应通过流内 error 或 aborted 结果表达</returns>
public delegate ValueTask<AssistantMessageStream> AgentStreamFunction(Model model, LlmContext context, SimpleStreamOptions options);

/// <summary>【AgentCore】【默认流式函数】为没有实例级发送函数的 Agent 和低层运行时提供统一后备入口</summary>
public static class AgentStreaming
{
    private static AgentStreamFunction? _defaultStreamFunction;

    /// <summary>【AgentCore】【默认流式函数】设置进程内后备函数；清空后恢复 Tau 的提供方注册表发送路径</summary>
    /// <param name="streamFunction">后备发送函数；null 清除覆盖</param>
    public static void SetDefaultStreamFunction(AgentStreamFunction? streamFunction) => Volatile.Write(ref _defaultStreamFunction, streamFunction);

    /// <summary>【AgentCore】【默认流式函数】读取后备发送函数供当前运行选择</summary>
    /// <returns>当前函数；未配置时为空</returns>
    public static AgentStreamFunction? GetDefaultStreamFunction() => Volatile.Read(ref _defaultStreamFunction);
}
