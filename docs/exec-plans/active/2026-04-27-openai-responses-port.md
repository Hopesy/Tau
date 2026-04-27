# Tau.Ai OpenAI Responses 家族移植

## 目标

把 pi-mono 的 `openai-responses` 与 `openai-codex-responses` 两套 API 真正移植进 `Tau.Ai`，让 OpenAI 现代 Responses 协议（含 Codex 的 ChatGPT backend 路径）在 Tau 这一侧成为一等 provider，而不是继续被 `OpenAiCompatibleProvider`（chat-completions wire）兜底。

## 范围

- 包含：
  - 新增 `Providers/OpenAiResponses/` 目录，落 `OpenAiResponsesShared.cs` / `OpenAiResponsesProvider.cs` / `OpenAiCodexResponsesProvider.cs`
  - 共享 `convertResponsesMessages` / `convertResponsesTools` / `processResponsesStream` 等价的 C# 实现
  - `openai-responses`：`{baseUrl}/responses` SSE，`reasoning` / `serviceTier` / `prompt_cache_*` 入参，`response.*` event → Tau `StreamEvent` 翻译
  - `openai-codex-responses`：`{baseUrl}/codex/responses` SSE，JWT → `chatgpt-account-id`、`originator: tau`（默认；可由 `Model.Headers` 覆盖）、`OpenAI-Beta: responses=experimental` 头，3 次指数退避重试，`response.done`/`response.incomplete` 归一为 `response.completed`
  - `BuiltInProviders.RegisterAll` 把这两个 `api` 切到新 provider，`OpenAiCompatibleProvider` 继续给 `mistral-conversations` / `azure-openai-responses` 用
  - 测试：`StubHandler` 驱动的 SSE 翻译、tool-call id 回环、Codex header 形状、BuiltInProviders 解析类型
  - 同步 `next.md` / `docs/QUALITY_SCORE.md` / `docs/ARCHITECTURE.md` 中跟 provider 保真度相关的描述
- 不包含：
  - **Codex WebSocket transport**（pi-mono `openai-codex-responses.ts` 中的 `processWebSocketStream` / 会话级 socket 缓存）
  - Codex 的 `service_tier` 价格倍率（Tau `Usage` 当前不带 `cost`）
  - `cacheRetention === "long"` → `prompt_cache_retention: "24h"` 上行参数
  - GitHub Copilot 在 openai-responses 路径下的动态头与 vision 行为
  - Azure 专用 `azure-openai-responses` provider（仍走 OpenAiCompatible 兜底）
  - Bedrock SigV4、Gemini CLI 完整版、Mistral 原生 conversations
  - OAuth login / refresh 流程
  - 引用结构 / `Tau.slnx` / HintPath workaround 收口

## 背景

- 相关文档：
  - `docs/ARCHITECTURE.md`
  - `docs/QUALITY_SCORE.md`
  - `docs/exec-plans/active/2026-04-23-tau-port-baseline.md`
  - `next.md`（P0 provider/API fidelity 段落）
- 相关代码路径：
  - 新增：`src/Tau.Ai/Providers/OpenAiResponses/`
  - 修改：`src/Tau.Ai/Providers/BuiltInProviders.cs`
  - 测试：`tests/Tau.Ai.Tests/`（新增 `OpenAiResponsesProviderTests.cs`、`OpenAiCodexResponsesProviderTests.cs`、`OpenAiResponsesSharedTests.cs`，收紧 `BuiltInProvidersTests.cs`）
  - 参考来源：`pi-mono-main/packages/ai/src/providers/openai-responses.ts`、`openai-codex-responses.ts`、`openai-responses-shared.ts`、`transform-messages.ts`、`simple-options.ts`
- 已知约束：
  - 现有 `Tau.Ai.AssistantMessage` 的 `Usage` 只有 `InputTokens / OutputTokens / CacheReadTokens / CacheWriteTokens`，不带 `cost`，所以 service-tier 价格倍率本轮不会落地
  - 现有 `Tau.Ai.ContentBlock` 已有 `ThinkingContent.ThinkingSignature` / `TextContent.TextSignature` / `ToolCallContent` 三种 block，pi-mono 的 `ResponseReasoningItem` 结构可以序列化进 `ThinkingSignature` 字符串（与 pi-mono 行为一致）
  - 现有 `StreamEvent` 集合 = `start / text|thinking|toolcall_(start|delta|end) / done / error`，没有独立的 `response.created`/`output_item.added` 事件；本轮只在内部消费这些事件，对外仍然只发 Tau 已有事件
  - Tau `StreamOptions` 现没有 `reasoningEffort` / `reasoningSummary` / `serviceTier` / `textVerbosity`，本轮**新增**两个 options 类（`OpenAiResponsesOptions` / `OpenAiCodexResponsesOptions`）继承 `StreamOptions`
  - Tau `SimpleStreamOptions` 已有 `Reasoning` (ThinkingLevel) 字段，可以映射到 `reasoningEffort`
  - 测试只跑本地 `StubHandler`，不打真实 OpenAI / ChatGPT 网络
  - Windows `bash` 仍可能 `E_ACCESSDENIED`，验证按既定退路用顺序 `dotnet build/test`

## 风险

- 风险：Responses 协议事件流较密（reasoning_summary_part / text_start / output_text.delta / function_call_arguments.delta…），翻译时容易把"事件序"和"content block 索引"配错。
  - 缓解方式：用 currentItem / currentBlock 双指针镜像 pi-mono 实现，并写一组按真实顺序拼接的 SSE fixture 测试覆盖 message + reasoning + toolCall 三种 item 交叉的情形。
- 风险：tool-call id 用 `callId|itemId` 双段编码，若回写到下一轮请求时拆分错误，会触发 OpenAI "function_call.id 与 reasoning 配对失败" 错误。
  - 缓解方式：把 id 拼接/拆分集中在 `OpenAiResponsesShared`，并加专门 round-trip 单测：`AssistantMessage(toolCall id="call_x|fc_y")` → 序列化为 `function_call` 时 `id=fc_y`、`call_id=call_x`。
- 风险：Codex JWT 解析（`atob` + JSON.parse）在 .NET 端容易踩 `Convert.FromBase64String` padding / URL-safe base64 差异。
  - 缓解方式：实现专门的 JWT payload 解码器（处理 `-`/`_` → `+`/`/`、补 `=` padding），用一段写死的假 token 单测验证。
- 风险：把 `openai-responses` / `openai-codex-responses` 从 `OpenAiCompatibleProvider` 切走后，依赖现有 chat-completions 兜底语义的调用链回归。
  - 缓解方式：仅替换这两个 api 的 builtin 注册，`OpenAiCompatibleProvider` 类本身不动；`BuiltInProvidersTests` 加断言确认这两个 api 的实例类型已切换，旧 `mistral` / `azure-openai-responses` 路径不变。
- 风险：本机 `dotnet test` 异步流式测试容易因为 `Task.Run` 的 fire-and-forget 让断言竞态。
  - 缓解方式：复用现有 `await foreach (var evt in stream)` 模式，`StubHandler` 内部直接同步返回响应字符串 + `HttpCompletionOption.ResponseHeadersRead` 配合 MemoryStream 模拟 SSE 分段。
- 风险：`originator: tau` 上线后被 `chatgpt.com/backend-api` 拒绝（pi-mono 用 `pi`，可能上游严格匹配）。
  - 缓解方式：默认 `tau`，明确允许 `Model.Headers["originator"]` 覆盖；本轮不接真实 ChatGPT 网络，等到第一次 e2e 验证再据此回收策略；不主动冒用 `pi` 标识。

## 里程碑

1. 共享层：`OpenAiResponsesShared.cs`（消息/工具 → ResponseInput 形状、SSE event → Tau StreamEvent 翻译、tool-call id 编解码、JWT payload 解码、stop reason 映射）+ 单测。
2. `OpenAiResponsesProvider`：`{baseUrl}/responses` POST，标准 `Authorization: Bearer`，`reasoning.effort/summary` / `prompt_cache_key` / `service_tier` 入参；接 `BuiltInProviders.RegisterAll`；StubHandler SSE 集成测试。
3. `OpenAiCodexResponsesProvider`：`{baseUrl}/codex/responses` POST，JWT → `chatgpt-account-id`、`originator: tau`、`OpenAI-Beta: responses=experimental`；指数退避重试；`response.done`/`incomplete` 归一；StubHandler SSE 集成测试 + header 形状测试 + 重试测试。
4. 文档同步：`next.md` 把"openai-responses / openai-codex-responses 提高协议保真度"标记完成（保留 WebSocket / service-tier pricing / Copilot 头作为新条目），`docs/QUALITY_SCORE.md` 与 `docs/ARCHITECTURE.md` 中跟 provider fidelity 相关的描述更新到当前实现。
5. 验证：`dotnet build src/Tau.Ai/Tau.Ai.csproj` + `dotnet test tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj`；同 round 写 history 落到 `docs/histories/2026-04/`。

## 验证方式

- 命令（仓库标准）：
  - `bash scripts/verify-dotnet.sh --skip-restore`
- 当前机器的等价顺序兜底：
  - `dotnet build src/Tau.Ai/Tau.Ai.csproj --no-restore`
  - `dotnet build tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-restore`
  - `dotnet test tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj --no-build --no-restore`
- 手工检查：
  - `BuiltInProvidersTests` 显式断言 `openai-responses` 与 `openai-codex-responses` 解析出的实例类型分别是 `OpenAiResponsesProvider` 与 `OpenAiCodexResponsesProvider`
  - `OpenAiCodexResponsesProviderTests` 用一段写死 JWT 校验 `chatgpt-account-id` 头被正确写入
- 观测检查：本轮无新增运行时观测；error 路径全部走 `ErrorEvent` 且不抛异常。

## 设计细节（与上轮 brainstorm 对齐）

### 文件布局

```
src/Tau.Ai/Providers/OpenAiResponses/
  OpenAiResponsesShared.cs        // 公共：消息/工具转换、SSE event → Tau StreamEvent、id 编解码、JWT 解码
  OpenAiResponsesProvider.cs      // api = "openai-responses"
  OpenAiCodexResponsesProvider.cs // api = "openai-codex-responses"
```

`OpenAiCompatibleProvider` 继续承担 `mistral-conversations` 与 `azure-openai-responses` 的兜底，本轮不删。

### Stream event 翻译表

| pi-mono Responses event | Tau 行为 |
|---|---|
| `response.created` | `partial.ResponseId = event.response.id`，不发独立事件 |
| `response.output_item.added` (`reasoning`/`message`/`function_call`) | 追加 `ThinkingContent` / `TextContent` / `ToolCallContent`，发 `ThinkingStartEvent` / `TextStartEvent` / `ToolCallStartEvent` |
| `response.reasoning_summary_part.added` / `_text.delta` | `ThinkingDeltaEvent`，并镜像写回 `currentItem.summary` |
| `response.content_part.added` / `output_text.delta` / `refusal.delta` | `TextDeltaEvent`，refusal 也并入 text block |
| `response.function_call_arguments.delta` / `.done` | `ToolCallDeltaEvent`，`done` 时把 partialJson 收口为最终 arguments |
| `response.output_item.done` | 对应 `XxxEndEvent`；reasoning 的 signature = `JSON.stringify(item)`，message 的 signature = `{v:1,id,phase}` JSON 串，toolCall 把 `partialJson` 落定 |
| `response.completed` | 写 `Usage`，映射 `StopReason`，发 `DoneEvent` |
| `error` / `response.failed` | `ErrorEvent` |

### tool-call id 编解码

- 出站（Tau → 请求）：`AssistantMessage` 中的 `ToolCallContent.Id` 形如 `callId|itemId`，序列化为 `function_call` 时拆出 `call_id=callId`，`id=itemId`。当跨模型/跨 provider 时 itemId 重写为 `fc_<hash>`，且必须以 `fc_` 起头。
- 入站（响应 → Tau）：`response.output_item.added(function_call)` 把 `id = "{call_id}|{item.id}"`。
- `function_call_output`（toolResult 上行）只用 `callId`。

### Codex JWT → header

```
payload    = base64UrlDecode(token.split('.')[1])
accountId  = JSON.parse(payload)["https://api.openai.com/auth"].chatgpt_account_id

headers["chatgpt-account-id"] = accountId
headers["originator"]         = "tau"            // 不冒用 pi；如上游严格校验，可由 Model.Headers 覆盖
headers["User-Agent"]         = `tau (${RuntimeInformation.OSDescription})`
headers["OpenAI-Beta"]        = "responses=experimental"
headers["Authorization"]      = `Bearer ${token}`
headers["accept"]             = "text/event-stream"
headers["content-type"]       = "application/json"
```

`Model.Headers` / `options.Headers` 的 entry 在最后合入并允许覆盖以上任何键，使用方需要冒用 `pi` 时显式传入即可。

### StopReason 映射

| `response.completed` 时 status | 中间映射 | 若 content 含 `ToolCallContent` |
|---|---|---|
| `completed` | `EndTurn` | → `ToolUse` |
| `incomplete` | `MaxTokens` | （保持） |
| `failed` / `cancelled` | `Error` | （保持，且会带 `ErrorMessage`）|
| `in_progress` / `queued` | `EndTurn` | → `ToolUse` |
| 缺失 / 未识别 | `EndTurn` | → `ToolUse` |

Codex 路径多一层归一：`response.done` / `response.incomplete` 在到达共享层前由 `mapCodexEvents` 统一改写成 `response.completed`，然后再走上表。

### 重试

`isRetryableError`：HTTP 429 / 500 / 502 / 503 / 504 或响应文本匹配 `rate.?limit|overloaded|service.?unavailable|upstream.?connect|connection.?refused`。退避 `1s, 2s, 4s`，最多 3 次。仅 Codex provider 启用；标准 `openai-responses` 不做客户端重试（pi-mono 也不做）。

## 进度记录

- [ ] 1. 共享层（converter + stream event 翻译 + id 编解码 + JWT decoder）
- [ ] 2. `OpenAiResponsesProvider` + 注册切换 + 测试
- [ ] 3. `OpenAiCodexResponsesProvider` + 测试
- [ ] 4. `next.md` / `QUALITY_SCORE` / `ARCHITECTURE` 同步
- [ ] 5. `dotnet build/test` 全绿 + history 落地

## 决策记录

- 2026-04-27：决定一次完成 `openai-responses` + `openai-codex-responses` 两族，而不是分两轮。原因是它们共享 converter / stream event 翻译，分轮会让公共层走两遍，且 next.md P0 把这两条放一起。
- 2026-04-27：决定 Codex 只做 SSE，**不**移植 WebSocket transport。原因是 WebSocket 是 ChatGPT Pro 会话复用优化，依赖 `ClientWebSocket` 与 socket 缓存层，自带一片复杂度，跟核心协议保真度正交，单列到 next 跟踪即可。
- 2026-04-27：决定走"两个 provider + 一份共享 helper"的物理切分，而不是单一 provider + mode flag。原因是 Codex 的 JWT 头、重试、event 归一全是 codex-only 流程，与 responses 主路径同面只会让两边都难读；另外 Tau 现有惯例就是"一个 provider 一个文件类"。
- 2026-04-27：决定 service-tier 价格倍率、`prompt_cache_retention: "24h"`、Copilot 动态头、Azure 专用 provider 全部落出 scope。原因是 Tau `Usage` 当前不带 `cost`、`StreamOptions` 不暴露 cache retention 上行参数、Copilot 是另一个 provider 维度的事，全在本轮一起做会让公共层语义不收敛。
