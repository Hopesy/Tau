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
- built-in model catalog + shared default selection resolver
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
- generated model seed/generator/catalog（`generated-models.seed.json` -> `generate-tau-ai-models.ps1` -> `GeneratedBuiltInModels.g.cs`）
- shared typed/default model selection（default provider/model、canonical `provider/model` 引用、冲突检测）
- model compatibility / routing metadata（`Model.Compat`，OpenAI-compatible provider 当前消费 stream usage、max tokens field、reasoning format、tool stream、strict mode、OpenRouter/Vercel routing）
- custom model/provider 配置入口（`TAU_MODELS_FILE`、`./.tau/models.json`、`~/.tau/models.json`，当前支持 Tau 已注册 API 的 `providers/baseUrl/api/apiKey/authHeader/headers/compat/models/modelOverrides` 子集）

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
- model registry 从“只有手写 `BuiltInModels`”推进到“手写基线 + generated catalog”：新增 seed JSON、generator 脚本和 `GeneratedBuiltInModels.g.cs`，并先把 Azure Responses / OpenAI Codex / GitHub Copilot Responses / Google Gemini CLI / Antigravity 扩到一批更接近上游的已支持模型族覆盖（当前 generated seed 共 66 个模型）
- model metadata 从“只能描述 id/name/cost/modalities”推进到“可携带 Tau 当前能实际消费的 compatibility/routing”：`Model.Compat` 支持 OpenAI-compatible 的 stream usage、`max_tokens`/`max_completion_tokens`、reasoning format/map、z.ai tool stream、tool strict mode、OpenRouter `provider` routing 与 Vercel AI Gateway `providerOptions.gateway`；generator 会保留 seed 里的 compat 字段。
- custom model registry 从“必须改代码注册模型”推进到“启动时合并 models.json”：`ModelConfigurationStore` 会从 `TAU_MODELS_FILE`、当前目录 `.tau/models.json`、用户目录 `.tau/models.json` 中取第一个存在文件，按 provider 合并 built-in/generated models、provider-level override、per-model override 和 custom models；`StreamFunctions` 会在请求前解析 models.json 的 `apiKey`、`authHeader` 与 provider/model headers，支持 env/literal/`!command` value resolution。动态 provider API 注册和 OAuth login 仍是后续切片。

### Tau.Agent

维持原来的双层循环、工具执行、interceptor 和状态模型，继续作为所有应用面的共同 runtime 内核。

### Tau.CodingAgent

仍然是最完整的用户路径：

- 最小 CLI 宿主
- 基础 coding tools
- 与 `ModelCatalog` 对齐的 provider / model 默认解析（CodingAgent / WebUi / Mom 复用同一解析器）
- `RuntimeCodingAgentRunner.Create(provider, model, history)` 显式宿主工厂
- 本地 session 持久化：默认写入当前目录 `./.tau/coding-agent-session.json`，也可通过 `TAU_CODING_AGENT_SESSION_FILE` 指定路径；启动时按保存的 provider/model/messages/session display name rehydrate，回合结束后保存当前消息和 display name
- 最小 session lifecycle：`/new` 清空当前 runtime messages 和 display name，并把空会话立即写回当前 session store；`/session` 输出当前平面 session 的 display name、model、消息计数、tool call 数和 session 文件路径；当前保留已选 model/provider，不实现上游多 session resume/tree/branch/full stats
- 最小 session display name：`/name [display name | clear]` 查看、设置或清空当前 session display name，并随 session store 持久化
- 最小退出命令：`/quit` 与文本 `exit` 一样结束当前 CLI loop，不调用 runner，不进入 LLM conversation
- 最小帮助命令：`/help` 列出当前 Tau 已支持的本地命令，避免用户只能靠文档猜命令面
- 最小 copy 命令：`/copy` 复制最后一条 assistant 文本消息到系统剪贴板；剪贴板写入通过 `ICodingAgentClipboard` 抽象隔离，便于测试和后续替换
- 最小 export 命令：`/export <path>` 把当前 Tau 平面 session snapshot 导出为 JSON；当前复用 `CodingAgentSessionStore` 格式，不实现上游 HTML/JSONL/tree export
- 最小 import 命令：`/import <path>` 从 Tau snapshot JSON 严格导入当前平面 session，恢复 messages/provider/model/display name；无效 JSON 或缺失文件直接报错，不回落空 session
- 最小 settings / model selection：`/model`、`/provider`、`/models`、`/providers`，默认模型写入 `./.tau/coding-agent-settings.json`，也可通过 `TAU_CODING_AGENT_SETTINGS_FILE` 指定路径
- 最小 auth 管理入口：`/auth [provider]` 查询当前 provider 凭证来源，`/login [provider]` 给出已配置或未移植 OAuth/login 的明确提示；不会回显密钥
- 最小手动 compaction：`/compact [instructions]` 使用当前模型生成会话摘要，重置 runtime state，并把摘要作为单条 user message 保留到 session store；当前不移植上游 JSONL/tree/branch/auto-compaction 体系
- `CodingAgentCommandRouter` 承载 slash command 解析和本地命令执行，`CodingAgentHost` 保持为输入循环、UI 渲染、runtime event、退出信号和 session 持久化宿主；host 会把当前 session store path 注入 router，供 `/session` 做本地状态报告
- `CodingAgentCommandCatalog` 维护当前已支持 slash command 的名称、usage 和描述，`/help` 与参数错误共用同一事实源，避免继续在 router 内散落命令字符串；当前已支持 `/help`、`/name`、`/copy`、`/export`、`/import`、`/new`、`/session`、`/quit`、`/model`、`/provider`、`/models`、`/providers`、`/auth`、`/login`、`/compact`

这个显式 runner 工厂现在也是 `WebUi / Mom` 继续往宿主化推进的关键共享边界；本地 session/settings/auth-status 入口则为后续真实 OAuth、slash command、compaction 与 WebUi/Mom 共享会话语义打底。

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
- `MomOptions` 同时暴露 `EventsPath` 与 `RunningStatusStaleAfterMinutes`，配置会在 `Program.cs` 中规范化为绝对路径/实际数值
- `FileDelegationProcessor` 扫描 `inbox/*.txt|*.md|*.json`
- `MomEventProcessor` 扫描 `events/*.json`，把上游 mom 风格的 `immediate`、`one-shot`、`periodic` 事件转换成 inbox `.json` 委派请求
- event `.json` 支持 `type/channelId/text/at/schedule/timezone/provider/model/title/metadata/attachments`；入队 prompt 形如 `[EVENT:file:type:schedule] text`，metadata 会补 `event/eventType/eventFile/channel/user/userName/ts/date`
- `channelId` 会映射到 `DefaultWorkingDirectory/<channelId>`，从而复用现有 `MEMORY.md`、`log.jsonl` 和 `status.json` channel/workspace seam；无效事件会移入 `archive/invalid-events`
- `periodic` 当前实现五段 cron 的本地匹配（`*`、`*/n`、单值、范围、逗号）和 timezone 转换，同一事件文件同一分钟只入队一次；它是本地 worker seam，不等同于已经接上 Slack queue
- `MomChannelMessage` / `MomChannelAttachment` 是 file delegation、local events 和未来 Slack adapter 共用的 Slack-compatible envelope：统一承载 `channel/user/userName/displayName/ts/threadTs/text/attachments/provider/model/title/metadata`，再生成 `DelegationRequest`，避免后续 Slack/backfill/queue 各自拼 request metadata
- `IMomChannelTransport` / `IMomChannelResponder` / `MomChannelMessageProcessor` 固定未来 Slack adapter 的输入输出边界：transport 读入 channel message，responder 负责响应/thread/typing/upload，processor 负责 busy-state、stop placeholder、attachment staging、status/log writeback 与 runner 调用
- `.txt/.md` 请求继续兼容为纯 prompt
- `.json` 请求支持 `prompt/provider/model/workingDirectory/title/metadata/attachments`
- `title` 进入 runner session name，`metadata` / `attachments` 会被包装成 `<delegation_context>` 注入实际 prompt
- `ChannelAttachmentStore` 会把本地存在的 request/event attachment staging 到 `workingDirectory/attachments/<timestamp>_<filename>`，写入 `attachments/attachments.jsonl` 记录 `original/local/source`，并让 channel log 保留上游 mom 风格的 `original/local` 附件元数据；尚不存在的相对附件路径会保留为工作目录内路径，给后续 Slack 下载/adapter 接线留兼容入口
- `RuntimeDelegationAgentRunner` 在 `<delegation_context>` 之前还会注入 `<mom_runtime_context>`，把本地 Mom 的 workspace/channel layout、events 文件格式、attachment manifest、memory/log/status 路径和 `[SILENT]` 事件约定写成运行时前缀；这是本地 worker 的语义，不代表 Slack adapter 已接通
- `ChannelWorkspaceLayout` 统一定义并创建本地 workspace/channel 路径：workspace `MEMORY.md` / `SYSTEM.md` / `skills/` / `events/`，channel `MEMORY.md` / `log.jsonl` / `context.json` / `status.json` / `last_prompt.jsonl` / `attachments/` / `scratch/` / `skills/`；这样后续 Slack adapter、sandbox 和 tool delegation 不再各自拼路径字符串
- `SYSTEM.md` 的非空内容会以 `system_configuration_log` 注入 `<delegation_context>`；workspace/channel `skills/**/SKILL.md` 会被解析为 prompt 中的 skill docs inventory。当前这是本地 prompt/context seam，不代表 Tau.Mom 已经能执行自定义 mom skills
- `ChannelPromptDebugStore` 会在每次调用 runner 前写 `workingDirectory/last_prompt.jsonl`，记录 mom runtime context、delegation context、实际 runner input、恢复的 session messages、当前 user prompt、attachment count 和 image attachment count；这是对齐上游 mom `last_prompt.jsonl` 的本地可观测 seam，不代表图片附件已经以多模态内容传入 runner
- `workingDirectory/MEMORY.md` 与其父目录的 `MEMORY.md` 会以 current/parent workspace memory 注入同一段上下文
- `workingDirectory/log.jsonl` 会记录本地 file delegation 的用户请求与 bot 结果；最近非 bot 文本消息会以 channel history 注入同一段上下文，malformed 行、空文本和当前消息 `ts` 会被跳过
- `ChannelLogStore` 统一承载 `log.jsonl` append、history 读取和过滤语义，避免 runner / processor / 后续 Slack adapter 各自解析 channel log
- `workingDirectory/context.json` 保存 Tau-native channel session snapshot；`ChannelSessionStore` 会在 delegation 前恢复上一轮 runtime messages，并在完成后写回当前 runner messages/provider/model/session name。它不是上游 JSONL session tree 的完整移植，但已经给后续 Slack session sync 留出本地持久化 seam
- `workingDirectory/status.json` 记录当前或最近一次 delegation 的 `running/completed/failed` 状态、请求文件、provider/model、workdir、开始/完成时间、错误和响应摘要；本地 worker 会跳过同一 workdir 内未过期的 `running` 状态，默认 60 分钟后视为 stale
- `ChannelStatusStore` 统一承载 `status.json` 读写与 running/stale 判定语义，避免 file worker 和后续 adapter 各自拼状态文件
- `IDelegationAgentRunner` 返回 `DelegationExecution`：response、结构化 `DelegationToolEvent`（phase/toolName/toolCallId/isError/durationMs）、stop reason、`DelegationUsage`（input/output/cache tokens 与可选总成本）、provider/model/workingDirectory/metadata
- `RuntimeDelegationAgentRunner` 通过可注入的 `Func<provider, model, ICodingAgentRunner>` 工厂构造内核 runner，便于测试和后续 Slack/workspace/sandbox 适配层复用
- 结果序列化到 outbox `.json`，包含 title、attachments、stop reason、结构化 tool events 与 usage
- 原始请求归档到 archive
- 支持 `--once`

当前短板：

- 仍未接 Slack
- 没有完整 Slack/workspace/sandbox / 多任务委派模型；当前只有 Slack-compatible message envelope、transport/responder/processor seam、`events/`、`MEMORY.md`、`SYSTEM.md`、`skills/` docs inventory、`scratch/`、`log.jsonl`、`context.json`、`last_prompt.jsonl` 与 `status.json` 的最小本地状态/上下文注入
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

- `Tau.CodingAgent`、`Tau.WebUi`、`Tau.Mom`、`Tau.CodingAgent.Tests` 与 `Tau.Agent.Tests` 已收回到正常 `ProjectReference`，干净构建不再依赖预先存在的 DLL `HintPath`
- `Tau.slnx` 当前已可通过 `dotnet build Tau.slnx --verbosity minimal` 完成 solution-level build
- 当前机器的 `bash scripts/verify-dotnet.sh --skip-restore` 仍会落到 WSL 并失败于缺少 `/bin/bash`，因此本机继续使用等价 PowerShell / dotnet 顺序命令做验证兜底

## 本地开发

```bash
bash scripts/verify-dotnet.sh
bash scripts/verify-dotnet.sh --skip-restore
powershell -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore
powershell -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore -RunSmoke
dotnet run --project src/Tau.CodingAgent/Tau.CodingAgent.csproj --no-build
dotnet run --project src/Tau.WebUi/Tau.WebUi.csproj --no-build -- --urls http://127.0.0.1:5088
dotnet run --project src/Tau.Mom/Tau.Mom.csproj --no-build -- --once
dotnet run --project src/Tau.Pods/Tau.Pods.csproj --no-build -- probe tau.pods.json
```

当前已知限制：

- `Tau.CodingAgent.csproj` / `Tau.WebUi.csproj` / `Tau.Mom.csproj` / `Tau.Pods.csproj` 已可独立 build
- `Tau.CodingAgent` 默认把本地会话保存到当前目录 `.tau/coding-agent-session.json`，`TAU_CODING_AGENT_SESSION_FILE` 可覆盖路径
- `Tau.CodingAgent` 默认把本地模型设置保存到当前目录 `.tau/coding-agent-settings.json`，`TAU_CODING_AGENT_SETTINGS_FILE` 可覆盖路径
- `Tau.WebUi` 的 `/api/status`、`/api/catalog`、`POST /api/sessions` 已可返回真实 JSON
- `Tau.WebUi` 的 session 已可持久化到 `output/webui-sessions.json`
- `Tau.Mom --once` 已可真实处理结构化请求并写出带 `provider/model/workingDirectory/title/metadata/attachments` 的 outbox；file request 与 due `events/*.json` 会先映射为 `MomChannelMessage`，再生成统一 `DelegationRequest`；本地存在的 attachment 会 staging 到 `workingDirectory/attachments/` 并保留 original/local 元数据；runner 执行前会创建 `scratch/`、workspace/channel `skills/`、`attachments/` 和 `events/`，输入会合并 request context、workspace memory、`SYSTEM.md`、skill docs inventory 与最近 channel history，并通过 `context.json` 恢复/保存同一 workdir 的 runtime messages；调用 runner 前会写 `workingDirectory/last_prompt.jsonl` 作为 prompt/debug 快照；处理完成后会把本地请求/结果追加到 `workingDirectory/log.jsonl`，并把当前或最近一次运行状态写到 `workingDirectory/status.json`；同一 workdir 内已有新鲜 `running` 状态时会保留 inbox 请求并跳过处理
- `Tau.Pods probe` 已可对本地 HTTP endpoint 返回真实健康结果
- `Tau.Pods exec` 已可对 SSH pod 通过系统 `ssh` 客户端执行远程命令
- `Tau.slnx` 当前已可 build；如果本机 `bash` 入口不可用，可按 `scripts/verify-dotnet.sh` 中的项目顺序直接执行等价 `dotnet build/test` 命令
- `scripts/verify-dotnet.ps1` 当前提供 Windows PowerShell 等价验证入口，覆盖与 `verify-dotnet.sh` 相同的 restore/build/test 项目顺序，并支持可选 `-RunSmoke` 执行 `WebUi` 和 `Mom --once` 的最小运行态 smoke
