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

- [x] Anthropic OAuth 真实 login / refresh 流程
- [x] GitHub Copilot device flow
- [x] OpenAI Codex OAuth flow
- [x] Gemini CLI / Antigravity login flow
- [x] auth.json 的迁移、写回、刷新后持久化策略

#### 配置 / 安全

- [x] auth.json schema 明文化
- [~] secret 持久化边界和脱敏规则（已补默认 `./.tau/auth.json` / `./.tau/models.json` / JSONL session 本地状态忽略、`auth.json` Unix 0600 写入、OAuth metadata 保留字段过滤、auth status 不回显密钥、models.json credential header 状态识别、command-backed `apiKey` 状态检查不执行 `!command` 的回归，以及 HTML transcript 导出（`/export` / `/share`）默认对常见 AWS / GitHub / Slack / Anthropic / OpenAI / Bearer / JWT secret 模式做 `[redacted]` 替换（`TAU_CODING_AGENT_REDACT_SECRETS=0` 可关闭）；仍需继续梳理 JSONL 流式 export / runtime 日志输出脱敏边界）
- [x] provider-specific headers 支持（models.json 已能合并静态 provider/model headers，并在 StreamFunctions 层解析 provider/model request headers）
- [x] Bedrock AWS SSO / AssumeRole / credential_process / IMDS / ECS / web identity credential chain（已覆盖全部六个源 + AssumeRole 的 source_profile 与 credential_source 两种触发方式（Environment / EcsContainer / Ec2InstanceMetadata）；SigV4 签名器泛化到任意 service，STS XML 解析共享，token cache 路径 / portal endpoint / sts endpoint 均可在 BedrockOptions 中显式覆盖；剩余增量：SSO token 自动刷新和真实云端 e2e）
- [x] 自定义 provider / custom model 配置入口（`TAU_MODELS_FILE`、`./.tau/models.json`、`~/.tau/models.json`，支持 Tau 已注册 API 的 `providers/baseUrl/api/apiKey/authHeader/headers/compat/models/modelOverrides` 子集）
- [x] models.json 的 `apiKey/authHeader`、shell/env value resolution、运行时 request auth 合并

### Tau.CodingAgent

- [x] flat session 持久化（`TAU_CODING_AGENT_SESSION_FILE` 或 `./.tau/coding-agent-session.json`，启动自动 rehydrate，回合后保存）
- [x] JSONL session tree baseline（默认 `./.tau/coding-agent-session.jsonl`，`TAU_CODING_AGENT_TREE_SESSION_FILE` 或 `.jsonl` 形式的 `TAU_CODING_AGENT_SESSION_FILE` 可覆盖；已支持 header、append-only message/model/session-info/label entries、entry id/parentId/timestamp、runner diff 同步、current branch restore）
- [~] session lifecycle（已补 `/new`、`/session` tree stats、估算 token/context usage、auto-compaction threshold budget、retry policy display、JSONL `cwd/parentSession` metadata、`/name`、`/tree` 过滤/搜索模式与 label timestamp、`/label`、`/fork`、`/clone`、`/resume`、`.jsonl` export/import；仍缺真正的 interactive tree navigator 和更完整 session metadata）
- [x] settings / model selection / provider selection（`/model`、`/provider`、`/models`、`/providers`，默认写入 `TAU_CODING_AGENT_SETTINGS_FILE` 或 `./.tau/coding-agent-settings.json`；同一 settings 文件支持上游兼容 `treeFilterMode` 作为 `/tree` 默认过滤模式，并支持 retry attempts/base delay 字段供 `/retry` 和生产入口使用）
- [~] auth 管理入口（已补 `/auth` 状态查看和 `/login` 骨架提示；真实 OAuth/device flow 仍在 Tau.Ai OAuth backlog）
- [x] slash command router 抽离（`CodingAgentCommandRouter`；当前命令行为不变，为 `/compact` / login flow 等后续命令留 seam）
- [x] local quit command（`/quit` 结束当前 CLI loop，不调用 runner，不进入 LLM conversation）
- [x] local help command（`/help` 列出当前 Tau 已支持命令，已纳入 `/prompts`、`/skills` 与 `/extensions`）
- [x] slash command catalog（`CodingAgentCommandCatalog` 统一当前本地命令 name/usage/description，`/help` 和 usage 错误共用）
- [x] local session name command（`/name [display name | clear]` 查看、设置或清空当前 session display name，并写入 session store）
- [x] local copy command（`/copy` 复制最后一条 assistant 文本到系统剪贴板，clipboard 写入通过 `ICodingAgentClipboard` 隔离）
- [x] local export command（`/export` 默认导出 standalone HTML transcript；`/export <path>` 对 `.html/.htm` 路径导出 HTML，对 `.jsonl` 路径导出当前 branch JSONL，其他路径导出 Tau 平面 session snapshot JSON；HTML 提供 branch outline、filter/search、current-branch JSONL timeline、文本 code fence rendering、inline code span rendering、plaintext link rendering、Markdown heading/list/blockquote block rendering、Markdown strong/emphasis span rendering、Markdown pipe table rendering、Markdown task list rendering、image metadata caption、长 tool result folding、tool call JSON argument rendering、长 tool call arguments folding 并内嵌可下载 JSONL）
- [x] local history command（`/history [count|all]` 在 editor 启用时列出最近输入；non-interactive 模式返回未启用错误；持久化结合 `FileInputHistoryStore`）
- [x] local import command（`/import <path>` 严格导入 Tau snapshot JSON 或 resume JSONL session，并恢复 messages/provider/model/display name；仍缺上游 share/import richer metadata）
- [~] manual / auto compaction（已补 `/compact [instructions]`，当前使用当前模型生成摘要并把 flat session 压成 summary message；JSONL tree 会追加 `compaction` entry，记录 `summary`、`firstKeptEntryId`、估算 `tokensBefore`、`fromHook`、`isSplitTurn` 和 `turnPrefixSummary` baseline；manual / auto compaction 默认优先按 `TAU_CODING_AGENT_COMPACT_KEEP_RECENT_TOKENS` 的 token budget 保留 recent messages，再回落到最近 4 条 message，可用 `TAU_CODING_AGENT_COMPACT_KEEP_RECENT_MESSAGES` 调整 fallback，branch restore / resume / clone export 会重建 summary + retained messages + post-compaction messages；如果 retained cut point 落在一个 user turn 中间，恢复 runtime 时会把 split-turn prefix context 拼入 summary message，HTML transcript timeline 也会展示并可搜索该字段；已补 `TAU_CODING_AGENT_AUTO_COMPACT_TOKENS` / `TAU_CODING_AGENT_AUTO_COMPACT_INSTRUCTIONS`，普通消息执行前超过阈值会自动 compact 并写 `fromHook=true`，context overflow 会恢复回合前 snapshot、compact、记录 `fromHook=true` boundary 并重试同一输入；`/session` 会显示当前估算 token、模型 context window 和 auto threshold 剩余量；已升级为上游结构化 summarization prompt（Goal/Progress/Decisions/Next Steps）和 iterative update summarization（前一次 compaction summary 作为 previous-summary 合并新信息）；仍缺上游 LLM-generated split-turn summarization 的独立 LLM 调用、compaction extension events 和 cancellation UI 语义）
- [~] retry / rollback（已补普通回合失败/取消 rollback baseline：runner exception、取消或错误型 `AgentEndEvent` 会恢复回合前 snapshot，并避免 flat JSON / JSONL tree 持久化失败输入；已补 host-level retryable error auto-retry baseline：settings 优先、`TAU_CODING_AGENT_AUTO_RETRY_ATTEMPTS` / `TAU_CODING_AGENT_AUTO_RETRY_BASE_DELAY_MS` env 兜底控制 429/5xx/rate limit/timeout/network 等 transient error 的有限重试，重试前恢复 snapshot；已补 `/retry [current|default|off|<max attempts> [base delay ms]]` settings command baseline，可持久化并同进程热更新；已补 Tau-native JSONL `auto_retry_start` / `auto_retry_end` audit entries，成功时先持久化成功 attempt messages 再写 retry end，失败或耗尽时只保留 retry audit；已补 retry delay cancellation visibility baseline，取消 delay 时显示明确状态、写 `Retry cancelled` end audit，并保持失败输入不落盘；context overflow 已单独走 compact-and-retry baseline；仍缺上游 RPC/settings UI 控制和完整 retry cancellation UI）
- [~] JSONL tree navigator / richer session metadata（已补命令行 tree filter/search：`default/no-tools/user-only/labeled-only/all`、settings `treeFilterMode`、`--search query` 与 `--label-time`；仍缺真正的 TUI interactive navigator、overlay search/fold/select 和完整 metadata）
- [~] dynamic slash command registry / prompt registry / skills/extensions discovery（已补 prompt template discovery/expansion baseline：`~/.tau/prompts`、`./.tau/prompts`、`TAU_CODING_AGENT_PROMPT_PATHS`、`/prompts`、`$1/$@/$ARGUMENTS/${@:N[:L]}` 参数替换；已补 skill command discovery/expansion baseline：`~/.tau/skills`、`./.tau/skills`、`TAU_CODING_AGENT_SKILL_PATHS`、`/skills`、`/skill:<name>`、`disable-model-invocation` 和默认 system prompt inventory；已补 JSON extension command/resource/load diagnostics baseline：`~/.tau/extensions`、`./.tau/extensions`、`TAU_CODING_AGENT_EXTENSION_PATHS`、`/extensions`、`response/prompt` 参数替换、`sendToRunner`、重复命令 `name:1/name:2`、`promptPaths/skillPaths` 资源贡献、extension 文件/resource 明细、坏 JSON 和缺失显式路径诊断；仍缺完整 TypeScript extension runtime、custom tools/events、theme loader、interactive resource selector 和 richer runtime diagnostics）
- [x] standalone HTML transcript export（`/export` 默认 HTML，`/export <path.html|path.htm>` 显式 HTML，覆盖 text/thinking/tool call/tool result/image 内容，并提供 branch outline、filter/search、cwd/parent metadata、current-branch JSONL timeline、label/model/compaction events、本地 Download JSONL、文本 fenced code block 的独立 code rendering baseline、inline code span rendering baseline、plaintext link rendering baseline、Markdown heading/list/blockquote block rendering baseline、Markdown strong/emphasis span rendering baseline、Markdown pipe table rendering baseline、Markdown task list rendering baseline、image metadata caption baseline、长 tool result 默认折叠 baseline、tool call JSON arguments 格式化 baseline 和长 tool call arguments 默认折叠 baseline）
- [~] share/Gist export parity 和上游 richer HTML template（已补 `/share` secret Gist baseline：复用 HTML transcript export、检查 `gh auth status`、执行 `gh gist create --public=false`，并支持 `TAU_SHARE_VIEWER_URL` 覆盖预览 URL；HTML transcript 已支持 message deep-link/copy-link、branch outline filter/search、label badge、model/label/compaction timeline entries 和 `targetId` 定位；仍缺真实 `gh` smoke、Tau 专属 share viewer 和完整上游 richer HTML template）
- [~] richer rendering（已补 HTML transcript 文本 fenced code block -> `code-block` / `<code>` baseline、普通文本 backtick inline code span -> `<code class="inline-code">` baseline、普通文本 Markdown-style `[label](http/https...)` 与裸 `http(s)` URL 外链 baseline、普通文本 angle-bracket autolink `<http(s)://...>` baseline、普通文本 heading/list/blockquote block rendering baseline、嵌套列表（缩进升级 `<ul>` / `<ol>` 栈）baseline、普通文本 horizontal rule (`---`/`***`/`___`) -> `<hr>` baseline、普通文本 strong/emphasis span rendering baseline、普通文本 strikethrough (`~~text~~`) -> `<del>` baseline、普通文本 Markdown pipe table -> 可横向滚动 `<table>` baseline、普通文本 task list -> disabled checkbox baseline、image mime type / byte count caption baseline、长 tool result -> `<details class="tool-result-fold">` 默认折叠 baseline、tool call JSON arguments -> `code-block` / `<code data-language="json">` 格式化 baseline，以及长 tool call arguments -> `<details class="tool-call-arguments-fold">` 默认折叠 baseline；仍缺完整 Markdown/highlight renderer、custom tool renderer 和上游 richer HTML template）
- [x] 显式 `Create(provider, model, history)` runner 工厂
- [x] 与 `ModelCatalog` 对齐的默认模型解析层继续收口
- [x] 把当前 `Tau.CodingAgent` / `Tau.WebUi` / `Tau.Mom` / `Tau.CodingAgent.Tests` / `Tau.Agent.Tests` 的 DLL `HintPath` workaround 收回到更正常的 `ProjectReference` 结构
- [x] 解决当前本机上 `Tau.slnx` / metaproj / workload resolver 的 build 异常（`dotnet build Tau.slnx --verbosity minimal` 已通过）

### Tau.Tui

- [~] 真正的输入编辑器（`InteractiveInputEditor` baseline 已落：key-by-key 读取、char append、backspace/delete、左/右光标、Home/End、Ctrl+Left/Right 词级跳转、Ctrl+Backspace 删除前一个词、Ctrl+Delete 删除下一个词、Ctrl-A/E 行首行尾跳转、Ctrl-K/U kill-to-end/start、Ctrl-R 反向 history 搜索（再按 R 切到更旧匹配，Esc/Ctrl-G 取消，Enter 提交）、Enter 提交、Ctrl-C 取消、`InputHistory` Up/Down 历史回放（去重 + capacity）；`Tau.CodingAgent` 在交互式 console（无 redirected stdin/stdout、未设 `TAU_CODING_AGENT_DISABLE_INPUT_EDITOR=1`）下默认使用 editor，非交互回退 `Console.ReadLine`；history 通过 `FileInputHistoryStore` 持久化到 `~/.tau/coding-agent-history`（可被 `TAU_CODING_AGENT_HISTORY_FILE` 覆盖），启动加载 / 提交追加 / 超 capacity 截断；通过 `IConsoleKeyReader`/`IInteractiveRenderer` seam 测试；仍缺多行/wrap 渲染）
- [ ] 组件系统
- [ ] 消息区 / 状态区
- [~] 键盘体系（`SystemConsoleKeyReader` + `InteractiveInputEditor` 已支持主要导航/编辑键 + 词级跳转/删除 + Ctrl-A/E/K/U readline 习惯 + Ctrl-R 反向搜索；仍缺自定义绑定层）
- [ ] 更稳定的差分渲染层

## P1：后续应用面

### Tau.WebUi

- [x] 最小聊天 UI
- [x] session 持久化（`output/webui-sessions.json`）
- [x] provider/model 选择入口（`/api/catalog` + 会话设置）
- [x] 流式消息绑定（NDJSON streaming endpoint + 前端 ReadableStream 增量渲染）
- [x] richer rendering / thinking / tool timeline 展示（fenced code blocks、inline code、links、headings/lists/blockquotes、strong/emphasis、tables、task lists、thinking details、tool call cards with status/input/output）
- [x] auth/settings UX（`/api/auth/{provider}` 状态查询 + 前端 provider/model 切换时刷新 auth 状态；真实 login flow 仍在 Tau.Ai OAuth backlog）
- [x] 附件体系（`WebChatAttachmentDto` + 前端 file picker/preview/remove + 发送时 base64 content + text extraction）
- [x] session lifecycle（session delete / export JSON download / import file upload / title rename / last-opened session restore baseline）
- [~] 更高层的 WebUi 行为测试（已补 `WebChatService` fake-runner 流式消息、附件 prompt 和持久化行为测试，以及 Minimal API endpoint 的 NDJSON streaming、session 持久化、export/import/delete、rename/restore 和错误状态回归；仍缺 browser 级 WebUi flow 测试）

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
- [x] workspace layout bootstrap（统一创建 `scratch/`、workspace/channel `skills/`、`attachments/`、`events/`，并把 `SYSTEM.md` 与 Agent Skills prompt inventory 注入 prompt context）
- [x] Slack-compatible channel message envelope（`MomChannelMessage` / `MomChannelAttachment` 统一 file/events/未来 Slack adapter 的 channel/user/ts/thread/attachment/request metadata 映射）
- [x] fake Slack transport / responder seam（`IMomChannelTransport` / `IMomChannelResponder` / `MomChannelMessageProcessor` 先固定 Slack adapter 输入输出契约，不直接接真实 SDK）
- [x] Slack event mapper seam（`SlackEventMapper` 固定 Socket Mode `app_mention` / DM / skip / mention stripping / file metadata 到 `MomChannelMessage` 的 receive-side 规则）
- [x] Slack Web API responder seam（`SlackWebApiResponder` 用 `HttpClient` 固定 `chat.postMessage`、thread response、`files.uploadV2` 与 token 脱敏错误边界）
- [x] Slack Socket Mode transport seam（`auth.test` / `apps.connections.open` / WebSocket text frame / envelope ack / `SlackSocketModeEnabled` worker 开关）
- [x] Slack startup backfill seam（`SlackBackfillService` 对已有 `log.jsonl` 的 channel 调 `conversations.history`，oldest/cursor 分页、去重过滤、log-only old message writeback）
- [x] Slack private file download seam（`SlackAttachmentDownloader` 使用 bot token 下载 `url_private_download/url_private`，并复用 `ChannelAttachmentStore` 写 `original/local/source` manifest）
- [x] Slack per-channel queue seam（`MomChannelQueueDispatcher` 固定同频道顺序处理、不同频道独立推进、pending queue limit 和 stop bypass）
- [x] Slack true cancellable stop seam（`MomChannelRunRegistry` 跟踪当前 in-process channel run，stop bypass queue 后取消 linked runner token，写 `cancelled` status 并回复 `_Stopped_`）
- [x] Agent Skills prompt inventory parity（workspace/channel `skills/**/SKILL.md` -> XML `<available_skills>`，sandbox path mapping，channel override，`disable-model-invocation`）
- [x] Mom sandbox/tool delegation seam（`MomSandboxConfig` / `IMomSandboxExecutor` / `MomToolSet`，默认 host sandbox，配置层支持 `docker:<container>`，runner 默认工具切到 `bash/read/write/edit/attach`，attach 产物进入 execution attachments）
- [ ] real Slack smoke
- [~] workspace / sandbox / tool delegation（已补 workspace memory context、本地 attachment staging、scratch 目录、SYSTEM.md、Agent Skills prompt inventory、host sandbox executor、docker path/command construction seam 和 `bash/read/write/edit/attach` tools；仍缺真实 Docker sandbox smoke）
- [~] message / runtime flow（已补最小 `log.jsonl` channel history 注入、本地 request/result 写回、`context.json` runtime messages、`last_prompt.jsonl` prompt debug snapshot、`status.json` runtime 状态、本地 busy-state guard、Slack-compatible envelope、Slack event mapper、Slack Socket Mode transport seam、Slack startup backfill seam、channel processor busy/stop/typing/thread response seam、true cancellable stop seam、Slack Web API responder seam、Slack private file download seam 与 per-channel queue seam，仍缺真实 Slack session sync / 多消息 runtime flow）
- [ ] 更高层 delegation flow 与端到端测试

### Tau.Pods

- [x] config init/list/validate/status
- [x] probe（HTTP endpoint / TCP ssh target）
- [x] exec（SSH pod remote command execution）
- [x] deploy / stop / restart / health / logs（`PodLifecycleService` + CLI commands，SSH-based deploy/stop/restart/logs 和 HTTP/SSH health check；logs 通过 journalctl 拉 `tau-pod-<name>` unit，回退 `~/.tau_pods/<name>.log`，可配置 tail）
- [~] 真正的 CLI 运维命令体系（已补 `health/deploy/stop/restart/logs`、可省略 path 的 target-command 参数解析、SSH lifecycle metadata 命令转义和 CLI 参数回归测试；仍缺更完整模型生命周期、远端 transport hardening 和真实运维 smoke）

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
