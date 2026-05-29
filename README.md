# Tau

Tau 是 [pi-mono](https://github.com/badlogic/pi-mono) 的 .NET 10 移植仓库，目标是在 C# / .NET 生态中重建其核心 AI Agent 能力，而不是简单包一层兼容壳。

当前仓库已经从单纯 **CLI-first 收口** 进入 **多应用面最小产品切片阶段**：

- `Tau.Ai` / `Tau.Agent` / `Tau.CodingAgent` / `Tau.Tui` 已有可运行核心基线
- `Tau.WebUi` 已从 Hello World 推进到 **可持久化 session + provider/model 选择 + 流式/附件/会话管理 + JSON/HTML/Markdown/JSONL 导入导出 + CodingAgent JSONL 只读预览/tree metadata/filter/audit/保守导入** 的 Web 宿主
- `Tau.Mom` 已从纯文本 worker 推进到 **支持结构化委派请求 + 本地 events + Slack-compatible message envelope + transport/responder seam + Slack event mapper + Slack Socket Mode transport seam + Slack Web API responder seam + Slack startup backfill + Slack private file download + per-channel queue dispatcher + true cancellable stop + Mom sandbox/tool delegation seam + runtime delegation response/tool/usage 可观测事件 + 显式 sandbox validation 入口 + Docker sandbox validate/exec 可测试 seam + 附件 staging + workspace layout bootstrap + prompt debug snapshot + 本地多消息 session/model carry-over** 的本地委派宿主
- `Tau.Pods` 已从静态 config CLI 推进到 **支持 probe / exec / health / deploy / stop / restart / model lifecycle / vLLM serve command planner 与 `vllm plan/preflight/deploy/status/health/stop` CLI baseline、vLLM revision/prefetch、health retry/backoff 且 SSH exec 走 ArgumentList argv 构造并结构化处理本地 ssh 进程失败** 的运维 CLI

## 当前状态

- 已有：
  - `Tau.Ai`：消息抽象、流事件、EventStream、provider 注册、model catalog、OAuth / env auth 解析、`auth.json` / `models.json` 本地配置和 secret 状态边界；`models.json` 里显式标记为 OpenAI-compatible 的未知 API 可在运行时注册到 provider registry；Bedrock SSO cache token 过期时可在 AWS CLI cache 含 `clientId` / `clientSecret` / `refreshToken` / 未过期 `registrationExpiresAt` 时调用 OIDC `CreateToken` 刷新，并 best-effort 写回 cache；`JsonlTauLogSink` 写出的 Tau runtime event log 会先生成完整 JSONL，再复用 `JsonlSecretRedactor` 递归脱敏 JSON string value（category、event 和 field value），保留 field key / number / bool / null，可用 `TAU_LOG_REDACT_SECRETS=0` 在 sink 创建时显式关闭；`JsonlSecretRedactor` 提供通用 JSONL 行级 string value redaction foundation，当前已被 CodingAgent JSONL tree session 写出、WebUi JSONL export/import / CodingAgent JSONL preview-import 以及 Mom 的 `log.jsonl` / `last_prompt.jsonl` 脱敏复用；该能力保留 field key，不覆盖非标准 secret pattern，也不是通用 secret scanner
  - `Tau.Agent`：双层循环 runtime、工具执行、状态与事件骨架
  - `Tau.Tui`：交互式输入编辑器、持久化 history、自定义 keybinding、Ctrl+P / Ctrl+Shift+P / Ctrl+L app action dispatch、组件树基础层（`ITuiComponent` / `TuiContainer` / `TuiBox` / `TuiTextBlock`）、单选/多选选择列表基础层（`TuiSelectList` / `TuiMultiSelectList`）、单选列表 footer hint 行、纯函数式差分渲染计划器（`TuiDiffRenderer`）、render surface 合同（`ITuiRenderSurface`）、最小 ANSI diff sink（`TuiAnsiRenderSurface`）、单组件 overlay/input session host（`TuiOverlayHost` / `TuiSelectorSession` / `TuiMultiSelectSession`）、message transcript foundation（`TuiMessageArea`）、status line foundation（`TuiStatusBar`）、viewport/scrollback foundation（`TuiScrollbackBuffer`）、组合 transcript viewport foundation（`TuiTranscriptViewport`）和 viewport host/diff apply wrapper（`TuiTranscriptViewportHost`）；当前已被 `Tau.CodingAgent` 的 `/theme select`、交互式 `/settings` selector、`/scoped-models` selector、`/model select` selector、`/auth select` provider status selector、`/login` OAuth provider selector、`/logout` OAuth provider selector 和 `/thinking select` thinking level selector baseline 复用，但仍未把 scrollback/viewport 接入完整 terminal host、overlay compositing 或硬件 cursor
  - `Tau.Tui` 当前还新增 `TuiTranscriptSession`，把 transcript viewport host 包成可启动/停止、可自动渲染、可处理 PageUp/PageDown/Home/End 滚动输入的 runtime seam；它仍未接 CodingAgent 主屏或完整 terminal lifecycle
  - `Tau.CodingAgent`：最小可运行 CLI、`--print/-p` 非交互单次执行、`--mode rpc` LF JSONL headless baseline、基础文件/命令工具、和 `ModelCatalog` 对齐的默认 provider / model 接线、显式 `Create(provider, model, history)` runner 工厂、JSONL session tree baseline（`/metadata [entry-id]` session/entry inspector、`/session` token/context budget、`/session` retry policy display、`/tree` filter/search modes、`/tree --interactive` navigator + overlay search/filter cycle/Left-Right page/Ctrl-Alt branch navigation/Space fold/selected metadata、`cwd` / `parentSession` metadata、settings `treeFilterMode` / retry fields / default thinking level / steeringMode / followUpMode / autoCompactionEnabled / label timestamp、`/label`、`/fork`、`/fork --summarize [instructions]`、`/clone`、`/resume`、`.jsonl` export/import、`branch_summary` entries、手动/自动 `compaction` entry metadata 与 recent-message retention baseline、`auto_retry_start` / `auto_retry_end` audit entries）、运行中 steering/follow-up CLI baseline、RPC `prompt` / `steer` / `follow_up` / `abort` / `new_session`（含可选 `parentSession` metadata）/ `get_state` / `set_model` / `cycle_model` / `get_available_models` / `set_thinking_level` / `cycle_thinking_level` / `set_auto_retry` / `abort_retry` / `bash` / `abort_bash` / `set_steering_mode` / `set_follow_up_mode` / `set_auto_compaction` / `switch_session` / `get_fork_messages` / `compact` / `fork` / `clone` / `get_session_stats` / `get_messages` / `get_commands` / `export_html` / `get_last_assistant_text` / `set_session_name` baseline、普通回合失败/取消 rollback baseline、host-level retryable error auto-retry baseline、`/retry` settings command baseline、`/thinking [current|select|cycle|off|minimal|low|medium|high|xhigh]` settings/TUI selector baseline、`/auth [current|select|provider]` provider auth 状态与 TUI selector baseline、`/login [select|provider]` OAuth provider selector/login baseline、`/logout [select|provider]` OAuth provider selector/auth.json 清理 baseline 和 `/changelog [count|all]` release notes baseline、context overflow compact-and-retry baseline、prompt template discovery/expansion、skill command discovery/expansion、JSON 声明式 extension command / prompt/skill resource discovery / load diagnostics baseline、带 cwd/parent metadata、branch JSONL timeline、branch outline filter/search、label/model/compaction/branch-summary/retry entries、内嵌 JSONL 下载、message deep-link/copy-link、code fence rendering baseline、inline code span rendering baseline、plaintext link rendering baseline、Markdown heading/list/blockquote block rendering baseline、嵌套 list 会保留子 `<ul>/<ol>` 位于父 `<li>` 内的 HTML 结构、Markdown strong/emphasis span rendering baseline、Markdown pipe table rendering baseline、Markdown task list rendering baseline、image metadata caption baseline、long tool result folding baseline、tool call JSON argument rendering baseline 和 long tool call arguments folding baseline 的 `.html/.htm` transcript export，以及基于 GitHub CLI 的 `/share` secret Gist baseline
  - `Tau.CodingAgent` 还具备 `switch_session` / `get_fork_messages` RPC session utility baseline：`switch_session` 可切到指定 JSONL tree session，`get_fork_messages` 可返回当前 tree 里的 user message 列表供 fork selector 使用；`/hotkeys` 当前 editor keybinding listing baseline 可显示交互式 editor 实际注入的 `IKeyBindingMap`；Ctrl+P / Ctrl+Shift+P 在空闲输入 prompt 会按 settings `enabledModels` scope 或全部可用模型切换到下一个/上一个模型，并保存默认 provider/model；Ctrl+L 会打开模型选择器并保留当前输入 draft；`/reload` baseline 可在当前进程重读 settings、JSON extension resources、prompts、skills、context files、theme status 和交互式 editor keybindings，并把重新加载的 skills/context files 回灌给 runner system prompt；`/theme select` 会在真实交互式 editor 会话中打开 TUI selector，选择后写回 settings `theme`，取消时不修改 settings；交互式 `/settings` 会打开 TUI settings selector baseline，可选择 auto-compaction、steering/follow-up mode、tree filter、thinking level、scoped models 或 theme 并写回 settings；`/thinking select` 会在真实交互式 editor 会话中打开当前模型可用的 thinking level selector，选择后立即按模型能力 clamp、更新 runner 并写回 settings `defaultThinkingLevel`；交互式 `/scoped-models` 会打开多选 selector，支持过滤、模型 toggle、provider toggle、enable all、clear、显式保存和取消，并保留既有 per-entry thinking suffix；`enabledModels` 条目支持 `provider/model:off|minimal|low|medium|high|xhigh`，Ctrl+P/Ctrl+Shift+P 和 RPC `cycle_model` 切到带 suffix 的 scoped model 时会同步 runner/default thinking 并按目标模型能力 clamp；交互式裸 `/model`、`/model select [search]` 或 Ctrl+L 会打开 `CodingAgentModelSelector`，按当前 `enabledModels` scope 或全部已配置凭证模型展示单选列表，有 scoped 候选时可用 Tab 在 `scoped` / `all` 之间切换，顶部显示 `Model Selector` / `Search:` 轻量 chrome，普通字符会更新搜索过滤，Backspace 会回退搜索，列表下方显示 `Model Name: ...`，并在底部提示只显示已配置凭证模型，选择后保存默认 provider/model 并重新 clamp 当前 thinking；实际 `/model`、`/provider`、Ctrl+P/Ctrl+Shift+P、Ctrl+L 和 RPC `get_available_models` / `set_model` / `cycle_model` 都只使用 auth-configured provider/model，`/scoped-models` 仍维护全部注册模型 scope；`/auth select` 会打开 provider auth status selector，只检查当前 provider credential 状态，不写凭证、不启动 OAuth login；交互式裸 `/login` 或 `/login select` 会筛选当前注册且有 OAuth provider 的 provider，选择后调用现有 OAuth login flow 并保存到 `auth.json`；交互式裸 `/logout` 或 `/logout select` 会筛选当前有本地 OAuth credential 且注册了 OAuth provider 的 provider，选择后只删除对应 `auth.json` credential entry；`/changelog` baseline 会读取 `docs/releases/feature-release-notes.md` 或 `TAU_CODING_AGENT_CHANGELOG_FILE` 指定文件并输出最近 release notes；完整上游 app/session/tree/extension shortcut registry、完整多层 settings selector、完整 model selector theme/terminal host parity、per-entry thinking UI editor、TUI theme rendering、TypeScript extension runtime、完整 OAuth login dialog/session parity、credential refresh UX、启动 changelog 更新提醒、`collapseChangelog` 设置和安装遥测仍未完成
  - `Tau.CodingAgent` 已补 `/settings [current|path|select]` CLI/settings/TUI selector baseline：无 selector 的会话保留 settings 摘要，交互式会话的裸 `/settings` 或显式 `/settings select` 会打开 TUI selector；`/settings current` 显示当前 settings 文件路径、当前 provider/model 与 thinking、默认 provider/model、tree filter、retry policy、default thinking、steering/follow-up mode、auto-compaction 设置和 scoped models 状态，`/settings path` 只输出路径；settings selector 可继续进入 scoped models 多选 selector；当前不等于完整上游 settings list/submenu、images/terminal/transport/packages 等全量配置面或可编辑 TUI parity
  - `Tau.CodingAgent` 已补 `/thinking [current|select|cycle|off|minimal|low|medium|high|xhigh]` CLI/settings/TUI selector baseline：裸 `/thinking` 和 `/thinking current` 继续只显示当前 reasoning level，`/thinking select` 在真实交互式 editor 会话中打开 `CodingAgentThinkingSelector`，列表按当前模型能力展示可用档位和说明文案，选择后立即更新 runner `ThinkingLevel` 并写回 settings `defaultThinkingLevel`；无 selector 会话返回明确 unavailable，取消不修改 settings。模型能力 clamp 已接入：非 reasoning 模型归一 off，不支持 xhigh 的 reasoning 模型把 xhigh 归一 high。当前仍不是完整上游全 settings selector parity
  - `Tau.CodingAgent` 已补 `/scoped-models [current|select|set|add|remove|clear|all] [provider/model[:thinking] ...]` CLI/settings/TUI selector baseline：无 selector 的会话保留当前 scope 摘要，交互式会话的裸 `/scoped-models` 或显式 `/scoped-models select` 会打开多选 selector；selector 支持 filter、Enter/Space toggle、Ctrl+A enable all、Ctrl+X clear、Ctrl+P provider toggle、Alt+Up/Down reorder、Ctrl+S save 和 Esc cancel，保存后写 settings `enabledModels` 有序模型 scope，并保留既有 per-entry thinking suffix；`enabledModels` 条目支持 `:off|minimal|low|medium|high|xhigh`，空闲输入 prompt 的 Ctrl+P / Ctrl+Shift+P 和 RPC `cycle_model` 会复用该 scope 切换模型，并在命中 suffix 时同步 runner/default thinking 后按目标模型能力 clamp；`clear` / `all` 回到 all enabled / no filter。当前不等于完整上游 per-entry thinking 编辑 UI parity
  - `Tau.CodingAgent` 已补 `/model [current|select [search]|provider/model|model] or /model <provider> <model>` CLI/settings/TUI selector baseline：交互式会话中裸 `/model` 或 `/model select [search]` 会打开 `CodingAgentModelSelector`，复用当前 TUI selector foundation，优先展示当前 `enabledModels` scope 中已配置凭证的模型，scope 缺失时展示全部已配置凭证模型；有 scoped 候选时默认进入 `scoped`，可用 Tab 切到 `all` 或切回 `scoped`；`select [search]` 会设置初始过滤，打开后顶部固定显示 `Model Selector` 和 `Search:`，普通字符输入会继续更新搜索过滤，Backspace 回退过滤；列表下方显示 `Model Name: ...`，底部显示 `Only showing models with configured auth`，选择后更新当前 runner model、保存 settings 默认 provider/model、同步 tree session 并重新 clamp 当前 thinking；显式 `/model` 和 `/provider` 也会拒绝未配置凭证的 provider/model。无 selector 的裸 `/model` 继续显示 current model，无 selector 的 `/model select` 返回明确 unavailable。Ctrl+L 触发同一 selector 并保留当前输入 draft；当前不等于完整上游 theme/dynamic-border/terminal-host parity 或 per-entry thinking 编辑 UI parity
  - `Tau.CodingAgent` 已补 RPC `get_settings` / `update_settings` baseline：headless client 可以读取和批量更新 Tau 当前已真实生效的 settings 字段，并把默认模型、thinking、retry、steering/follow-up queue mode、auto-compaction 开关与 theme 字段同步到当前 runner/settings；当前不等于完整上游 settings selector UI、theme selector / terminal / packages 等全量配置面 parity
  - `Tau.CodingAgent` 已补 interactive tree fold persistence seam：JSONL tree session 会追加 `tree_state` metadata entry 持久化 collapsed entry ids，`/tree --interactive` 退出时写入 session-scoped fold snapshot，启动时优先恢复当前 session 的 folded ids，再回退旧 settings `treeCollapsedEntryIds`；settings 回写仍作为兼容 fallback 保留。当前仍不是完整上游 TreeSelector、多选或 TUI metadata inspector
  - `Tau.CodingAgent` JSONL tree session 写出默认复用 `JsonlSecretRedactor`：session header、message、model/session metadata、compaction、tree state 等 append-only JSONL 行会按 `TAU_CODING_AGENT_REDACT_SECRETS` 递归脱敏 string value，object key、number、bool 和 null 保持原样；`TAU_CODING_AGENT_REDACT_SECRETS=0` 可关闭。被写入的 secret 会被替换为 `[redacted]`，不是可恢复加密
  - `Tau.WebUi`：最小聊天页、status/catalog/session/messages/auth API、本地 session 持久化、provider/model 选择、NDJSON 流式消息、附件、tool timeline、session delete/export/import/rename/restore baseline、JSON/HTML/Markdown/线性 JSONL transcript 导入导出、`.jsonl` 前端上传识别、JSONL import `application/problem+json` 错误码/行号返回、WebUi JSONL export/import 与 CodingAgent JSONL preview/import 默认 string-value secret redaction（`TAU_WEBUI_REDACT_SECRETS=0` 可关闭）、CodingAgent JSONL session 只读 preview endpoint、preview tree metadata、`search` / `currentBranchOnly` preview filter、conservative import source tree/audit result、CodingAgent JSONL conservative import endpoint、runtime-coding-agent 接线
  - `Tau.Mom`：本地文件委派处理链，支持 `.txt/.md/.json` 请求、`provider/model/workingDirectory/title/metadata/attachments` 结构化字段、`MomLocalDelegationFlow` 统一 events -> inbox 与 file delegation、同 workdir/channel 的 provider/model/session metadata carry-over、Slack-compatible channel message envelope、transport/responder/processor seam、Slack event mapper、Slack Socket Mode transport seam、Slack Web API responder seam、Slack startup backfill、Slack private file download、per-channel queue dispatcher、true cancellable stop、Mom sandbox/tool delegation seam、runtime delegation response/tool/usage log events、`--validate-sandbox` 显式 sandbox validation 入口、Docker sandbox validate/exec 可注入 process runner seam、本地附件 staging、本地 `events/*.json` 唤醒、workspace layout bootstrap、`log.jsonl` / `last_prompt.jsonl` 默认 secret redaction baseline、`--once`、outbox 结果落盘和 archive 归档
  - `Tau.Pods`：`init / list / validate / status / probe / exec / health / deploy / stop / restart / model list / model pull / model remove / model status / vllm plan / vllm preflight / vllm deploy / vllm status / vllm health / vllm stop` 命令、sample config、validator、主动 endpoint/tcp 探测、SSH 远程命令执行、SSH lifecycle metadata 管理、SSH exec `ArgumentList` argv hardening、长耗时 model pull keepalive、revision-aware HF download、vLLM serve command planner 与 SSH orchestration baseline、deploy prefetch 闭环、本地 ssh 启动失败/runner 异常/cancellation 结构化返回、AOT 友好的 JSON source-gen
- 未完成：
  - 完整 TUI 交互层、message/status area 与真实 terminal host 的集成、overlay compositing、viewport/scrollback 管理、硬件 cursor、完整 OAuth login dialog/session / resource selector UI
  - 完整上游 TreeSelector、多选 / session-scoped richer fold state / richer metadata inspector、上游自动 branch switching summarization hooks、上游 LLM split-turn summarization / compaction extension events / cancellation UI parity、上游 auto-retry settings UI parity、完整 retry cancellation UI、完整 TUI 运行中输入 overlay / keybinding hints、完整 RPC extension UI / streamed bash output / 上游 settings selector 与全量配置面 parity、完整 TS extension runtime/custom tools/events、theme selector / TUI theme rendering / theme file watcher、full resource selector、完整 model selector theme/terminal host parity、per-entry thinking 编辑 UI、完整 Markdown/highlight renderer、richer HTML template / share viewer parity、模式/配置系统
  - Web UI 与 CodingAgent branch/tree session 的完整语义对齐；当前只有只读 preview 和 preview-derived conservative import，不会替换 WebChatStore，也不保留完整 branch/tree 语义
  - Mom 的真实 Slack smoke、真实 Docker sandbox smoke 和更高层 workspace/session 委派；当前只有本地 workdir/channel provider/model carry-over
  - Pods 的真实 vLLM smoke、更完整模型生命周期、更完整远端 transport hardening、失败回滚和真实运维 smoke；当前只有 SSH command/service orchestration baseline 与 `vllm plan/deploy/status/health/stop` 入口
  - 更高层行为回归测试

更细的状态请看：

- `docs/ARCHITECTURE.md`
- `docs/QUALITY_SCORE.md`
- `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
- `docs/exec-plans/active/2026-04-23-tau-port-baseline.md`（历史 P1 基线与决策记录）

## 当前应用面最小能力

### Tau.WebUi

- `/` 返回内嵌 HTML/JS 的聊天页
- `/healthz`
- `/api/status`
- `/api/catalog`
- `/api/sessions`
- `/api/sessions/{id}`
- `POST /api/sessions`
- `PUT /api/sessions/{id}`
- `POST /api/sessions/{id}/messages`
- `POST /api/sessions/{id}/messages/stream`
- `GET /api/sessions/{id}/export`
- `GET /api/sessions/{id}/export.jsonl`
- `GET /api/sessions/{id}/export.html`
- `GET /api/sessions/{id}/export.md`
- `POST /api/sessions/import`
- `POST /api/sessions/import.jsonl`
- `POST /api/sessions/import.coding-agent-jsonl/preview`
- `POST /api/sessions/import.coding-agent-jsonl`
- `DELETE /api/sessions/{id}`
- 会话持久化到 `output/webui-sessions.json`
- 每个 session 可独立选择 provider / model，并支持 title rename 与 last-opened session restore baseline

### Tau.Mom

- 默认扫描 `mom/inbox`
- 默认扫描 `mom/events`
- 输出结果到 `mom/outbox`
- 原始请求归档到 `mom/archive`
- 支持 `.txt / .md / .json` 三种输入
- `.json` 请求支持：
  - `prompt`
  - `provider`
  - `model`
  - `workingDirectory`
  - `title`
  - `metadata`
  - `attachments`
- `title` 会写入当前 delegation session name，`metadata` / `attachments` 会作为结构化上下文注入 runner 输入
- `.txt/.md/.json` 请求、`mom/events/*.json` 和 Slack Socket Mode event JSON 都会先映射成 `MomChannelMessage` / `MomChannelAttachment`，统一 channel、user、ts、thread、attachment、provider/model/title/metadata 语义；当前 Slack event mapper 已覆盖 `app_mention`、DM、bot/self/subtype skip、mention stripping 和文件元数据；`SlackSocketModeTransport` 已能通过 `auth.test` / `apps.connections.open` 打开 Socket Mode URL、读取 WebSocket 文本帧并 ack envelope，再交给 mapper
- `IMomChannelTransport` / `IMomChannelResponder` / `MomChannelMessageProcessor` 已固定未来 Slack adapter 的输入输出边界：transport 读入 channel message，responder 负责响应/thread/typing/upload，processor 负责 busy-state、true cancellable stop、attachment staging、status/log writeback 与 runner 调用；`SlackEventMapper` 已固定 Socket Mode event JSON 到 channel envelope 的过滤/字段规则，`SlackSocketModeTransport` 已固定 open/ack/read/reconnect 接缝并可由 `SlackSocketModeEnabled` 启用，`SlackBackfillService` 会在 Socket Mode worker 启动前对已有 `log.jsonl` 的 channel 调 `conversations.history`，按 oldest/cursor 回填旧消息但不触发 delegation，`SlackWebApiResponder` 已用 `HttpClient` 固定 `chat.postMessage`、`chat.update`、`chat.delete` 和 `files.uploadV2` 调用契约；如果 responder 支持 runtime response，processor 会先发 `_Thinking_ ...` 占位，完成后更新最终消息，遇到 `[SILENT]` 响应会删除占位且不追加最终回复；`SlackAttachmentDownloader` 已用 bot token 下载 Slack private file URL 并写入 attachment manifest，`MomChannelQueueDispatcher` 已固定 per-channel 顺序处理、pending queue limit 和 stop bypass；`MomChannelRunRegistry` 会让 stop 命令取消当前 in-process runner token，并在完成后写 `cancelled` 状态
- runner 执行前会确保本地 workspace/channel layout 存在：`scratch/`、workspace-level `skills/`、channel-level `skills/`、`attachments/` 和 `events/`
- runner 输入会先注入 `<mom_runtime_context>`，说明本地 workspace/channel layout、`SYSTEM.md`、`scratch/`、skill docs、events 文件格式、attachment manifest、memory/log/status 路径和 `[SILENT]` 事件响应约定；这只是本地 Mom 语义，不等于 Slack adapter 已接通。skill docs 按上游 Agent Skills 格式暴露为 `<available_skills>`，脚本仍通过 `bash/read/write/edit` 使用，不会额外注册成直接 tool 名
- `MomOptions.Sandbox` 默认 `host`，可配置为 `docker:<container>`；当前已固定 host executor、docker command/path translation seam、`--validate-sandbox` 显式 validation 入口、Docker validate/exec 可注入 process runner seam、workspace path authority 和 `bash/read/write/edit/attach` 五个上游同名 Mom tools，真实 Docker container smoke 仍后置
- `RuntimeDelegationAgentRunner` 默认会为 Mom 创建专用工具集，而不是继续暴露通用 CodingAgent 的 `shell/read_file/write_file/edit_file` 名称；`attach` 会把 workspace 内文件回传到 `DelegationExecution.Attachments`，Slack channel processor 会在响应后调用 responder upload
- 每次调用 runner 前会写 `workingDirectory/last_prompt.jsonl`，记录 mom runtime context、delegation context、实际 runner input、恢复的 session messages、当前 user prompt、attachment count 和 image attachment count，便于对照上游 mom 的 prompt/debug 行为排查后续 Slack/session 问题；默认会对常见 secret pattern 做 JSON string value 脱敏，可用 `TAU_MOM_REDACT_SECRETS=0` 显式关闭
- 本地存在的 `attachments` 文件会复制到 `workingDirectory/attachments/<timestamp>_<filename>`，Slack private file URL 会先用 `SlackBotToken` 下载到同一目录，再通过 `attachments/attachments.jsonl` 记录 `original/local/source`；`log.jsonl` 写入时会保留上游 mom 风格的 `original/local` 附件元数据
- `workingDirectory/MEMORY.md` 与其父目录的 `MEMORY.md` 会作为 workspace memory 注入同一段上下文
- 父目录 `SYSTEM.md` 的非空内容会以 `system_configuration_log` 注入同一段上下文；它用于记录安装包、环境变量、配置文件等本地环境修改
- workspace/channel `skills/**/SKILL.md` 会被扫描为 prompt 中的 Agent Skills XML inventory；channel skill 会覆盖 workspace 同名 skill，`disable-model-invocation: true` 会隐藏，脚本仍通过 `bash/read/write/edit` 使用
- `workingDirectory/log.jsonl` 会记录本地 file delegation 的用户请求与 bot 结果；最近非 bot 文本消息会作为 channel history 注入同一段上下文，malformed 行、空文本、当前 `ts` 会被跳过
- `workingDirectory/context.json` 会保存 Tau-native channel session snapshot；同一 workdir 后续 delegation 会先恢复上一轮 runtime messages，再在完成后写回当前 messages/provider/model/session name
- `workingDirectory/status.json` 会记录当前或最近一次 delegation 的 `running/completed/failed/cancelled` 状态、请求文件、provider/model、workdir、时间、错误与响应摘要；本地 worker 会跳过同一 workdir 内未过期的 `running` 状态，默认 60 分钟后视为 stale，给 Slack busy-state / stop flow 留本地状态面
- `mom/events/*.json` 支持上游 mom 风格的 `immediate`、`one-shot`、`periodic` 事件，事件会被转换成 inbox `.json` 委派请求；`channelId` 会映射到 `DefaultWorkingDirectory/<channelId>`，`immediate/one-shot` 入队后删除，`periodic` 按五段 cron + timezone 在同一分钟只入队一次
- outbox `.json` 会回写 `title` / `attachments`，并保留 `stopReason`、结构化 tool events 与 usage
- 支持 `--once`

### Tau.Pods

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
- sample `tau.pods.json`

当前 `Pods` 行为：

- `probe`
  - 对 `endpoint` 做 HTTP GET 健康探测
  - 对 `sshHost/sshPort` 做 TCP 连通性探测
- `exec`
  - 对 SSH pod 通过系统 `ssh` 客户端执行远程命令，进程启动参数使用 `ProcessStartInfo.ArgumentList` 构造，避免本地 argv 拼接和 shell quoting 漂移
  - 本地 `ssh` 启动失败、runner 异常和 cancellation 会返回结构化 failure result，不直接把底层异常炸出 CLI；错误文本只保留异常类型和短消息，不带 stack trace 或远端命令内容
  - 对 endpoint pod 明确返回 unsupported
- `health`
  - 对 enabled pods 执行 HTTP `/health` 或 SSH `echo ok`
- `deploy / stop / restart`
  - 对 SSH pod 写入/删除 `~/.tau_pods/<deployment>.json` lifecycle metadata
  - 对 endpoint pod 明确返回 unsupported
  - `<path>` 可省略；`deploy gpu-1 model-id` 使用默认 `tau.pods.json`，`deploy custom.json gpu-1 model-id` 使用显式配置文件
- `model list / pull / remove / status`
  - 只支持 SSH pod；endpoint pod 明确返回 unsupported
  - 默认 remote Hugging Face cache 路径为 `$HOME/.cache/huggingface/hub`，pod config 可用 `modelsPath` 覆盖
  - `pull` 优先执行 `huggingface-cli download <model> --cache-dir <modelsPath>`，`--revision` / `--snapshot` 会传给两条 Hugging Face 下载路径；目标环境缺少 CLI 时回退 `python -m huggingface_hub.commands.huggingface_cli download ...`
  - `status` 区分 cached / missing；missing 会作为命令失败返回，但不是 SSH transport failure
- `vllm plan`
  - 只读取本地 config 并调用 `PodVllmCommandPlanner` 打印 plan-only 输出
  - 默认输出文本 plan；带 `--json` 时输出 machine-readable JSON plan，包含 deployment/modelPath/port/servedModel/systemd unit/metadata/remote command
  - 不执行 SSH、不调用 `systemctl`、不写远端状态
- `vllm preflight / deploy / status / health / stop`
  - 只支持 SSH pod；endpoint pod 明确返回 unsupported
  - `preflight/deploy` 会先解析远端 HF cache snapshot；指定 `--revision` / `--snapshot` 时优先查 `refs/<revision>` 或 `snapshots/<revision>`，否则使用有效 `refs/main` 或唯一 snapshot
  - `deploy` 复用 planner 生成 serve/systemd/metadata 计划，再通过 SSH 写 `~/.tau_pods/<deployment>.service` / `.json`
  - `deploy --prefetch` 只在 preflight 失败且失败类型属于模型 cache/snapshot 缺口时调用 `model pull`，随后重新 preflight；下载失败不会执行 service 写入或 rollback
  - 远端优先使用 `systemctl --user enable --now` 启动 user unit；没有 systemd 或 systemd user command 失败时 fallback 到 `nohup`、`.pid` 和 `.log`
  - `deploy` 默认按 12 次、5 秒 backoff 执行远端 `/health` readiness 窗口；health 非 ready 或部署命令失败时会尝试 rollback cleanup；可用 `--no-health` 跳过探测，也可用 `--health-attempts` / `--health-backoff-ms` 调整窗口
  - `status` 查询 systemd user unit、fallback pid 或 metadata；`health` 默认单次探测远端 `http://127.0.0.1:<port>/health`，也可显式设置 retry/backoff，并返回 `ready/unhealthy/dead/starting/unknown`、`failureKind` 和 attempts；`stop` disable/remove user unit 或 kill fallback pid，并清理 metadata/service/pid
  - 当前是 SSH command/service orchestration baseline，prefetch 仍只由 fake runner 固定命令合同，尚未做真实 GPU pod、HF download、vLLM 启动 smoke 或多版本 rollout smoke

## 本地开发

仓库标准验证链：

```bash
bash scripts/verify-dotnet.sh
bash scripts/verify-dotnet.sh --skip-restore
```

Windows PowerShell 等价入口：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore
powershell -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore -RunSmoke
```

No-env PowerShell 验证入口：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\verify-no-env.ps1 -SkipRestore
powershell -ExecutionPolicy Bypass -File .\scripts\verify-no-env.ps1 -SkipRestore -RunSmoke
```

`verify-no-env.ps1` 通过 `scripts/invoke-no-env.ps1` 只对子进程清除 OpenAI / Anthropic / Google / Azure / AWS / HF / Slack / Tau auth/config 相关环境变量，设置 `PI_NO_LOCAL_LLM=1` 与 `TAU_NO_LOCAL_LLM=1`，并把 `TAU_AUTH_FILE`、`TAU_MODELS_FILE`、CodingAgent session/settings/history/keybindings 和 `TAU_LOG_FILE` 指到临时目录。脚本不会移动用户真实 `auth.json`，也不会输出任何 secret 值；`-RunSmoke` 会额外做 `tau-ai list` 和 CodingAgent RPC `get_state` no-env smoke。

CodingAgent 直达 no-env wrapper：

```powershell
$rpcInput = Join-Path $env:TEMP "tau-rpc-input.jsonl"
[System.IO.File]::WriteAllText($rpcInput, '{"id":"state","type":"get_state"}' + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
powershell -ExecutionPolicy Bypass -File .\scripts\pi-test.ps1 --no-env --no-build --input-file $rpcInput -- --mode rpc --no-context-files --no-themes
```

`pi-test.ps1` 对照上游 `pi-test.sh --no-env` 的职责，直接启动 `Tau.CodingAgent`，并在 `--no-env` 时复用 `invoke-no-env.ps1` 的子进程环境清理和临时 Tau 状态。需要给 RPC/stdin 场景喂输入时使用 `--input-file <path>`；脚本会用 shell redirection 交给子进程，避免 PowerShell stdin BOM 破坏 JSONL。该 wrapper 是 PowerShell-first 等价入口，不移动用户真实 auth 文件，也不是 Unix shell wrapper。

Release artifact baseline：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\plan-release.ps1 patch -CurrentVersion 0.1.0
powershell -ExecutionPolicy Bypass -File .\scripts\build-release-artifacts.ps1 -Configuration Release
powershell -ExecutionPolicy Bypass -File .\scripts\smoke-release-artifacts.ps1 -ArtifactRoot .\artifacts\tau-win-x64
powershell -ExecutionPolicy Bypass -File .\scripts\package-release-artifacts.ps1 -ArtifactRoot .\artifacts\tau-win-x64
powershell -ExecutionPolicy Bypass -File .\scripts\build-release-matrix.ps1 -Runtimes win-x64
powershell -ExecutionPolicy Bypass -File .\scripts\package-release-matrix.ps1 -Runtimes win-x64
```

`plan-release.ps1` 对照上游 `scripts/release.mjs` 的 clean worktree、version bump / explicit semver、changelog release section、commit/tag、publish 和 push 流程，生成 Tau 的 dry-run 发布计划。它会读取 `git status`、检查 release notes 与 release 脚本是否存在、计算 `major|minor|patch` 或显式 `x.y.z` 的下一版本，并列出应运行的 no-env gate、release matrix build/package 命令；脚本不会修改版本、release notes、history，不会执行 `git commit`、`git tag`、publish 或 push。当前 Tau 还没有 repo-owned `Version` / `VersionPrefix` / `PackageVersion`，所以 bump 目标需要传 `-CurrentVersion x.y.z` 才能计算下一版本；显式 `x.y.z` 目标可以规划，但会记录无法和当前仓库版本比较。`-AllowDirty` 只用于 planning-only 场景，真实 release 前仍要求 clean worktree。

如果需要用 `build-release-artifacts.ps1 -SkipRestore` 做离线/复用 restore 的发布验证，必须先执行带 RID 的 restore，例如 `dotnet restore Tau.slnx -r win-x64 --verbosity minimal`；普通 `dotnet restore Tau.slnx` 不会生成 `net10.0/win-x64` publish 需要的 assets target。

当前 release artifact 输出到 `artifacts/tau-<rid>/`，Windows 当前平台是 `artifacts/tau-win-x64/`。目录内包含 `apps/**` 的 `dotnet publish` 输出、`manifest.json`、完整当前 `docs/` payload、`README.md`、`LICENSE`，以及 `bin/pi.cmd`、`bin/tau-ai.cmd`、`bin/pi-ai.cmd`、`bin/mom.cmd`、`bin/pi-pods.cmd`、`bin/tau-web-ui.cmd` wrapper。`manifest.json` 的 `releasePayload` 会审计上游 `build-binaries.sh` 复制清单：`readme`、`license`、`docs` 为已复制；`changelog` 记录为 Tau-native `docs/releases/feature-release-notes.md`；`package-json` 记录为 Tau-native `manifest.json`；`theme` 与 `export-html` 记录为编译进 Tau 代码的 inline 实现；当前没有 root `examples/`、Photon wasm/image resize pipeline、交互式 raster assets 或 Bun/koffi native module 依赖，因此分别以 `missing` 或 `not-applicable` 留在 manifest 审计里。artifact smoke 会验证 payload manifest、release notes 文件、AI CLI provider list、`pi` RPC `get_state`、Pods help、WebUi health/status/catalog/session store 和 Mom `--once` 本地 delegation 链路。

`package-release-artifacts.ps1` 支持 `-ArchiveFormat auto|zip|tar.gz`，`auto` 会按 RID 对齐上游 `build-binaries.sh`：`win-*` 生成 `artifacts/releases/tau-<rid>.zip`，`linux-*` / `osx-*` 生成 `artifacts/releases/tau-<rid>.tar.gz`。脚本会保留 `tau-<rid>` 顶层目录，解压到全新临时目录并校验结构；只有 archive RID 等于宿主 RID 时才默认运行 executable smoke，非宿主 RID 只做归档/解压结构校验，除非显式传 `-ForceSmoke`。`package-release-matrix.ps1` 用于对已经存在的 `artifacts/tau-<rid>/` 批量归档，默认矩阵是 `osx-arm64`、`osx-x64`、`linux-x64`、`linux-arm64`、`win-x64`；`build-release-matrix.ps1` 则按同一矩阵逐个 restore/build/package。当前这把 Phase 5 从 current-RID zip 推进到上游风格 archive format/RID matrix baseline，并补上当前 Tau release payload copy/manifest audit baseline；仍不声明非宿主平台已在本机执行 smoke，也不关闭 examples/Photon/interactive raster assets parity、exact auth-backup parity、version/changelog/tag/publish automation 或真实外部 e2e release smoke。

GitHub Actions CI baseline：

```text
.github/workflows/tau-ci.yml
```

当前 CI 在 `push main`、`pull_request` 和手动触发时运行 Windows PowerShell gate：按 `global.json` 安装 .NET SDK，`dotnet restore Tau.slnx`，执行 `verify-no-env.ps1 -SkipRestore -RunSmoke`，构建 Release artifact，通过 `package-release-matrix.ps1 -Runtimes win-x64` 打包 `artifacts/releases/tau-win-x64.zip`，再用 `package-release-artifacts.ps1 -ArchiveFormat tar.gz -SkipExecutableSmoke` 做 tar.gz 格式/解压结构 smoke，并先解压 smoke Windows zip 后再上传该 zip 作为 workflow artifact。该 workflow 复用仓库现有 PowerShell 脚本，不另建一套 CI-only 行为；它关闭的是 Windows current-RID CI/release artifact baseline，不代表非宿主平台 executable smoke、Unix shell wrapper、version/tag/publish automation 或真实外部 e2e release smoke 已完成。

当前机器上的现场现实：

- 项目级验证仍保留 bash 入口，但旧 harness-init 的通用脚手架已移除
- 但本机 bash 服务可能报 `Bash/Service/CreateInstance/E_ACCESSDENIED`，或者落到 WSL 后缺少 `/bin/bash`
- 因此 Windows 本机优先使用 `scripts/verify-dotnet.ps1`
- 如果两类脚本入口都不可用，再退回等价顺序的 `dotnet build/test/run` 命令

当前已确认的运行 / 验证现实是：

- `Tau.CodingAgent.csproj` 已可独立 `dotnet build --no-restore`
- `Tau.CodingAgent` 已有 flat JSON session 和 JSONL tree session 双路径；`/session` 会显示估算 token、模型 context window、auto-compaction threshold budget、当前 retry policy、JSONL `cwd` 和 clone/export 产生的 `parentSession`；`/tree [max entries] [default|no-tools|user-only|labeled-only|all] [--label-time] [--search query]` 会读取 settings `treeFilterMode` 作为默认过滤模式，并在 header 中显示 `cwd` / `parent` metadata；`/tree --interactive` 支持移动、选择、取消、overlay search、filter cycle、n/N 搜索跳转、普通 Left/Right 分页、Ctrl/Alt+Left 折叠或跳到上一个分支段、Ctrl/Alt+Right 展开或跳到下一个分支段、Space fold/expand 和 selected entry type/depth/branch/leaf metadata；`/fork <entry-id> --summarize [instructions]` 会先收集被离开 branch 的消息，调用当前模型生成结构化摘要，把 JSONL `branch_summary` entry 挂到目标 entry 下，并在 branch restore 时把摘要作为上下文 user message 注入 runtime；settings JSON 还可保存 retry max attempts/base delay、default thinking level、steering/follow-up queue mode 和 auto-compaction enabled override，生产入口按 settings 优先、env 兜底读取 retry 策略，并会把 default thinking level 按初始模型能力 clamp 后与 queue mode 恢复到 runner；交互式 console 在 runner streaming 期间会通过 `ICodingAgentTurnInputSource` 接收运行中输入，Enter 转为 steering，Alt+Enter 转为 follow-up，并调用 `AgentRuntime` 现有队列；manual / auto compaction 会写 JSONL `compaction` entry，默认从当前 compaction boundary 后优先按 `TAU_CODING_AGENT_COMPACT_KEEP_RECENT_TOKENS` 的 token budget 保留 recent messages，再回落到最近 4 条 message，可用 `TAU_CODING_AGENT_COMPACT_KEEP_RECENT_MESSAGES` 调整，branch restore / resume / clone export 会重建 summary + retained messages + post-compaction messages；当 retained cut point 落在一个 user turn 中间时，JSONL 会写 `isSplitTurn` / `turnPrefixSummary`，恢复 runtime 时把 split-turn prefix context 拼入 summary message，HTML timeline 也会展示并可搜索；context overflow 错误会恢复失败回合前 snapshot、自动 compact、记录 `fromHook=true` compaction boundary，然后重试同一输入；普通 runner exception、取消或 provider error 型 `AgentEndEvent` 会恢复回合前 snapshot，避免失败输入污染 flat JSON / JSONL tree session；`/retry [current|default|off|<max attempts> [base delay ms]]` 会查看或修改同进程 retry 策略并写入 settings；`/thinking [current|select|cycle|off|minimal|low|medium|high|xhigh]` 会查看、交互式选择、设置、循环或关闭当前 runner 的 reasoning level，并按当前模型能力 clamp 后写入同一 settings 文件；`TAU_CODING_AGENT_AUTO_RETRY_ATTEMPTS` / `TAU_CODING_AGENT_AUTO_RETRY_BASE_DELAY_MS` 继续作为 settings 未配置时的 env fallback；retry 前同样恢复回合前 snapshot，并向 JSONL tree 写入 `auto_retry_start` / `auto_retry_end` audit entries；成功时先持久化成功 attempt 的 user/assistant messages，再写 retry end，失败或耗尽时只保留 retry audit 而不持久化失败输入；retry delay 被取消时会显示明确状态、写 `Retry cancelled` end audit 并保持失败输入不落盘；HTML transcript export 会显示 tree cwd / parent metadata，按 current branch JSONL 渲染 message、`session_info`、`model_change`、`label`、`compaction`、`branch_summary` 和 retry timeline entries，branch outline 支持 `default/no-tools/user-only/labeled-only/all` 过滤和搜索，给消息节点写入 JSONL entry id、提供 per-message copy link，并支持 `targetId` deep link 定位；branch summary timeline 会显示摘要、来源 entry，以及 read/modified file 列表；文本内容中的 fenced code block 会渲染为独立 `code-block` / `<code>` 区块并继续安全 HTML escape，普通文本里的 backtick inline code span 会渲染为 `<code class="inline-code">`，普通文本段会把 Markdown-style `[label](http/https...)` 与裸 `http(s)` URL 渲染成外链，code fence 和 inline code 内不做链接化，非 http(s) scheme 仍按文本安全输出；普通文本段检测到 heading/list/blockquote block marker 时会改用轻量 rich-text block rendering，渲染 `h1`-`h6`、`ul/ol/li` 和 `blockquote`，并继续复用 inline code/link 安全输出；普通文本里的 `**strong**` / `__strong__` 和 `*emphasis*` / `_emphasis_` 会分别渲染为 `<strong>` / `<em>`，inline code 和 code fence 内不会触发 emphasis；普通文本段中的 Markdown pipe table（header + separator + data rows）会渲染为可横向滚动的 `<table>`，单元格继续复用 inline code/link/strong/emphasis 安全输出，code fence 内的表格文本保持代码块；普通文本列表项里的 `[ ]` / `[x]` task marker 会渲染为 disabled checkbox，任务文本继续复用 inline code/link/strong/emphasis 安全输出，code fence 内任务列表文本保持代码块；图片内容会继续以内嵌 data URI 渲染，并在 caption 显示 mime type 和估算字节数；长 tool result 文本会默认折叠到 `<details class="tool-result-fold">` 但保留完整内容；tool call arguments 中可解析 JSON 会格式化为 `code-block` / `<code data-language="json">`，不可解析参数仍按原始 `<pre>` 安全转义，超长 arguments 会默认折叠到 `<details class="tool-call-arguments-fold">` 并保留全文；`/share` 会把当前 HTML transcript 交给 GitHub CLI 创建 secret gist，预览 URL 可用 `TAU_SHARE_VIEWER_URL` 覆盖；`/extensions` 会显示 JSON extension command、extension JSON 文件、prompt/skill resource paths、重复命令解析名和坏 JSON / 缺失路径等 load diagnostics；`/label`、`/fork`、`/fork --summarize`、`/clone`、`/resume`、`/compact`、`/retry`、`/thinking`、自动 compaction threshold、context overflow compact-and-retry、失败回合 rollback、host-level auto-retry、运行中 steering/follow-up 转发、`/prompts`、`/skills`、`/skill:<name>`、`/extensions`、JSON extension command、`/export [path]`、`/share` 可通过 targeted test 或真实 CLI smoke 运行
- `/hotkeys` 会列出当前交互式 editor 的实际 keybindings；默认会包含 Ctrl+P / Ctrl+Shift+P 的 model cycle action 和 Ctrl+L 的 model selector action，自定义 keybinding JSON 和 `action: "None"` 禁用默认绑定会反映到输出，print/RPC/redirected 模式因未创建 editor 会返回不可用错误
- `/reload` 会重读 settings 并热更新当前 retry 策略和按当前模型能力 clamp 后的 runner thinking level，重读 JSON extension commands/resources 后刷新 extension-contributed prompt/skill/theme paths，再重载 prompts、skills、context files、theme status 和交互式 editor keybindings；如果 runner 使用生成的 system prompt，会用最新 skills/context files 刷新 prompt inventory。当前 theme loader/status baseline 已完成：默认发现 built-in `dark/light`、用户 `~/.tau/themes`、项目 `.tau/themes`、`TAU_CODING_AGENT_THEME_PATHS`、重复 `--theme <path>` / `--theme=<path>` 和 extension-contributed `themePaths` / `theme-paths`，`--no-themes` / `-nt` 可禁用默认主题来源；`/theme select` 已能复用 `TuiSelectorSession` 做交互式主题选择；完整上游 theme selector parity、TUI theme rendering、theme file watcher、TypeScript extension runtime lifecycle 和 full resource selector 仍未完成
- `/theme [current|list|select|set|clear] [name]` 会查看、列出、交互式选择、显式设置或清空主题。`select` 复用 `CodingAgentThemeStore.LoadStatus()` 的主题列表，在真实交互式 editor 会话中通过 `TuiSelectorSession` + `TuiAnsiRenderSurface` 打开单选列表；非交互/RPC/redirected 会话没有 selector 时返回明确错误，不会写入 ANSI 副作用
- `/auth [current|select|provider]` 会查看当前或指定 provider 的凭证状态；真实交互式 editor 会话中，`/auth select` 会打开 TUI 单选列表展示各 provider configured/missing、credential source、OAuth/login capability，选择后只输出所选 provider status。该 selector 不写入 `auth.json`，不执行 OAuth login，也不读取或回显 secret；完整上游 OAuth login-session selector parity 仍未完成
- `/login [select|provider]` 会对指定 provider 或当前 provider 执行现有 OAuth login flow。真实交互式 editor 会话中，裸 `/login` 和 `/login select` 会先打开同一 TUI provider selector，只列出当前注册且有 OAuth provider 的 provider；选择后调用 provider login 并把 credentials 保存到 `auth.json`。没有 selector 的裸 `/login` 继续沿用当前 provider，显式 `/login <provider>` 不走 selector。当前仍不是完整上游 OAuth login dialog/session UI parity，也不会回显 secret
- `/logout [select|provider]` 会删除本地 `auth.json` 中对应 provider 的 credential entry；真实交互式 editor 会话中，裸 `/logout` 或 `/logout select` 会先打开 TUI provider selector，只列出当前有本地 OAuth credential 且注册了 OAuth provider 的 provider；没有 selector 的裸 `/logout` 继续沿用当前 provider，显式 `/logout <provider>` 不走 selector。环境变量和 `models.json` credential 配置不会被修改，也不会回显任何 secret。完整上游 OAuth login dialog/session parity、credential refresh UX 和真实外部 OAuth e2e 仍未完成
- `/changelog [count|all]` 会读取 Tau 仓库发布记录表并输出最近条目；默认来源是当前目录向上查找的 `docs/releases/feature-release-notes.md`，也可通过 `TAU_CODING_AGENT_CHANGELOG_FILE` 指定文件。当前这是本地 release notes 命令，不等于上游启动 changelog 渲染、`collapseChangelog` 设置或 install/update telemetry parity
- `/settings [current|path|select]` 在真实交互式 editor 会话中会打开 TUI settings selector，选择 auto-compaction、steering/follow-up mode、tree filter、thinking level、scoped models 或 theme 后写回同一 settings 文件；没有 selector 的会话裸 `/settings` 继续显示摘要，`/settings current` 固定显示 settings 文件路径和有效配置摘要，`/settings path` 只输出路径。当前这是 Tau-native selector baseline，不等于完整上游多层 SettingsList、images/terminal/transport/packages 等全量配置面或完整 TUI edit parity
- `/scoped-models [current|select|set|add|remove|clear|all] [provider/model[:thinking] ...]` 会查看或修改同一 settings 文件里的 `enabledModels`；显式 scope 保留输入顺序，`clear` / `all` 表示启用全部模型且不写过滤数组。条目支持 `:off|minimal|low|medium|high|xhigh` per-entry thinking suffix；该配置入口仍基于全部注册模型，允许用户提前配置尚未登录的 provider；真实交互式 editor 会话中，裸 `/scoped-models` 或 `/scoped-models select` 会打开多选 TUI selector，保存时保留既有 suffix 但暂不提供逐项编辑；空闲输入 prompt 的 Ctrl+P / Ctrl+Shift+P 和 RPC `cycle_model` 会按当前 scope 中已配置凭证的模型或全部已配置凭证模型切换下一个/上一个模型，并在命中 suffix 时同步 runner/default thinking 后按目标模型能力 clamp；Ctrl+L、交互式裸 `/model` 或 `/model select [search]` 会按同一 auth-filtered scope 打开单选模型 selector；有 scoped 候选时可用 Tab 在 scoped/all 候选间切换，顶部显示 `Model Selector` / `Search:`，普通字符和 Backspace 可交互式调整过滤，列表下方显示当前选中模型名称，底部显示 `Only showing models with configured auth`，并在选择后保存默认 provider/model 且重新 clamp 当前 thinking；非交互会话继续保留摘要/错误边界。当前这是 CLI/settings/TUI selector + model cycle/model selector auth filtering + footer/scope/detail/search chrome + per-entry thinking + 模型能力 clamp baseline，不等于完整上游 theme/terminal host 或 per-entry thinking 编辑 UI parity
- RPC `get_available_models` 只返回已配置凭证的 provider/model；RPC `set_model`、`cycle_model` 和 `update_settings.settings.model` 会拒绝未配置凭证的模型。`cycle_model` 会按 settings `enabledModels` 有序 scope 中已配置凭证的模型或全部已配置凭证模型切到下一个模型，遇到 `provider/model:thinking` suffix 时同步 runner/default thinking，保存默认模型，返回 `{ model, thinkingLevel, isScoped }`；候选不足两个时返回显式 `data: null`，active prompt 期间拒绝切换
- RPC `set_steering_mode` / `set_follow_up_mode` 会更新 runner queue mode 并写入 settings；`set_auto_compaction` 写入 settings-backed boolean 状态，`get_state` 返回真实 `steeringMode`、`followUpMode` 和 `autoCompactionEnabled`。Tau 的实际自动 compaction 仍需要 `TAU_CODING_AGENT_AUTO_COMPACT_TOKENS` 提供 threshold budget
- RPC `set_auto_retry` / `abort_retry` 复用现有 retry policy 和 JSONL retry audit：`set_auto_retry` 会按 settings/default retry policy 开关 headless retry，`abort_retry` 会取消 pending retry delay 并写 `Retry cancelled` end audit；失败输入仍按 rollback 语义不落盘
- RPC `get_settings` / `update_settings` 复用同一个 `CodingAgentSettingsStore`，可读取和批量更新默认模型、tree filter、retry、default thinking、enabledModels（含 `provider/model:thinking` 字符串条目）、steering/follow-up mode 与 auto-compaction enabled；`update_settings` 会立即同步 runner model/thinking/queue mode、retry options 和 auto-compaction 状态，active prompt 期间拒绝更新
- `Tau.WebUi.csproj` 已可独立 `dotnet build --no-restore`
- `Tau.Mom.csproj` 已可独立 `dotnet build --no-restore`
- `Tau.Pods.csproj` 已可独立 `dotnet build --no-restore`
- `Tau.slnx` 已可通过 `dotnet build Tau.slnx --verbosity minimal`
- `Tau.CodingAgent.Tests` 已通过 347 个测试
- `Tau.WebUi.Tests` 已通过 39 个测试
- `Tau.Agent.Tests` 已通过 81 个 Mom/Slack channel runtime 测试
- `Tau.Pods.Tests` 已通过 89 个测试
- `Tau.Tui.Tests` 已通过 144 个测试
- `Tau.Ai.Tests` 已通过 211 个测试
- 主验证链已经改成 `scripts/verify-dotnet.sh`，按显式项目顺序 restore / build / test
- Windows 本机已补 `scripts/verify-dotnet.ps1`，可完整覆盖同一组 restore / build / test 验证链，并可通过 `-RunSmoke` 额外执行 `WebUi` 与 `Mom --once` 的最小运行态验证

最小启动验证：

```bash
dotnet run --project src/Tau.CodingAgent/Tau.CodingAgent.csproj --no-build
dotnet run --project src/Tau.WebUi/Tau.WebUi.csproj --no-build -- --urls http://127.0.0.1:5088
dotnet run --project src/Tau.Mom/Tau.Mom.csproj --no-build -- --once
dotnet run --project src/Tau.Pods/Tau.Pods.csproj --no-build -- probe tau.pods.json
dotnet run --project src/Tau.Pods/Tau.Pods.csproj --no-build -- exec tau.pods.json <pod-id> <command>
dotnet run --project src/Tau.Pods/Tau.Pods.csproj --no-build -- deploy <pod-id> <model-id>
dotnet run --project src/Tau.Pods/Tau.Pods.csproj --no-build -- vllm deploy --json --config tau.pods.json --pod <pod-id> --prefetch --revision <rev> <model-id> --name <deployment-name>
dotnet run --project src/Tau.Pods/Tau.Pods.csproj --no-build -- vllm health --json tau.pods.json <pod-id> <deployment-name>
```

CodingAgent JSONL session smoke：

```powershell
$env:TAU_CODING_AGENT_TREE_SESSION_FILE = "$env:TEMP\tau-session.jsonl"
$env:TAU_CODING_AGENT_SESSION_FILE = "$env:TEMP\tau-session.json"
"/session`n/tree`n/quit" | dotnet run --project src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-build
```

CodingAgent HTML transcript export smoke：

```powershell
$html = "$env:TEMP\tau-session.html"
"/export $html`n/quit" | dotnet run --project src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-build
"/export`n/quit" | dotnet run --project src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-build
```

CodingAgent auto-compaction threshold smoke：

```powershell
$env:TAU_CODING_AGENT_AUTO_COMPACT_TOKENS = "4096"
$env:TAU_CODING_AGENT_AUTO_COMPACT_INSTRUCTIONS = "Keep current goal, decisions, blockers, and changed files."
$env:TAU_CODING_AGENT_COMPACT_KEEP_RECENT_TOKENS = "20000"
$env:TAU_CODING_AGENT_COMPACT_KEEP_RECENT_MESSAGES = "4"
dotnet run --project src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-build
```

CodingAgent auto-retry smoke：

```powershell
$env:TAU_CODING_AGENT_AUTO_RETRY_ATTEMPTS = "3"
$env:TAU_CODING_AGENT_AUTO_RETRY_BASE_DELAY_MS = "2000"
"/retry`n/retry off`n/retry default`n/quit" | dotnet run --project src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-build
```

CodingAgent thinking level smoke：

```powershell
"/thinking`n/thinking high`n/thinking cycle`n/thinking off`n/quit" | dotnet run --project src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-build
```

CodingAgent settings summary smoke：

```powershell
"/settings`n/settings path`n/quit" | dotnet run --project src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-build
```

CodingAgent scoped models smoke：

```powershell
$temp = Join-Path $env:TEMP ("tau-scoped-models-smoke-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $temp -Force | Out-Null
$env:TAU_CODING_AGENT_SETTINGS_FILE = Join-Path $temp "settings.json"
"/scoped-models`n/scoped-models set google/gemini-2.5-pro`n/scoped-models add openai/gpt-5.4`n/scoped-models clear`n/quit" | dotnet run --project src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-build
```

CodingAgent RPC mode smoke：

```powershell
$temp = Join-Path $env:TEMP ("tau-rpc-smoke-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $temp -Force | Out-Null
$env:TAU_CODING_AGENT_SESSION_FILE = Join-Path $temp "session.json"
$env:TAU_CODING_AGENT_TREE_SESSION_FILE = Join-Path $temp "session.jsonl"
$env:TAU_CODING_AGENT_SETTINGS_FILE = Join-Path $temp "settings.json"
$html = Join-Path $temp "session.html"
@(
  @{ id = "state1"; type = "get_state" }
  @{ id = "commands1"; type = "get_commands" }
  @{ id = "name1"; type = "set_session_name"; name = "RPC Smoke" }
  @{ id = "think1"; type = "set_thinking_level"; level = "high" }
  @{ id = "think2"; type = "cycle_thinking_level" }
  @{ id = "bash1"; type = "bash"; command = "echo tau-rpc" }
  @{ id = "last1"; type = "get_last_assistant_text" }
  @{ id = "html1"; type = "export_html"; outputPath = $html }
) | ForEach-Object { $_ | ConvertTo-Json -Compress } |
  dotnet run --project src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-build -- --mode rpc
```

CodingAgent skill command discovery smoke：

```powershell
$skillDir = "$env:TEMP\tau-skill-smoke\reviewer"
New-Item -ItemType Directory -Path $skillDir -Force | Out-Null
@'
---
name: reviewer
description: Review smoke skill
---
Inspect the requested path.
'@ | Set-Content -Path (Join-Path $skillDir "SKILL.md") -Encoding UTF8
$env:TAU_CODING_AGENT_SKILL_PATHS = $skillDir
"/skills`n/quit" | dotnet run --project src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-build
```

CodingAgent extension command discovery smoke：

```powershell
$extensionDir = "$env:TEMP\tau-extension-smoke"
New-Item -ItemType Directory -Path $extensionDir -Force | Out-Null
@'
{
  "name": "hello",
  "description": "Say hello from smoke",
  "argumentHint": "<name>",
  "response": "Hello $ARGUMENTS",
  "resources": {
    "promptPaths": ["./prompts"],
    "skillPaths": ["./skills"]
  }
}
'@ | Set-Content -Path (Join-Path $extensionDir "hello.json") -Encoding UTF8
$env:TAU_CODING_AGENT_EXTENSION_PATHS = $extensionDir
"/extensions`n/hello Tau`n/quit" | dotnet run --project src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-build
```
