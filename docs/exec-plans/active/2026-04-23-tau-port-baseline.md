# Tau pi-mono 移植基线与多应用面 P1 收口计划

## 目标

把 Tau 从“CLI-first 的可运行移植项目”继续推进到“多应用面都已经脱离模板壳、具备真实最小产品切片”的状态，并在此基础上进入第二层能力收口：

- `Tau.CodingAgent` 继续作为最完整主路径
- `Tau.WebUi` 从最小聊天宿主推进到可配置、可持久化的 Web 宿主
- `Tau.Mom` 从本地文件 worker 继续走向真实委派语义
- `Tau.Pods` 从 config CLI 继续走向 pod lifecycle

这份 plan 不再把 `WebUi / Mom / Pods` 当作“只做规划”，而是把它们视为已经进入实现、但仍处于早期产品切片阶段的真实模块。

## 当前阶段结论

截至 2026-04-24，Tau 的真实状态可以概括为：

- `Tau.Ai` 已完成第一轮 provider / auth / model registry 收口，并补了 request-body source-gen serializer runtime 回归
- `Tau.Agent` 继续稳定提供双层循环与工具执行骨架
- `Tau.CodingAgent` 已恢复独立 build/run/test，并新增了显式 provider/model/history 注入 runner 的宿主边界
- `Tau.Tui` 已具备最小 transcript / input buffer / session 层
- `Tau.WebUi` 已从 Hello World 推进到 **可持久化 session + provider/model 选择** 的第二层 Web 宿主
- `Tau.Mom` 已从 Worker 模板推进到 **inbox/outbox/archive + --once** 的本地文件委派 worker
- `Tau.Pods` 已从控制台占位推进到 **init/list/validate/status** 的真实 config CLI

因此当前主线已经从“先把 Tau 变成 CLI-first 项目”转到“在守住 CLI 主路径的同时，把其他应用面从第一层切片继续收口到第二层能力”。

## 范围

- 包含：
  - 维持 `Tau.CodingAgent` 的项目级 build/test/运行闭环
  - 继续收口 `Tau.Ai` 的 provider / auth / registry fidelity
  - 推进 `Tau.WebUi` 的会话、配置、流式体验和前端宿主能力
  - 推进 `Tau.Mom` 的 runner seam、Slack/workspace/sandbox 委派语义
  - 推进 `Tau.Pods` 的 SSH / deploy / lifecycle / model management
  - 同步维护 README、architecture、quality、history 与验证命令
- 不包含：
  - 一次性追求与上游 `pi-mono` 的 1:1 全量完成度
  - 在没有验证证据时，强行把单一 solution build 当唯一门禁
  - 在 release 仍是仓库元数据制品阶段时，假装已经有完整产品发布链

## 背景

- 相关文档：
  - `docs/ARCHITECTURE.md`
  - `docs/product-specs/tau-port-overview.md`
  - `docs/QUALITY_SCORE.md`
  - `docs/CICD.md`
- 相关代码路径：
  - `src/Tau.Ai/`
  - `src/Tau.Agent/`
  - `src/Tau.CodingAgent/`
  - `src/Tau.Tui/`
  - `src/Tau.WebUi/`
  - `src/Tau.Mom/`
  - `src/Tau.Pods/`
  - `tests/`
- 已知约束：
  - `Tau.slnx` 当前已可通过 `dotnet build Tau.slnx --verbosity minimal`
  - `Tau.CodingAgent` / `Tau.WebUi` / `Tau.Mom` / `Tau.CodingAgent.Tests` 已收回到 `ProjectReference`
  - 当前 Windows 环境下 `bash scripts/verify-dotnet.sh --skip-restore` 会落到 WSL 并因缺少 `/bin/bash` 失败，所以本地验证仍接受“仓库标准命令写 bash，现场执行可退回等价顺序 dotnet 命令”的现实

## 风险

- 风险：多应用面都进入实现后，文档和计划继续停留在 CLI-only 叙事。
  - 缓解方式：每次产品切片推进后，同轮同步 README / architecture / quality / plan / history。
- 风险：`WebUi / Mom / Pods` 继续横向铺开，但每个模块都缺第二层能力和真实验证。
  - 缓解方式：按“最短变成真实产品宿主”的顺序推进：WebUi 会话与配置、Mom 委派 seam、Pods lifecycle。
- 风险：过早清理 `HintPath` workaround，再次踩回 metaproj / workload resolver 异常。
  - 缓解方式：把引用结构收口单列为独立工程任务，不和产品能力改动混做。
- 风险：只在 README 写“支持了某模块”，但没有真实 build/test/run 证据。
  - 缓解方式：每个新增切片必须至少具备 build + test 或 build + runtime smoke。

## 里程碑

1. CLI-first 基线稳定化（已完成）
2. `Tau.Ai` 第一轮 provider / auth / registry 收口（已完成）
3. `Tau.WebUi / Tau.Mom / Tau.Pods` 第一层真实产品切片（已完成）
4. `Tau.WebUi` 第二层能力：持久化 + provider/model 选择（已完成）
5. 多应用面第二层继续收口：
   - WebUi 流式 / richer UX
   - Mom Slack / workspace / sandbox
   - Pods SSH / lifecycle / model management
6. 工程化收口：
   - 恢复 `Tau.slnx` solution-level build
   - 收回 `HintPath` workaround
   - 让 release 开始对应真实 Tau 产物

## 实施切片

### 切片 A：CLI-first 与 provider/auth 基线

- `Tau.Tui` 最小交互层
- `Tau.CodingAgent` 宿主抽象与 smoke 测试
- `Tau.Ai` provider/auth/registry 第一轮收口

### 切片 B：多应用面第一层产品切片

- `Tau.WebUi` 最小聊天宿主
- `Tau.Mom` 本地文件委派 worker
- `Tau.Pods` config CLI

### 切片 C：`Tau.WebUi` 第二层能力

- 会话持久化到本地 store
- `/api/catalog` provider/model 列表
- `PUT /api/sessions/{id}` 配置更新入口
- `RuntimeCodingAgentRunner.Create(provider, model, history)` 显式宿主接线
- 最小测试覆盖 runner/store

### 切片 C2：`Tau.CodingAgent` 会话与设置基础层

- CLI 启动时从本地 session store rehydrate messages/provider/model
- 回合结束后保存当前 runtime messages
- session JSON 先覆盖 Tau 当前消息抽象：user / assistant / toolResult 与 text / thinking / image / toolCall
- `/new` 先做最小 session reset：清空当前 runtime messages 和 display name，并把空快照写回当前 session store；`/session` 先做当前平面 session status：输出 display name、model、消息计数、tool call 数和 session 文件路径；`/name` 先做当前 session display name 的查看、设置和清空；当前不实现上游多 session resume/tree/branch/full stats
- `CodingAgentSettingsStore` 保存默认 provider/model，启动优先级为 env > session > settings
- `/model`、`/provider`、`/models`、`/providers` 提供最小模型查看、切换和列表入口；命令不进入 LLM conversation context
- `/auth [provider]` 通过 `ProviderAuthResolver.GetStatus(...)` 汇报 env/auth.json/models.json/OAuth 状态，`/login [provider]` 先做明确的未移植提示，不回显密钥
- slash command 解析从 `CodingAgentHost` 抽到 `CodingAgentCommandRouter`，host 只负责输入循环、结果渲染、运行时事件、退出信号和 session 持久化
- `/quit` 结束当前 CLI loop，作为本地控制命令不进入 LLM conversation；当前仍保留文本 `exit` 兼容路径
- `/help` 列出当前 Tau 已支持命令；暂不移植上游 extension/prompt/skill 动态 slash command 发现
- `CodingAgentCommandCatalog` 统一当前本地 slash command 的 name / usage / description，避免 `/help`、usage 错误和后续命令迁移继续散落重复字符串
- `/name [display name | clear]` 在 Tau 当前单文件 session snapshot 上保存 display name，作为上游 session_info display name 的最小等价物
- `/copy` 复制最后一条 assistant 文本消息到系统剪贴板；通过 `ICodingAgentClipboard` 抽象隔离系统 clipboard 写入，生产实现使用平台常见命令，测试使用 fake
- `/export <path>` 导出当前 Tau 平面 session snapshot JSON，复用 `CodingAgentSessionStore` 格式；暂不实现上游 HTML default、JSONL tree、import/share/export-html 体系
- `/import <path>` 严格读取 Tau snapshot JSON 并恢复当前平面 session 的 messages/provider/model/display name；无效文件通过 `LoadStrict()` 报错，不再像启动路径 `Load()` 那样静默回落空 session
- `/compact [instructions]` 先做最小手动 compaction：使用当前模型生成会话摘要，`Reset()` runtime state，并把摘要保留成单条 user summary message
- 暂不引入上游完整 JSONL session tree、branch、compaction、label、extension entry，避免一次性过度移植

### 切片 D：`Tau.Mom` 第二层入口

- 为 runner / result schema 补更稳定 seam
- 把文件委派抽象继续推进到 Slack/workspace/sandbox 可接线状态

### 切片 E：`Tau.Pods` 第二层入口

- 补 SSH / deploy / lifecycle
- 进入真实 pod transport / model lifecycle

### 切片 F：工程化与门禁

- 保持 `scripts/verify-dotnet.sh` 为主 CI
- 在当前环境接受顺序 `dotnet build/test` 作为 bash 不可用时的本地等价验证
- `Tau.slnx` 与引用结构已恢复到可 build 状态，后续继续补运行态 smoke 和 release 产物

## 验证方式

- 仓库标准命令：
  - `bash scripts/verify-dotnet.sh`
  - `bash scripts/verify-dotnet.sh --skip-restore`
  - `powershell -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1`
  - `powershell -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`
  - `powershell -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore -RunSmoke`
- Solution build：
  - `dotnet build Tau.slnx --verbosity minimal`
- 当前机器上的等价顺序验证：
  - `dotnet build src/Tau.CodingAgent/Tau.CodingAgent.csproj --no-restore`
  - `dotnet build src/Tau.WebUi/Tau.WebUi.csproj --no-restore`
  - `dotnet test tests/Tau.CodingAgent.Tests/Tau.CodingAgent.Tests.csproj --no-build --no-restore`
- 运行态 smoke：
  - `dotnet run --project src/Tau.WebUi/Tau.WebUi.csproj --no-build -- --urls http://127.0.0.1:5088`
  - `GET /api/status`
  - `GET /api/catalog`
  - `POST /api/sessions`
  - 检查 `output/webui-sessions.json`
  - `dotnet run --project src/Tau.Mom/Tau.Mom.csproj --no-build -- --once`

## 阶段退出标准

只有满足下面条件，才能认为这份计划当前阶段基本完成，并进入更高层能力：

- `Tau.CodingAgent` 主路径继续可重复 build/test/run
- `Tau.WebUi` 已具备会话持久化与 provider/model 选择，不再只是内存聊天页
- `Tau.Mom` / `Tau.Pods` 都已经从模板壳进入真实切片，并有明确第二层 backlog
- 仓库文档、质量评分、next 与当前实现状态一致
- `Tau.slnx` / 引用 workaround 已完成基础收口，而不是继续藏在产品改动里

## 进度记录

- [x] CLI-first P0 收口
- [x] `Tau.Tui` 最小交互层
- [x] `Tau.CodingAgent` 宿主抽象与 smoke 测试
- [x] `Tau.Ai` 第一轮 provider / auth / registry 收口
- [x] `Tau.WebUi / Tau.Mom / Tau.Pods` 第一层真实产品切片
- [x] `Tau.WebUi` 第二层：持久化 session + provider/model 选择 + runtime history rehydrate
- [x] `Tau.Ai` OpenAI Responses / Codex Responses 专用 SSE provider
- [x] `Tau.Ai` Mistral 专用 provider
- [x] `Tau.Ai` Bedrock ConverseStream provider（bearer / SigV4 / shared profile / binary eventstream）
- [x] `Tau.Ai` Google Vertex ADC token exchange（service account / authorized user）
- [x] `Tau.Ai` Google Gemini CLI / Antigravity provider fidelity（headers / fallback / retry / empty-stream）
- [x] `Tau.Ai` Azure OpenAI Responses 专用 provider（base URL/resource/deployment/api-version/api-key）
- [x] `Tau.Ai` OpenAI Codex Responses WebSocket transport（WebSocket/auto / session socket cache）
- [x] `Tau.Ai` OpenAI Responses service-tier pricing multiplier（flex/priority cost multiplier / Codex default->requested tier）
- [x] `Tau.Ai` GitHub Copilot Responses 动态 headers / vision / tool-result image payload
- [x] `Tau.Ai` generated model seed / generator / catalog merge
- [x] `Tau.Ai` typed/default model parsing 与共享默认选择
- [x] `Tau.Ai` OpenAI-compatible compatibility / routing metadata（`Model.Compat` / generator / request body）
- [x] `Tau.Ai` custom model/provider 配置入口（`ModelConfigurationStore` / models.json merge / provider 与 model override / request auth headers）
- [x] `Tau.CodingAgent` 本地 session 持久化（`TAU_CODING_AGENT_SESSION_FILE` / `./.tau/coding-agent-session.json`，启动 rehydrate，回合后保存）
- [x] `Tau.CodingAgent` 最小 session reset（`/new`，清空当前 runtime messages 并写回当前 session store）
- [x] `Tau.CodingAgent` 最小 session status（`/session`，报告当前平面 session stats 与 session store path，不进入 LLM conversation）
- [x] `Tau.CodingAgent` 最小 session display name（`/name [display name | clear]`，随 session store 持久化并显示在 `/session`）
- [x] `Tau.CodingAgent` 最小退出命令（`/quit`，通过 command result 退出 host loop，不调用 runner）
- [x] `Tau.CodingAgent` 最小帮助命令（`/help`，列出当前 Tau 已支持本地命令，不调用 runner）
- [x] `Tau.CodingAgent` slash command catalog（统一已支持命令 name / usage / description，`/help` 与参数错误共用）
- [x] `Tau.CodingAgent` 最小 copy 命令（`/copy`，复制最后一条 assistant 文本到 clipboard，不进入 LLM conversation）
- [x] `Tau.CodingAgent` 最小 export 命令（`/export <path>`，导出当前 Tau snapshot JSON，不进入 LLM conversation）
- [x] `Tau.CodingAgent` 最小 import 命令（`/import <path>`，严格导入当前 Tau snapshot JSON 并恢复平面 session，不进入 LLM conversation）
- [x] `Tau.CodingAgent` settings / model selection（`TAU_CODING_AGENT_SETTINGS_FILE` / `./.tau/coding-agent-settings.json`，`/model` / `/provider` / `/models` / `/providers`）
- [x] `Tau.CodingAgent` 最小 auth 管理入口（`/auth` status / `/login` 未移植提示，基于 `ProviderAuthResolver.GetStatus`，不回显 secret）
- [x] `Tau.CodingAgent` slash command router 抽离（`CodingAgentCommandRouter`，保持 `/model` / `/provider` / `/models` / `/providers` / `/auth` / `/login` 行为不变）
- [x] `Tau.CodingAgent` 最小手动 compaction（`/compact [instructions]`，当前模型生成摘要后压缩为单条 summary message）
- [x] `Tau.Mom` runner / result schema 收口（结构化 `DelegationToolEvent`、stop reason、`DelegationUsage` 含 token + 可选总成本、可注入 `ICodingAgentRunner` 工厂）
- [ ] `Tau.WebUi` 流式 UI / richer rendering / attachment
- [ ] `Tau.Mom` Slack / workspace / sandbox / delegation semantics
- [ ] `Tau.Pods` SSH / lifecycle / model management
- [x] `Tau.slnx` / metaproj / workload resolver 异常收口（当前 `dotnet build Tau.slnx --verbosity minimal` 已通过）
- [x] `HintPath` workaround 收回到更正常的 `ProjectReference`

## 决策记录

- 2026-04-23：决定先把 Tau 收口为 CLI-first 的 .NET 移植项目，而不是同时平推所有应用面。
- 2026-04-23：决定先补 `Tau.Ai` 的 provider / auth / registry，而不是直接把所有应用面做满。
- 2026-04-24：决定把 `Tau.WebUi / Tau.Mom / Tau.Pods` 从“只做规划”改为“进入真实切片实现”。原因是这三个模块已经有 build/run 级证据，继续把它们写成占位会让仓库知识失真。
- 2026-04-24：决定在 `Tau.WebUi` 内引入持久化 session 与 provider/model 选择，而不是继续依赖全局 `TAU_PROVIDER / TAU_MODEL`。原因是 Web 宿主必须支持会话级配置，不应该把全局环境变量当成 session state。
- 2026-04-24：决定把 `RuntimeCodingAgentRunner` 提升为显式 `Create(provider, model, history)` 工厂，同时保留 `CreateDefault()`。原因是 CodingAgent、WebUi、Mom 都需要共享同一 runtime 内核，但宿主配置边界不同。
- 2026-04-24：决定继续保留 `HintPath` workaround，不在本轮产品能力改动里顺手收引用结构。原因是当前 metaproj / workload resolver 异常仍未解除，强行收口风险高于收益。

- 2026-04-29：决定把 Bedrock 单独收口为专用 ConverseStream provider，而不是继续保留 placeholder 或引入 AWS SDK。原因是 Tau.Ai 当前坚持零外部依赖，最小可验证闭环是 HttpClient + SigV4 + AWS event stream parser，并用 StubHandler 固定 bearer/SigV4/body/event 翻译行为。
- 2026-04-30：决定在不引入 AWS SDK 的前提下补最小 shared credentials/profile 读取。原因是 Bedrock 本地使用高概率依赖 `AWS_PROFILE`，静态 profile 文件解析足以覆盖常见开发路径；SSO、AssumeRole、credential_process、IMDS/ECS/web identity 继续作为后续 credential chain 任务。
- 2026-04-30：决定把 Vertex ADC 收口为 Tau.Ai 内部最小 resolver，而不是引入 Google auth SDK。原因是当前 provider 层保持 HttpClient + 零 provider SDK 依赖，先覆盖 service account JWT bearer 与 authorized user refresh token 两条常见 ADC 路径即可，external_account/impersonation 留后续 auth chain 切片。
- 2026-04-30：决定按上游 Gemini CLI provider 做窄范围请求保真移植：补 headers、Antigravity fallback、retry delay、empty SSE retry 与 Claude thinking beta header；OAuth login、image/tool multimodal routing 和 generated model 全量同步继续留作后续切片。
- 2026-04-30：决定把 `azure-openai-responses` 从 OpenAI-compatible fallback 拆为专用 Responses provider。原因是 Azure Responses 请求使用 `input` 与 deployment name，不应继续走 chat-completions `messages` 语义；本轮继续保持 HttpClient + source-gen JSON，不引入 Azure/OpenAI SDK。
- 2026-04-30：决定把 Codex WebSocket 先做成可测试 transport seam，而不是直接绑定真实 ChatGPT e2e。原因是当前迁移目标是 provider 协议保真与本地回归闭环；`ClientWebSocket` 默认实现和 Fake WebSocket 测试能先固定 URL/header/frame/session reuse 语义，真实服务漂移留给后续 e2e。
- 2026-04-30：决定把 Responses service-tier 的 effective tier 存在 `Usage` 上，并在 `ModelCatalog.CalculateCost` 统一应用 `flex/priority` 倍率。原因是 Tau 当前把 usage 事实与 cost 计算分层，provider 不应直接写入成本；Codex `default` -> requested tier 的特殊规则也应作为 usage 归一事实进入后续计算。
- 2026-04-30：决定把 GitHub Copilot 动态 headers 抽成共享 helper，并在 Responses shared converter 层补 tool-result 图片编码。原因是 `X-Initiator` / `Copilot-Vision-Request` 不是某个单一 request body 字段，而是基于上下文的 provider 语义；图片 tool-result 也属于 Responses message conversion 的共享事实，后续其他 Copilot/OpenAI 路径可直接复用。
- 2026-04-30：决定先做 Tau 自己的 generated model seed/generator 接缝，而不是直接同步上游完整 `models.generated.ts` 和 `generate-models.ts`。原因是 Tau 当前 provider/API 支持面还在收口中，先导入“已经能跑的 API 家族”的新增模型，比一次引入大量当前还不支持的模型更稳。
- 2026-05-01：决定继续扩 generated seed 时仍只纳入 Tau 已具备真实调用路径的 API 家族，并继续排除 `github-copilot` 的 `openai-completions` 模型。原因是 Copilot completions 路径当前还没有移植 Responses 路径之外的特定 header/兼容语义，模型表不能领先于 provider 行为。

- 2026-05-01：决定把 provider/model 默认解析收口到 `ModelCatalog`，而不是继续在 CodingAgent/WebUi/Mom 各自维护一份 switch/default 逻辑。原因是 generated catalog 已经成为模型 source of truth，默认 provider、默认 model、canonical `provider/model` 引用和 `default` 关键字解析都应该共享同一套规则，避免多个宿主各自漂移。
- 2026-05-02：决定只把 Tau provider 当前能实际消费的 compatibility / routing 元数据接到 `Model.Compat`，而不是先照搬上游全部自定义 provider schema。原因是 OpenAI-compatible provider 现在已经能验证 stream usage、max token field、reasoning format/map、tool stream、strict mode、OpenRouter/Vercel routing；还不能实际消费的字段继续留到 custom model 配置入口和后续 provider 行为切片。
- 2026-05-02：决定先落地 models.json 的模型目录合并入口，而不是一次性移植上游完整 request auth / dynamic provider 注册。原因是 Tau 当前已经有 `ProviderAuthResolver` 与已注册 API 家族，最小可靠切片是让用户无需改代码即可添加 OpenAI-compatible/custom model，并复用现有 provider 调用路径；`apiKey/authHeader`、shell/env/command value resolution 和 request headers 已在 `StreamFunctions` 层接入；OAuth login 和动态 API 注册继续拆成后续配置/auth 切片。

- 2026-05-02：决定先为 `Tau.CodingAgent` 落地 Tau-native 的最小 JSON session store，而不是一次性移植上游完整 JSONL session tree。原因是当前最缺的是跨运行的 conversation rehydrate 基础能力；branch、label、compaction、extension entry 和 settings/slash 需要在有稳定消息持久化后分切片接入。

- 2026-05-03：决定把 `Tau.CodingAgent` 的模型切换先做成最小 slash command，不引入完整上游命令系统。原因是当前只有 provider/model selection 需要本地可操作入口；先让命令不进入 LLM context，并把默认 provider/model 写入 settings store，后续再扩 auth、compaction 与通用 slash command registry。

- 2026-05-03：决定把 `Tau.CodingAgent` auth 管理先收口成 `/auth` 状态查看和 `/login` 未移植提示，而不是在本切片内实现真实 OAuth/device flow。原因是 Tau.Ai 已有 env/auth.json/OAuth credential resolver 和 models.json request auth 合并，CodingAgent 当前最需要的是安全可见的凭证状态面；实际 Anthropic/Copilot/Codex/Gemini OAuth login 仍应在 Tau.Ai auth provider 切片中逐个实现。

- 2026-05-03：决定把 `Tau.CodingAgent` 的 slash command 解析从 `CodingAgentHost` 抽到 `CodingAgentCommandRouter`，而不是继续在 host 里堆命令分支。原因是 session/settings/auth 已经让 host 同时承担输入循环、命令解析、UI 输出和持久化，继续追加 `/compact`、auth login 或 richer command 会让主循环变成难测的杂糅类；先固定一个无 UI 依赖、可单测的 command seam。

- 2026-05-03：决定把 `Tau.CodingAgent` 的 compaction 先收口成最小手动版，而不是同步上游完整 session-manager / JSONL tree / auto-compaction。原因是 Tau 当前只有平面消息持久化和 `AgentRuntime.Reset()`，先用当前模型生成摘要并把运行态压成单条 summary message，能尽快提供真实可用的 `/compact`，同时不伪装成已经具备 branch、tokensBefore 边界、auto-retry 或 compaction metadata 的完整体系。

- 2026-05-03：决定先补 `Tau.CodingAgent` 的 `/new`，而不是直接跳到 `/session`、`/resume` 或 tree/fork。原因是当前 Tau 只有单文件 session snapshot，没有多 session 索引和分支结构；先把“清空当前会话并立即持久化”的最小 lifecycle 做实，能与现有 session store 和 `AgentRuntime.Reset()` 直接闭环，同时为后续 richer session 管理留稳定 seam。

- 2026-05-03：决定把 `Tau.CodingAgent` 的 `/session` 先做成当前平面 runtime status，而不是同步上游 JSONL session tree/resume/full stats。原因是 Tau 当前只有单文件 session snapshot 和 runtime messages；先固定 model、消息计数、tool call 数与 session 文件路径的本地命令，能提供可见状态面，同时不伪装成已有多 session/branch 体系。

- 2026-05-03：决定把 `Tau.CodingAgent` 的 `/quit` 做成 command result 上的退出信号，而不是在 router 里直接操作 UI 或进程。原因是 router 应保持无 UI 依赖、可单测；host 负责渲染 goodbye、停止 loop 和持久化，文本 `exit` 继续保留为兼容入口。

- 2026-05-03：决定把 `Tau.CodingAgent` 的 `/help` 先做成静态命令列表，而不是同步上游 extension/prompt/skill 动态发现。原因是 Tau 当前 slash command 还处于本地基础层，先把已支持的命令面暴露给用户，后续等扩展、prompt、skill 体系存在后再接动态来源。

- 2026-05-03：决定为 `Tau.CodingAgent` 增加 `CodingAgentCommandCatalog`，统一本地 slash command 的名称、usage 和描述，而不是继续把 `/help` 输出与参数错误字符串散落在 router 分支里。原因是当前命令数量已经进入两位数，后续继续迁移 `/export`、`/copy`、`/resume` 等命令时需要一个轻量事实源，先不引入完整上游动态命令 registry。

- 2026-05-03：决定把 `Tau.CodingAgent` 的 `/name` 收口成 Tau 当前单文件 session snapshot 的 display name 字段，而不是同步上游 `session_info` JSONL entry。原因是 Tau 还没有 session tree/entry log；直接在 snapshot 上保存 name 能覆盖当前可验证需求，并让 `/session` 输出包含 display name，后续迁移 JSONL/tree 时再映射到 session_info entry。

- 2026-05-03：决定把 `Tau.CodingAgent` 的 `/copy` 先做成“复制最后一条 assistant 文本消息”，而不是同步上游完整富文本/HTML/session export 剪贴板语义。原因是 Tau 当前 TUI 仍是最小 transcript，先用 `ICodingAgentClipboard` 固定可测试 seam 和用户最常用路径；图片、tool timeline、HTML 和多消息选择留给 richer rendering/export 切片。

- 2026-05-03：决定把 `Tau.CodingAgent` 的 `/export` 先做成显式路径的 Tau snapshot JSON 导出，而不是同步上游默认 HTML export 或 JSONL session tree export。原因是 Tau 当前事实源是 `CodingAgentSessionStore` 单文件 snapshot；先复用已验证序列化格式能提供可用备份/迁移入口，`/import`、HTML、JSONL tree 和 share 留给后续 session manager 切片。

- 2026-05-03：决定把 `Tau.CodingAgent` 的 `/import` 先做成 Tau snapshot JSON 的严格导入，而不是同步上游 JSONL tree/import/share。原因是 `/export` 已固定单文件 snapshot 格式，最小可用闭环应该先能恢复 messages/provider/model/display name；导入失败必须显式报错，避免把坏备份静默当成空会话。

- 2026-05-03：决定先把 `Tau.CodingAgent`、`Tau.WebUi` 和 `Tau.CodingAgent.Tests` 的 DLL `HintPath` 收回到 `ProjectReference`，而不是继续要求先按手工顺序生成依赖 DLL。原因是 `/import` 验证时已经暴露 stale assembly / 并行构建顺序风险；这次只收口已能稳定验证的 CodingAgent/WebUi 链，`Tau.Mom` 和 `Tau.slnx` 继续作为独立工程化任务。

- 2026-05-03：决定把 `Tau.Mom` 也一并从 DLL `HintPath` 收回到 `ProjectReference`。原因是这轮已经确认干净构建可以按 project reference 链路稳定完成，继续保留唯一一处 workaround 只会让引用结构长期分叉，并继续增加 stale assembly 风险。

- 2026-05-03：重新验证 `Tau.slnx` 后决定把 solution-level build 标记为当前可用。原因是所有 `HintPath` workaround 收回 `ProjectReference` 后，`dotnet build Tau.slnx --verbosity minimal` 已能真实通过；当前仓库标准 bash 脚本在本机失败的根因是 WSL `/bin/bash` 缺失，而不是 Tau solution build。

- 2026-05-04：决定先把 `Tau.Mom` 的 runner / result schema 收口为结构化事实，再去接 Slack。原因是当前 `IReadOnlyList<string> ToolEvents` 把 toolName/toolCallId/isError/duration 全部塞回字符串里，后续 Slack/workspace/sandbox 适配层既看不见持续时间，也无法可靠把多次调用按 ID 关联起来；本轮先把 ToolEvents 拆成 `DelegationToolEvent { Phase, ToolName, ToolCallId, IsError, DurationMs }`，并补 `StopReason`（沿用 Tau.Ai `StopReason` 加 `cancelled`/`error`）和 `DelegationUsage`（input/output/cache token + 可选总成本，用 `ModelCatalog.CalculateCost` 计算），同时把 `RuntimeDelegationAgentRunner` 改成接受 `Func<string, string, ICodingAgentRunner>` 工厂。这样 Slack/Workspace 适配层可以基于结构化事件直接渲染 `→ tool / ✓ tool (1.2s)`，单测可以用 fake runner 直接驱动事件流，且 outbox JSON 仍向后保留所有旧字段。
