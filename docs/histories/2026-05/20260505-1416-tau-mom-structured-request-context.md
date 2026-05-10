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
* **[SYSTEM and skill docs context]**: `RuntimeDelegationAgentRunner` 会把非空 `SYSTEM.md` 作为 `system_configuration_log` 注入 prompt，并扫描 workspace/channel `skills/**/SKILL.md` frontmatter 生成 skill docs inventory；当前只作为本地上下文，不声明 custom mom skill runtime 已接通。
* **[Complete port plan]**: 新增 `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`，把用户明确收口的“完完整整的移植”升级为跨模块 parity closure 计划；旧 baseline plan 保留为 P1 基线和决策历史。
* **[Slack-compatible message envelope]**: 新增 `MomChannelMessage` / `MomChannelAttachment`，统一承载 channel、user、userName、displayName、ts、threadTs、text、attachments、provider/model/title/metadata；`FileDelegationProcessor` 与 `MomEventProcessor` 先映射到同一 envelope，再生成 `DelegationRequest`，给后续 fake Slack transport、real Slack adapter、backfill、queue 和 file download 留稳定接缝。
* **[Transport/responder seam]**: 新增 `IMomChannelTransport` / `IMomChannelResponder` / `MomChannelMessageProcessor`，固定未来 Slack adapter 的输入输出边界；processor 负责 busy-state、stop placeholder、typing、thread response、attachment staging、status/log writeback 和 runner 调用，responder 只负责响应/thread/typing/upload。
* **[Attachment path normalization]**: `FileDelegationProcessor` 会按已解析的 `workingDirectory` 规范化相对/绝对 attachment 路径，避免 outbox 和 runner 看到的还是相对字符串。
* **[Outbox contract sync]**: `DelegationResult` 新增 `title` 与 `attachments`，让结果文件保留请求上下文，而不是只剩 provider/model/workdir 和 metadata。
* **[Smoke isolation]**: `scripts/verify-dotnet.ps1 -RunSmoke` 的 Mom smoke 改用临时 `workdir` 和 `events` 目录，通过 immediate event 触发 `--once`，并断言 smoke 运行会写出 channel log、final channel status、`last_prompt.jsonl`、`scratch/`、workspace `skills/` 与 channel `skills/`，避免运行态验证在仓库根目录生成 `log.jsonl` / `status.json`。
* **[Tests]**: 升级 `FileDelegationProcessorTests`，断言 `title` / `attachments` 进入 request 和 outbox，断言 `log.jsonl` 写入用户请求与 bot 结果，覆盖 malformed JSONL + 无尾随换行时的去重和追加行为，覆盖 completed / failed 两类 `status.json`，并覆盖新鲜 `running` 状态跳过、过期 `running` 状态继续处理两类 busy-state 行为；升级 `RuntimeDelegationAgentRunnerTests`，断言 structured context、workspace memory、channel history、`<mom_runtime_context>`、`context.json` restore/save、`last_prompt.jsonl` prompt debug snapshot、`scratch/` / `skills/` 目录 bootstrap、`SYSTEM.md` 注入与 skill docs inventory 真正进入 runner 输入和 runtime flow，并设置 session name；新增 `MomEventProcessorTests`，覆盖 immediate、one-shot、periodic 和 invalid event 文件处理；新增 `MomChannelMessageTests`，覆盖 Slack-compatible envelope 到 `DelegationRequest` 的字段映射和 local request 默认值；新增 `MomChannelMessageProcessorTests`，覆盖 channel message delegation + thread response、busy-state 响应、stop placeholder。
* **[Project reference cleanup]**: `tests/Tau.Agent.Tests` 对 `Tau.Mom` 改回 `ProjectReference`，不再通过 `HintPath` 读取旧 DLL，避免测试看到 stale `MomOptions` / runtime 类型。
* **[Auth store hardening]**: 验证过程中发现并行测试会让默认 `ModelCatalog` 碰到其他测试临时设置的 `TAU_AUTH_FILE`；`OAuthCredentialStore` 现在对短暂锁定、不可读或坏 JSON 的 `auth.json` 返回空凭证，避免模型查询因为临时 auth 文件状态崩溃。
* **[Verification]**: `dotnet build src\Tau.Mom\Tau.Mom.csproj --no-restore`、`dotnet test tests\Tau.Agent.Tests\Tau.Agent.Tests.csproj --no-restore` 全部通过（20/20）；`dotnet test tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj` 通过（79/79）；`powershell -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` 与 `powershell -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore -RunSmoke` 均通过。

### 🧠 Design Intent (Why)

上一轮已经把 `Tau.Mom` 的结果层收口成结构化 `ToolEvents/StopReason/Usage`，但请求层仍然半成品：`title` 虽然存在于 `DelegationRequest`，runner 实际只吃 `Prompt`；`metadata` 和未来的 attachment 语义也没有进入模型上下文。这会让 `.json` 请求看起来“支持结构化字段”，实际 agent 执行时却拿不到这些信息。

这轮没有直接移植上游 `mom` 的完整 Slack/store/sandbox 体系，而是先把 Tau 当前已经声明的 request 契约做实：

- `title` 进入 session name，给后续 thread/session 呈现稳定锚点；
- `metadata` / `attachments` 用稳定文本 envelope 进入 runner，不破坏当前 `RunAsync(string input)` 的边界；
- `MEMORY.md` 作为最小 workspace memory 进入同一个 envelope，对齐上游 mom 的持久记忆方向；
- `log.jsonl` 先以最近人类消息摘要进入同一个 envelope，同时本地 file delegation 会写回用户请求和 bot 结果，对齐上游 mom 的 channel history 方向，但不伪装成已经完成 Slack session sync；
- `status.json` 先记录本地 `running/completed/failed` 状态，对齐后续 Slack adapter 需要的 busy-state / stop flow，但不提前引入 Slack SDK 或事件队列；
- 读取 `status.json` 做同一 workdir 的最小 busy-state guard，避免本地 worker 在 Slack adapter 尚未接入前重复处理同一 channel/workspace；同时用默认 60 分钟 stale 窗口保留崩溃恢复能力；
- `events/*.json` 先走本地文件唤醒，把上游 immediate / one-shot / periodic 的形态压到现有 inbox 委派链上；这样不会提前承诺 Slack Socket Mode 已接通，但已经可以让外部脚本、计划任务或手工文件唤醒 Tau.Mom；
- `attachments` 先走本地 staging，把已存在的文件复制到 `workingDirectory/attachments/` 并写 manifest / channel log 元数据；这样先固定上游 `original/local` 布局和 agent 可见路径，不把 Slack authenticated download、backfill 和 sandbox workspace 绑在同一次改动里；
- `<mom_runtime_context>` 先走 runner 输入前缀，把上游 mom system prompt 中的 workspace layout、events、memory/log/status、attachment manifest 和 `[SILENT]` 规则落到当前 Tau seam；这样不需要改 `Tau.CodingAgent` 的全局 system prompt，也不假装已经具备完整 Slack `AgentSession`；
- `context.json` 先走 Tau-native 平面 session snapshot，把同一 channel/workdir 的 runtime messages 接续起来；这比直接同步上游 `SessionManager` JSONL tree 风险低，并且复用当前已经验证过的 `CodingAgentSessionStore`。
- `last_prompt.jsonl` 先走本地 debug snapshot，把真正传给 runner 的 prompt 组成、恢复前消息和附件计数落到 channel workdir；这对后续 Slack session sync、附件下载和 stop/queue 排障更有价值，同时不假装当前 string-only runner 已经支持图片多模态输入。
- `ChannelWorkspaceLayout` 先把上游 workspace layout 里的 `scratch/`、`skills/` 和 `SYSTEM.md` 变成本地真实目录和 prompt 事实；这为后续 sandbox/tool delegation 留稳定路径，同时不提前实现 custom skill loader。
- outbox 保留同一组字段，保证后续 Slack/workspace 适配层能从结果文件恢复完整请求上下文。

这样下一步继续接 Slack/workspace/sandbox 时，不需要再回头重做 request contract，只要把现有 context seam 接到更高层宿主即可。

本轮补的 `MomChannelMessage` 是这个方向的下一步：先把 Slack 的消息事实压成 Tau-native envelope，而不是直接把 Slack SDK 的事件类型传进 worker。这样 file delegation、local events、未来 backfill 和实时 Slack event 会进入同一个 request-mapping seam；后续 fake Slack transport / real Slack adapter 只负责采集消息、响应消息和下载文件，不再拥有 `DelegationRequest` 语义。

随后补的 `MomChannelMessageProcessor` 把 channel runtime 语义从 Slack transport 里提前剥离出来：busy-state 读取 `status.json`，stop 先返回诚实的本地 cancellation 未接线提示，thread message 走 `RespondInThreadAsync`，普通 message 走 `RespondAsync`，并统一写 status/log 与 staging attachments。真实 Slack adapter 接入时只需要实现 `IMomChannelTransport` / `IMomChannelResponder`。

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
* `src/Tau.Mom/MomChannelMessage.cs`
* `src/Tau.Mom/MomChannelMessageProcessor.cs`
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
* `src/Tau.Ai/Auth/OAuth/OAuthCredentialStore.cs`
* `tests/Tau.Agent.Tests/FileDelegationProcessorTests.cs`
* `tests/Tau.Agent.Tests/MomChannelMessageTests.cs`
* `tests/Tau.Agent.Tests/MomChannelMessageProcessorTests.cs`
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
