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
- `Tau.Mom`：支持结构化委派请求、Slack-compatible channel runtime 和 Mom sandbox/tool seam 的本地委派宿主
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
- custom model registry 从“必须改代码注册模型”推进到“启动时合并 models.json”：`ModelConfigurationStore` 会从 `TAU_MODELS_FILE`、当前目录 `.tau/models.json`、用户目录 `.tau/models.json` 中取第一个存在文件，按 provider 合并 built-in/generated models、provider-level override、per-model override 和 custom models；`StreamFunctions` 会在请求前解析 models.json 的 `apiKey`、`authHeader` 与 provider/model headers，支持 env/literal/`!command` value resolution。auth status 只检查 credential 配置是否存在，不解析 env、不执行 `!command`、不回显 secret；默认本地 `.tau/models.json` 已被忽略。动态 provider API 注册和更完整 runtime config UX 仍是后续切片。

### Tau.Agent

维持原来的双层循环、工具执行、interceptor 和状态模型，继续作为所有应用面的共同 runtime 内核。

### Tau.CodingAgent

仍然是最完整的用户路径：

- 最小 CLI 宿主
- 基础 coding tools
- 与 `ModelCatalog` 对齐的 provider / model 默认解析（CodingAgent / WebUi / Mom 复用同一解析器）
- `RuntimeCodingAgentRunner.Create(provider, model, history)` 显式宿主工厂
- 本地 session 持久化：继续保留 Tau 平面 snapshot JSON（默认 `./.tau/coding-agent-session.json`，`TAU_CODING_AGENT_SESSION_FILE` 可覆盖），同时新增上游风格 JSONL tree session（默认 `./.tau/coding-agent-session.jsonl`，`TAU_CODING_AGENT_TREE_SESSION_FILE` 可覆盖；如果 `TAU_CODING_AGENT_SESSION_FILE` 指向 `.jsonl`，则把它当 tree session 使用并关闭平面 snapshot 写入）；启动时优先恢复显式 tree session 或非空 tree session，否则回落 flat JSON
- JSONL session tree 基线：`CodingAgentTreeSessionStore` 写入 `type=session` header、append-only `message` / `model_change` / `session_info` / `label` / `compaction` / `auto_retry_start` / `auto_retry_end` entries，每个 entry 带 `id` / `parentId` / `timestamp`；header 的 `cwd` 和 `parentSession` 会进入 tree summary，host 通过 runner message diff 同步当前 branch，保留 flat JSON 兼容入口
- session lifecycle：`/new` 清空当前 runtime messages 和 display name，并在 tree 中追加新的 root marker；`/session` 输出 flat stats、估算 token/context usage、auto-compaction threshold budget、当前 retry policy、tree file/leaf/entry/message/branch/label/cwd/parent 信息；`/tree [max entries] [default|no-tools|user-only|labeled-only|all] [--label-time] [--search query]` 输出当前 JSONL tree 的短 id / parent / 当前 branch / leaf 标记 / label，可按上游 tree selector 的核心过滤模式隐藏 bookkeeping、tool-only assistant 和 tool result，可按 entry text / label / 基础 metadata 搜索，并在 header 显示 cwd / parent metadata 和最新 label timestamp；`/tree --interactive` 提供 j/k/方向键移动、g/G/Home/End 跳转、Enter 选择、q/Esc 取消、`/` overlay search、`f` filter cycle、n/N 搜索结果跳转、普通 Left/Right 分页、Ctrl/Alt+Left 折叠或跳到上一个分支段、Ctrl/Alt+Right 展开或跳到下一个分支段、Space 折叠/展开当前 entry descendants，并显示 selected entry type/depth/branch/leaf metadata；未显式传过滤模式时会读取 settings `treeFilterMode`，无效值回退 `default`；`/label <entry-id> [label | clear]` 以 append-only label entry 标记历史节点；`/fork <entry-id>` 从历史 entry 切出新 branch 并恢复该 branch messages；`/clone` 把当前 active branch 导出到新的 `coding-agent-sessions/*.jsonl` session 并立即切换过去，空 branch 返回 `Nothing to clone yet`；`/resume [latest | path.jsonl]` 恢复 JSONL session；当前仍未完成完整上游 TreeSelector、多选、fold 持久化和更完整 session metadata inspector
- 最小 session display name：`/name [display name | clear]` 查看、设置或清空当前 session display name，并随 session store 持久化
- 最小退出命令：`/quit` 与文本 `exit` 一样结束当前 CLI loop，不调用 runner，不进入 LLM conversation
- 最小帮助命令：`/help` 列出当前 Tau 已支持的本地命令，避免用户只能靠文档猜命令面
- prompt template 基线：`CodingAgentPromptTemplateStore` 会加载用户目录 `~/.tau/prompts`、项目目录 `./.tau/prompts` 和 `TAU_CODING_AGENT_PROMPT_PATHS` 指定的 `.md` 文件/目录；`/prompts` 列出可用模板，非内置 slash 输入命中模板名时会在发给 runner 前展开 `$1`、`$@`、`$ARGUMENTS` 和 `${@:N[:L]}` 参数占位
- skill command 基线：`CodingAgentSkillStore` 会加载用户目录 `~/.tau/skills`、项目目录 `./.tau/skills` 和 `TAU_CODING_AGENT_SKILL_PATHS` 指定的 skill 文件/目录；`/skills` 列出 `/skill:<name>` 命令，`/skill:<name> args` 会把 `SKILL.md` body 包装成上游风格 `<skill name="..." location="...">` block 后发送给 runner；`disable-model-invocation: true` 的 skill 不进入默认 system prompt inventory，但仍可显式命令调用
- extension command/resource/diagnostics 基线：`CodingAgentExtensionCommandStore` 会加载用户目录 `~/.tau/extensions`、项目目录 `./.tau/extensions` 和 `TAU_CODING_AGENT_EXTENSION_PATHS` 指定的 `.json` 文件/目录；`/extensions` 列出本地 extension slash commands，并显示 extension JSON 文件路径、每个文件的 command/prompt/skill resource 计数、重复 command 的解析名、prompt/skill resource paths，以及坏 JSON、不可读文件、缺失显式路径等 load diagnostics；非内置 slash 输入会先查 extension command，再查 skill/prompt，保持上游“extension command 先于 input/skill/template”的执行顺序；JSON command 支持单命令或 `commands[]`、`name`、`description`、`argumentHint`、`response`、`prompt`、`sendToRunner`，重复 name 会按上游规则解析成 `name:1`、`name:2`；同一 JSON 还可通过 `resources.promptPaths` / `resources.skillPaths` 或顶层 `promptPaths` / `skillPaths` 贡献 prompt/skill 发现路径，路径相对 extension JSON 所在目录解析；当前这是 Tau-native 声明式 command/resource/load diagnostics baseline，不等于已移植上游 TypeScript extension runtime、custom tools、events、theme loader 和 interactive resource selector
- 最小 copy 命令：`/copy` 复制最后一条 assistant 文本消息到系统剪贴板；剪贴板写入通过 `ICodingAgentClipboard` 抽象隔离，便于测试和后续替换
- export / share 命令：`/export [path]` 默认导出 standalone HTML transcript；显式 `.html/.htm` 路径同样导出 HTML（session header、消息、thinking、tool call、tool result、图片内容、branch outline、cwd / parent metadata，并内嵌可下载 JSONL）；HTML 会按 current branch JSONL 渲染 `message`、`session_info`、`model_change`、`label` 与 `compaction` timeline entries，消息节点绑定 JSONL entry id 和 label badge，branch outline 可滚动到 message / model / label / compaction entry，并支持 `default/no-tools/user-only/labeled-only/all` 过滤和搜索，提供 per-message copy link，并支持 `targetId` deep link 自动定位和高亮；文本内容中的 fenced code block 会拆成独立 `code-block` / `<code>` 区块；普通文本继续安全转义，backtick inline code span 会渲染为 `<code class="inline-code">`，并会把 Markdown-style `[label](http/https...)` 与裸 `http(s)` URL 渲染成外链，code fence 和 inline code 内不链接，非 http(s) scheme 仍按文本输出；如果普通文本段检测到 heading/list/blockquote block marker，则会渲染为轻量 rich-text block，支持 `h1`-`h6`、`ul/ol/li` 与 `blockquote`，并复用 inline code/link 安全输出；普通文本里的 `**strong**` / `__strong__` 和 `*emphasis*` / `_emphasis_` 会分别渲染为 `<strong>` / `<em>`，inline code、code fence 和单词内部下划线不会触发 emphasis；普通文本段中的 Markdown pipe table（header + separator + data rows）会渲染为可横向滚动的 `<table>`，单元格继续复用 inline code/link/strong/emphasis 安全输出，code fence 内表格文本保持代码块；普通文本列表项里的 `[ ]` / `[x]` task marker 会渲染为 disabled checkbox，任务文本继续复用 inline code/link/strong/emphasis 安全输出，code fence 内任务列表文本保持代码块；图片内容继续以内嵌 data URI 渲染，并补充 mime type 与估算字节数 caption；长 tool result 文本会默认折叠为 `<details class="tool-result-fold">` 并保留完整输出；tool call arguments 中可解析 JSON 会格式化为 `code-block` / `<code data-language="json">`，不可解析参数仍按原始 `<pre>` 安全转义，超长参数会默认折叠为 `<details class="tool-call-arguments-fold">` 并保留全文；`.jsonl` 路径导出当前 branch 为独立 JSONL session，并保留 parent session path；其他路径继续导出 Tau 平面 session snapshot JSON；`/share` 会复用同一 HTML transcript exporter，检查 `gh auth status`，再用 `gh gist create --public=false` 创建 secret gist，预览 URL 默认沿用 pi-compatible viewer 且可通过 `TAU_SHARE_VIEWER_URL` 覆盖；当前仍未实现上游完整 Markdown/highlight renderer、custom tool renderer、richer HTML template 和 Tau 专属 share viewer
- import 命令：`/import <path>` 对非 `.jsonl` 路径严格导入 Tau snapshot JSON 并恢复 messages/provider/model/display name，同时把导入结果同步到 tree；对 `.jsonl` 路径等价于 resume JSONL session；无效 JSON 或缺失文件直接报错，不回落空 session
- 最小 settings / model selection：`/model`、`/provider`、`/models`、`/providers`，默认模型写入 `./.tau/coding-agent-settings.json`，也可通过 `TAU_CODING_AGENT_SETTINGS_FILE` 指定路径；同一 settings 文件支持上游兼容的 `treeFilterMode` 字段作为 `/tree` 默认过滤模式，并支持 Tau-native `retryMaxAttempts` / `retryBaseDelayMilliseconds` 字段作为 transient retry 策略；`/model` / `/provider` 保存默认模型时会保留这些非模型设置
- 最小 auth 管理入口：`/auth [provider]` 查询当前 provider 凭证来源，`/login [provider]` 给出已配置或未移植 OAuth/login 的明确提示；不会回显密钥
- compaction baseline：`/compact [instructions]` 使用当前模型生成会话摘要，重置 runtime state，并把摘要作为单条 user message 保留到 flat session store；JSONL tree 会追加上游风格 `compaction` entry，记录 `summary`、`firstKeptEntryId`、估算 `tokensBefore`、`fromHook`、Tau-native `isSplitTurn` 和 `turnPrefixSummary` baseline；默认从当前 compaction boundary 后优先按 `TAU_CODING_AGENT_COMPACT_KEEP_RECENT_TOKENS` 的 token budget 保留 recent messages，再回落到最近 4 条 message，可用 `TAU_CODING_AGENT_COMPACT_KEEP_RECENT_MESSAGES` 调整，恢复 branch 时会按上游顺序重建 summary message、retained recent messages 和 compaction 后的新消息；如果 retained cut point 落在一个 user turn 中间，恢复时会把确定性的 split-turn prefix context 拼入 summary message，HTML transcript 也会展示并搜索该字段；`TAU_CODING_AGENT_AUTO_COMPACT_TOKENS` 可开启普通消息执行前的自动 token-threshold compaction，`TAU_CODING_AGENT_AUTO_COMPACT_INSTRUCTIONS` 可补充摘要指令，自动触发会写 `fromHook=true`；context overflow 错误会触发 Tau-native compact-and-retry baseline：先恢复失败回合前 snapshot，再 compact、记录 `fromHook=true` compaction entry、更新回滚基线并重试同一输入；`/session` 会显示当前估算 token、模型 context window 和 auto threshold 剩余量；当前仍未移植上游 LLM-generated split-turn summarization、compaction extension events 和 cancellation UI parity
- 普通回合 rollback / retry baseline：`CodingAgentHost` 在调用 runner 前记录当前 messages/provider/model/name snapshot；如果 runner 抛异常、普通回合被取消，或 runtime 返回带错误消息的 `AgentEndEvent`，host 会恢复回合前 snapshot，再持久化 flat JSON 和 JSONL tree，避免失败输入、半截 assistant 或工具结果污染 session；生产入口按 settings 优先、`TAU_CODING_AGENT_AUTO_RETRY_ATTEMPTS` / `TAU_CODING_AGENT_AUTO_RETRY_BASE_DELAY_MS` env 兜底读取 host-level transient error retry 策略，当前识别 overloaded、rate limit、429、5xx、service unavailable、network/connection/timeout/fetch 等错误；`/retry [current|default|off|<max attempts> [base delay ms]]` 可查看、关闭、恢复 env/default 或设置 attempts/base delay，并在同进程立即更新 host retry 策略；retry 会写入 Tau-native JSONL `auto_retry_start` / `auto_retry_end` audit entries，成功时先同步成功 attempt 的 user/assistant messages 再追加 retry end，失败或耗尽时只保留 retry audit，不持久化失败输入；retry delay 被取消时会显示明确状态、写 `Retry cancelled` end audit，并回滚失败输入；context overflow 单独走 compact-and-retry baseline，不参与普通 transient retry；当前仍不是上游完整 RPC/settings UI 控制或完整 retry cancellation UI parity
- `CodingAgentCommandRouter` 承载 slash command 解析和本地命令执行，`CodingAgentHost` 保持为输入循环、UI 渲染、runtime event、退出信号和 session 持久化宿主；host 会把当前 session store path、tree controller 和 retry option update callback 注入 router，供 `/session`、`/tree`、`/label`、`/fork`、`/clone`、`/resume`、`/retry` 复用同一事实源
- `CodingAgentCommandCatalog` 维护当前已支持 slash command 的名称、usage 和描述，`/help` 与参数错误共用同一事实源，避免继续在 router 内散落命令字符串；当前已支持 `/help`、`/name`、`/copy`、`/export`、`/share`、`/import`、`/new`、`/session`、`/tree`、`/label`、`/fork`、`/clone`、`/resume`、`/quit`、`/model`、`/provider`、`/models`、`/providers`、`/prompts`、`/skills`、`/extensions`、`/auth`、`/login`、`/retry`、`/compact`

这个显式 runner 工厂现在也是 `WebUi / Mom` 继续往宿主化推进的关键共享边界；本地 session/settings/auth-status 入口则为后续真实 OAuth、slash command、compaction 与 WebUi/Mom 共享会话语义打底。

### Tau.WebUi

当前实现为第二层 Web 宿主：

- 内嵌 HTML/JS 的单页聊天入口，支持 NDJSON streaming、附件 preview/send、tool timeline 和 client-side Markdown rendering
- health/status/catalog/session/messages/auth API
- session 本地持久化到 `output/webui-sessions.json`
- 每个 session 可独立配置 provider / model / title
- `PUT /api/sessions/{id}` 支持会话设置更新和 title rename
- session delete/export/import 已接入，前端会记住 last-opened session 并在 reload 时恢复仍存在的会话
- `WebUiApplication.MapWebUiEndpoints()` 让生产入口和 endpoint tests 共用同一套 Minimal API route table
- `WebUiRunnerFactory` 通过显式 provider/model/history 驱动 runtime

当前短板：

- 还缺 browser 级端到端 WebUi flow 测试
- 当前 WebUi session 仍是 WebChatStore DTO，不是 CodingAgent JSONL session/tree 语义
- 还没有 auth login 的完整交互管理层
- richer rendering 仍未达到完整上游 renderer/template parity

### Tau.Mom

当前实现为结构化本地委派宿主：

- `MomOptions` 统一 inbox/outbox/archive/poll/default provider/default model/default workdir 配置
- `MomOptions` 同时暴露 `EventsPath`、`RunningStatusStaleAfterMinutes`、`SlackBackfillEnabled`、`SlackBackfillMaxPages`、`SlackBackfillPageSize` 与 `SlackChannelQueueLimit`，配置会在 `Program.cs` 中规范化为绝对路径/实际数值
- `FileDelegationProcessor` 扫描 `inbox/*.txt|*.md|*.json`
- `MomEventProcessor` 扫描 `events/*.json`，把上游 mom 风格的 `immediate`、`one-shot`、`periodic` 事件转换成 inbox `.json` 委派请求
- event `.json` 支持 `type/channelId/text/at/schedule/timezone/provider/model/title/metadata/attachments`；入队 prompt 形如 `[EVENT:file:type:schedule] text`，metadata 会补 `event/eventType/eventFile/channel/user/userName/ts/date`
- `channelId` 会映射到 `DefaultWorkingDirectory/<channelId>`，从而复用现有 `MEMORY.md`、`log.jsonl` 和 `status.json` channel/workspace seam；无效事件会移入 `archive/invalid-events`
- `periodic` 当前实现五段 cron 的本地匹配（`*`、`*/n`、单值、范围、逗号）和 timezone 转换，同一事件文件同一分钟只入队一次；它是本地 worker seam，实时 Slack 消息则走独立的 channel queue dispatcher
- `MomChannelMessage` / `MomChannelAttachment` 是 file delegation、local events、Slack event mapper 和 Slack startup backfill 共用的 Slack-compatible envelope：统一承载 `channel/user/userName/displayName/ts/threadTs/text/attachments/provider/model/title/metadata`，再生成 `DelegationRequest`，避免后续 Slack/backfill/queue 各自拼 request metadata
- `IMomChannelTransport` / `IMomChannelResponder` / `MomChannelMessageProcessor` 固定 Slack adapter 的输入输出边界：transport 读入 channel message，responder 负责响应/thread/typing/upload，processor 负责 busy-state、true cancellable stop、attachment staging、status/log writeback 与 runner 调用；`SlackEventMapper` 已固定 `app_mention` / DM / skip / mention stripping / file metadata 到 `MomChannelMessage` 的 receive-side 转换规则；`SlackSocketModeTransport` 已固定 `auth.test` / `apps.connections.open` / WebSocket text frame / envelope ack / reconnect 接缝；`SlackBackfillService` 已固定 startup `conversations.history` 回填：只处理已有 `log.jsonl` 的 channel，按 latest ts 设置 oldest，最多分页 3 次，过滤重复/其他 bot/非 `file_share` subtype，按时间顺序写回 log，不触发 delegation；`SlackWebApiResponder` 已先用 `HttpClient` 固定 `chat.postMessage` / `files.uploadV2` 的真实 Web API 调用契约和 token 脱敏错误边界；`SlackAttachmentDownloader` 已固定 bot-token private file download；`MomChannelQueueDispatcher` 已固定 per-channel 顺序处理、pending queue limit、不同 channel 独立推进和 stop bypass；`MomChannelRunRegistry` 已固定当前进程内 active run 的 linked cancellation token，stop 命令会取消当前 runner、返回 `_Stopping..._`，完成后写 `cancelled` status 并回复 `_Stopped_`
- `.txt/.md` 请求继续兼容为纯 prompt
- `.json` 请求支持 `prompt/provider/model/workingDirectory/title/metadata/attachments`
- `title` 进入 runner session name，`metadata` / `attachments` 会被包装成 `<delegation_context>` 注入实际 prompt
- `ChannelAttachmentStore` 会把本地存在的 request/event attachment staging 到 `workingDirectory/attachments/<timestamp>_<filename>`，写入 `attachments/attachments.jsonl` 记录 `original/local/source`，并让 channel log 保留上游 mom 风格的 `original/local` 附件元数据；Slack private file URL 会先通过 `SlackAttachmentDownloader` 使用 bot token 下载到同一 attachment store，再进入 delegation request
- `RuntimeDelegationAgentRunner` 在 `<delegation_context>` 之前还会注入 `<mom_runtime_context>`，把本地 Mom 的 workspace/channel layout、events 文件格式、attachment manifest、memory/log/status 路径和 `[SILENT]` 事件约定写成运行时前缀；这是本地 worker 的语义，不代表 Slack adapter 已接通
- `ChannelWorkspaceLayout` 统一定义并创建本地 workspace/channel 路径：workspace `MEMORY.md` / `SYSTEM.md` / `skills/` / `events/`，channel `MEMORY.md` / `log.jsonl` / `context.json` / `status.json` / `last_prompt.jsonl` / `attachments/` / `scratch/` / `skills/`；这样后续 Slack adapter、sandbox 和 tool delegation 不再各自拼路径字符串
- `SYSTEM.md` 的非空内容会以 `system_configuration_log` 注入 `<delegation_context>`；workspace/channel `skills/**/SKILL.md` 会被解析为上游 Agent Skills 风格的 `<available_skills>`，并按 sandbox 映射 skill file path。Mom skills 不是额外 tool 注册；agent 读取 `SKILL.md` 后通过 `bash/read/write/edit` 使用脚本
- `MomOptions.Sandbox` 支持 `host` 和 `docker:<container>` 配置；当前已实现 host executor、docker command/path translation seam、workspace path authority 和 `bash/read/write/edit/attach` 五个上游同名 Mom tools，默认 runner 会使用 Mom tool set 而不是通用 CodingAgent 工具名。真实 Docker container smoke 仍是后续切片
- `ChannelPromptDebugStore` 会在每次调用 runner 前写 `workingDirectory/last_prompt.jsonl`，记录 mom runtime context、delegation context、实际 runner input、恢复的 session messages、当前 user prompt、attachment count 和 image attachment count；这是对齐上游 mom `last_prompt.jsonl` 的本地可观测 seam，不代表图片附件已经以多模态内容传入 runner
- `workingDirectory/MEMORY.md` 与其父目录的 `MEMORY.md` 会以 current/parent workspace memory 注入同一段上下文
- `workingDirectory/log.jsonl` 会记录本地 file delegation 的用户请求与 bot 结果；最近非 bot 文本消息会以 channel history 注入同一段上下文，malformed 行、空文本和当前消息 `ts` 会被跳过
- `ChannelLogStore` 统一承载 `log.jsonl` append、history 读取和过滤语义，避免 runner / processor / 后续 Slack adapter 各自解析 channel log
- `workingDirectory/context.json` 保存 Tau-native channel session snapshot；`ChannelSessionStore` 会在 delegation 前恢复上一轮 runtime messages，并在完成后写回当前 runner messages/provider/model/session name。它不是上游 JSONL session tree 的完整移植，但已经给后续 Slack session sync 留出本地持久化 seam
- `workingDirectory/status.json` 记录当前或最近一次 delegation 的 `running/completed/failed/cancelled` 状态、请求文件、provider/model、workdir、开始/完成时间、错误和响应摘要；本地 worker 会跳过同一 workdir 内未过期的 `running` 状态，默认 60 分钟后视为 stale
- `ChannelStatusStore` 统一承载 `status.json` 读写与 running/stale 判定语义，避免 file worker 和后续 adapter 各自拼状态文件
- `IDelegationAgentRunner` 返回 `DelegationExecution`：response、结构化 `DelegationToolEvent`（phase/toolName/toolCallId/isError/durationMs）、stop reason、`DelegationUsage`（input/output/cache tokens 与可选总成本）、provider/model/workingDirectory/metadata
- `RuntimeDelegationAgentRunner` 通过可注入的 `Func<provider, model, workingDirectory, attachFile, ICodingAgentRunner>` 工厂构造内核 runner，便于测试和后续 Slack/workspace/sandbox 适配层复用；默认路径会创建 Mom sandbox executor 与 `MomToolSet`
- 结果序列化到 outbox `.json`，包含 title、attachments、stop reason、结构化 tool events 与 usage
- 原始请求归档到 archive
- 支持 `--once`

当前短板：

- 仍未完成端到端 Slack parity：真实 Slack smoke 和 session sync 仍缺
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
- `PodsConfigStore` / `PodsConfigValidator`
- `PodProbeService`
- `PodExecService`
- `PodLifecycleService`
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

当前短板：

- 还没有真实模型进程编排、镜像/服务管理和 rollout 状态机
- 没有完整远端 transport hardening 和真实运维 smoke

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
- `Tau.CodingAgent` 默认把本地 flat snapshot 保存到当前目录 `.tau/coding-agent-session.json`，`TAU_CODING_AGENT_SESSION_FILE` 可覆盖路径；JSONL tree session 默认保存到 `.tau/coding-agent-session.jsonl`，`TAU_CODING_AGENT_TREE_SESSION_FILE` 可覆盖路径；如果 `TAU_CODING_AGENT_SESSION_FILE` 指向 `.jsonl`，则作为 tree session 路径使用；`/clone` 生成的 branch session 默认放在当前 tree session 所在目录下的 `coding-agent-sessions/`
- `Tau.CodingAgent` 默认把本地模型、tree 默认过滤和 retry 策略设置保存到当前目录 `.tau/coding-agent-settings.json`，`TAU_CODING_AGENT_SETTINGS_FILE` 可覆盖路径
- `Tau.CodingAgent` 默认从 `~/.tau/prompts` / `./.tau/prompts`、`~/.tau/skills` / `./.tau/skills` 和 `~/.tau/extensions` / `./.tau/extensions` 发现 prompt templates、skills 与 JSON extension commands；`TAU_CODING_AGENT_PROMPT_PATHS`、`TAU_CODING_AGENT_SKILL_PATHS` 和 `TAU_CODING_AGENT_EXTENSION_PATHS` 可指定额外文件或目录
- `TAU_CODING_AGENT_AUTO_COMPACT_TOKENS` 设置为正整数时，会在普通消息进入 runner 前估算当前 session + 待发送输入的 token 数，超过阈值时先调用 compaction；`TAU_CODING_AGENT_AUTO_COMPACT_INSTRUCTIONS` 会作为自动摘要的附加指令；`TAU_CODING_AGENT_COMPACT_KEEP_RECENT_TOKENS` 可调整 compaction 后保留 recent messages 的 token budget，默认 20000；`TAU_CODING_AGENT_COMPACT_KEEP_RECENT_MESSAGES` 可调整 token budget 未命中时回落保留的最近 message 数，默认 4；普通 runner exception、取消或错误型 `AgentEndEvent` 会回滚到回合前 snapshot；生产入口优先从 settings 读取 retry 配置，未配置时从 `TAU_CODING_AGENT_AUTO_RETRY_ATTEMPTS` 和 `TAU_CODING_AGENT_AUTO_RETRY_BASE_DELAY_MS` 读取，env 未设置时按 3 次 retry、2000ms exponential base delay 运行；`/retry` 可在当前进程里立即改写 retry 策略，并在 JSONL tree / HTML transcript 中保留 retry start/end audit entries；`/session` 会把同一估算器的当前 token、模型 context window、auto threshold budget 和 retry policy 展示出来
- `Tau.WebUi` 的 `/api/status`、`/api/catalog`、`POST /api/sessions` 已可返回真实 JSON
- `Tau.WebUi` 的 session 已可持久化到 `output/webui-sessions.json`
- `Tau.Mom --once` 已可真实处理结构化请求并写出带 `provider/model/workingDirectory/title/metadata/attachments` 的 outbox；file request、due `events/*.json` 与 Slack event JSON 会先映射为 `MomChannelMessage`，再生成统一 `DelegationRequest`；本地存在的 attachment 会 staging 到 `workingDirectory/attachments/` 并保留 original/local 元数据；runner 执行前会创建 `scratch/`、workspace/channel `skills/`、`attachments/` 和 `events/`，输入会合并 request context、workspace memory、`SYSTEM.md`、Agent Skills prompt inventory 与最近 channel history，并通过 `context.json` 恢复/保存同一 workdir 的 runtime messages；调用 runner 前会写 `workingDirectory/last_prompt.jsonl` 作为 prompt/debug 快照；默认 runner 工具集已切到 Mom 的 `bash/read/write/edit/attach`，host sandbox 可本地执行，docker sandbox 当前固定配置和路径/命令构造 seam；处理完成后会把本地请求/结果追加到 `workingDirectory/log.jsonl`，并把当前或最近一次运行状态写到 `workingDirectory/status.json`；同一 workdir 内已有新鲜 `running` 状态时会保留 inbox 请求并跳过处理
- `Tau.Pods probe` 已可对本地 HTTP endpoint 返回真实健康结果
- `Tau.Pods exec` 已可对 SSH pod 通过系统 `ssh` 客户端执行远程命令
- `Tau.slnx` 当前已可 build；如果本机 `bash` 入口不可用，可按 `scripts/verify-dotnet.sh` 中的项目顺序直接执行等价 `dotnet build/test` 命令
- `scripts/verify-dotnet.ps1` 当前提供 Windows PowerShell 等价验证入口，覆盖与 `verify-dotnet.sh` 相同的 restore/build/test 项目顺序，并支持可选 `-RunSmoke` 执行 `WebUi` 和 `Mom --once` 的最小运行态 smoke
