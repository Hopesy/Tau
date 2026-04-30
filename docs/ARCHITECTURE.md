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

## 当前阶段实现现实

截至 2026-04-24，Tau 已不再只是 CLI-first 主路径，而是已经把三个应用面从模板壳推进到真实最小产品切片，并开始进入第二层能力：

- `Tau.WebUi`：可持久化 session + provider/model 选择的 Web 宿主
- `Tau.Mom`：支持结构化委派请求的本地委派宿主
- `Tau.Pods`：支持主动健康探测的 pod 运维 CLI

但这三个模块都还只完成了 **早期产品切片**，距离上游完整产品形态仍有明显距离。

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
├── Tau.Mom/                       # Worker Service：委派/机器人宿主
└── Tau.Pods/                      # 控制台应用：Pod 管理 CLI
tests/
├── Tau.Ai.Tests/
├── Tau.Agent.Tests/
├── Tau.CodingAgent.Tests/
├── Tau.Tui.Tests/
└── Tau.Pods.Tests/
```

## 当前各模块边界

### Tau.Ai

当前已具备：

- provider registry
- built-in model catalog
- provider auth resolver / env auth / auth.json
- OpenAI / Anthropic / Google 主链
- OpenAI Responses / OpenAI Codex Responses 专用 SSE provider
- Mistral 专用 provider（tool-call id 归一、x-affinity、reasoning 参数）
- Amazon Bedrock ConverseStream 专用 provider（bearer token / SigV4、shared credentials/profile、Converse payload、binary eventstream 解析）
- Google Vertex 专用 provider（API key / service account ADC JWT bearer / authorized user refresh token exchange）
- Google Gemini CLI / Antigravity provider（Cloud Code Assist headers、Antigravity fallback、retry delay、empty SSE retry、Claude thinking beta header）
- Azure OpenAI Responses 专用 provider（Azure base URL/resource name、deployment name map、api-version、api-key header、Responses SSE）
- OpenAI Codex Responses WebSocket transport（`transport=WebSocket/Auto`、`response.create` frame、`session_id` / `x-client-request-id`、会话级 socket cache、SSE fallback）
- OpenAI Responses / Codex Responses service-tier usage 与 cost multiplier（`flex=0.5`、`priority=2`，Codex `default` -> requested tier 归一）
- GitHub Copilot Responses 动态 headers / vision 行为（`X-Initiator`、`Openai-Intent`、`Copilot-Vision-Request`、tool-result image payload）

这一轮又补了：

- OpenAI chat-completions / OpenAI-compatible / Responses request body 的 source-gen 元数据收口
- 避免 `List<Dictionary<string, object>> -> List<object>`、Responses input/tools payload 以及 primitive metadata 缺失导致的 runtime serializer 崩溃
- Bedrock 从占位错误推进到可本地验证的真实调用路径：`/model/{modelId}/converse-stream`、`AWS_BEARER_TOKEN_BEDROCK` bearer、env/explicit/shared profile credential SigV4、text/tool/thinking/usage 事件翻译
- Vertex 从 ADC placeholder 推进到真实 token exchange：service account JSON 使用 RS256 JWT bearer grant，authorized user JSON 使用 refresh token grant，最终以 `Authorization: Bearer` 调用 `streamGenerateContent`
- Gemini CLI / Antigravity 从简化单 endpoint 请求推进到更接近上游：Gemini CLI fingerprint headers、Antigravity sandbox fallback、403/404 cascade、429/5xx retry delay、empty SSE retry、Claude thinking beta header 与 `requestType: agent` payload
- Azure OpenAI Responses 从 OpenAI-compatible chat-completions fallback 切到专用 Responses provider：请求体使用 `input`，model 参数解析为 deployment name，支持 `AZURE_OPENAI_BASE_URL` / `AZURE_OPENAI_RESOURCE_NAME` / `AZURE_OPENAI_API_VERSION` / `AZURE_OPENAI_DEPLOYMENT_NAME_MAP`，并通过 StubHandler 固定 `api-key` header、URL、reasoning 与 usage 行为
- Codex Responses 从 SSE-only 扩展到 WebSocket/auto transport：新增 `ClientWebSocket` transport seam，WebSocket frame 使用 `type=response.create`，同 `SessionId` 的空闲连接可复用，`auto` 在 WebSocket 未开始前失败时回退现有 SSE 路径，Fake WebSocket tests 固定 URL/header/frame/reuse 行为
- Responses service-tier 从“只写 request body”补到 usage/cost 层：`Usage.ServiceTier` 记录 effective tier，`ModelCatalog.CalculateCost` 统一应用 `flex=0.5`、`priority=2` 倍率，Codex 响应 `default` 且请求 `flex/priority` 时按请求 tier 计价
- GitHub Copilot Responses 从“只有 provider/model 映射”补到更接近上游：内建 model 带上 Copilot Chat 静态 headers 与 image input modality，请求按最后一条消息生成 `X-Initiator`，发送图片时自动加 `Copilot-Vision-Request=true`，并允许 `ToolResultMessage` 以 `function_call_output` 数组携带图片内容

### Tau.Agent

维持原来的双层循环、工具执行、interceptor 和状态模型，继续作为所有应用面的共同 runtime 内核。

### Tau.CodingAgent

仍然是最完整的用户路径：

- 最小 CLI 宿主
- 基础 coding tools
- 与 `ModelCatalog` 对齐的 provider / model 默认解析
- `RuntimeCodingAgentRunner.Create(provider, model, history)` 显式宿主工厂

这个显式 runner 工厂现在也是 `WebUi / Mom` 继续往宿主化推进的关键共享边界。

### Tau.WebUi

当前实现为第二层 Web 宿主：

- 内嵌 HTML/JS 的单页聊天入口
- health/status/catalog/session/messages API
- session 本地持久化到 `output/webui-sessions.json`
- 每个 session 可独立配置 provider / model
- `PUT /api/sessions/{id}` 支持会话设置更新
- `WebUiRunnerFactory` 通过显式 provider/model/history 驱动 runtime

当前短板：

- 不是流式 UI
- 没有附件体系
- 还没有 auth/settings 更完整的管理层
- richer rendering 仍然不足

### Tau.Mom

当前实现为结构化本地委派宿主：

- `MomOptions` 统一 inbox/outbox/archive/poll/default provider/default model/default workdir 配置
- `FileDelegationProcessor` 扫描 `inbox/*.txt|*.md|*.json`
- `.txt/.md` 请求继续兼容为纯 prompt
- `.json` 请求支持 `prompt/provider/model/workingDirectory/title/metadata`
- 调用 runtime runner 处理 prompt
- 结果序列化到 outbox `.json`
- 原始请求归档到 archive
- 支持 `--once`

当前短板：

- 仍未接 Slack
- 没有 workspace / sandbox / 多任务委派模型
- 当前 outbox error 主要仍取决于外部 provider 可达性

### Tau.Pods

当前实现为 pod 运维 CLI 的第二层入口：

- `init [path]`
- `list [path]`
- `validate [path]`
- `status [path]`
- `probe [path]`
- `PodsConfigStore` / `PodsConfigValidator`
- `PodProbeService`
- `System.Text.Json` source-gen 上下文，兼容仓库级 AOT/trim 约束

当前 `probe` 语义：

- 对 `endpoint` 做 HTTP GET 健康探测
- 对 `sshHost/sshPort` 做 TCP 连通性探测
- 返回 `ok / transport / latency / target / summary`
- 探测失败时返回非零 exit code

当前短板：

- 还没有 deploy / stop / restart / lifecycle
- 没有模型生命周期和远端 pod 编排能力

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
| AOT | `IsAotCompatible=true`（类库），Web / Worker 应用除外 |

## 分层与依赖方向

```
Tau.Tui ──────────────────────────┐
Tau.CodingAgent ──────────────────┼──→ Tau.Agent ──→ Tau.Ai
Tau.Mom ──────────────────────────┘
Tau.WebUi ────────────────────────────→ Tau.Agent ──→ Tau.Ai
Tau.Pods（完全独立）
```

当前额外现实：

- `Tau.WebUi` / `Tau.Mom` 暂时通过 `Reference + HintPath` 引用 `Tau.CodingAgent` / `Tau.Tui`
- `Tau.WebUi` / `Tau.Mom` 还通过 `HintPath` 引用 `Tau.Ai`
- 这是当前为绕开 `.NET 10 SDK / solution metaproj / workload resolver` 异常而接受的工程化 workaround
- 后续仍需要收回到更正常的 project reference 结构

## 本地开发

```bash
bash scripts/verify-dotnet.sh
bash scripts/verify-dotnet.sh --skip-restore
dotnet run --project src/Tau.CodingAgent/Tau.CodingAgent.csproj --no-build
dotnet run --project src/Tau.WebUi/Tau.WebUi.csproj --no-build -- --urls http://127.0.0.1:5088
dotnet run --project src/Tau.Mom/Tau.Mom.csproj --no-build -- --once
dotnet run --project src/Tau.Pods/Tau.Pods.csproj --no-build -- probe tau.pods.json
```

当前已知限制：

- `Tau.CodingAgent.csproj` / `Tau.WebUi.csproj` / `Tau.Mom.csproj` / `Tau.Pods.csproj` 已可独立 build
- `Tau.WebUi` 的 `/api/status`、`/api/catalog`、`POST /api/sessions` 已可返回真实 JSON
- `Tau.WebUi` 的 session 已可持久化到 `output/webui-sessions.json`
- `Tau.Mom --once` 已可真实处理结构化请求并写出带 `provider/model/workingDirectory/metadata` 的 outbox
- `Tau.Pods probe` 已可对本地 HTTP endpoint 返回真实健康结果
- `Tau.Pods exec` 已可对 SSH pod 通过系统 `ssh` 客户端执行远程命令
- `Tau.slnx` 仍有 solution-level metaproj / workload resolver 异常，暂时不是可信门禁入口
