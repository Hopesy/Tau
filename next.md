# Tau next

这份文件只记录 **当前还没做完、后续需要继续推进的缺口**，方便快速检索。

当前完整移植总路线图：`docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`。后续所有 `pi-mono-main` parity 工作默认按这份 plan 推进，旧 `2026-04-23-tau-port-baseline.md` 保留为已完成 P1 基线和决策历史。

## P0：当前最值得继续推进的项

### Tau.Ai

#### Provider / API fidelity

- [x] `Amazon Bedrock` 真实实现 SigV4 / bearer token / shared credentials profile 调用，不再返回 placeholder
- [x] `openai-responses` 与 `openai-codex-responses` 提高协议保真度，补齐和上游更一致的 payload / stream 语义（SSE 路径）
- [x] `Mistral` 从 OpenAI-compatible 过渡到更接近原生 conversations 行为
- [x] `Google Vertex` 从 API key 模式扩到真正 ADC token exchange
- [x] `Google Gemini CLI / Antigravity` 从当前简化版凭证载荷扩到更完整的请求/响应细节
- [x] Codex WebSocket transport / 会话级 socket 缓存
- [x] OpenAI Responses service-tier cost / pricing multiplier
- [x] GitHub Copilot dynamic headers / vision behavior 的完整 Responses 路径
- [x] Azure dedicated `azure-openai-responses` provider，不再继续走 OpenAI-compatible 兜底

#### Model registry / generated models

- [x] 建立 generated models 管线，不再只靠手写 `BuiltInModels`
- [~] 引入更完整的 provider 列表和模型全集（已把当前已支持 API 家族扩到 66 个 generated seed，仍未覆盖全部上游 provider）
- [x] 支持 typed/default model 解析与更接近上游的 default model 策略
- [x] 把当前 Tau 可实际消费的 compatibility / capability / routing 元数据补到 `Model` / generator / OpenAI-compatible provider（OpenRouter / Vercel routing、reasoning/max-tokens/tool-stream/strict/stream-usage 兼容字段）

#### OAuth / auth

- [ ] Anthropic OAuth 真实 login / refresh 流程
- [ ] GitHub Copilot device flow
- [ ] OpenAI Codex OAuth flow
- [ ] Gemini CLI / Antigravity login flow
- [ ] auth.json 的迁移、写回、刷新后持久化策略

#### 配置 / 安全

- [ ] auth.json schema 明文化
- [ ] secret 持久化边界和脱敏规则
- [x] provider-specific headers 支持（models.json 已能合并静态 provider/model headers，并在 StreamFunctions 层解析 provider/model request headers）
- [ ] Bedrock AWS SSO / AssumeRole / credential_process / IMDS / ECS / web identity credential chain
- [x] 自定义 provider / custom model 配置入口（`TAU_MODELS_FILE`、`./.tau/models.json`、`~/.tau/models.json`，支持 Tau 已注册 API 的 `providers/baseUrl/api/apiKey/authHeader/headers/compat/models/modelOverrides` 子集）
- [x] models.json 的 `apiKey/authHeader`、shell/env value resolution、运行时 request auth 合并

### Tau.CodingAgent

- [x] session 持久化（`TAU_CODING_AGENT_SESSION_FILE` 或 `./.tau/coding-agent-session.json`，启动自动 rehydrate，回合后保存）
- [~] session lifecycle（已补 `/new` 清空当前会话并写回 session store，`/session` 报告当前平面 session stats 与文件路径，`/name` 持久化当前 session display name；仍缺 resume/tree/full stats 等多 session 管理能力）
- [x] settings / model selection / provider selection（`/model`、`/provider`、`/models`、`/providers`，默认写入 `TAU_CODING_AGENT_SETTINGS_FILE` 或 `./.tau/coding-agent-settings.json`）
- [~] auth 管理入口（已补 `/auth` 状态查看和 `/login` 骨架提示；真实 OAuth/device flow 仍在 Tau.Ai OAuth backlog）
- [x] slash command router 抽离（`CodingAgentCommandRouter`；当前命令行为不变，为 `/compact` / login flow 等后续命令留 seam）
- [x] local quit command（`/quit` 结束当前 CLI loop，不调用 runner，不进入 LLM conversation）
- [x] local help command（`/help` 列出当前 Tau 已支持命令，先不移植上游扩展/prompt/skill 命令发现）
- [x] slash command catalog（`CodingAgentCommandCatalog` 统一当前本地命令 name/usage/description，`/help` 和 usage 错误共用）
- [x] local session name command（`/name [display name | clear]` 查看、设置或清空当前 session display name，并写入 session store）
- [x] local copy command（`/copy` 复制最后一条 assistant 文本到系统剪贴板，clipboard 写入通过 `ICodingAgentClipboard` 隔离）
- [x] local export command（`/export <path>` 导出当前 Tau 平面 session snapshot JSON；仍缺上游 HTML/JSONL/tree export）
- [x] local import command（`/import <path>` 严格导入 Tau snapshot JSON，并恢复 messages/provider/model/display name；仍缺上游 JSONL tree/share/import 体系）
- [~] manual compaction（已补 `/compact [instructions]`，当前使用当前模型生成摘要并把 session 压成单条 summary message；仍缺 auto-compaction、branch/tree/metadata 与上游完整 session-manager 语义）
- [ ] richer rendering
- [x] 显式 `Create(provider, model, history)` runner 工厂
- [x] 与 `ModelCatalog` 对齐的默认模型解析层继续收口
- [x] 把当前 `Tau.CodingAgent` / `Tau.WebUi` / `Tau.Mom` / `Tau.CodingAgent.Tests` / `Tau.Agent.Tests` 的 DLL `HintPath` workaround 收回到更正常的 `ProjectReference` 结构
- [x] 解决当前本机上 `Tau.slnx` / metaproj / workload resolver 的 build 异常（`dotnet build Tau.slnx --verbosity minimal` 已通过）

### Tau.Tui

- [ ] 真正的输入编辑器
- [ ] 组件系统
- [ ] 消息区 / 状态区
- [ ] 键盘体系
- [ ] 更稳定的差分渲染层

## P1：后续应用面

### Tau.WebUi

- [x] 最小聊天 UI
- [x] session 持久化（`output/webui-sessions.json`）
- [x] provider/model 选择入口（`/api/catalog` + 会话设置）
- [ ] 流式消息绑定
- [ ] richer rendering / thinking / tool timeline 展示
- [ ] auth/settings UX
- [ ] 附件体系
- [ ] 更高层的 WebUi 行为测试

### Tau.Mom

- [x] 本地文件委派 worker
- [x] `--once`
- [x] inbox/outbox/archive
- [x] 结构化 `.json` 请求（`prompt/provider/model/workingDirectory/title/metadata/attachments`，title/metadata/attachments 已进入 runtime/outbox）
- [x] local attachment staging（本地存在的 request/event attachment 会复制到 `workingDirectory/attachments/`，并通过 `attachments/attachments.jsonl` 与 `log.jsonl` 保留 `original/local` 元数据）
- [x] runner / result schema seam（结构化 `DelegationToolEvent` + stop reason + `DelegationUsage` + 可注入 `ICodingAgentRunner` 工厂，留给 Slack/workspace/sandbox 适配层接线）
- [x] workspace memory context（`workingDirectory/MEMORY.md` 与父目录 `MEMORY.md` 注入 delegation prompt）
- [x] channel history context（`workingDirectory/log.jsonl` 最近非 bot 文本消息注入 delegation prompt，跳过 malformed/空文本/current ts）
- [x] local channel log writeback（本地 file delegation 完成后把用户请求和 bot 结果追加到 `workingDirectory/log.jsonl`）
- [x] local runtime status（本地 file delegation 执行前后写 `workingDirectory/status.json`，记录 `running/completed/failed`、请求文件、provider/model、时间、错误与响应摘要）
- [x] local busy-state guard（同一 workdir 已有未过期 `running` 状态时保留 inbox 请求并跳过处理，默认 60 分钟后视为 stale）
- [x] local events wake-up（`events/*.json` 的 `immediate` / `one-shot` / `periodic` 转换为 inbox 委派请求，channelId 映射到本地 channel workdir）
- [x] mom runtime context seam（`<mom_runtime_context>` 注入 workspace/channel layout、events 文件格式、attachment manifest、memory/log/status 路径与 `[SILENT]` 约定）
- [x] local channel session context（`workingDirectory/context.json` 使用 Tau-native session snapshot 恢复/保存同一 workdir 的 runtime messages）
- [x] prompt debug snapshot（调用 runner 前写 `workingDirectory/last_prompt.jsonl`，记录 mom runtime context、delegation context、实际 runner input、恢复的 session messages、当前 prompt 和 attachment/image attachment count）
- [x] workspace layout bootstrap（统一创建 `scratch/`、workspace/channel `skills/`、`attachments/`、`events/`，并把 `SYSTEM.md` 与 skill docs inventory 注入 prompt context）
- [x] Slack-compatible channel message envelope（`MomChannelMessage` / `MomChannelAttachment` 统一 file/events/未来 Slack adapter 的 channel/user/ts/thread/attachment/request metadata 映射）
- [x] fake Slack transport / responder seam（`IMomChannelTransport` / `IMomChannelResponder` / `MomChannelMessageProcessor` 先固定 Slack adapter 输入输出契约，不直接接真实 SDK）
- [ ] Slack 对接
- [~] workspace / sandbox / tool delegation（已补 workspace memory context、本地 attachment staging、scratch 目录、SYSTEM.md 和 skill docs inventory，仍缺 sandbox/tool delegation 与 skill runtime loader）
- [~] message / runtime flow（已补最小 `log.jsonl` channel history 注入、本地 request/result 写回、`context.json` runtime messages、`last_prompt.jsonl` prompt debug snapshot、`status.json` runtime 状态、本地 busy-state guard、Slack-compatible envelope 与 channel processor busy/stop/typing/thread response seam，仍缺真实 Slack session sync / cancellable stop/queue / 多消息 runtime flow）
- [ ] 更高层 delegation flow 与端到端测试

### Tau.Pods

- [x] config init/list/validate/status
- [x] probe（HTTP endpoint / TCP ssh target）
- [x] exec（SSH pod remote command execution）
- [ ] deploy / stop / restart / health
- [ ] 真正的 CLI 运维命令体系

## P2：工程化

- [ ] release 产物改为真实 Tau 可执行产物
- [ ] solution build 的环境异常诊断文档化
- [ ] provider e2e 测试（当前 Bedrock 已有 StubHandler 级 bearer/SigV4/shared profile/eventstream 回归，Vertex 已有 ADC token/SSE 回归，Gemini CLI/Antigravity 已有 headers/fallback/retry/empty-stream 回归，仍缺真实云端 e2e）
- [ ] coding-agent 默认路径的更高层回归测试
- [ ] 可观测性：provider 调用、auth、tool execution、session / delegation / pod probe 的最小日志
- [ ] `scripts/verify-dotnet.sh` 对运行态 smoke 的进一步自动化
- [x] `scripts/verify-dotnet.ps1` 对运行态 smoke 的进一步自动化（`-RunSmoke` 已覆盖 `WebUi` 与 `Mom --once`）
- [ ] 把 `verify-dotnet.ps1 -RunSmoke` 接到 CI 或补 bash 等价 smoke

## 当前已知环境现实

- [ ] 当前 Windows 环境下 `bash scripts/verify-dotnet.sh --skip-restore` 会落到 WSL 并失败于缺少 `/bin/bash`
- [x] Windows 本机已补 `scripts/verify-dotnet.ps1` 作为等价项目级验证入口
- [ ] 本地标准命令继续保持 bash 形式，但现场执行要接受 PowerShell 脚本或等价顺序 `dotnet` 验证作为兜底
- [x] `Tau.Mom` 也已收回 `ProjectReference`
