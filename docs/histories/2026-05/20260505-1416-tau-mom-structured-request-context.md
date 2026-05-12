## [2026-05-05 14:16] | Task: tau-mom structured request context

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Runtime**: `Windows PowerShell / .NET 10 preview`

### 📥 User Query

> 继续继续 / 继续从参考项目迁移剩余的功能

### 🛠 Changes Overview

**Scope:** `Tau.Mom`, `Tau.Agent.Tests`

**Key Actions:**

* **[Structured request fields become live]**: 扩展 `DelegationRequest`，把 `.json` 请求的 `attachments` 纳入正式契约；保留已有 `title` / `metadata`，不再只把它们停留在输入文件层。
* **[Runner context injection]**: `RuntimeDelegationAgentRunner` 现在会把 `title` 设为 `ICodingAgentRunner.SessionName`，并把 `title`、`metadata`、`attachments` 包装成 `<delegation_context>` 前缀，再与原始 `prompt` 一起传给 `RunAsync(...)`。
* **[Workspace memory]**: `RuntimeDelegationAgentRunner` 会读取 `workingDirectory/MEMORY.md` 与其父目录的 `MEMORY.md`，并把非空内容以 current/parent workspace memory 的形式注入同一个 `<delegation_context>`。
* **[Channel history]**: `RuntimeDelegationAgentRunner` 会读取 `workingDirectory/log.jsonl`，把最近 20 条非 bot 文本消息以 channel history 注入同一个 `<delegation_context>`；malformed 行、空文本、bot 消息和当前消息 `ts` 会被跳过。
* **[Channel log writeback]**: `FileDelegationProcessor` 会在本地 file delegation 完成后把用户请求和 bot 结果追加到 `workingDirectory/log.jsonl`；用户消息按 `ts` 做简单去重，日志使用 compact JSONL，附件保留为本地路径。
* **[Log store seam]**: 新增 `ChannelLogStore` 统一 `log.jsonl` 的 append / history 读取 / 过滤语义，避免 runner 和 processor 各自实现一套松散的解析逻辑。
* **[Log append hardening]**: `ChannelLogStore` 的去重检查会跳过 malformed JSONL 行；追加新行时如果旧文件没有尾随换行，会先补行边界，避免把 bot 结果粘到上一条 JSON 后面。
* **[Runtime status]**: 新增 `ChannelStatus` / `ChannelStatusStore`，本地 file delegation 执行前写 `running`，执行完成后写 `completed` 或 `failed` 到 `workingDirectory/status.json`，记录请求文件、provider/model、workdir、时间、错误和响应摘要。
* **[Busy-state guard]**: `FileDelegationProcessor` 现在会读取 `workingDirectory/status.json`；如果同一 workdir 存在未过期 `running` 状态，就保留 inbox 请求并跳过处理，避免本地 worker 在后续 Slack busy-state 接线前重复启动同一 channel/workspace 的 delegation。`MomOptions.RunningStatusStaleAfterMinutes` 默认 60 分钟，过期 `running` 会被视为 stale 后继续处理，防止崩溃后永久卡住。
* **[Local events wake-up]**: 新增 `MomEventFile` / `MomEventProcessor`，把上游 mom `events/*.json` 的 `immediate`、`one-shot`、`periodic` 文件事件迁移成本地 inbox 委派请求；事件 prompt 使用 `[EVENT:file:type:schedule] text` 格式，`channelId` 映射到 `DefaultWorkingDirectory/<channelId>`，metadata 自动补 `event/eventType/eventFile/channel/user/userName/ts/date`。
* **[Cron/event hardening]**: `periodic` 支持五段 cron 的 `*`、`*/n`、单值、范围和逗号组合，并按 timezone 转换后在同一分钟只入队一次；无效事件会移入 `archive/invalid-events`，避免坏 JSON 在 worker loop 里反复污染日志。
* **[Options wiring]**: `MomOptions` 新增 `EventsPath`，`Program.cs` 现在会把 `EventsPath` 和 `RunningStatusStaleAfterMinutes` 从配置实际传入运行时；`Worker` 和 `--once` 会先处理 due events，再处理 inbox 文件。
* **[Local attachment store]**: 新增 `ChannelAttachmentStore` / `ChannelAttachmentEntry`，把上游 mom `ChannelStore.processAttachments()` 的本地可验证部分迁移到 Tau：本地存在的 request/event attachment 会复制到 `workingDirectory/attachments/<timestamp>_<filename>`，`attachments/attachments.jsonl` 记录 `original/local/source`，`log.jsonl` 用户消息保留 `original/local` 附件元数据。
* **[Mom runtime context]**: 新增 `MomRuntimeContext`，把上游 mom `buildSystemPrompt()` 中不依赖 Slack SDK 的本地运行规则迁移成 `<mom_runtime_context>` 前缀，注入 workspace/channel layout、events 文件格式、attachment manifest、memory/log/status 路径和 `[SILENT]` 事件响应约定。
* **[Channel session context]**: 新增 `ChannelSessionStore`，复用 `CodingAgentSessionStore` 在 `workingDirectory/context.json` 中保存 Tau-native channel session snapshot；`RuntimeDelegationAgentRunner` 会在 delegation 前恢复上一轮 messages/provider/model/session name，完成后写回当前 runner messages。
* **[Prompt debug snapshot]**: 新增 `ChannelPromptDebugStore`，在调用 runner 前写 `workingDirectory/last_prompt.jsonl`，记录 mom runtime context、delegation context、实际 runner input、恢复的 session messages、当前 prompt 和 attachment/image attachment count，对齐上游 mom 的本地 prompt 排障文件。
* **[Workspace layout bootstrap]**: 新增 `ChannelWorkspaceLayout`，统一创建并描述 workspace/channel 路径：workspace `MEMORY.md` / `SYSTEM.md` / `skills/` / `events/`，channel `MEMORY.md` / `log.jsonl` / `context.json` / `status.json` / `last_prompt.jsonl` / `attachments/` / `scratch/` / `skills/`。
* **[SYSTEM and skill docs context]**: `RuntimeDelegationAgentRunner` 会把非空 `SYSTEM.md` 作为 `system_configuration_log` 注入 prompt，并扫描 workspace/channel `skills/**/SKILL.md` frontmatter 生成 Agent Skills prompt inventory；Mom skills 通过 `read/bash` 使用脚本，不额外注册成 direct tool。
* **[Complete port plan]**: 新增 `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`，把用户明确收口的“完完整整的移植”升级为跨模块 parity closure 计划；旧 baseline plan 保留为 P1 基线和决策历史。
* **[Slack-compatible message envelope]**: 新增 `MomChannelMessage` / `MomChannelAttachment`，统一承载 channel、user、userName、displayName、ts、threadTs、text、attachments、provider/model/title/metadata；`FileDelegationProcessor` 与 `MomEventProcessor` 先映射到同一 envelope，再生成 `DelegationRequest`，给后续 fake Slack transport、real Slack adapter、backfill、queue 和 file download 留稳定接缝。
* **[Transport/responder seam]**: 新增 `IMomChannelTransport` / `IMomChannelResponder` / `MomChannelMessageProcessor`，固定未来 Slack adapter 的输入输出边界；processor 负责 busy-state、true cancellable stop、typing、thread response、attachment staging、status/log writeback 和 runner 调用，responder 只负责响应/thread/typing/upload。
* **[Slack event mapper]**: 新增 `SlackEventMapper` / `SlackUserSnapshot`，用纯 JSON 固定上游 Slack Socket Mode `app_mention` 与 `message` 事件到 `MomChannelMessage` 的转换规则：channel mention 会剥离 `<@...>`，DM 会触发处理，bot/self/非 `file_share` subtype 会跳过，file metadata 会保留 name 与 private URL。
* **[Slack Socket Mode transport]**: 新增 `SlackSocketModeTransport` / `ISlackSocketModeConnector` / `SlackSocketModeWorker`，固定 `auth.test`、`apps.connections.open`、WebSocket text frame、envelope ack、reconnect delay 和 `SlackSocketModeEnabled` 配置开关；默认不开启，避免影响本地 inbox/events worker。
* **[Slack Web API responder]**: 新增 `SlackWebApiResponder`，用 `HttpClient` 固定 `chat.postMessage`、thread response 和 `files.uploadV2` 的真实 Slack Web API 调用契约；`SlackBotToken` / `SlackApiBaseUrl` 进入 `MomOptions` 和 `appsettings.json`，错误消息不回显 token。
* **[Slack attachment download]**: 新增 `SlackAttachmentDownloader`，在 channel processor 生成 delegation request 前用 `SlackBotToken` 下载 `url_private_download/url_private`，保存到 `workingDirectory/attachments/`，并复用 `ChannelAttachmentStore` 写 `original/local/source` manifest；token 缺失或下载失败只降级保留原 attachment，不阻断消息处理。
* **[Slack channel queue]**: 新增 `MomChannelQueueDispatcher` / `MomChannelCommands`，把实时 Slack 消息改成 per-channel 顺序队列；同频道工作串行，不同频道可独立推进，`SlackChannelQueueLimit` 默认 5，队列满会回复用户并丢弃额外 work，`stop` 不进入 pending queue 而是直接走 processor 的 stop path。
* **[Slack true cancellable stop]**: 新增 `MomChannelRunRegistry`，在 `MomChannelMessageProcessor` 启动 runner 前登记当前进程内 active channel run 和 linked cancellation token；`stop` 仍 bypass pending queue，但现在会取消当前 runner token、立即回复 `_Stopping..._`，runner 结束后写 `cancelled` status 并回复 `_Stopped_`。
* **[Mom sandbox config]**: 新增 `MomSandboxConfig` / `IMomSandboxExecutor` / `MomSandboxExecutorFactory`，默认 `host`，配置层支持 `docker:<container>`；host executor 会在 channel working directory 内执行，docker executor 固定 `/workspace` path translation 和 `docker exec -w /workspace <container> sh -c ...` command construction。
* **[Mom tool set]**: 新增 `MomToolSet` 和上游同名 `bash/read/write/edit/attach` tools；`bash` 通过 sandbox executor 执行并做 tail truncation，`read` 支持 text/image 和 head truncation，`write/edit/attach` 都限制 workspace path boundary，避免 Mom runtime 继续暴露通用 CodingAgent 工具名。
* **[Agent Skills prompt inventory parity]**: `ChannelWorkspaceLayout` 的 skill inventory 从简化列表对齐到上游 Agent Skills XML `<available_skills>` 格式，支持 channel skill 覆盖 workspace 同名 skill、`disable-model-invocation: true` 隐藏，以及 host/docker sandbox path translation。
* **[Runner tool override]**: `RuntimeCodingAgentRunner.Create(...)` 新增 tools/system prompt override；`RuntimeDelegationAgentRunner` 默认按 workingDirectory 创建 Mom sandbox executor 和 Mom tool set，同时保留旧的 fake runner factory 构造重载供测试使用。
* **[Attach result flow]**: `DelegationExecution` 新增 `Attachments`；Mom `attach` tool 会把文件加入 execution attachments，file outbox 会合并请求附件与 runner attach 产物，Slack/channel processor 会在文本响应后调用 responder upload。
* **[Channel workspace helper]**: 新增 `MomChannelWorkspace`，统一 `channelId -> workingDirectory/<safe-channel-id>` 的路径解析；`MomChannelMessageProcessor` 和 Slack backfill 共用同一个安全 path segment 规则，避免后续 processor/backfill/session sync 各自拼 workdir。
* **[Slack startup backfill]**: 新增 `SlackBackfillService`，在 `SlackSocketModeWorker` 开始读取 Socket Mode 消息前，对 `DefaultWorkingDirectory` 下已有 `log.jsonl` 的 channel 调 `conversations.history`；使用现有最大 `ts` 作为 `oldest`、`inclusive=false`、cursor 分页和 `SlackBackfillMaxPages` 上限，按时间顺序把启动前缺失消息写回 `log.jsonl`，但不触发 delegation。
* **[Backfill filters and attachments]**: backfill 会跳过缺 `ts`、已存在 `ts`、其他 bot、非 `file_share` subtype、缺 user、空文本且无文件的消息；bot 自己的历史消息以 `user=bot/isBot=true` 进入 log；用户文本复用 `SlackEventMapper.StripSlackMentions`，文件复用 `SlackAttachmentDownloader` 下载并写 attachment manifest。
* **[Backfill config]**: `MomOptions` / `Program.cs` / `appsettings.json` 新增 `SlackBackfillEnabled`、`SlackBackfillMaxPages`、`SlackBackfillPageSize`；默认启用、最多 3 页、每页 1000，且 Socket Mode backfill 失败只记录 warning 后继续启动实时 worker。
* **[Channel log helpers]**: `ChannelLogStore` 新增 `ReadTimestamps(...)`、`AppendMessageAsync(...)` 和按任意 `ts` 去重的 `HasLogEntry(...)`，让 backfill 复用同一 JSONL 容错读取、尾随换行修复和 append 语义，不把历史消息写入逻辑散进 Slack service。
* **[Attachment path normalization]**: `FileDelegationProcessor` 会按已解析的 `workingDirectory` 规范化相对/绝对 attachment 路径，避免 outbox 和 runner 看到的还是相对字符串。
* **[Outbox contract sync]**: `DelegationResult` 新增 `title` 与 `attachments`，让结果文件保留请求上下文，而不是只剩 provider/model/workdir 和 metadata。
* **[Smoke isolation]**: `scripts/verify-dotnet.ps1 -RunSmoke` 的 Mom smoke 改用临时 `workdir` 和 `events` 目录，通过 immediate event 触发 `--once`，并断言 smoke 运行会写出 channel log、final channel status、`last_prompt.jsonl`、`scratch/`、workspace `skills/` 与 channel `skills/`，避免运行态验证在仓库根目录生成 `log.jsonl` / `status.json`。
* **[Tests]**: 升级 `FileDelegationProcessorTests`，断言 `title` / `attachments` 进入 request 和 outbox，断言 `log.jsonl` 写入用户请求与 bot 结果，覆盖 malformed JSONL + 无尾随换行时的去重和追加行为，覆盖 completed / failed 两类 `status.json`，并覆盖新鲜 `running` 状态跳过、过期 `running` 状态继续处理两类 busy-state 行为；升级 `RuntimeDelegationAgentRunnerTests`，断言 structured context、workspace memory、channel history、`<mom_runtime_context>`、`context.json` restore/save、`last_prompt.jsonl` prompt debug snapshot、`scratch/` / `skills/` 目录 bootstrap、`SYSTEM.md` 注入与 Agent Skills XML inventory 真正进入 runner 输入和 runtime flow，并设置 session name，新增 workspace factory/attach result 和 `disable-model-invocation` 覆盖；新增 `MomEventProcessorTests`，覆盖 immediate、one-shot、periodic 和 invalid event 文件处理；新增 `MomChannelMessageTests`，覆盖 Slack-compatible envelope 到 `DelegationRequest` 的字段映射、attachment-only request 和 local request 默认值；新增 `MomChannelMessageProcessorTests`，覆盖 channel message delegation + thread response、busy-state 响应、detached running status、active run cancellation 和 `cancelled` status；新增 `SlackWebApiResponderTests`，覆盖 post message、thread message、multipart upload、Slack ok=false 和缺 token；新增 `SlackEventMapperTests`，覆盖 app_mention、DM、bot/self/subtype/channel chatter skip 和 file-only DM；新增 `SlackSocketModeTransportTests`，覆盖 auth/open、Socket URL、envelope ack、mapper yield、缺 app token 和 Slack ok=false 脱敏；新增 `SlackAttachmentDownloaderTests`，覆盖 Slack private URL 下载、processor 前置下载和 manifest；新增 `SlackBackfillServiceTests`，覆盖 existing-log channel selection、oldest/cursor 分页、过滤去重、bot message、attachment download/manifest、log-only writeback 和缺 log 时跳过 HTTP；新增 `MomChannelQueueDispatcherTests`，覆盖同频道顺序处理、跨频道独立推进、queue full 拒绝、stop bypass 和 stop 取消当前 work 后继续 pending work；新增 `MomSandboxAndToolsTests`，覆盖 sandbox parse/path mapping、host executor、workspace path boundary、`read/write/edit/attach` tools 和 truncation。
* **[Project reference cleanup]**: `tests/Tau.Agent.Tests` 对 `Tau.Mom` 改回 `ProjectReference`，不再通过 `HintPath` 读取旧 DLL，避免测试看到 stale `MomOptions` / runtime 类型。
* **[Auth store hardening]**: 验证过程中发现并行测试会让默认 `ModelCatalog` 碰到其他测试临时设置的 `TAU_AUTH_FILE`；`OAuthCredentialStore` 现在对短暂锁定、不可读或坏 JSON 的 `auth.json` 返回空凭证，避免模型查询因为临时 auth 文件状态崩溃。
* **[Verification]**: `dotnet build src\Tau.Mom\Tau.Mom.csproj --no-restore` 通过；`dotnet test tests\Tau.Agent.Tests\Tau.Agent.Tests.csproj --no-restore` 通过（54/54）；`powershell -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore -RunSmoke` 通过，覆盖 source/test build、`Tau.Ai.Tests` 79/79、`Tau.Agent.Tests` 54/54、`Tau.Tui.Tests` 4/4、`Tau.CodingAgent.Tests` 54/54、`Tau.Pods.Tests` 7/7、WebUi smoke 和 Mom smoke。

### 🧠 Design Intent (Why)

上一轮已经把 `Tau.Mom` 的结果层收口成结构化 `ToolEvents/StopReason/Usage`，但请求层仍然半成品：`title` 虽然存在于 `DelegationRequest`，runner 实际只吃 `Prompt`；`metadata` 和未来的 attachment 语义也没有进入模型上下文。这会让 `.json` 请求看起来“支持结构化字段”，实际 agent 执行时却拿不到这些信息。

这轮没有直接移植上游 `mom` 的完整 Slack/store/sandbox 体系，而是先把 Tau 当前已经声明的 request 契约做实：

- `title` 进入 session name，给后续 thread/session 呈现稳定锚点；
- `metadata` / `attachments` 用稳定文本 envelope 进入 runner，不破坏当前 `RunAsync(string input)` 的边界；
- `MEMORY.md` 作为最小 workspace memory 进入同一个 envelope，对齐上游 mom 的持久记忆方向；
- `log.jsonl` 先以最近人类消息摘要进入同一个 envelope，同时本地 file delegation 会写回用户请求和 bot 结果，对齐上游 mom 的 channel history 方向，但不伪装成已经完成 Slack session sync；
- `status.json` 先记录本地 `running/completed/failed/cancelled` 状态，对齐 Slack adapter 需要的 busy-state / stop flow，但不提前引入 Slack SDK 或事件队列；
- 读取 `status.json` 做同一 workdir 的最小 busy-state guard，避免本地 worker 在 Slack adapter 尚未接入前重复处理同一 channel/workspace；同时用默认 60 分钟 stale 窗口保留崩溃恢复能力；
- `events/*.json` 先走本地文件唤醒，把上游 immediate / one-shot / periodic 的形态压到现有 inbox 委派链上；这样不会提前承诺 Slack Socket Mode 已接通，但已经可以让外部脚本、计划任务或手工文件唤醒 Tau.Mom；
- `attachments` 先走本地 staging，把已存在的文件复制到 `workingDirectory/attachments/` 并写 manifest / channel log 元数据；这样先固定上游 `original/local` 布局和 agent 可见路径，不把 Slack authenticated download、backfill 和 sandbox workspace 绑在同一次改动里；
- `<mom_runtime_context>` 先走 runner 输入前缀，把上游 mom system prompt 中的 workspace layout、events、memory/log/status、attachment manifest 和 `[SILENT]` 规则落到当前 Tau seam；这样不需要改 `Tau.CodingAgent` 的全局 system prompt，也不假装已经具备完整 Slack `AgentSession`；
- `context.json` 先走 Tau-native 平面 session snapshot，把同一 channel/workdir 的 runtime messages 接续起来；这比直接同步上游 `SessionManager` JSONL tree 风险低，并且复用当前已经验证过的 `CodingAgentSessionStore`。
- `last_prompt.jsonl` 先走本地 debug snapshot，把真正传给 runner 的 prompt 组成、恢复前消息和附件计数落到 channel workdir；这对后续 Slack session sync、附件下载和 stop/queue 排障更有价值，同时不假装当前 string-only runner 已经支持图片多模态输入。
- `ChannelWorkspaceLayout` 先把上游 workspace layout 里的 `scratch/`、`skills/` 和 `SYSTEM.md` 变成本地真实目录和 prompt 事实；这为后续 sandbox/tool delegation 留稳定路径，同时不提前实现 custom skill loader。
- outbox 保留同一组字段，保证后续 Slack/workspace 适配层能从结果文件恢复完整请求上下文。

这样下一步继续接 Slack/workspace/sandbox 时，不需要再回头重做 request contract，只要把现有 context seam 接到更高层宿主即可。

本轮补的 `MomChannelMessage` 是这个方向的下一步：先把 Slack 的消息事实压成 Tau-native envelope，而不是直接把 Slack SDK 的事件类型传进 worker。这样 file delegation、local events、Slack startup backfill 和实时 Slack event 会进入同一个 request-mapping seam；后续 fake Slack transport / real Slack adapter 只负责采集消息、响应消息和下载文件，不再拥有 `DelegationRequest` 语义。

随后补的 `MomChannelMessageProcessor` 把 channel runtime 语义从 Slack transport 里提前剥离出来：busy-state 读取 `status.json`，thread message 走 `RespondInThreadAsync`，普通 message 走 `RespondAsync`，并统一写 status/log 与 staging attachments。真实 Slack adapter 接入时只需要实现 `IMomChannelTransport`，发送侧已经可以复用 `SlackWebApiResponder`。

继续补的 `SlackAttachmentDownloader` 把 Slack authenticated download 放在 processor 层，而不是 mapper 或 transport 层：mapper 保持纯 JSON 映射，transport 保持 Socket Mode open/read/ack，下载则需要 bot token、工作目录和 attachment manifest，属于 channel runtime 语义。下载失败时保留原 attachment 并继续处理，避免 Slack 文件权限问题把整条消息吞掉。

这轮的 `MomChannelQueueDispatcher` 对齐上游 `ChannelQueue` 的关键行为：同频道消息串行，不同频道可以并行；pending queue 达到 `SlackChannelQueueLimit` 后直接回复并丢弃；`stop` 不进入队列，而是直接走 processor 的 stop path。

继续补的 `SlackBackfillService` 对齐上游 startup backfill 的边界，但刻意不复用实时 processor：backfill 是启动时的历史补账，只应该补 `log.jsonl` 和附件 manifest，不应该把旧消息重新派发给 coding agent。它只扫描已经有 `log.jsonl` 的 channel，这样不会把 Slack workspace 里所有频道都主动拉进 Tau；同时把 old-message filter、timestamp 去重、bot 自身消息保留和 cursor/page limit 固定在本地测试里，为后续真实 Slack smoke 留出明确检查点。

继续补的 `MomChannelRunRegistry` 把 stop 从“状态文件上的占位响应”推进成真正的 in-process cancellation：`status.json` 只能表达最近运行状态，不能跨进程取消一个已经不存在的 runner；因此 Tau 只对当前进程登记的 active channel run 暴露 stop，取消 linked `CancellationToken` 后让 `IDelegationAgentRunner` 自然退出，再把最终状态写成 `cancelled`。如果只有旧 `running` status 但没有 active registry entry，processor 会明确回复当前进程没有可取消 runner。

这轮的 sandbox/tool 切片继续保持同一个原则：先固定 Tau-native 边界，再接外部环境。上游 mom 的核心不是“某个 Docker 命令”，而是 executor 抽象加 `bash/read/write/edit/attach` 工具集。Tau 现在默认 host sandbox，本地测试能验证命令执行、path boundary、文件读写编辑和 attach result；`docker:<container>` 先落配置解析、`/workspace` path translation 和 `docker exec` command construction，真实 container smoke 后置，避免把本机没有稳定 Docker 环境的状态误写成迁移完成。

对 skills 的处理同样按上游事实收口：`loadMomSkills()` 只是发现 workspace/channel `SKILL.md` 并放进 system prompt，脚本仍由 `bash/read/write/edit` 执行；因此 Tau 不新增 direct-tool loader，而是补齐 XML `<available_skills>`、channel override、`disable-model-invocation` 和 sandbox path translation。这样既对齐上游，也避免以后再重复造一套不在参考项目里的技能执行层。

### 📁 Files Modified

* `src/Tau.Mom/DelegationRequest.cs`
* `src/Tau.Mom/DelegationResult.cs`
* `src/Tau.Mom/ChannelAttachmentEntry.cs`
* `src/Tau.Mom/ChannelAttachmentStore.cs`
* `src/Tau.Mom/ChannelLogEntry.cs`
* `src/Tau.Mom/ChannelLogStore.cs`
* `src/Tau.Mom/ChannelSessionStore.cs`
* `src/Tau.Mom/ChannelStatus.cs`
* `src/Tau.Mom/ChannelStatusStore.cs`
* `src/Tau.Mom/ChannelWorkspaceLayout.cs`
* `src/Tau.Mom/IMomChannelTransport.cs`
* `src/Tau.Mom/MomChannelCommands.cs`
* `src/Tau.Mom/MomChannelMessage.cs`
* `src/Tau.Mom/MomChannelMessageProcessor.cs`
* `src/Tau.Mom/MomChannelQueueDispatcher.cs`
* `src/Tau.Mom/MomChannelWorkspace.cs`
* `src/Tau.Mom/MomChannelRunRegistry.cs`
* `src/Tau.Mom/MomSandbox.cs`
* `src/Tau.Mom/MomToolOutputTruncator.cs`
* `src/Tau.Mom/MomTools.cs`
* `src/Tau.Mom/SlackAttachmentDownloader.cs`
* `src/Tau.Mom/SlackBackfillService.cs`
* `src/Tau.Mom/SlackEventMapper.cs`
* `src/Tau.Mom/SlackSocketModeTransport.cs`
* `src/Tau.Mom/SlackSocketModeWorker.cs`
* `src/Tau.Mom/SlackWebApiResponder.cs`
* `src/Tau.Mom/MomEventFile.cs`
* `src/Tau.Mom/MomEventProcessor.cs`
* `src/Tau.Mom/MomRuntimeContext.cs`
* `src/Tau.Mom/ChannelPromptDebugStore.cs`
* `src/Tau.Mom/MomCompactJsonContext.cs`
* `src/Tau.Mom/FileDelegationProcessor.cs`
* `src/Tau.Mom/MomJsonContext.cs`
* `src/Tau.Mom/MomOptions.cs`
* `src/Tau.Mom/Program.cs`
* `src/Tau.Mom/RuntimeDelegationAgentRunner.cs`
* `src/Tau.CodingAgent/Runtime/RuntimeCodingAgentRunner.cs`
* `src/Tau.Ai/Auth/OAuth/OAuthCredentialStore.cs`
* `tests/Tau.Agent.Tests/FileDelegationProcessorTests.cs`
* `tests/Tau.Agent.Tests/MomChannelMessageTests.cs`
* `tests/Tau.Agent.Tests/MomChannelMessageProcessorTests.cs`
* `tests/Tau.Agent.Tests/MomChannelQueueDispatcherTests.cs`
* `tests/Tau.Agent.Tests/MomSandboxAndToolsTests.cs`
* `tests/Tau.Agent.Tests/SlackAttachmentDownloaderTests.cs`
* `tests/Tau.Agent.Tests/SlackBackfillServiceTests.cs`
* `tests/Tau.Agent.Tests/SlackEventMapperTests.cs`
* `tests/Tau.Agent.Tests/SlackSocketModeTransportTests.cs`
* `tests/Tau.Agent.Tests/SlackWebApiResponderTests.cs`
* `tests/Tau.Agent.Tests/MomEventProcessorTests.cs`
* `tests/Tau.Agent.Tests/RuntimeDelegationAgentRunnerTests.cs`
* `tests/Tau.Agent.Tests/Tau.Agent.Tests.csproj`
* `tests/Tau.Ai.Tests/OAuthCredentialStoreTests.cs`
* `scripts/verify-dotnet.ps1`
* `README.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-04-23-tau-port-baseline.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `next.md`
