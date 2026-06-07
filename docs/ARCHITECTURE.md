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

截至 2026-05-24，Tau 已不再只是 CLI-first 主路径，而是已经把三个应用面从模板壳推进到真实最小产品切片，并开始进入第二层能力：

- `Tau.WebUi`：可持久化 session + provider/model 选择的 Web 宿主
- `Tau.Mom`：支持结构化委派请求、Slack-compatible channel runtime、Mom sandbox/tool seam 和 delegation runtime event 的本地委派宿主
- `Tau.Pods`：支持主动健康探测、model lifecycle 和 vLLM SSH orchestration baseline 的 pod 运维 CLI

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
├── Tau.Ai.Cli/                    # 控制台应用：pi-ai login/list 等价入口 baseline
├── Tau.Agent/                     # 类库：Agent 运行时
│   ├── Abstractions/              # IAgentTool, AgentEvents, AgentState, IToolInterceptor
│   ├── Runtime/                   # AgentRuntime, AgentLoopConfig, ToolExecutor, ContextTransformer
│   ├── Platform/                  # AgentApplication / builder / delegate tool / session store
│   └── Extensions/                # IAgentExtension 扩展接口
├── Tau.CodingAgent/               # 控制台应用：编码 Agent CLI
├── Tau.Tui/                       # 类库：终端 UI
├── Tau.WebUi/                     # ASP.NET Core：Web 聊天界面
├── Tau.Mom/                       # Worker Service：委派/机器人宿主
└── Tau.Pods/                      # 控制台应用：Pod 管理 CLI
examples/
├── Tau.Agent.ConsoleExample/      # Agent platform console example
└── Tau.Agent.HttpExample/         # Agent platform ASP.NET Core example
tests/
├── Tau.Ai.Tests/
├── Tau.Agent.Tests/
├── Tau.CodingAgent.Tests/
├── Tau.Tui.Tests/
├── Tau.WebUi.Tests/
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
- standalone AI CLI baseline：`Tau.Ai.Cli` 提供 `tau-ai help/list/login [provider]`，复用 `Tau.Ai` OAuth provider registry；默认写 Tau auth store（`TAU_AUTH_FILE`、`./.tau/auth.json`、`~/.tau/auth.json`），`--auth-file auth.json` 可显式复刻上游 `pi-ai` 当前目录 `auth.json` 写入语义
- public helper / test provider baseline：`ToolArgumentValidator`、`AiHeaderUtilities`、`ShortHash`、`ContextOverflowDetector`、`JsonSchemaHelpers.StringEnum`、`StreamingJsonParser` 和 `Tau.Ai.Providers.Faux` 覆盖 Tau 当前需要公开给 .NET 消费者的 tool validation、headers、short hash、context overflow、string enum schema helper、streaming incomplete JSON parser 与 scripted provider 子集
- Bedrock AWS SSO cache token refresh baseline：当 AWS CLI cache 中已有 `clientId`、`clientSecret`、`refreshToken` 和未过期 `registrationExpiresAt` 时，过期 access token 会通过 SSO OIDC `CreateToken` 刷新，并 best-effort 写回 cache；`BedrockOptions.SsoOidcEndpoint` 或 `AWS_ENDPOINT_URL_SSO_OIDC` 可覆盖 OIDC endpoint。当前仍不做 OIDC `RegisterClient`，也没有真实 AWS 云端 e2e。

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
- custom model registry 从“必须改代码注册模型”推进到“启动时合并 models.json”：`ModelConfigurationStore` 会从 `TAU_MODELS_FILE`、当前目录 `.tau/models.json`、用户目录 `.tau/models.json` 中取第一个存在文件，按 provider 合并 built-in/generated models、provider-level override、per-model override 和 custom models；`StreamFunctions` 会在请求前解析 models.json 的 `apiKey`、`authHeader` 与 provider/model headers，支持 env/literal/`!command` value resolution。auth status 只检查 credential 配置是否存在，不解析 env、不执行 `!command`、不回显 secret；默认本地 `.tau/models.json` 已被忽略。`models.json` 中未预注册 provider 只有在 `apiKind` / `apiType` 显式标记为 `openai-compatible`、`openai-completions` 或 `openai-chat-completions` 时才会运行时注册为 OpenAI-compatible provider，未知或拼错 API 不静默兜底；更完整 runtime config UX 仍是后续切片。
- public helper surface 从只有内部散落实现推进到 .NET-native helper/provider baseline：`AiHeaderUtilities.ToDictionary(...)` 对应上游 `headersToRecord`，`ShortHash.Compute(...)` 固定上游 `shortHash` 输出，`ContextOverflowDetector` 固定上游 context overflow provider pattern / non-overflow exclusion / silent usage overflow 判断，`JsonSchemaHelpers.StringEnum(...)` 输出 provider-compatible string enum JSON schema，`StreamingJsonParser.ParseStreamingJson(...)` 对齐上游 `parseStreamingJson` 的 complete JSON fast path、incomplete object/array/string/literal/number best-effort recovery 和 invalid/empty fallback `{}`；`Tau.Ai.Providers.Faux` 对齐上游 public faux provider 的 register/unregister、queued/factory responses、text/thinking/tool-call helper、usage/cache estimate 和 streamed delta baseline；`CodingAgentRetryClassifier` 已复用同一 overflow detector；OpenAI-style / OpenAI Responses shared parser / Anthropic / Mistral / Bedrock 的 streaming tool-call arguments baseline 已接入 `StreamingJsonParser` object raw-text helper，`ToolCallContent.Arguments` 保持合法 JSON object 字符串，`ToolCallDeltaEvent.Delta` 仍保留 provider 原始增量；`Tau.Ai.Cli` 已覆盖上游 `pi-ai` 的 help/list/login 基线。完整 TypeBox/AJV、发布层 `pi-ai` bin alias、exact TypeScript export/subpath shape、真实 OAuth/provider e2e 仍不是本切片完成项。
- runtime event log 脱敏从“调用方自觉”推进到 `JsonlTauLogSink` 默认行为：sink 创建时按 `TAU_LOG_REDACT_SECRETS` 构造 `TauSecretRedactor`，先生成完整 JSONL 行，再通过 `JsonlSecretRedactor` 递归处理 category、event 和 field value 等 string value；object key、number、bool 和 null 保留，`TAU_LOG_REDACT_SECRETS=0` 可关闭。该能力只覆盖 `ITauLogSink -> JsonlTauLogSink` 事件日志；其它 JSONL writer/importer 需要显式接入同一 helper。
- JSONL 行级脱敏从 runtime log 专用实现扩成 `JsonlSecretRedactor` foundation：JSON 行会递归只处理 string value，不改 object key，number/bool/null 保留，invalid JSON fallback 到整行 pattern redaction；`Tau.CodingAgent` 已复用该 helper 处理 JSONL tree session 写出，`Tau.WebUi` 已复用该 helper 处理 WebUi-local JSONL export/import 与 CodingAgent JSONL preview/import，`Tau.Mom` 已复用该 helper 处理 `log.jsonl` 和 `last_prompt.jsonl`；field key 保持原样，非标准 secret pattern 仍需后续按真实泄漏样本扩展。
- Bedrock SSO refresh 选择继续保持 Tau 的无 AWS SDK 边界，只消费 AWS CLI token cache 已有的 client registration/refresh metadata；缺 refresh metadata、registration 过期或 OIDC refresh 失败时，会返回明确的 `aws sso login --profile <profile>` 诊断，而不是静默退回其它 credential source。

### Tau.Agent

当前从“只有底层 runtime”推进到上游 `packages/agent/src/agent.ts` 的高层 facade baseline：

- `AgentRuntime` 继续承载双层循环、provider run trace、工具执行 trace、steering/follow-up 队列、interceptor、runtime log context 和状态模型，是 WebUi / Mom / CodingAgent 共享内核。
- `Agent` / `AgentOptions` 是 .NET-native 高层入口：持有 model/provider registry/system prompt/tools/interceptors/options，暴露 `PromptAsync`、`ContinueAsync`、`Subscribe`、`Steer`、`FollowUp`、queue clear、`Abort`、`WaitForIdleAsync` 和 `Reset`。
- `Tau.Agent.Platform` 是当前 Agent 应用底座的薄 public surface：`AgentApplication` / `AgentApplicationBuilder` 统一 provider registry、model、system prompt、tools、session id、log sink 和 initial/restored messages；`DelegateAgentTool` 让应用用 delegate 注册工具；`AgentRunResult` 暴露 final messages、assistant text、usage、stop reason、error/cancel 状态和可审计 events；`IAgentSessionStore` / `AgentSessionSnapshot` / `InMemoryAgentSessionStore` 提供 UI-free conversation 保存/恢复合同。
- `AgentState` 现在同时暴露 system prompt、model、tools、messages、streaming message、pending tool calls、error 和 streaming 状态。
- `MessageStartEvent` / `MessageEndEvent` 可承载任意 `ChatMessage`，facade 会为 prompt user message 发送 message lifecycle；runtime 对 tool result 也会发送 message lifecycle，以靠近上游 `agent-loop.ts` 事件语义。
- `examples/Tau.Agent.ConsoleExample` 和 `examples/Tau.Agent.HttpExample` 证明 Tau 可以作为普通 .NET Agent 应用底座消费：两个示例都使用 `Tau.Ai.Providers.Faux`、delegate tool、`InMemoryAgentSessionStore` 和 runtime log sink，并通过 `scripts/verify-agent-platform-examples.ps1` 做本地 smoke；该 smoke 已接入 `scripts/verify-dotnet.ps1 -RunSmoke`。

当前仍不是完整上游 Agent package parity：`agent_end.messages`、`turn_end.message/toolResults`、tool update partial result、schema validation、tool exception -> error tool result、provider run + tool execution runtime trace、facade/runtime/proxy/event public API compile-sample、high-level facade 的 stream failure / cancellation synthetic assistant message、low-level `AgentRuntime.RunStream(...)` / `EventStream<AgentEvent, ChatMessage[]>` wrapper、parallel tool start/update timing、assistant stream cancellation terminal event、aborted `ErrorEvent` stop reason preservation、tool cancellation cleanup、parallel sibling cancellation result preservation、`TransformContext` cancellation 和第一版 Agent platform API / examples 都已落地；剩余更大范围缺口现在转为真实 provider/OAuth e2e、provider/auth missing UX、package/release 消费边界、`index`/export 形状决策和真实 proxy/e2e 验证。

### Tau.Tui

当前从输入编辑器继续推进到可复用 TUI foundation：

- `ITuiComponent` / `ITuiInputComponent` 定义组件渲染和键盘输入的最小合同
- `TuiContainer` / `TuiBox` / `TuiTextBlock` / `TuiTruncatedText` 提供纵向组件树、padding/background 容器、上游 `components/text.ts` 的文本块渲染与 background formatter/cache baseline，以及上游 `components/truncated-text.ts` 的首行截断文本组件；`TuiBox` 也覆盖上游 `components/box.ts` 的 background formatter 与 bg-sample/child-line cache invalidation
- `TuiSpacer` 提供上游 `components/spacer.ts` 的空行占位组件：默认 1 行、可动态更新 line count、负数按空输出处理，并可在 `TuiContainer` / `TuiBox` 等组合树中插入 vertical gap
- `TuiText` 提供终端可见宽度、ANSI escape 忽略、CJK/emoji 宽字符估算、截断、padding 和 word wrap helper
- `TuiLoader` / `TuiCancellableLoader` 提供上游 loader 家族的库层 foundation：10 帧 spinner、message update、formatter hook、可选 timer、手动 tick、render request callback，以及 Escape/Ctrl-C abort signal
- `TuiMarkdown` 提供上游 `components/markdown.ts` 的库层 foundation：heading、paragraph、inline code/bold/italic/strike/link、fenced code、list、table、blockquote、horizontal rule、padding/background/cache 和 OSC 8 hyperlink 可选输出
- `TuiTerminalImage` / `TuiImage` 提供上游 `terminal-image.ts` 与 `components/image.ts` 的库层 foundation：terminal capability detection、kitty/iTerm2 escape encoding、PNG/JPEG/GIF/WebP dimension sniffing、row calculation、fallback text、hyperlink helper、image component cache 和 protocol/fallback render
- `TuiInputSequenceBuffer` 提供上游 `stdin-buffer.ts` 的输入序列缓冲 foundation：把跨 chunk 的 CSI/OSC/DCS/APC/SS3/meta escape、SGR mouse、bracketed paste 和高字节 meta 输入拆成完整 data/paste 事件，并提供 flush/clear/destroy seam；当前已被 `TuiProcessTerminal` lifecycle seam 消费
- `TuiProcessTerminal` 提供上游 `terminal.ts` 的可测试 lifecycle seam：通过 `ITuiProcessTerminalTransport` 抽象 raw mode、stdin resume/pause、resize、Windows VT input、terminal dimensions 和 output write；覆盖 bracketed paste、Kitty keyboard protocol query/enable/disable、modifyOtherKeys fallback、drain input、ANSI cursor/clear/title 操作和 `TAU_TUI_WRITE_LOG` / `PI_TUI_WRITE_LOG` 诊断写日志；当前仍缺真实 TTY/PTY smoke、硬件 cursor 和 CodingAgent 主屏接管
- `TuiSelectList` 提供上游 selector 家族需要的基础单选列表：过滤、选中态、j/k/方向键/Home/End/PageUp/PageDown/Enter/Esc/Ctrl-C、描述列对齐、滚动提示和 footer hint 行
- `TuiMultiSelectList` / `TuiMultiSelectSession` 提供 scoped-model selector 需要的多选列表：filter、Enter/Space toggle、Ctrl+A enable all、Ctrl+X clear、Ctrl+P provider toggle、Alt+Up/Down reorder、Ctrl+S save、Esc/Ctrl-C cancel，并保留 null = all enabled 的选择语义
- `TuiSettingsList` / `TuiSettingsListSession` 提供上游 `components/settings-list.ts` 的库层和 session foundation：label/value 对齐、description wrapping、可选搜索、Enter/Space value cycle、submenu delegate / done callback、Esc/Ctrl-C cancel、滚动提示、宽度截断和变更/取消结果返回；当前已被 `Tau.CodingAgent` 产品级 `/settings select` 主设置面复用，完整 upstream submenus/focus stack 仍属后续
- `TuiDiffRenderer` / `TuiRenderFrame` / `TuiRenderOperation` 提供纯函数式差分渲染计划：首帧/强制/宽高变化走 full redraw，稳定尺寸下只返回 changed/cleared lines
- `ITuiRenderSurface` / `TuiOverlayHost` / `TuiSelectorSession` / `TuiSettingsListSession` 提供单个 focused input component 的可测试 render/input loop：初始渲染、按键读取、组件 input 分发、diff apply、selector select/change/cancel result；当前已被 `Tau.CodingAgent` `/theme select`、交互式 `/settings`、`/scoped-models`、`/model select`、`/auth select` 和 `/thinking select` selector 接线复用
- `InteractiveInputEditor` / `InteractiveConsoleSession` 已支持 app action result：Ctrl+P / Ctrl+Shift+P / Ctrl+L 不再被当成普通编辑键吞掉，而是返回可由宿主处理的 model cycle / model selector action，同时保留当前输入 draft
- `TuiAnsiRenderSurface` 提供最小 ANSI diff sink：full redraw 输出 synchronized output + clear/home + 全量行，稳定尺寸 line diff 输出 cursor-position + clear-line + replacement text
- `TuiMessageArea` / `TuiStatusBar` 提供消息区和状态区的库层 foundation：role prefix、wrap、continuation indent、bottom-anchored visible transcript、left/right status segment、右侧状态保留和窄宽截断
- `TuiScrollbackBuffer` 提供纯内存 viewport/scrollback foundation：append/replace/clear、`maxLines` oldest-line trim、height resize 后 offset clamp、line/page scroll、bottom-follow state 和 visible lines 计算；`Lines` 只暴露只读视图，避免外部绕过 trim/scroll invariant
- `TuiTranscriptViewport` 组合 `TuiMessageArea`、`TuiStatusBar` 和 `TuiScrollbackBuffer`，提供固定高度 transcript viewport：最后一行固定 status，消息区占剩余高度，支持 append/set/clear、line/page scroll、resize rewrap 和 bottom-follow 语义；当前仍是纯内存 foundation，不写 Console、不接 terminal host
- `TuiTranscriptViewportHost` 把 `TuiTranscriptViewport`、`ITuiRenderSurface` 和 `TuiDiffRenderer` 包成可复用 runtime host：每次 `Render()` 会按 surface width/height resize viewport，生成 `TuiRenderFrame`，diff 上一帧后把 `TuiRenderDiff` apply 到 surface；支持 append/set/clear messages、status 更新、line/page scroll、Home-style scroll top、End-style scroll bottom 和 reset frame。`TuiTranscriptSession` 在其上提供 start/stop、auto-render mutation、滚动键输入和可注入 key reader seam。它们仍只是 transcript runtime seam，不是完整 terminal lifecycle、overlay compositor、硬件 cursor 或 CodingAgent 主屏

当前这仍是 foundation + 若干 CodingAgent selector / app action 接线，已经能用 targeted tests 固定组件/render/selector/session/ANSI sink/message-status/scrollback/transcript viewport/viewport host/transcript session/loader/markdown/terminal-image/stdin-buffer/ProcessTerminal lifecycle foundation、Ctrl+P/Ctrl+Shift+P model cycle 行为和 Ctrl+L model selector 行为，并已把主题、settings、scoped models、model select、auth status、`/login` OAuth provider 选择、`/logout` OAuth provider 选择和 `/thinking select` thinking level 选择接回真实命令；但 `TuiScrollbackBuffer` / `TuiTranscriptViewport` / `TuiTranscriptViewportHost` / `TuiTranscriptSession` 尚未接管 `InteractiveConsoleSession`、`TuiAnsiRenderSurface`、CodingAgent 主屏或 selector overlay，完整上游 TUI host、overlay compositing、硬件 cursor、theme rendering、real TTY/PTY smoke、real terminal image smoke、Marked/highlight Markdown integration 和完整 OAuth login dialog/session / resource selector UI 仍是后续切片。

### Tau.CodingAgent

仍然是最完整的用户路径：

- 最小 CLI 宿主
- 基础 coding tools
- 与 `ModelCatalog` 对齐的 provider / model 默认解析（CodingAgent / WebUi / Mom 复用同一解析器）
- `RuntimeCodingAgentRunner.Create(provider, model, history)` 显式宿主工厂
- 运行中 steering / follow-up baseline：`ICodingAgentRunner.Steer(string)` / `FollowUp(string)` 暴露到 host，`CodingAgentHost` 在 active runner turn 期间消费可注入的 `ICodingAgentTurnInputSource`，生产 source 用非阻塞 `Console.KeyAvailable` 轮询把 Enter 映射为 steering、Alt+Enter 映射为 follow-up
- RPC mode baseline：`--mode rpc` 走严格 LF-delimited JSONL stdin/stdout，`CodingAgentRpcHost` 直接复用 runner / flat session / JSONL tree / settings seam，当前覆盖 `prompt`、`steer`、`follow_up`、`abort`、`new_session`、`get_state`、`get_settings`、`update_settings`、`set_model`、`cycle_model`、`get_available_models`、`set_thinking_level`、`cycle_thinking_level`、`set_auto_retry`、`abort_retry`、`bash`、`abort_bash`、`set_steering_mode`、`set_follow_up_mode`、`set_auto_compaction`、`switch_session`、`get_fork_messages`、`compact`、`fork`、`clone`、`get_session_stats`、`get_messages`、`get_commands`、`export_html`、`get_last_assistant_text`、`set_session_name`；`new_session` 会读取可选 `parentSession` 并写入 JSONL header metadata；`get_state` 返回 runner/settings backed `steeringMode`、`followUpMode` 和 `autoCompactionEnabled`；`get_settings` / `update_settings` 读写 Tau 当前已真实生效的 settings 字段，并同步 runner model/thinking/queue mode、retry、auto-compaction 与 theme 设置状态；`bash` 通过可注入 `ICodingAgentShellRunner` 后台执行 shell 命令并返回 output/exitCode/cancelled/truncated，`abort_bash` 只取消当前 shell 命令；它是 Tau-native headless baseline，不等于完整上游 extension UI sub-protocol、streamed bash output、settings selector UI / theme selector / terminal / packages 等全量配置面和 full command parity
- 本地 session 持久化：继续保留 Tau 平面 snapshot JSON（默认 `./.tau/coding-agent-session.json`，`TAU_CODING_AGENT_SESSION_FILE` 可覆盖），同时新增上游风格 JSONL tree session（默认 `./.tau/coding-agent-session.jsonl`，`TAU_CODING_AGENT_TREE_SESSION_FILE` 可覆盖；如果 `TAU_CODING_AGENT_SESSION_FILE` 指向 `.jsonl`，则把它当 tree session 使用并关闭平面 snapshot 写入）；启动时优先恢复显式 tree session 或非空 tree session，否则回落 flat JSON
- JSONL session tree 基线：`CodingAgentTreeSessionStore` 写入 `type=session` header、append-only `message` / `model_change` / `session_info` / `label` / `compaction` / `branch_summary` / `auto_retry_start` / `auto_retry_end` entries，每个 entry 带 `id` / `parentId` / `timestamp`；写出时默认按 `TAU_CODING_AGENT_REDACT_SECRETS` 复用 `JsonlSecretRedactor` 递归脱敏 string value，object key、number、bool 和 null 保持原样，`TAU_CODING_AGENT_REDACT_SECRETS=0` 可关闭；header 的 `cwd` 和 `parentSession` 会进入 tree summary，host 通过 runner message diff 同步当前 branch，保留 flat JSON 兼容入口
- session lifecycle：`/new` 清空当前 runtime messages 和 display name，并在 tree 中追加新的 root marker；`/metadata [entry-id]` 输出 JSONL session header、cwd/parent/leaf、entry/message/branch/label 统计、最近 metadata entries，或指定 entry 的 id/type/parent/timestamp/path/depth/children/label/message preview/model/session/label/compaction/branch-summary/retry/tree-state 关键字段；`/session` 输出 flat stats、估算 token/context usage、auto-compaction threshold budget、当前 retry policy、tree file/leaf/entry/message/branch/label/cwd/parent 信息；`/tree [max entries] [default|no-tools|user-only|labeled-only|all] [--label-time] [--search query]` 输出当前 JSONL tree 的短 id / parent / 当前 branch / leaf 标记 / label，可按上游 tree selector 的核心过滤模式隐藏 bookkeeping、tool-only assistant 和 tool result，可按 entry text / label / branch summary / 基础 metadata 搜索，并在 header 显示 cwd / parent metadata 和最新 label timestamp；`/tree --interactive` 提供 j/k/方向键移动、g/G/Home/End 跳转、Enter 选择、q/Esc 取消、`/` overlay search、`f` filter cycle、n/N 搜索结果跳转、普通 Left/Right 分页、Ctrl/Alt+Left 折叠或跳到上一个分支段、Ctrl/Alt+Right 展开或跳到下一个分支段、Space 折叠/展开当前 entry descendants，并显示 selected entry type/depth/branch/leaf metadata；退出 interactive navigator 时会追加 `tree_state` metadata entry 记录 collapsed entry ids，下一次启动优先恢复 session-scoped fold snapshot；settings `treeCollapsedEntryIds` 仍作为兼容 fallback 读取/写回；未显式传过滤模式时会读取 settings `treeFilterMode`，无效值回退 `default`；`/label <entry-id> [label | clear]` 以 append-only label entry 标记历史节点；`/fork <entry-id>` 从历史 entry 切出新 branch 并恢复该 branch messages；`/fork <entry-id> --summarize [instructions]` 会先汇总被离开 branch 到共同祖先之间的消息，调用当前模型生成结构化摘要，把 `branch_summary` entry 挂在目标 entry 下并写入 `fromId/readFiles/modifiedFiles`，恢复 runtime 时把摘要作为 context user message 注入；`/clone` 把当前 active branch 导出到新的 `coding-agent-sessions/*.jsonl` session 并立即切换过去，空 branch 返回 `Nothing to clone yet`；`/resume [latest | path.jsonl]` 恢复 JSONL session；当前仍不是完整上游 TreeSelector、自动 branch switching summarization hooks、多选和完整 TUI metadata inspector
- 最小 session display name：`/name [display name | clear]` 查看、设置或清空当前 session display name，并随 session store 持久化
- 最小退出命令：`/quit` 与文本 `exit` 一样结束当前 CLI loop，不调用 runner，不进入 LLM conversation
- 最小帮助命令：`/help` 列出当前 Tau 已支持的本地命令，避免用户只能靠文档猜命令面
- 最小 hotkeys 命令：`/hotkeys` 列出当前交互式 editor 注入的 `IKeyBindingMap`，输出 action、按键组合和说明；默认包含 Ctrl+P / Ctrl+Shift+P model cycle action 和 Ctrl+L model selector action；自定义 keybindings 和 `EditorAction.None` 禁用默认绑定会反映到输出；print/RPC/redirected 这类没有 editor 的模式返回不可用错误；完整上游 app/session/tree/extension shortcut registry 仍未移植
- 最小 reload 命令：`/reload` 重读 settings，并把 retry policy、default thinking level 与 steering/follow-up queue mode 同步到当前 host/runner；重读 JSON extension commands/resources，更新 extension-contributed prompt/skill/theme paths；随后重载 prompts、skills、context files、theme status 和交互式 editor keybindings。`RuntimeCodingAgentRunner.RefreshSystemPromptResources(...)` 只会在 runner 使用生成 system prompt 时刷新 skill/context inventory，自定义 system prompt 不被覆盖。当前 context files 已有 `AGENTS.md` / `CLAUDE.md` baseline；theme loader/status baseline 已能发现 built-in、用户、项目、env/CLI 与 extension-contributed theme paths，并报告 load diagnostics；完整 theme selector、TUI theme rendering、theme file watcher、TypeScript extension runtime lifecycle 和完整 resource selector 仍未完成。
- 最小 changelog 命令：`/changelog [count|all]` 读取 Tau 本地 release notes 表（默认当前目录向上查找 `docs/releases/feature-release-notes.md`，可由 `TAU_CODING_AGENT_CHANGELOG_FILE` 覆盖），输出日期、功能域、用户价值和变更摘要；当前是 Tau-native CLI baseline，不等于上游启动 changelog 更新提醒、`collapseChangelog` 设置或安装/更新 telemetry parity
- 最小 settings 命令：`/settings [current|path|select]` 在真实交互式 editor 会话中打开 `TuiSettingsList` 主设置面，可直接写回 auto-compaction、terminal show images / clear on shrink、image auto-resize / block images、show hardware cursor、editor padding、autocomplete max visible、steering/follow-up mode、tree filter、thinking level、quiet startup、collapse changelog、install telemetry，并可从同一 settings-list 入口打开 scoped models 或 theme 既有子 selector；没有 selector 的会话裸 `/settings` 保留摘要输出，`/settings current` 固定展示当前 settings 文件路径和有效配置摘要，`/settings path` 只输出路径。当前是 Tau-native SettingsList main surface baseline，不等于完整上游 package/transport/shell path/npm settings、完整 settings submenus、image ingestion/runtime terminal 行为或完整 TUI edit parity
- 最小 thinking 命令：`/thinking [current|select|cycle|off|minimal|low|medium|high|xhigh]` 可查看、交互式选择、循环、显式设置或关闭当前 runner reasoning level，并把默认值写回 settings `defaultThinkingLevel`；`select` 通过可注入 `CodingAgentThinkingSelector` 复用 `TuiSelectList` / `TuiSelectorSession` / `TuiAnsiRenderSurface`，生产入口只在真实交互式 editor 存在时启用，取消选择不修改 settings；运行态已按当前模型能力 clamp：非 reasoning 模型归一 off，不支持 xhigh 的 reasoning 模型把 xhigh 归一 high。当前是 Tau-native thinking selector + capability clamp baseline，不等于完整上游全 settings UI parity
- 最小 theme 命令：`/theme [current|list|select|set|clear] [name]` 查看当前主题、列出可发现主题、用 TUI selector 交互式选择、校验并持久化 settings `theme`，或清空为默认 `dark`；列表复用 `CodingAgentThemeStore` 的 built-in、用户、项目、env/CLI 和 extension-contributed theme paths 以及 load diagnostics；`select` 通过可注入 `CodingAgentThemeSelector` 复用 `TuiSelectList` / `TuiSelectorSession` / `TuiAnsiRenderSurface`，生产入口只在真实交互式 editor 存在时启用，取消选择不修改 settings。当前是 Tau-native theme selector baseline，不等于完整上游 theme rendering 或 theme file watcher parity
- 最小 scoped models / model selector / model cycle 命令面：`/scoped-models [current|select|set|add|remove|clear|all] [provider/model[:thinking] ...]` 查看或配置持久化模型切换 scope；显式 scope 写入 settings `enabledModels` 并保留输入顺序，条目可带 `:off|minimal|low|medium|high|xhigh` per-entry thinking suffix，`clear` / `all` 清空该字段表示 all enabled / no filter；`/scoped-models` 仍是配置入口，基于全部注册模型维护 scope，允许用户预先加入尚未配置凭证的 provider/model；真实交互式 editor 会话中裸 `/scoped-models` 或 `/scoped-models select` 会打开 `CodingAgentScopedModelsSelector`，复用 `TuiMultiSelectList` / `TuiMultiSelectSession` / `TuiAnsiRenderSurface` 支持 filter、toggle、provider toggle、enable all、clear、reorder、save/cancel，并在保存时保留既有 suffix metadata；实际模型使用入口已经收口到已配置凭证模型：`/model select [search]`、交互式裸 `/model`、Ctrl+L、Ctrl+P / Ctrl+Shift+P、显式 `/model`、`/provider` 和 RPC `get_available_models` / `set_model` / `cycle_model` / settings model update 都只展示、切换或接受 auth-configured provider/model；Ctrl+P / Ctrl+Shift+P 与 RPC `cycle_model` 切到带 suffix 的 scoped model 时会同步 runner/default thinking，并按目标模型能力 clamp；模型 selector 在有 scoped 候选时默认显示 scoped scope，可用 Tab 在 `scoped` / `all` 已配置凭证候选之间切换，顶部显示 `Model Selector` / `Search:` 轻量 chrome，普通字符输入会更新过滤，Backspace 会回退过滤，列表下方显示 `Model Name: ...`，底部显示 `Only showing models with configured auth`，选择后保存默认 provider/model、重新 clamp 当前 thinking 并同步 tree session。当前是 Tau-native CLI/settings/TUI selector + model selector/cycle + provider auth filtering + selector footer/scope/detail/search chrome + per-entry thinking + 模型能力 clamp baseline，不等于完整上游 theme/dynamic-border/terminal-host parity 或 per-entry thinking 编辑 UI parity
- prompt template 基线：`CodingAgentPromptTemplateStore` 会加载用户目录 `~/.tau/prompts`、项目目录 `./.tau/prompts` 和 `TAU_CODING_AGENT_PROMPT_PATHS` 指定的 `.md` 文件/目录；`/prompts` 列出可用模板，非内置 slash 输入命中模板名时会在发给 runner 前展开 `$1`、`$@`、`$ARGUMENTS` 和 `${@:N[:L]}` 参数占位
- skill command 基线：`CodingAgentSkillStore` 会加载用户目录 `~/.tau/skills`、项目目录 `./.tau/skills` 和 `TAU_CODING_AGENT_SKILL_PATHS` 指定的 skill 文件/目录；`/skills` 列出 `/skill:<name>` 命令，`/skill:<name> args` 会把 `SKILL.md` body 包装成上游风格 `<skill name="..." location="...">` block 后发送给 runner；`disable-model-invocation: true` 的 skill 不进入默认 system prompt inventory，但仍可显式命令调用
- extension command/resource/diagnostics 基线：`CodingAgentExtensionCommandStore` 会加载用户目录 `~/.tau/extensions`、项目目录 `./.tau/extensions` 和 `TAU_CODING_AGENT_EXTENSION_PATHS` 指定的 `.json` 文件/目录；`/extensions` 列出本地 extension slash commands，并显示 extension JSON 文件路径、每个文件的 command/prompt/skill/theme resource 计数、重复 command 的解析名、prompt/skill/theme resource paths，以及坏 JSON、不可读文件、缺失显式路径等 load diagnostics；非内置 slash 输入会先查 extension command，再查 skill/prompt，保持上游“extension command 先于 input/skill/template”的执行顺序；JSON command 支持单命令或 `commands[]`、`name`、`description`、`argumentHint`、`response`、`prompt`、`sendToRunner`，重复 name 会按上游规则解析成 `name:1`、`name:2`；同一 JSON 还可通过 `resources.promptPaths` / `resources.skillPaths` / `resources.themePaths` 或顶层 `promptPaths` / `skillPaths` / `themePaths` 贡献 prompt/skill/theme 发现路径，路径相对 extension JSON 所在目录解析；当前这是 Tau-native 声明式 command/resource/load diagnostics baseline，不等于已移植上游 TypeScript extension runtime、custom tools、events、theme selector / TUI theme rendering 和 interactive resource selector
- 最小 copy 命令：`/copy` 复制最后一条 assistant 文本消息到系统剪贴板；剪贴板写入通过 `ICodingAgentClipboard` 抽象隔离，便于测试和后续替换
- export / share 命令：`/export [path]` 默认导出 standalone HTML transcript；显式 `.html/.htm` 路径同样导出 HTML（session header、消息、thinking、tool call、tool result、图片内容、branch outline、cwd / parent metadata，并内嵌可下载 JSONL）；HTML 会按 current branch JSONL 渲染 `message`、`session_info`、`model_change`、`label`、`compaction` 与 `branch_summary` timeline entries，消息节点绑定 JSONL entry id 和 label badge，branch outline 可滚动到 message / model / label / compaction / branch summary entry，并支持 `default/no-tools/user-only/labeled-only/all` 过滤和搜索；branch summary timeline 会展示 summary、source entry、read files 和 modified files，提供 per-message copy link，并支持 `targetId` deep link 自动定位和高亮；文本内容中的 fenced code block 会拆成独立 `code-block` / `<code>` 区块；普通文本继续安全转义，backtick inline code span 会渲染为 `<code class="inline-code">`，并会把 Markdown-style `[label](http/https...)` 与裸 `http(s)` URL 渲染成外链，code fence 和 inline code 内不链接，非 http(s) scheme 仍按文本输出；如果普通文本段检测到 heading/list/blockquote block marker，则会渲染为轻量 rich-text block，支持 `h1`-`h6`、`ul/ol/li` 与 `blockquote`，嵌套 list 会把子 `<ul>/<ol>` 保留在父 `<li>` 内，并复用 inline code/link 安全输出；普通文本里的 `**strong**` / `__strong__` 和 `*emphasis*` / `_emphasis_` 会分别渲染为 `<strong>` / `<em>`，inline code、code fence 和单词内部下划线不会触发 emphasis；普通文本段中的 Markdown pipe table（header + separator + data rows）会渲染为可横向滚动的 `<table>`，单元格继续复用 inline code/link/strong/emphasis 安全输出，code fence 内表格文本保持代码块；普通文本列表项里的 `[ ]` / `[x]` task marker 会渲染为 disabled checkbox，任务文本继续复用 inline code/link/strong/emphasis 安全输出，code fence 内任务列表文本保持代码块；图片内容继续以内嵌 data URI 渲染，并补充 mime type 与估算字节数 caption；长 tool result 文本会默认折叠为 `<details class="tool-result-fold">` 并保留完整输出；tool call arguments 中可解析 JSON 会格式化为 `code-block` / `<code data-language="json">`，不可解析参数仍按原始 `<pre>` 安全转义，超长参数会默认折叠为 `<details class="tool-call-arguments-fold">` 并保留全文；`.jsonl` 路径导出当前 branch 为独立 JSONL session，并保留 parent session path；其他路径继续导出 Tau 平面 session snapshot JSON；`/share` 会复用同一 HTML transcript exporter，检查 `gh auth status`，再用 `gh gist create --public=false` 创建 secret gist，预览 URL 默认沿用 pi-compatible viewer 且可通过 `TAU_SHARE_VIEWER_URL` 覆盖；当前仍未实现上游完整 Markdown/highlight renderer、custom tool renderer、richer HTML template 和 Tau 专属 share viewer
- import 命令：`/import <path>` 对非 `.jsonl` 路径严格导入 Tau snapshot JSON 并恢复 messages/provider/model/display name，同时把导入结果同步到 tree；对 `.jsonl` 路径等价于 resume JSONL session；无效 JSON 或缺失文件直接报错，不回落空 session
- 最小 settings / model selection：`/settings`、`/theme`、`/model`、`/provider`、`/models`、`/providers`、`/thinking`、`/scoped-models`，默认模型、主题、thinking level 和模型 scope 写入 `./.tau/coding-agent-settings.json`，也可通过 `TAU_CODING_AGENT_SETTINGS_FILE` 指定路径；`/settings [current|path|select]` 会在交互式会话打开 `TuiSettingsList` 主设置面，或在 summary/path 模式展示 settings 文件事实；`/thinking select` 在交互式会话打开 thinking selector，在无 selector 会话返回明确 unavailable；`/scoped-models` 在交互式会话打开多选 selector，在无 selector 会话继续输出摘要；`/model select [search]` 和交互式裸 `/model` 在有 selector 的会话中打开模型单选 selector，无 selector 的裸 `/model` 继续显示 current model；Ctrl+P / Ctrl+Shift+P 在空闲输入 prompt 会复用 `enabledModels` scope 或全部可用模型循环切换并保存默认模型，Ctrl+L 会打开同一模型 selector 并保留输入 draft；同一 settings 文件支持上游兼容的 `treeFilterMode` 字段作为 `/tree` 默认过滤模式，支持 Tau-native `retryMaxAttempts` / `retryBaseDelayMilliseconds` 字段作为 transient retry 策略，支持 `defaultThinkingLevel` 字段作为启动时的默认 reasoning/thinking level，支持 `steeringMode` / `followUpMode` queue mode 启动恢复，支持 `autoCompactionEnabled` boolean override，支持 `theme` 字段记录当前主题名，支持 terminal/image/editor/changelog/telemetry 相关 settings-list 字段写回，并支持上游兼容 `enabledModels` 有序数组作为模型切换 scope；旧 `queueMode` 读入时迁移为 `steeringMode`，`enabledModels=null` 或空值表示 all enabled / no filter；`/model` / `/provider` / `/retry` / `/thinking` / `/theme` / `/settings` selector / `/scoped-models` / Ctrl+P model cycle / Ctrl+L model selector 保存设置时会保留其他非本命令设置
- 最小 auth 管理入口：`/auth [current|select|provider]` 查询当前或指定 provider 凭证来源；真实交互式 editor 会话中 `/auth select` 会打开 `CodingAgentAuthSelector`，复用 `TuiSelectList` / `TuiSelectorSession` / `TuiAnsiRenderSurface` 展示 provider configured/missing、source、OAuth/login capability，并在选择后只输出所选 provider status；`/login [select|provider]` 在 selector 可用的交互式会话中会先列出当前注册且有 OAuth provider 的 provider，选择后调用现有 OAuth login flow 并保存到 `auth.json`，无 selector 的裸 `/login` 继续使用当前 provider，显式 `/login <provider>` 保持兼容；`/logout [select|provider]` 在 selector 可用的交互式会话中会先列出当前有本地 OAuth credential 且注册了 OAuth provider 的 provider，选择后删除本地 `auth.json` 中对应 provider 的 credential entry，无 selector 的裸 `/logout` 继续使用当前 provider，显式 `/logout <provider>` 保持兼容；不会回显密钥，也不会修改环境变量或 `models.json` credential 配置；当前仍不是完整上游 OAuth login dialog/session parity、credential refresh UX 或真实外部 OAuth e2e
- compaction baseline：`/compact [instructions]` 使用当前模型生成会话摘要，重置 runtime state，并把摘要作为单条 user message 保留到 flat session store；JSONL tree 会追加上游风格 `compaction` entry，记录 `summary`、`firstKeptEntryId`、估算 `tokensBefore`、`fromHook`、Tau-native `isSplitTurn` 和 `turnPrefixSummary` baseline；默认从当前 compaction boundary 后优先按 `TAU_CODING_AGENT_COMPACT_KEEP_RECENT_TOKENS` 的 token budget 保留 recent messages，再回落到最近 4 条 message，可用 `TAU_CODING_AGENT_COMPACT_KEEP_RECENT_MESSAGES` 调整，恢复 branch 时会按上游顺序重建 summary message、retained recent messages 和 compaction 后的新消息；如果 retained cut point 落在一个 user turn 中间，恢复时会把确定性的 split-turn prefix context 拼入 summary message，HTML transcript 也会展示并搜索该字段；`TAU_CODING_AGENT_AUTO_COMPACT_TOKENS` 可开启普通消息执行前的自动 token-threshold compaction，`TAU_CODING_AGENT_AUTO_COMPACT_INSTRUCTIONS` 可补充摘要指令，自动触发会写 `fromHook=true`；context overflow 错误会触发 Tau-native compact-and-retry baseline：先恢复失败回合前 snapshot，再 compact、记录 `fromHook=true` compaction entry、更新回滚基线并重试同一输入；`/session` 会显示当前估算 token、模型 context window 和 auto threshold 剩余量；当前仍未移植上游 LLM-generated split-turn summarization、compaction extension events 和 cancellation UI parity
- 普通回合 rollback / retry baseline：`CodingAgentHost` 在调用 runner 前记录当前 messages/provider/model/name snapshot；如果 runner 抛异常、普通回合被取消，或 runtime 返回带错误消息的 `AgentEndEvent`，host 会恢复回合前 snapshot，再持久化 flat JSON 和 JSONL tree，避免失败输入、半截 assistant 或工具结果污染 session；生产入口按 settings 优先、`TAU_CODING_AGENT_AUTO_RETRY_ATTEMPTS` / `TAU_CODING_AGENT_AUTO_RETRY_BASE_DELAY_MS` env 兜底读取 host-level transient error retry 策略，当前识别 overloaded、rate limit、429、5xx、service unavailable、network/connection/timeout/fetch 等错误；`/retry [current|default|off|<max attempts> [base delay ms]]` 可查看、关闭、恢复 env/default 或设置 attempts/base delay，并在同进程立即更新 host retry 策略；`/thinking [current|select|cycle|off|minimal|low|medium|high|xhigh]` 可查看、交互式选择、设置、循环或关闭当前 runner 的 reasoning level，并按当前模型能力 clamp 后把默认值写回 settings；retry 会写入 Tau-native JSONL `auto_retry_start` / `auto_retry_end` audit entries，成功时先同步成功 attempt 的 user/assistant messages 再追加 retry end，失败或耗尽时只保留 retry audit，不持久化失败输入；retry delay 被取消时会显示明确状态、写 `Retry cancelled` end audit，并回滚失败输入；context overflow 单独走 compact-and-retry baseline，不参与普通 transient retry；当前仍不是上游完整 RPC/settings UI 控制或完整 retry cancellation UI parity；运行中 steering / follow-up 目前已有 CLI baseline 和 RPC baseline，尚未做成完整 TUI overlay 或 keybinding hints
- `CodingAgentCommandRouter` 承载 slash command 解析、本地命令执行、model selector action 和 model cycle action；`CodingAgentHost` 保持为输入循环、UI 渲染、runtime event、退出信号、运行中 steering/follow-up 转发和 session 持久化宿主；host 会把当前 session store path、tree controller、settings store、retry option update callback、extension resource state、context file store、theme store、auth/theme/settings/thinking/scoped-models/model selector seam、editor key binding map、keybinding reload callback 和可测试 changelog store 注入 router，供 `/session`、`/tree`、`/label`、`/fork`、`/fork --summarize`、`/clone`、`/resume`、`/auth`、`/login`、`/logout`、`/changelog`、`/settings`、`/theme`、`/model select`、`/scoped-models`、Ctrl+P/Ctrl+Shift+P model cycle、Ctrl+L model selector、`/retry`、`/thinking`、`/hotkeys` 和 `/reload` 复用同一事实源
- `CodingAgentCommandCatalog` 维护当前已支持 slash command 的名称、usage 和描述，`/help` 与参数错误共用同一事实源，避免继续在 router 内散落命令字符串；当前已支持 `/help`、`/reload`、`/hotkeys`、`/settings`、`/theme`、`/name`、`/copy`、`/files`、`/export`、`/share`、`/import`、`/new`、`/session`、`/metadata`、`/tree`、`/label`、`/fork`、`/clone`、`/resume`、`/quit`、`/model`、`/provider`、`/models`、`/providers`、`/scoped-models`、`/prompts`、`/skills`、`/extensions`、`/auth`、`/login`、`/logout`、`/changelog`、`/retry`、`/thinking`、`/history`、`/find`、`/clear`、`/compact`

这个显式 runner 工厂现在也是 `WebUi / Mom` 继续往宿主化推进的关键共享边界；本地 session/settings/auth-status 入口则为后续真实 OAuth、slash command、compaction 与 WebUi/Mom 共享会话语义打底。

### Tau.WebUi

当前实现为第二层 Web 宿主：

- 内嵌 HTML/JS 的单页聊天入口，支持 NDJSON streaming、附件 preview/send、tool timeline 和 client-side Markdown rendering
- health/status/catalog/session/messages/auth API
- session 本地持久化到 `output/webui-sessions.json`
- 每个 session 可独立配置 provider / model / title
- `PUT /api/sessions/{id}` 支持会话设置更新和 title rename
- session delete/export/import 已接入，前端会记住 last-opened session 并在 reload 时恢复仍存在的会话；`GET /api/sessions/{id}/export.jsonl` 会把当前 WebChat DTO 导出为 WebUi-local 线性 JSONL transcript，首行 `type=session` header，后续 `type=message` entries 使用稳定 `message-000001` id 和线性 `parentId`；JSONL export 和 `POST /api/sessions/import.jsonl` 默认复用 `JsonlSecretRedactor` 递归脱敏 JSON string value，保留 field key / number / bool / null，可用 `TAU_WEBUI_REDACT_SECRETS=0` 关闭；import 会校验同一线性 transcript 并导回新的 WebChat session，支持 `application/x-ndjson` / `application/jsonl` / `application/json-lines` / `text/plain` / 空 content type，unsupported content type 返回 `415 application/problem+json`，parser error 返回 `400 application/problem+json` 且包含稳定 `code`、可选 `line` 和 detail；前端 import input 已支持 `.jsonl` 并按扩展名选择 JSON 或 JSONL endpoint
- `POST /api/sessions/import.coding-agent-jsonl/preview` 提供 CodingAgent JSONL session 只读预览：解析 session header、message timeline summary 和 tree metadata，返回 leaf entry、root/branch point/branch entry/message/label counts、entry type histogram、current branch entry ids，以及每个 entry 的 id/type/parent/timestamp/depth/child count/current-leaf/current-branch/label metadata；query string 支持 `search` 和 `currentBranchOnly` 只过滤 preview `Messages`，并返回 filter audit（总 message 数、命中数、命中 entry ids）。preview 和 conservative import 默认按 `TAU_WEBUI_REDACT_SECRETS` 处理 JSON string value，malformed JSONL 返回 `400 application/problem+json`，title 固定为 `Invalid CodingAgent JSONL preview`。preview endpoint 不持久化、不导入、不替换 `WebChatStore`
- `POST /api/sessions/import.coding-agent-jsonl` 提供 preview-derived conservative import：复用同一 parser，把 timeline message 保守转换成 WebChat message 并通过现有 `ImportSession` 持久化；user 保持 user，assistant/toolResult 等非 user 统一导入为 assistant，tool call/result/thinking/image 只写入可审计文本标记，避免生成空消息。响应会返回导入后的 `session`、source tree metadata 和 source audit/warnings；该 endpoint 不保留 CodingAgent branch/tree 结构，也不替换 `WebChatStore`
- `WebUiApplication.MapWebUiEndpoints()` 让生产入口和 endpoint tests 共用同一套 Minimal API route table
- `WebUiRunnerFactory` 通过显式 provider/model/history 驱动 runtime

当前短板：

- 当前 WebUi session 仍是 WebChatStore DTO；虽已有 WebUi-local 线性 JSONL transcript export/import roundtrip、CodingAgent JSONL 只读 preview tree metadata 和 conservative import，但还不是 CodingAgent JSONL branch/session-tree 导入或持久化语义
- 还没有 auth login 的完整交互管理层
- richer rendering 仍未达到完整上游 renderer/template parity

### Tau.Mom

当前实现为结构化本地委派宿主：

- `MomOptions` 统一 inbox/outbox/archive/poll/default provider/default model/default workdir 配置
- `MomOptions` 同时暴露 `EventsPath`、`RunningStatusStaleAfterMinutes`、`SlackBackfillEnabled`、`SlackBackfillMaxPages`、`SlackBackfillPageSize` 与 `SlackChannelQueueLimit`，配置会在 `Program.cs` 中规范化为绝对路径/实际数值
- `FileDelegationProcessor` 扫描 `inbox/*.txt|*.md|*.json`
- `MomEventProcessor` 扫描 `events/*.json`，把上游 mom 风格的 `immediate`、`one-shot`、`periodic` 事件转换成 inbox `.json` 委派请求
- `MomLocalDelegationFlow` 统一本地 `events -> inbox request -> runner -> outbox/status/log/archive` 流程，`Worker` 后台轮询和 `Program --once` 复用同一条 flow，避免两条入口分叉
- event `.json` 支持 `type/channelId/text/at/schedule/timezone/provider/model/title/metadata/attachments`；入队 prompt 形如 `[EVENT:file:type:schedule] text`，metadata 会补 `event/eventType/eventFile/channel/user/userName/ts/date`
- `channelId` 会映射到 `DefaultWorkingDirectory/<channelId>`，从而复用现有 `MEMORY.md`、`log.jsonl` 和 `status.json` channel/workspace seam；无效事件会移入 `archive/invalid-events`
- `periodic` 当前实现五段 cron 的本地匹配（`*`、`*/n`、单值、范围、逗号）和 timezone 转换，同一事件文件同一分钟只入队一次；它是本地 worker seam，实时 Slack 消息则走独立的 channel queue dispatcher
- `MomChannelMessage` / `MomChannelAttachment` 是 file delegation、local events、Slack event mapper 和 Slack startup backfill 共用的 Slack-compatible envelope：统一承载 `channel/user/userName/displayName/ts/threadTs/text/attachments/provider/model/title/metadata`，再生成 `DelegationRequest`，避免后续 Slack/backfill/queue 各自拼 request metadata
- `IMomChannelTransport` / `IMomChannelResponder` / `MomChannelMessageProcessor` 固定 Slack adapter 的输入输出边界：transport 读入 channel message，responder 负责响应/thread/typing/upload，processor 负责 busy-state、true cancellable stop、attachment staging、status/log writeback 与 runner 调用；`SlackEventMapper` 已固定 `app_mention` / DM / skip / mention stripping / file metadata 到 `MomChannelMessage` 的 receive-side 转换规则；`SlackSocketModeTransport` 已固定 `auth.test` / `apps.connections.open` / WebSocket text frame / envelope ack / reconnect 接缝；`SlackBackfillService` 已固定 startup `conversations.history` 回填：只处理已有 `log.jsonl` 的 channel，按 latest ts 设置 oldest，最多分页 3 次，过滤重复/其他 bot/非 `file_share` subtype，按时间顺序写回 log，不触发 delegation；`SlackWebApiResponder` 已先用 `HttpClient` 固定 `chat.postMessage` / `chat.update` / `chat.delete` / `files.uploadV2` 的真实 Web API 调用契约和 token 脱敏错误边界；`MomChannelMessageProcessor` 会在 responder 支持 runtime response 时先创建 `_Thinking_ ...` 占位消息，完成后更新同一条消息，遇到 `[SILENT]` 响应则删除占位并避免追加最终回复；`SlackAttachmentDownloader` 已固定 bot-token private file download；`MomChannelQueueDispatcher` 已固定 per-channel 顺序处理、pending queue limit、不同 channel 独立推进和 stop bypass；`MomChannelRunRegistry` 已固定当前进程内 active run 的 linked cancellation token，stop 命令会取消当前 runner、返回 `_Stopping..._`，完成后写 `cancelled` status 并回复 `_Stopped_`
- `.txt/.md` 请求继续兼容为纯 prompt
- `.json` 请求支持 `prompt/provider/model/workingDirectory/title/metadata/attachments`
- `title` 进入 runner session name，`metadata` / `attachments` 会被包装成 `<delegation_context>` 注入实际 prompt
- `ChannelAttachmentStore` 会把本地存在的 request/event attachment staging 到 `workingDirectory/attachments/<timestamp>_<filename>`，写入 `attachments/attachments.jsonl` 记录 `original/local/source`，并让 channel log 保留上游 mom 风格的 `original/local` 附件元数据；Slack private file URL 会先通过 `SlackAttachmentDownloader` 使用 bot token 下载到同一 attachment store，再进入 delegation request
- `RuntimeDelegationAgentRunner` 在 `<delegation_context>` 之前还会注入 `<mom_runtime_context>`，把本地 Mom 的 workspace/channel layout、events 文件格式、attachment manifest、memory/log/status 路径和 `[SILENT]` 事件约定写成运行时前缀；这是本地 worker 的语义，不代表 Slack adapter 已接通
- `ChannelWorkspaceLayout` 统一定义并创建本地 workspace/channel 路径：workspace `MEMORY.md` / `SYSTEM.md` / `skills/` / `events/`，channel `MEMORY.md` / `log.jsonl` / `context.json` / `status.json` / `last_prompt.jsonl` / `attachments/` / `scratch/` / `skills/`；这样后续 Slack adapter、sandbox 和 tool delegation 不再各自拼路径字符串
- `SYSTEM.md` 的非空内容会以 `system_configuration_log` 注入 `<delegation_context>`；workspace/channel `skills/**/SKILL.md` 会被解析为上游 Agent Skills 风格的 `<available_skills>`，并按 sandbox 映射 skill file path。Mom skills 不是额外 tool 注册；agent 读取 `SKILL.md` 后通过 `bash/read/write/edit` 使用脚本
- `MomOptions.Sandbox` 支持 `host` 和 `docker:<container>` 配置；当前已实现 host executor、docker command/path translation seam、Docker validate/exec 可注入 `IMomSandboxProcessRunner` seam、`--validate-sandbox` 显式 validation 入口、workspace path authority 和 `bash/read/write/edit/attach` 五个上游同名 Mom tools，默认 runner 会使用 Mom tool set 而不是通用 CodingAgent 工具名。真实 Docker container smoke 仍是后续切片
- `ChannelPromptDebugStore` 会在每次调用 runner 前写 `workingDirectory/last_prompt.jsonl`，记录 mom runtime context、delegation context、实际 runner input、恢复的 session messages、当前 user prompt、attachment count 和 image attachment count；默认通过 `TAU_MOM_REDACT_SECRETS` 控制常见 secret pattern 的 JSON string value redaction（`0/false` 可关闭）；这是对齐上游 mom `last_prompt.jsonl` 的本地可观测 seam，不代表图片附件已经以多模态内容传入 runner
- `workingDirectory/MEMORY.md` 与其父目录的 `MEMORY.md` 会以 current/parent workspace memory 注入同一段上下文
- `workingDirectory/log.jsonl` 会记录本地 file delegation 的用户请求与 bot 结果；写入时默认对 JSON string value 中的常见 secret pattern 做脱敏，读取 channel history 注入 prompt 前也会再次 redaction；最近非 bot 文本消息会以 channel history 注入同一段上下文，malformed 行、空文本和当前消息 `ts` 会被跳过
- `ChannelLogStore` 统一承载 `log.jsonl` append、history 读取和过滤语义，避免 runner / processor / 后续 Slack adapter 各自解析 channel log
- `workingDirectory/context.json` 保存 Tau-native channel session snapshot；`ChannelSessionStore.LoadMetadata()` 会暴露上一轮 provider/model/session name/message count，`MomModelSelectionResolver` 在请求未显式传 provider/model 时沿用同一 workdir 的 provider/model，显式传 `provider/model` 时优先使用请求值，并保留 `google` -> `google-gemini-cli` 归一；`RuntimeDelegationAgentRunner` 会在 delegation 前恢复上一轮 runtime messages/session name，并在完成后写回当前 runner messages/provider/model/session name。它只是本地 channel/workdir 的 multi-message carry-over seam，不是上游 JSONL session tree，也不代表真实 Slack session sync 已完成
- `workingDirectory/status.json` 记录当前或最近一次 delegation 的 `running/completed/failed/cancelled` 状态、请求文件、provider/model、workdir、开始/完成时间、错误和响应摘要；本地 worker 会跳过同一 workdir 内未过期的 `running` 状态，默认 60 分钟后视为 stale
- `ChannelStatusStore` 统一承载 `status.json` 读写与 running/stale 判定语义，避免 file worker 和后续 adapter 各自拼状态文件
- `IDelegationAgentRunner` 返回 `DelegationExecution`：response、结构化 `DelegationToolEvent`（phase/toolName/toolCallId/isError/durationMs）、stop reason、`DelegationUsage`（input/output/cache tokens 与可选总成本）、provider/model/workingDirectory/metadata
- `RuntimeDelegationAgentRunner` 会向 Tau runtime log sink 记录 Mom delegation 事件：已有 `delegation.start` / `delegation.end`，并补充 `response.start`、`tool.start`、`tool.end`、`response.end` 和 `usage`；字段包含 provider/model/workingDirectory、toolCallId/toolName/isError/durationMs、response stopReason/characters/preview，以及 token usage / service tier / 可计算总成本。该日志是本地可观测 baseline，不等于完整 trace、auth status、tool execution 或 pod operation 统一协议
- `RuntimeDelegationAgentRunner` 通过可注入的 `Func<provider, model, workingDirectory, attachFile, ICodingAgentRunner>` 工厂构造内核 runner，便于测试和后续 Slack/workspace/sandbox 适配层复用；默认路径会创建 Mom sandbox executor 与 `MomToolSet`
- 结果序列化到 outbox `.json`，包含 title、attachments、stop reason、结构化 tool events 与 usage
- 原始请求归档到 archive
- 支持 `--once`

当前短板：

- 仍未完成端到端 Slack parity：真实 Slack smoke 和真实 Slack session sync 仍缺
- 没有完整 Slack/workspace/sandbox / 多任务委派模型；当前已有 Slack-compatible message envelope、Slack event mapper、Slack Socket Mode transport seam、Slack startup backfill seam、transport/responder/processor seam、Slack Web API responder seam、Slack private file download seam、per-channel queue dispatcher、true cancellable stop seam、Mom host sandbox/tool seam、docker path/command construction seam、`events/`、`MEMORY.md`、`SYSTEM.md`、`skills/` Agent Skills prompt inventory、`scratch/`、`log.jsonl`、`context.json`、`last_prompt.jsonl` 与 `status.json` 的最小本地状态/上下文注入
- 当前 outbox error 主要仍取决于外部 provider 可达性

### Tau.Pods

当前实现为 pod 运维 CLI 的第二层入口：

- `init [path]`
- `list [path]`
- `validate [path]`
- `status [path]`
- `probe [path]`
- `exec [path] <id> <command>`
- `health [path]`
- `deploy [path] <id> <model> [name]`
- `stop [path] <id> <name>`
- `restart [path] <id> <name>`
- `model list [path] <id>`
- `model pull [--json] [--config path] [--pod id] [--revision rev] [--snapshot rev] <model>`
- `model remove [path] <id> <model>`
- `model status [path] <id> <model>`
- `vllm plan [--json] [--config path] [--pod id] <model> [--name name] [--revision rev] [--snapshot rev] [--vllm <args...>]`
- `vllm preflight [--json] [--config path] [--pod id] <model> [--name name] [--revision rev] [--snapshot rev]`
- `vllm deploy [--json] [--no-health] [--prefetch] [--health-attempts n] [--health-backoff-ms n] [--config path] [--pod id] <model> [--name name] [--revision rev] [--snapshot rev] [--vllm <args...>]`
- `vllm status [--json] [path] <id> <name>`
- `vllm health [--json] [--health-attempts n] [--health-backoff-ms n] [path] <id> <name>`
- `vllm stop [--json] [path] <id> <name>`
- `PodsConfigStore` / `PodsConfigValidator`
- `PodProbeService`
- `PodExecService`
- `PodLifecycleService`
- `PodModelService`
- `System.Text.Json` source-gen 上下文，兼容仓库级 AOT/trim 约束

当前 `probe` 语义：

- 对 `endpoint` 做 HTTP GET 健康探测
- 对 `sshHost/sshPort` 做 TCP 连通性探测
- 返回 `ok / transport / latency / target / summary`
- 探测失败时返回非零 exit code

当前 lifecycle 语义：

- `health` 对 HTTP pod 调 `/health`，对 SSH pod 执行 `echo ok`
- `deploy / stop / restart` 当前基于 SSH pod 的 `~/.tau_pods/<deployment>.json` metadata 管理
- target command 支持默认 `tau.pods.json` 或显式 config path
- deployment name 会先规范化再作为远端 metadata 文件名，metadata 内容会 shell quote 后再交给 SSH
- SSH exec 进程参数使用 `ProcessStartInfo.ArgumentList` 构造，`-p`、`-o`、host 和 remote command 均作为独立 argv 传给系统 `ssh`，避免本地命令行拼接和 quoting 漂移
- `model list/pull/remove/status` 基于 SSH pod 的 Hugging Face cache 管理：默认 remote cache 路径为 `$HOME/.cache/huggingface/hub`，pod 可用 `modelsPath` 覆盖；`pull` 优先用 `huggingface-cli download`，缺 CLI 时回退 `python -m huggingface_hub.commands.huggingface_cli download`；长耗时 pull 会打开 SSH keepalive 参数
- 本地 `ssh` 启动失败、injected runner 异常和 cancellation 都会转为结构化 `PodExecResult` failure（`ExitCode=-1`），summary 分别固定为 `ssh process start failed`、`ssh process runner failed` 和 `ssh exec cancelled`；lifecycle service 会把底层 failure summary 继续透传到 health/deploy/stop/restart/logs/deployments 结果
- `PodVllmCommandPlanner` 已有纯本地 vLLM serve command planner baseline，并已通过 `vllm plan [--json] [path] <id> <model> [name]` 接入 CLI 预览入口：默认返回 deployment name、model cache path、port、served model name、systemd unit、planned metadata JSON 和 remote command 文本；`--json` 输出包含同一字段的 machine-readable JSON plan；它不执行 SSH，不调用 `systemctl`，不写真实远端状态，只固定后续 SSH orchestration 可复用的命令合同。生成的 model path 仍基于 Hugging Face cache directory convention，真实 vLLM 启动前还需要远端模型 snapshot 解析、环境校验和 smoke
- `PodVllmOrchestrationService` 把 vLLM plan 接到 SSH command baseline：`deploy` 会在远端写 `~/.tau_pods/<deployment>.service` 和 `.json`，优先安装/启动 systemd user unit，找不到 systemd 或 systemd user command 失败时 fallback 到 `nohup ... &` + `.pid` / `.log`；默认部署后按 12 次、5 秒 backoff 调用 `health` 探测远端 `/health`，`starting/unknown/not-ready` 会继续等待，`unhealthy/dead/not-found`、SSH 启动失败、runner failure 和 cancelled 会归类为终止失败，部署命令失败或 health 最终非 ready 时尝试 rollback cleanup；`--no-health` 可跳过 deploy readiness，`--health-attempts` / `--health-backoff-ms` 可调整窗口；`status` 查询 systemd user unit、fallback pid 或 metadata；`health` 默认单次探测，也可通过 retry/backoff 参数调整，并返回 `ready/unhealthy/dead/starting/unknown`、`failureKind`、attempts/maxAttempts；`stop` disable/remove systemd unit 或 kill fallback pid，并清理 metadata/service/pid。CLI 的 `--json` 输出 operation/result/plan/health/rollback，文本输出包含 `[remote-command]`、`[serve-command]`、stdout/stderr。当前成功只代表 SSH command 和配置的 health contract 通过，不代表真实 GPU/HF/vLLM 环境长期健康；metadata 仍是 `planned-vllm`，rollback 是 cleanup，不是多版本 rollout 状态机

当前短板：

- 还没有真实模型进程 smoke、镜像/服务管理、vLLM runner 健康校验、失败回滚和 rollout 状态机；当前 systemd/nohup 是 SSH command baseline，未用真实 GPU pod 验证
- 还缺更完整远端 transport hardening 和真实运维 smoke

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
- `Tau.CodingAgent` 默认把本地 flat snapshot 保存到当前目录 `.tau/coding-agent-session.json`，`TAU_CODING_AGENT_SESSION_FILE` 可覆盖路径；JSONL tree session 默认保存到 `.tau/coding-agent-session.jsonl`，`TAU_CODING_AGENT_TREE_SESSION_FILE` 可覆盖路径；如果 `TAU_CODING_AGENT_SESSION_FILE` 指向 `.jsonl`，则作为 tree session 路径使用；JSONL tree session 写出默认按 `TAU_CODING_AGENT_REDACT_SECRETS` 脱敏 string value，关闭时设置为 `0/false`；`/clone` 生成的 branch session 默认放在当前 tree session 所在目录下的 `coding-agent-sessions/`
- `Tau.CodingAgent` 默认把本地模型、theme、scoped model list、tree 默认过滤、retry 策略、default thinking level、steering/follow-up queue mode 和 auto-compaction boolean 设置保存到当前目录 `.tau/coding-agent-settings.json`，`TAU_CODING_AGENT_SETTINGS_FILE` 可覆盖路径；`enabledModels` 显式数组表示有序模型 scope，缺失/null 表示 all enabled / no filter；旧 `queueMode` 会在读取时映射为 `steeringMode`
- `Tau.CodingAgent` 默认从 `TAU_CODING_AGENT_KEYBINDINGS_FILE` 或用户目录 `~/.tau/coding-agent-keybindings.json` 加载 editor keybindings；`/hotkeys` 会显示当前交互式 editor 实际使用的绑定，非交互模式不会创建 editor，因此该命令会返回 unavailable；交互式模式下 `/reload` 会重新加载 keybinding 文件并替换 editor 当前绑定
- `Tau.CodingAgent` 默认从 `~/.tau/prompts` / `./.tau/prompts`、`~/.tau/skills` / `./.tau/skills` 和 `~/.tau/extensions` / `./.tau/extensions` 发现 prompt templates、skills 与 JSON extension commands；`TAU_CODING_AGENT_PROMPT_PATHS`、`TAU_CODING_AGENT_SKILL_PATHS` 和 `TAU_CODING_AGENT_EXTENSION_PATHS` 可指定额外文件或目录；`/reload` 会重读 extension resources，并让 prompt/skill stores 使用最新 extension-contributed resource paths
- `Tau.CodingAgent` 默认从 `~/.tau/themes` / `./.tau/themes` 发现 themes，`TAU_CODING_AGENT_THEME_PATHS`、repeatable `--theme` 和 extension `themePaths` 可追加文件或目录；`/theme list` 展示当前可发现主题和 diagnostics，`/theme set <name>` 写入 settings `theme`，`/theme clear` 回到默认 `dark`
- `Tau.CodingAgent` 默认加载 context files：先读用户目录 `~/.tau/AGENTS.md` 或 `~/.tau/CLAUDE.md`，再从当前目录父级到 cwd 逐层读取 `AGENTS.md` 或 `CLAUDE.md`；同一目录优先 `AGENTS.md`。生成 system prompt 会追加 `# Project Context`；`--no-context-files` / `-nc` 可禁用，`/reload` 会重读 context files。
- `Tau.CodingAgent` `/changelog` 默认从当前工作目录向上查找 `docs/releases/feature-release-notes.md`；`TAU_CODING_AGENT_CHANGELOG_FILE` 可指定替代 release notes 文件，便于打包或测试场景固定来源
- `TAU_CODING_AGENT_AUTO_COMPACT_TOKENS` 设置为正整数时，会在普通消息进入 runner 前估算当前 session + 待发送输入的 token 数，超过阈值时先调用 compaction；settings/RPC `autoCompactionEnabled=false` 会禁止生产入口自动触发，`true` 只恢复 boolean 开关，不会凭空生成 threshold；`TAU_CODING_AGENT_AUTO_COMPACT_INSTRUCTIONS` 会作为自动摘要的附加指令；`TAU_CODING_AGENT_COMPACT_KEEP_RECENT_TOKENS` 可调整 compaction 后保留 recent messages 的 token budget，默认 20000；`TAU_CODING_AGENT_COMPACT_KEEP_RECENT_MESSAGES` 可调整 token budget 未命中时回落保留的最近 message 数，默认 4；普通 runner exception、取消或错误型 `AgentEndEvent` 会回滚到回合前 snapshot；生产入口优先从 settings 读取 retry 配置，未配置时从 `TAU_CODING_AGENT_AUTO_RETRY_ATTEMPTS` 和 `TAU_CODING_AGENT_AUTO_RETRY_BASE_DELAY_MS` 读取，env 未设置时按 3 次 retry、2000ms exponential base delay 运行；生产入口也会从同一 settings 文件读取 `defaultThinkingLevel` 和 queue mode 并恢复到 runner；`/retry` 可在当前进程里立即改写 retry 策略，`/thinking` 可在当前进程里立即改写 reasoning level，并在 JSONL tree / HTML transcript 中保留 retry start/end audit entries；`/session` 会把同一估算器的当前 token、模型 context window、auto threshold budget 和 retry policy 展示出来
- `Tau.WebUi` 的 `/api/status`、`/api/catalog`、`POST /api/sessions` 已可返回真实 JSON
- `Tau.WebUi` 的 session 已可持久化到 `output/webui-sessions.json`，并支持 JSON / HTML / Markdown / WebUi-local JSONL transcript 导入导出；HTML / Markdown / WebUi-local JSONL / CodingAgent JSONL preview-import 默认对常见 secret pattern 做 string-value redaction，`TAU_WEBUI_REDACT_SECRETS=0` 可关闭；CodingAgent JSONL preview 当前还会返回 tree metadata、filter metadata 和 conservative import source audit，但不持久化 branch tree
- `Tau.Mom --once` 已可真实处理结构化请求并写出带 `provider/model/workingDirectory/title/metadata/attachments` 的 outbox；file request、due `events/*.json` 与 Slack event JSON 会先映射为 `MomChannelMessage`，再生成统一 `DelegationRequest`；本地存在的 attachment 会 staging 到 `workingDirectory/attachments/` 并保留 original/local 元数据；runner 执行前会创建 `scratch/`、workspace/channel `skills/`、`attachments/` 和 `events/`，输入会合并 request context、workspace memory、`SYSTEM.md`、Agent Skills prompt inventory 与最近 channel history，并通过 `context.json` 恢复/保存同一 workdir 的 runtime messages；调用 runner 前会写 `workingDirectory/last_prompt.jsonl` 作为 prompt/debug 快照；默认 `log.jsonl` / `last_prompt.jsonl` 会按 `TAU_MOM_REDACT_SECRETS` 做常见 secret pattern 脱敏；默认 runner 工具集已切到 Mom 的 `bash/read/write/edit/attach`，host sandbox 可本地执行，docker sandbox 当前固定配置和路径/命令构造 seam；runner 事件会进入 Tau runtime log sink，覆盖 response/tool/usage baseline；处理完成后会把本地请求/结果追加到 `workingDirectory/log.jsonl`，并把当前或最近一次运行状态写到 `workingDirectory/status.json`；同一 workdir 内已有新鲜 `running` 状态时会保留 inbox 请求并跳过处理
- `Tau.Pods probe` 已可对本地 HTTP endpoint 返回真实健康结果
- `Tau.Pods exec` 已可对 SSH pod 通过系统 `ssh` 客户端执行远程命令，且本地进程 argv 通过 `ArgumentList` 构造
- `Tau.Pods vllm preflight/deploy/status/health/stop` 已可生成并执行远端 SSH command baseline，preflight/deploy 支持 revision-aware snapshot 解析，deploy 可用 `--prefetch` 在模型 cache/snapshot 缺口时复用 `model pull` 后重新 preflight，并具备可配置 health retry/backoff 窗口与 failure classification；当前仍未证明真实 vLLM 进程、GPU 环境、HF download、systemd user session 或 fallback pid path 的端到端 smoke
- `Tau.slnx` 当前已可 build；如果本机 `bash` 入口不可用，可按 `scripts/verify-dotnet.sh` 中的项目顺序直接执行等价 `dotnet build/test` 命令
- `scripts/verify-dotnet.ps1` 当前提供 Windows PowerShell 等价验证入口，覆盖与 `verify-dotnet.sh` 相同的 restore/build/test 项目顺序，并支持可选 `-RunSmoke` 执行 `WebUi` 和 `Mom --once` 的最小运行态 smoke
