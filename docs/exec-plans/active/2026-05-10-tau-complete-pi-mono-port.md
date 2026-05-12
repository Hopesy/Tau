# Tau 完整 pi-mono 移植计划

## 目标

把 Tau 从“按模块补关键切片”升级为“以 `pi-mono-main` 为参考源，逐模块追到可评审的功能等价层”。这份 plan 是完整移植的总路线图，现有 `2026-04-23-tau-port-baseline.md` 继续作为已完成基线和决策历史，本文件负责后续 parity closure。

“完整”在这里不是一次性把所有代码机械翻译到 C#，而是每个上游能力都要在 Tau 中落成下列三件事：

- 有明确的 Tau-native 边界和数据模型。
- 有本地可验证的 test 或 smoke。
- 文档、history、quality、next 同步说明已完成/未完成，不把 seam 伪装成完整功能。

## 当前事实

- `Tau.Ai` 已经有多 provider 专用路径、generated model catalog、models.json custom config 和 provider auth resolver，但 OAuth login/refresh、完整 AWS credential chain、dynamic provider API 注册和真实 e2e 仍未闭合。
- `Tau.CodingAgent` 已有平面 session store、JSONL session tree 基线、settings、slash command router、基础命令、手动 compaction 和 opt-in auto-compaction threshold；当前已支持 append-only JSONL entries、labels、compaction entry metadata baseline、resume/fork/tree、`.jsonl` branch export/import、`.html/.htm` standalone transcript export、HTML branch outline、HTML 内嵌 JSONL 下载、基础 tree stats、`/session` 估算 token/context usage 与 auto threshold budget、prompt template baseline、skill command baseline、JSON extension command/resource baseline、`TAU_CODING_AGENT_AUTO_COMPACT_TOKENS` / `TAU_CODING_AGENT_AUTO_COMPACT_INSTRUCTIONS` 和 flat JSON 兼容，但仍缺上游 interactive tree navigator、retained-message cut-point、完整 TypeScript extension runtime/resource selector/diagnostics、share/Gist export 和 richer HTML template。
- `Tau.WebUi` 已有可持久化 session、provider/model 选择和最小聊天页，但仍缺 streaming、attachments、rich rendering、auth/settings UX。
- `Tau.Mom` 已有本地 inbox/outbox/events、runtime context、attachment staging、workspace layout、channel log/status/context/last_prompt seams，并已补 Slack Socket Mode/Web API seam、startup backfill seam、Slack private file download seam、per-channel queue seam、true cancellable stop seam、Agent Skills prompt inventory 和 Mom host sandbox/tool delegation seam；仍缺真实 Docker sandbox smoke 和端到端 Slack flow。
- `Tau.Pods` 已有 config/probe/exec，仍缺 deploy/stop/restart/health、model lifecycle 和远端 transport hardening。
- Windows 本机验证入口以 `powershell -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore -RunSmoke` 为准；不要把本机 bash/WSL 缺失误判为 Tau 失败。

## 总体顺序

完整移植按“可运行 seam -> adapter/transport -> e2e parity”的顺序推进，不再只按文件数量补代码：

1. **P0：收拢当前 dirty chain**
   - 保持当前 Tau.Mom 迁移链可 build/test/smoke。
   - 所有新 seam 都先走 fake/local transport 测试，再接真实外部 SDK。
   - 本阶段退出标准：`Tau.Mom` 独立 build、`Tau.Agent.Tests`、`verify-dotnet.ps1 -SkipRestore -RunSmoke` 全绿。

2. **P1：Tau.Mom full parity**
   - 建立 Slack-compatible channel message envelope，让 file/events/Slack/backfill 共享同一消息事实。
   - 接 Slack Socket Mode / Web API adapter：mention、DM、message logging、bot message logging、thread response、typing/update/delete/upload。
   - 已接 channel queue、busy-state、true cancellable stop、queue limit、startup backfill 和 old-message logging。
   - 已接 Slack file download 到 `ChannelAttachmentStore`，保留 `original/local/url` 语义。
   - 接 workspace/sandbox/tool delegation：`bash/read/write/edit/attach`，并保证 `scratch/`、`attachments/`、`SYSTEM.md`、`skills/` 的路径 authority 不分叉。
   - skill docs 对齐上游 Agent Skills prompt inventory；Mom skills 通过 `read/bash` 使用脚本，不额外注册为直接 tool 名。
   - 端到端验证先用 fake Slack transport，再用可配置真实 Slack smoke。

3. **P2：Tau.CodingAgent session / command / extension parity**
   - 已移植 JSONL SessionManager tree 的最小可用基线：session header、message/model/session-info/label entries、branch/fork/resume、tree output、基础 tree stats、`.jsonl` branch export/import。
   - 继续补 interactive tree navigator 和更完整的 session metadata。
   - 已补手动 compaction 的 JSONL entry metadata baseline、opt-in token-threshold auto-compaction 和 `/session` token budget/context usage；继续补 retry/rollback 和上游 recent-message retention。
   - 已补 prompt template discovery/expansion baseline、skill command discovery/expansion baseline 与 Tau-native JSON extension command/resource baseline；继续补完整 TypeScript extension runtime、custom tools/events、theme loader、resource selector 和 diagnostics。
   - 已补 standalone HTML transcript export、本地 JSONL download baseline 和 branch outline；继续补 share/export/import parity：share/Gist、labels/tree metadata、clipboard/rich content、richer HTML template。
   - 保持当前 Tau snapshot store 作为迁移/兼容入口，不直接破坏已有平面 session。

4. **P3：Tau.Ai provider/auth/model parity**
   - OAuth/device login/refresh：Anthropic、GitHub Copilot、OpenAI Codex、Gemini CLI/Antigravity。
   - AWS credential chain：SSO、AssumeRole、credential_process、IMDS、ECS、web identity。
   - dynamic provider API 注册：models.json 中未预注册 provider 的 runtime registration。
   - generated model seed 持续同步到 Tau 已支持 API 家族；模型表不能领先于 provider 行为。
   - 建立 provider e2e matrix，把 stub tests 与真实云端 smoke 分层。

5. **P4：Tau.WebUi parity**
   - Streaming message UI，和 `AssistantMessageStream` / agent events 对齐。
   - Attachments upload/download、image/tool result rendering。
   - Thinking/tool timeline/rich markdown/code block rendering。
   - Auth/settings UX：provider status、login entry、models.json/settings 管理。
   - Web session restore、rename、delete、export/import 与 CodingAgent session 语义对齐。

6. **P5：Tau.Tui parity**
   - 真正输入编辑器、选择/历史/快捷键体系。
   - 差分渲染、message area、status area、tool timeline。
   - 与 CodingAgent richer rendering 共享必要抽象，不把 UI 状态塞回 runtime。

7. **P6：Tau.Pods parity**
   - deploy/stop/restart/health lifecycle。
   - model lifecycle、remote command output、failure classification。
   - SSH/HTTP transport hardening，配置校验与安全边界。

8. **P7：release / CI / docs parity**
   - release 产物改成真实 Tau executable/package。
   - CI 接入 PowerShell 或 bash 等价 smoke，覆盖 WebUi/Mom/Pods 高价值运行态。
   - 文档、references、security、supply-chain、release notes 以可审计方式同步。

## 当前执行切片

当前执行切片已推进到 `Tau.CodingAgent` 的 JSONL session tree、HTML transcript export、prompt/skill 和 JSON extension command baseline：

- 新增 `CodingAgentTreeSessionStore` / `CodingAgentTreeSessionController`，保留 flat JSON snapshot 兼容入口，同时默认维护 `.tau/coding-agent-session.jsonl`；`TAU_CODING_AGENT_TREE_SESSION_FILE` 或 `.jsonl` 形式的 `TAU_CODING_AGENT_SESSION_FILE` 可指定 tree session 文件。
- JSONL 第一行写 `type=session` header，后续 append-only 写 `message`、`model_change`、`session_info`、`label` entries，每个 entry 带 `id`、`parentId`、`timestamp`；host 通过 runner message count diff 追加新消息，避免重写旧 history。
- `/session` 同时报告 flat stats、估算 token/context usage、auto-compaction threshold budget 和 tree file/leaf/entry/message/branch/label 信息；`/tree [max entries]` 输出短 id、parent、entry 摘要、label 和当前 branch 标记；`/label <entry-id> [label | clear]` 追加 label change；`/fork <entry-id>` 从历史 entry 切出新 branch 并恢复当前 runner messages；`/resume [latest | path.jsonl]` 恢复 JSONL session。
- `/export` 默认导出 standalone HTML transcript；`/export <path.html|path.htm>` 显式导出 HTML；HTML 提供 branch outline、内嵌当前 branch JSONL 并提供 Download JSONL 按钮；`/export <path.jsonl>` 导出当前 branch 为独立 JSONL session；`/import <path.jsonl>` 等价于 resume；其他非 `.jsonl/.html/.htm` 的 `/export` / `/import` 继续走 Tau flat snapshot JSON，避免破坏旧 session。
- prompt template discovery/expansion baseline 已接入：用户目录 `~/.tau/prompts`、项目目录 `./.tau/prompts` 和 `TAU_CODING_AGENT_PROMPT_PATHS` 指定路径可提供 `.md` prompt template；`/prompts` 列表展示模板，非内置 slash 输入命中模板时会在 runner 调用前展开参数占位。
- skill command discovery/expansion baseline 已接入：用户目录 `~/.tau/skills`、项目目录 `./.tau/skills` 和 `TAU_CODING_AGENT_SKILL_PATHS` 指定路径可提供 `SKILL.md`；`/skills` 列表展示 `/skill:<name>` 命令，`/skill:<name> args` 会把 skill body 包装成上游风格 `<skill>` block 后发送给 runner；可见 skill 会进入默认 system prompt inventory，`disable-model-invocation: true` 只保留显式命令调用。
- JSON extension command/resource baseline 已接入：用户目录 `~/.tau/extensions`、项目目录 `./.tau/extensions` 和 `TAU_CODING_AGENT_EXTENSION_PATHS` 指定路径可提供 `.json` command definitions；`/extensions` 列表展示命令，非内置 slash 输入先查 extension command，再查 skill/prompt；status-only command 直接写 UI，`sendToRunner=true` command 会展开 `prompt/response` 后发送给 runner，重复 name 按上游规则解析成 `name:1`、`name:2`；同一 JSON 可通过 `promptPaths/skillPaths` 或 `resources.promptPaths/resources.skillPaths` 贡献 prompt/skill 发现路径，路径相对 extension JSON 所在目录解析。
- 当前仍不把 interactive tree navigator、share/Gist export、richer HTML template、上游 retained-message cut-point、retry/rollback、完整 TypeScript extension runtime、custom tools/events、theme loader、resource selector 和 diagnostics 写成已完成。

## 风险与约束

- 不把“本地 seam 已完成”写成“真实 Slack/Sandbox 已完成”。
- 不为追求 1:1 文件数量而牺牲 Tau 当前 AOT/source-gen/零 provider SDK 的边界。
- 外部服务能力必须先有 fake transport/stub handler 测试，再接真实 e2e。
- Windows 本机验证串行执行；不要并行跑会写相同 `bin/obj/output` 的 build/test。

## 验证方式

- `dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal`
- `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal`
- targeted tests 覆盖 `TAU_CODING_AGENT_AUTO_COMPACT_TOKENS` / `TAU_CODING_AGENT_AUTO_COMPACT_INSTRUCTIONS`、pending input token estimate、普通消息前自动 compaction、flat session 持久化和 JSONL `fromHook=true`
- 真实 CLI smoke：临时 `TAU_CODING_AGENT_SKILL_PATHS` 下执行 `/skills`，确认真实进程能发现并列出 `/skill:<name>`
- 真实 CLI smoke：临时 `TAU_CODING_AGENT_EXTENSION_PATHS` 下执行 `/extensions` 与 status-only extension command，确认真实进程能发现并执行 JSON extension command；再通过同一 extension JSON 的 `resources.promptPaths/resources.skillPaths` 确认 `/prompts` 和 `/skills` 能发现 extension-contributed resources
- 真实 CLI smoke：`/export` 默认 HTML、`/import <snapshot.json>` -> `/export <session.html>`，检查 HTML 包含 user/assistant/thinking/tool call/tool result 内容
- `dotnet build src\Tau.Mom\Tau.Mom.csproj --no-restore`
- `dotnet test tests\Tau.Agent.Tests\Tau.Agent.Tests.csproj --no-restore`
- `powershell -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore -RunSmoke`

## 进度记录

- [x] 完整移植总路线图落到 active plan。
- [x] `Tau.Mom` Slack-compatible channel message envelope seam。
- [x] `Tau.Mom` fake Slack transport / responder seam（transport/responder interface + channel processor + fake responder tests）。
- [x] `Tau.Mom` Slack Web API responder seam（`chat.postMessage` / thread response / `files.uploadV2` + fake HTTP tests）。
- [x] `Tau.Mom` Slack event mapper seam（Socket Mode JSON `app_mention` / DM / skip / file metadata -> `MomChannelMessage` + mapper tests）。
- [x] `Tau.Mom` Slack Socket Mode transport seam（auth/open/read/ack/reconnect + config-gated worker）。
- [x] `Tau.Mom` Slack startup backfill seam（existing log channels + conversations.history + old-message log-only writeback）。
- [x] `Tau.Mom` Slack private file download seam（bot-token HTTP download + attachment manifest）。
- [x] `Tau.Mom` Slack per-channel queue seam（sequential same-channel processing + queue limit + stop bypass）。
- [x] `Tau.Mom` true cancellable stop seam（in-process run registry + linked cancellation token + cancelled status）。
- [x] `Tau.Mom` Agent Skills prompt inventory parity（XML `<available_skills>` + sandbox path mapping + channel override + disable-model-invocation）。
- [x] `Tau.Mom` sandbox/tool delegation seam（host sandbox + docker path/command construction + `bash/read/write/edit/attach` Mom tools）。
- [ ] `Tau.Mom` real Slack smoke。
- [ ] `Tau.Mom` Docker sandbox smoke。
- [x] `Tau.CodingAgent` JSONL SessionManager tree baseline（header + message/model/session-info/label entries + branch/fork/resume/tree + stats + `.jsonl` export/import）。
- [x] `Tau.CodingAgent` standalone HTML transcript export baseline（`/export` 默认 HTML + `.html/.htm` export + text/thinking/tool/image rendering + branch outline + 内嵌 JSONL 下载）。
- [x] `Tau.CodingAgent` manual compaction entry metadata baseline（`compaction` entry + `summary/firstKeptEntryId/tokensBefore/fromHook` + branch restore summary message）。
- [x] `Tau.CodingAgent` opt-in auto-compaction threshold baseline（`TAU_CODING_AGENT_AUTO_COMPACT_TOKENS` + pending input estimate + JSONL `fromHook=true`）。
- [x] `Tau.CodingAgent` `/session` token budget/context usage baseline（flat/tree session stats + model context window + auto-compaction threshold remaining）。
- [x] `Tau.CodingAgent` prompt template discovery/expansion baseline（`~/.tau/prompts` + `./.tau/prompts` + `TAU_CODING_AGENT_PROMPT_PATHS` + `/prompts` + 参数替换）。
- [x] `Tau.CodingAgent` skill command discovery/expansion baseline（`~/.tau/skills` + `./.tau/skills` + `TAU_CODING_AGENT_SKILL_PATHS` + `/skills` + `/skill:<name>` + system prompt inventory）。
- [x] `Tau.CodingAgent` JSON extension command/resource baseline（`~/.tau/extensions` + `./.tau/extensions` + `TAU_CODING_AGENT_EXTENSION_PATHS` + `/extensions` + status/runner command expansion + duplicate command invocation names + prompt/skill resource paths）。
- [ ] `Tau.CodingAgent` interactive tree navigator / richer session metadata。
- [~] `Tau.CodingAgent` auto-compaction / token threshold / retained-message cut-point（threshold 与 `/session` budget baseline 已完成，retained-message cut-point / retry / rollback 仍缺）。
- [ ] `Tau.Ai` OAuth/device login parity。
- [ ] `Tau.WebUi` streaming/attachments/rich rendering。
- [ ] `Tau.Pods` deploy/stop/restart/health。

## 决策记录

- 2026-05-10：决定新增完整移植总 plan，而不是继续只在 baseline plan 里追加零散条目。原因是用户目标已经明确升级为“完完整整的移植”，需要一个跨模块、跨阶段、可持续评审的 parity closure 路线图；旧 baseline plan 保留为已完成基线和历史决策来源。
- 2026-05-10：决定先把 `Tau.Mom` 的 Slack event 事实抽成 `MomChannelMessage`，而不是直接接 Slack SDK。原因是上游 `SlackEvent`、backfill message、local event 和 file delegation 都会汇入同一 channel log/session/status/attachment 语义；先固定 Tau-native envelope，可以让 file/events/fake Slack/real Slack 共用映射和测试。
- 2026-05-10：决定先落 `IMomChannelTransport` / `IMomChannelResponder` / `MomChannelMessageProcessor`，而不是让真实 Slack adapter 直接调用 `IDelegationAgentRunner`。原因是 busy-state、stop、typing、thread response、status/log writeback 和 attachment staging 都属于 Mom channel runtime 语义；Slack SDK adapter 应只负责收消息、发消息、上传/下载文件。

- 2026-05-10：决定先把 Slack Web API responder 单独落成 SlackWebApiResponder，不和 Socket Mode transport 混在同一切片。原因是发消息、thread 回复和文件上传的 HTTP 契约可以用 fake handler 本地验证，且不需要真实 Slack token；Socket Mode 收消息、用户/频道缓存、backfill 和文件下载继续作为下一切片。
- 2026-05-10：决定先把 Slack receive-side 规则落成 `SlackEventMapper`，仍不引入 Slack SDK。原因是上游 `app_mention` / `message` 的过滤、mention stripping、DM 触发和 file metadata 语义可以用纯 JSON 本地验证；真实 Socket Mode transport 后续只负责连接、ack 和用户/频道缓存。
- 2026-05-10：决定把 Socket Mode transport 做成 SlackSocketModeTransport + ISlackSocketModeConnector + config-gated SlackSocketModeWorker，而不是直接把 ClientWebSocket 写进 worker。原因是 open/ack/read/error 脱敏可以用 fake HTTP/WebSocket 本地验证，默认不开启也不会影响现有 inbox/events worker；backfill、文件下载、queue limit 和 stop cancellation 当时继续后置，后续已拆成独立切片补齐。
- 2026-05-11：决定把 Slack private file download 做成 `SlackAttachmentDownloader`，接在 `MomChannelMessageProcessor` 生成 `DelegationRequest` 之前，而不是塞进 `SlackEventMapper`。原因是 mapper 只负责 Slack JSON 到 Tau-native envelope 的纯映射，authenticated download 需要 bot token、HTTP、workingDirectory 和 attachment manifest，属于 channel runtime 语义。
- 2026-05-11：决定把 Slack queue 做成 `MomChannelQueueDispatcher`，由 `SlackSocketModeWorker` enqueue 后立即继续读 Socket Mode 消息，而不是继续在 worker 中 await processor。原因是上游 mom 的 `ChannelQueue` 是 per-channel sequential work queue；Tau 也需要同频道顺序处理、不同频道独立推进、pending queue limit，并让 `stop` 不排在已有 pending work 后面。
- 2026-05-11：决定把 Slack startup backfill 做成 `SlackBackfillService`，在 Socket Mode worker 启动读消息前对已有 `log.jsonl` 的 channel 执行 `conversations.history`，只写 `log.jsonl` 不触发 delegation。原因是上游 backfill 的目标是把启动前消息补进 channel history，而不是重放旧消息；Tau 保持 mapper/processor/queue 各自边界清楚，backfill 只复用 attachment download 和 log store。
- 2026-05-11：决定把 Slack stop cancellation 做成 `MomChannelRunRegistry`，只取消当前进程内 active channel run 的 linked `CancellationToken`，而不是把 `status.json` 当作可跨进程取消句柄。原因是 `status.json` 只能证明某个 workdir 最近处于 running，不能安全取消已经不存在的 runner；真实 stop 必须落在 in-memory runner token 上，状态文件只负责记录最终 `cancelled` 结果。
- 2026-05-11：决定先把 Mom sandbox/tool delegation 做成本地可验证 seam，而不是直接跑真实 Docker e2e。原因是上游 mom 的核心边界是 executor + `bash/read/write/edit/attach` tool set；Tau 先固定 `host` executor、`docker:<container>` 配置解析、workspace path translation、tool names 和 attach result contract，真实 Docker container smoke 后置，避免把不可用外部环境误写成迁移完成。
- 2026-05-11：决定把 Mom skills 收口为 Agent Skills prompt inventory，而不是实现一套“每个 skill 自动注册成 tool”的 runtime loader。原因是上游 mom 只把 `SKILL.md` 加载进 system prompt，skill 里的脚本仍通过 `bash/read/write/edit` 使用；Tau 因此对齐 XML `<available_skills>`、sandbox path mapping、channel override 和 `disable-model-invocation`，不重复造一个上游没有的工具注册层。
- 2026-05-12：决定先把 `Tau.CodingAgent` JSONL session tree 做成可运行 baseline，而不是一次性迁入上游所有 session-manager 细节。原因是 branch/fork/resume/tree/label 和 `.jsonl` export/import 是用户可见的核心缺口；share/Gist export、richer HTML template、interactive navigator 与 auto-compaction/token threshold 可以在同一 tree store 上继续补，不需要先破坏现有 flat JSON session 兼容。
- 2026-05-12：决定把 HTML export 先落成 Tau-native standalone transcript，而不是直接搬上游带模板 JS、theme、share/Gist 的完整导出系统。原因是当前 Tau 还没有上游 TUI renderer、extension tool renderer 和 share 服务；先保证 `/export` 默认路径和 `/export <path.html>` 能从当前 branch/session 真实导出可打开 transcript，并在 HTML 内提供 branch outline 和本地 JSONL download，再继续补 richer template 和 Gist/share。
- 2026-05-12：决定把手动 `/compact` 先落成 JSONL `compaction` entry metadata baseline，而不是等待 auto-compaction 一起实现。原因是当前 Tau manual compaction 已经会改变 runtime state，如果继续用普通 message 同步到 tree，会丢失上游 session-manager 的 compaction audit boundary；先写 `summary/firstKeptEntryId/tokensBefore/fromHook` 并在 branch restore 时重建 summary message，可以为后续 token threshold 和 retained-message cut-point 复用同一 entry 语义。
- 2026-05-12：决定把 auto-compaction 先做成 opt-in token threshold，而不是默认开启。原因是当前摘要仍需要真实 provider 调用，默认启用会让无凭证本地 CLI 在普通消息前先失败；用 `TAU_CODING_AGENT_AUTO_COMPACT_TOKENS` 明确开启，并把自动触发写成 JSONL `fromHook=true`，可以先固定 audit boundary，retry/rollback 和 retained-message cut-point 后续再补。
- 2026-05-12：决定先把 token budget UI 落在 `/session` 单行状态里，而不是新增新的 dashboard 或实时状态区。原因是当前 Tau TUI 还没有完整组件/status area，`/session` 已经是 flat/tree session stats 的事实源；复用 `CodingAgentTokenEstimator` 能让手动 compaction、auto-compaction threshold 和用户可见 context usage 使用同一估算语义。
- 2026-05-12：决定先移植文件型 prompt template baseline，而不是直接实现完整 extension runtime。原因是上游 prompt template 是用户可见的动态 slash command 面，且可以完全本地验证；Tau 先对齐 `.md` frontmatter、默认/显式 prompt 目录和参数替换，让 `/template args` 能真正进入 runner，extension command、skill command 和 resource selector 后续在同一发现层继续补。
- 2026-05-12：决定把 CodingAgent skill commands 做成独立 `CodingAgentSkillStore` baseline，而不是等待完整 extension/resource loader。原因是上游 `/skill:<name>` 本质是本地 `SKILL.md` 发现和 prompt expansion，Tau 可以先对齐默认/显式 skill 目录、frontmatter、`disable-model-invocation`、`/skills` 列表、`/skill:<name>` block expansion 和 system prompt inventory；extension command registry、resource selector 与 diagnostics 后续继续补。
- 2026-05-12：决定把 CodingAgent extension commands 先做成 Tau-native JSON 声明式 command/resource baseline，而不是直接嵌入上游 TypeScript/Jiti extension runtime。原因是上游 extension runtime 能执行任意代码并覆盖 commands/tools/events/UI/resource discovery；Tau 当前先交付可审计、可本地验证的 command surface 和 prompt/skill resource path 贡献，按上游顺序让 extension command 先于 skill/prompt 执行，并保留重复命令 `name:1/name:2` 解析规则；完整 TS runtime、custom tools/events、theme loader、resource selector 和 diagnostics 后续单独收口。
