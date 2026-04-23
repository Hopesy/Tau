# 架构总览

Tau 是 [pi-mono](https://github.com/badlogic/pi-mono) 的 .NET 10 移植版本。pi-mono 是一个 TypeScript AI Agent 工具集，Tau 用完全自建的 C# 14 抽象重新实现其核心设计。

## 设计哲学

不依赖 Microsoft.Extensions.AI，完全自建抽象层，忠实移植 pi-mono 的架构模式：
- **Push-pull EventStream**：`Channel<T>` 实现的事件桥接，生产者 Push，消费者 `IAsyncEnumerable` 迭代
- **Provider lazy registry**：`ConcurrentDictionary` + `Lazy<T>` 工厂委托
- **13 种 stream event** 的嵌套生命周期协议
- **Agent 双层循环**：inner loop (tool calls + steering), outer loop (follow-up messages)
- **abstract record 区分联合**：C# sealed record hierarchy + pattern matching

## 上游对照

| pi-mono 包 | Tau 项目 | 说明 |
|---|---|---|
| `@mariozechner/pi-ai` | `Tau.Ai` | 统一多供应商 LLM API（OpenAI、Anthropic、Google 等） |
| `@mariozechner/pi-agent-core` | `Tau.Agent` | Agent 运行时：工具调用、状态管理 |
| `@mariozechner/pi-coding-agent` | `Tau.CodingAgent` | 交互式编码 Agent CLI |
| `@mariozechner/pi-tui` | `Tau.Tui` | 终端 UI 库（差分渲染） |
| `@mariozechner/pi-web-ui` | `Tau.WebUi` | Web 聊天界面组件 |
| `@mariozechner/pi-mom` | `Tau.Mom` | Slack 机器人，委派消息给编码 Agent |
| `@mariozechner/pi-pods` | `Tau.Pods` | vLLM GPU Pod 部署管理 CLI |

## 仓库结构

```text
Tau.slnx                           # 解决方案文件
Directory.Build.props              # 全局构建配置（net10.0, AOT, TreatWarningsAsErrors）
global.json                        # SDK 版本锁定
src/
├── Tau.Ai/                        # 类库：LLM 抽象层（零外部依赖）
│   ├── Abstractions/              # 消息、内容块、流事件、模型、工具、选项
│   ├── Streaming/                 # EventStream<T,R> + AssistantMessageStream
│   ├── Providers/                 # IStreamProvider + ProviderRegistry + StreamFunctions
│   └── Serialization/             # System.Text.Json source-gen 上下文
├── Tau.Agent/                     # 类库：Agent 运行时
│   ├── Abstractions/              # IAgentTool, AgentEvents, AgentState, IToolInterceptor
│   ├── Runtime/                   # AgentRuntime, AgentLoopConfig, ToolExecutor, ContextTransformer
│   └── Extensions/                # IAgentExtension 扩展接口
├── Tau.CodingAgent/               # 控制台应用：编码 Agent CLI
├── Tau.Tui/                       # 类库：终端 UI
├── Tau.WebUi/                     # ASP.NET Core：Web 聊天界面
├── Tau.Mom/                       # Worker Service：Slack 机器人
└── Tau.Pods/                      # 控制台应用：Pod 管理 CLI
tests/
├── Tau.Ai.Tests/
├── Tau.Agent.Tests/
├── Tau.CodingAgent.Tests/
├── Tau.Tui.Tests/
└── Tau.Pods.Tests/
```

## 核心类型映射

| pi-mono 概念 | C# 实现 | 文件 |
|---|---|---|
| `Message` (discriminated union) | `abstract record ChatMessage` + sealed 子类 | `Tau.Ai/Abstractions/Messages.cs` |
| `ContentPart` | `abstract record ContentBlock` + sealed 子类 | `Tau.Ai/Abstractions/ContentBlocks.cs` |
| `AssistantMessageEvent` (13 types) | `abstract record StreamEvent` + sealed 子类 | `Tau.Ai/Abstractions/StreamEvents.cs` |
| `EventStream<T,R>` | `EventStream<TEvent,TResult>` (Channel-based) | `Tau.Ai/Streaming/EventStream.cs` |
| `AssistantMessageEventStream` | `AssistantMessageStream` | `Tau.Ai/Streaming/AssistantMessageStream.cs` |
| api-registry + lazy loading | `ProviderRegistry` (ConcurrentDict + Lazy) | `Tau.Ai/Providers/ProviderRegistry.cs` |
| `AgentTool` | `IAgentTool` 接口 | `Tau.Agent/Abstractions/IAgentTool.cs` |
| `AgentEvent` (11 types) | `abstract record AgentEvent` + sealed 子类 | `Tau.Agent/Abstractions/AgentEvents.cs` |
| beforeToolCall/afterToolCall | `IToolInterceptor` | `Tau.Agent/Abstractions/IToolInterceptor.cs` |
| Agent + agent-loop.ts | `AgentRuntime` (双层循环) | `Tau.Agent/Runtime/AgentRuntime.cs` |

## 技术栈

| 层面 | 选型 |
|---|---|
| 运行时 | .NET 10 (net10.0) |
| 语言 | C# 14 (preview) |
| HTTP 客户端 | `HttpClient` + `System.Net.Http.Json` |
| 流式处理 | `System.Threading.Channels` + `IAsyncEnumerable<T>` |
| CLI 框架 | `System.CommandLine` |
| 序列化 | `System.Text.Json` (source generator, AOT 友好) |
| 测试 | xUnit |
| 代码风格 | `dotnet format` + .editorconfig |
| AOT | `IsAotCompatible=true`（类库），Web 项目除外 |

## 分层与依赖方向

```
Tau.Tui ──────────────────────────┐
Tau.CodingAgent ──────────────────┼──→ Tau.Agent ──→ Tau.Ai
Tau.Mom ──────────────────────────┘
Tau.WebUi ────────────────────────────→ Tau.Agent ──→ Tau.Ai
Tau.Pods（完全独立）
```

- `Tau.Ai`：最底层，零仓库内依赖，零第三方依赖。仅用 BCL。
- `Tau.Agent`：依赖 `Tau.Ai`。双层循环 + 消息队列 + 工具执行器。
- `Tau.CodingAgent`：依赖 `Tau.Agent` + `Tau.Tui`。组装编码工具集 + CLI 交互。

## 核心设计决策

1. **完全自建抽象**：不依赖 Microsoft.Extensions.AI，接口设计忠于 pi-mono 原始架构。未来可通过薄适配器包互操作。
2. **Channel-based EventStream**：`EventStream<TEvent,TResult>` 用 `Channel<T>` 实现 push-pull bridge，生产者 `Push()`，消费者 `await foreach`，`ResultAsync` 等待最终结果。
3. **流事件协议**：13 种事件构成嵌套生命周期 — `start → (text|thinking|toolcall)_(start→delta*→end)* → done|error`。
4. **Lazy Provider Registry**：`ProviderRegistry` 用 `Lazy<T>` 工厂延迟初始化，`sourceId` 支持批量注销（对应 pi-mono 的插件清理）。
5. **Agent 双层循环**：inner loop 处理 tool calls + steering 消息注入，outer loop 处理 follow-up 消息，完整复刻 pi-mono 的 agent-loop.ts。
6. **IToolInterceptor 管道**：`BeforeToolCallAsync` 可拦截/阻止，`AfterToolCallAsync` 可修改结果，对应 pi-mono 的 hook 系统。
7. **DI 可选**：核心层可直接 `new` 使用，不强制依赖容器。
8. **AOT from day one**：JSON 序列化走 source generator，类库标记 `IsAotCompatible`。

## 数据流

```
用户输入
  → CodingAgent CLI 解析（System.CommandLine）
    → AgentRuntime.RunAsync() 启动双层循环
      → ContextTransformer 构建 LlmContext
        → ProviderRegistry.Get(api) 获取 IStreamProvider
          → provider.Stream() 返回 AssistantMessageStream
            → 内部 HttpClient SSE 请求 → Push() 事件
          ← 消费者 await foreach StreamEvent
        ← AgentRuntime 判断 DoneEvent / ToolCallContent
      → ToolExecutor 执行工具（顺序/并行）
        → IToolInterceptor 管道拦截
        ← ToolResult 写入会话状态
      → 继续内循环（如有新 tool calls 或 steering 消息）
    ← AgentEvent 流输出
  ← TUI 渲染

steering 注入: AgentRuntime.Steer(msg) → Channel → 下一轮内循环消费
follow-up 注入: AgentRuntime.FollowUp(msg) → Channel → 外循环继续
```

## 本地开发

```bash
bash scripts/verify-dotnet.sh                               # 当前稳定的项目级 restore/build/test 入口
bash scripts/verify-dotnet.sh --skip-restore                # 本机依赖已 restore 时更快
dotnet run --project src/Tau.CodingAgent/Tau.CodingAgent.csproj --no-build
```

当前已知限制：

- `Tau.CodingAgent.csproj` 已恢复独立 build / run
- `Tau.slnx` 仍有 solution-level metaproj / workload resolver 异常，暂时不是可信门禁入口

## 第三方依赖（极简）

| 项目 | 外部依赖 |
|---|---|
| Tau.Ai | 无（纯 BCL） |
| Tau.Agent | 无（纯 BCL + Tau.Ai） |
| Tau.CodingAgent | System.CommandLine |
| Tau.Mom | Microsoft.Extensions.Hosting |
| Tests | xUnit |
