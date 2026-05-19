# Tau

Tau 是 [pi-mono](https://github.com/badlogic/pi-mono) 的 .NET 10 移植仓库，目标是在 C# / .NET 生态中重建其核心 AI Agent 能力，而不是简单包一层兼容壳。

当前仓库已经从单纯 **CLI-first 收口** 进入 **多应用面最小产品切片阶段**：

- `Tau.Ai` / `Tau.Agent` / `Tau.CodingAgent` / `Tau.Tui` 已有可运行核心基线
- `Tau.WebUi` 已从 Hello World 推进到 **可持久化 session + provider/model 选择 + 流式/附件/会话管理** 的 Web 宿主
- `Tau.Mom` 已从纯文本 worker 推进到 **支持结构化委派请求 + 本地 events + Slack-compatible message envelope + transport/responder seam + Slack event mapper + Slack Socket Mode transport seam + Slack Web API responder seam + Slack startup backfill + Slack private file download + per-channel queue dispatcher + true cancellable stop + Mom sandbox/tool delegation seam + 附件 staging + workspace layout bootstrap + prompt debug snapshot** 的本地委派宿主
- `Tau.Pods` 已从静态 config CLI 推进到 **支持 probe / exec / health / deploy / stop / restart** 的运维 CLI

## 当前状态

- 已有：
  - `Tau.Ai`：消息抽象、流事件、EventStream、provider 注册、model catalog、OAuth / env auth 解析、`auth.json` / `models.json` 本地配置和 secret 状态边界
  - `Tau.Agent`：双层循环 runtime、工具执行、状态与事件骨架
  - `Tau.CodingAgent`：最小可运行 CLI、基础文件/命令工具、和 `ModelCatalog` 对齐的默认 provider / model 接线、显式 `Create(provider, model, history)` runner 工厂、JSONL session tree baseline（`/session` token/context budget、`/session` retry policy display、`/tree` filter/search modes、`/tree --interactive` navigator + overlay search/filter cycle/Space fold/selected metadata、`cwd` / `parentSession` metadata、settings `treeFilterMode` / retry fields / label timestamp、`/label`、`/fork`、`/clone`、`/resume`、`.jsonl` export/import、手动/自动 `compaction` entry metadata 与 recent-message retention baseline、`auto_retry_start` / `auto_retry_end` audit entries）、普通回合失败/取消 rollback baseline、host-level retryable error auto-retry baseline、`/retry` settings command baseline、context overflow compact-and-retry baseline、prompt template discovery/expansion、skill command discovery/expansion、JSON 声明式 extension command / prompt/skill resource discovery / load diagnostics baseline、带 cwd/parent metadata、branch JSONL timeline、branch outline filter/search、label/model/compaction/retry entries、内嵌 JSONL 下载、message deep-link/copy-link、code fence rendering baseline、inline code span rendering baseline、plaintext link rendering baseline、Markdown heading/list/blockquote block rendering baseline、Markdown strong/emphasis span rendering baseline、Markdown pipe table rendering baseline、Markdown task list rendering baseline、image metadata caption baseline、long tool result folding baseline、tool call JSON argument rendering baseline 和 long tool call arguments folding baseline 的 `.html/.htm` transcript export，以及基于 GitHub CLI 的 `/share` secret Gist baseline
  - `Tau.WebUi`：最小聊天页、status/catalog/session/messages/auth API、本地 session 持久化、provider/model 选择、NDJSON 流式消息、附件、tool timeline、session delete/export/import/rename/restore baseline、runtime-coding-agent 接线
  - `Tau.Mom`：本地文件委派处理链，支持 `.txt/.md/.json` 请求、`provider/model/workingDirectory/title/metadata/attachments` 结构化字段、Slack-compatible channel message envelope、transport/responder/processor seam、Slack event mapper、Slack Socket Mode transport seam、Slack Web API responder seam、Slack startup backfill、Slack private file download、per-channel queue dispatcher、true cancellable stop、Mom sandbox/tool delegation seam、本地附件 staging、本地 `events/*.json` 唤醒、workspace layout bootstrap、`last_prompt.jsonl` prompt debug snapshot、`--once`、outbox 结果落盘和 archive 归档
  - `Tau.Pods`：`init / list / validate / status / probe / exec / health / deploy / stop / restart` 命令、sample config、validator、主动 endpoint/tcp 探测、SSH 远程命令执行、SSH lifecycle metadata 管理、AOT 友好的 JSON source-gen
- 未完成：
  - 完整 TUI 交互层
  - 完整上游 TreeSelector、多选 / fold 持久化 / metadata inspector、上游 LLM split-turn summarization / compaction extension events / cancellation UI parity、上游 auto-retry RPC / settings UI parity、完整 retry cancellation UI、完整 TS extension runtime/custom tools/events/theme/resource selector、完整 Markdown/highlight renderer、richer HTML template / share viewer parity、模式/配置系统
  - Web UI 的 browser 级行为测试和更完整 CodingAgent session/tree 语义对齐
  - Mom 的真实 Slack smoke、Docker sandbox smoke 和更高层 workspace/session 委派
  - Pods 的更完整模型生命周期、远端 transport hardening 和真实运维 smoke
  - 更高层行为回归测试

更细的状态请看：

- `docs/ARCHITECTURE.md`
- `docs/product-specs/tau-port-overview.md`
- `docs/QUALITY_SCORE.md`
- `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
- `docs/exec-plans/active/2026-04-23-tau-port-baseline.md`

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
- `POST /api/sessions/import`
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
- `IMomChannelTransport` / `IMomChannelResponder` / `MomChannelMessageProcessor` 已固定未来 Slack adapter 的输入输出边界：transport 读入 channel message，responder 负责响应/thread/typing/upload，processor 负责 busy-state、true cancellable stop、attachment staging、status/log writeback 与 runner 调用；`SlackEventMapper` 已固定 Socket Mode event JSON 到 channel envelope 的过滤/字段规则，`SlackSocketModeTransport` 已固定 open/ack/read/reconnect 接缝并可由 `SlackSocketModeEnabled` 启用，`SlackBackfillService` 会在 Socket Mode worker 启动前对已有 `log.jsonl` 的 channel 调 `conversations.history`，按 oldest/cursor 回填旧消息但不触发 delegation，`SlackWebApiResponder` 已用 `HttpClient` 固定 `chat.postMessage` 和 `files.uploadV2` 调用契约，`SlackAttachmentDownloader` 已用 bot token 下载 Slack private file URL 并写入 attachment manifest，`MomChannelQueueDispatcher` 已固定 per-channel 顺序处理、pending queue limit 和 stop bypass；`MomChannelRunRegistry` 会让 stop 命令取消当前 in-process runner token，并在完成后写 `cancelled` 状态
- runner 执行前会确保本地 workspace/channel layout 存在：`scratch/`、workspace-level `skills/`、channel-level `skills/`、`attachments/` 和 `events/`
- runner 输入会先注入 `<mom_runtime_context>`，说明本地 workspace/channel layout、`SYSTEM.md`、`scratch/`、skill docs、events 文件格式、attachment manifest、memory/log/status 路径和 `[SILENT]` 事件响应约定；这只是本地 Mom 语义，不等于 Slack adapter 已接通。skill docs 按上游 Agent Skills 格式暴露为 `<available_skills>`，脚本仍通过 `bash/read/write/edit` 使用，不会额外注册成直接 tool 名
- `MomOptions.Sandbox` 默认 `host`，可配置为 `docker:<container>`；当前已固定 host executor、docker command/path translation seam、workspace path authority 和 `bash/read/write/edit/attach` 五个上游同名 Mom tools，真实 Docker container smoke 仍后置
- `RuntimeDelegationAgentRunner` 默认会为 Mom 创建专用工具集，而不是继续暴露通用 CodingAgent 的 `shell/read_file/write_file/edit_file` 名称；`attach` 会把 workspace 内文件回传到 `DelegationExecution.Attachments`，Slack channel processor 会在响应后调用 responder upload
- 每次调用 runner 前会写 `workingDirectory/last_prompt.jsonl`，记录 mom runtime context、delegation context、实际 runner input、恢复的 session messages、当前 user prompt、attachment count 和 image attachment count，便于对照上游 mom 的 prompt/debug 行为排查后续 Slack/session 问题
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
- sample `tau.pods.json`

当前 `Pods` 行为：

- `probe`
  - 对 `endpoint` 做 HTTP GET 健康探测
  - 对 `sshHost/sshPort` 做 TCP 连通性探测
- `exec`
  - 对 SSH pod 通过系统 `ssh` 客户端执行远程命令
  - 对 endpoint pod 明确返回 unsupported
- `health`
  - 对 enabled pods 执行 HTTP `/health` 或 SSH `echo ok`
- `deploy / stop / restart`
  - 对 SSH pod 写入/删除 `~/.tau_pods/<deployment>.json` lifecycle metadata
  - 对 endpoint pod 明确返回 unsupported
  - `<path>` 可省略；`deploy gpu-1 model-id` 使用默认 `tau.pods.json`，`deploy custom.json gpu-1 model-id` 使用显式配置文件

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

当前机器上的现场现实：

- 仓库规范和 workflow 仍然统一走 bash
- 但本机 bash 服务可能报 `Bash/Service/CreateInstance/E_ACCESSDENIED`，或者落到 WSL 后缺少 `/bin/bash`
- 因此 Windows 本机优先使用 `scripts/verify-dotnet.ps1`
- 如果两类脚本入口都不可用，再退回等价顺序的 `dotnet build/test/run` 命令

当前已确认的运行 / 验证现实是：

- `Tau.CodingAgent.csproj` 已可独立 `dotnet build --no-restore`
- `Tau.CodingAgent` 已有 flat JSON session 和 JSONL tree session 双路径；`/session` 会显示估算 token、模型 context window、auto-compaction threshold budget、当前 retry policy、JSONL `cwd` 和 clone/export 产生的 `parentSession`；`/tree [max entries] [default|no-tools|user-only|labeled-only|all] [--label-time] [--search query]` 会读取 settings `treeFilterMode` 作为默认过滤模式，并在 header 中显示 `cwd` / `parent` metadata；`/tree --interactive` 支持移动、选择、取消、overlay search、filter cycle、n/N 搜索跳转、Space fold/expand 和 selected entry type/depth/branch/leaf metadata；settings JSON 还可保存 retry max attempts/base delay，生产入口按 settings 优先、env 兜底读取 retry 策略；manual / auto compaction 会写 JSONL `compaction` entry，默认从当前 compaction boundary 后优先按 `TAU_CODING_AGENT_COMPACT_KEEP_RECENT_TOKENS` 的 token budget 保留 recent messages，再回落到最近 4 条 message，可用 `TAU_CODING_AGENT_COMPACT_KEEP_RECENT_MESSAGES` 调整，branch restore / resume / clone export 会重建 summary + retained messages + post-compaction messages；当 retained cut point 落在一个 user turn 中间时，JSONL 会写 `isSplitTurn` / `turnPrefixSummary`，恢复 runtime 时把 split-turn prefix context 拼入 summary message，HTML timeline 也会展示并可搜索；context overflow 错误会恢复失败回合前 snapshot、自动 compact、记录 `fromHook=true` compaction boundary，然后重试同一输入；普通 runner exception、取消或 provider error 型 `AgentEndEvent` 会恢复回合前 snapshot，避免失败输入污染 flat JSON / JSONL tree session；`/retry [current|default|off|<max attempts> [base delay ms]]` 会查看或修改同进程 retry 策略并写入 settings；`TAU_CODING_AGENT_AUTO_RETRY_ATTEMPTS` / `TAU_CODING_AGENT_AUTO_RETRY_BASE_DELAY_MS` 继续作为 settings 未配置时的 env fallback；retry 前同样恢复回合前 snapshot，并向 JSONL tree 写入 `auto_retry_start` / `auto_retry_end` audit entries；成功时先持久化成功 attempt 的 user/assistant messages，再写 retry end，失败或耗尽时只保留 retry audit 而不持久化失败输入；retry delay 被取消时会显示明确状态、写 `Retry cancelled` end audit 并保持失败输入不落盘；HTML transcript export 会显示 tree cwd / parent metadata，按 current branch JSONL 渲染 message、`session_info`、`model_change`、`label`、`compaction` 和 retry timeline entries，branch outline 支持 `default/no-tools/user-only/labeled-only/all` 过滤和搜索，给消息节点写入 JSONL entry id、提供 per-message copy link，并支持 `targetId` deep link 定位；文本内容中的 fenced code block 会渲染为独立 `code-block` / `<code>` 区块并继续安全 HTML escape，普通文本里的 backtick inline code span 会渲染为 `<code class="inline-code">`，普通文本段会把 Markdown-style `[label](http/https...)` 与裸 `http(s)` URL 渲染成外链，code fence 和 inline code 内不做链接化，非 http(s) scheme 仍按文本安全输出；普通文本段检测到 heading/list/blockquote block marker 时会改用轻量 rich-text block rendering，渲染 `h1`-`h6`、`ul/ol/li` 和 `blockquote`，并继续复用 inline code/link 安全输出；普通文本里的 `**strong**` / `__strong__` 和 `*emphasis*` / `_emphasis_` 会分别渲染为 `<strong>` / `<em>`，inline code 和 code fence 内不会触发 emphasis；普通文本段中的 Markdown pipe table（header + separator + data rows）会渲染为可横向滚动的 `<table>`，单元格继续复用 inline code/link/strong/emphasis 安全输出，code fence 内的表格文本保持代码块；普通文本列表项里的 `[ ]` / `[x]` task marker 会渲染为 disabled checkbox，任务文本继续复用 inline code/link/strong/emphasis 安全输出，code fence 内任务列表文本保持代码块；图片内容会继续以内嵌 data URI 渲染，并在 caption 显示 mime type 和估算字节数；长 tool result 文本会默认折叠到 `<details class="tool-result-fold">` 但保留完整内容；tool call arguments 中可解析 JSON 会格式化为 `code-block` / `<code data-language="json">`，不可解析参数仍按原始 `<pre>` 安全转义，超长 arguments 会默认折叠到 `<details class="tool-call-arguments-fold">` 并保留全文；`/share` 会把当前 HTML transcript 交给 GitHub CLI 创建 secret gist，预览 URL 可用 `TAU_SHARE_VIEWER_URL` 覆盖；`/extensions` 会显示 JSON extension command、extension JSON 文件、prompt/skill resource paths、重复命令解析名和坏 JSON / 缺失路径等 load diagnostics；`/label`、`/fork`、`/clone`、`/resume`、`/compact`、`/retry`、自动 compaction threshold、context overflow compact-and-retry、失败回合 rollback、host-level auto-retry、`/prompts`、`/skills`、`/skill:<name>`、`/extensions`、JSON extension command、`/export [path]`、`/share` 可通过 targeted test 或真实 CLI smoke 运行
- `Tau.WebUi.csproj` 已可独立 `dotnet build --no-restore`
- `Tau.Mom.csproj` 已可独立 `dotnet build --no-restore`
- `Tau.Pods.csproj` 已可独立 `dotnet build --no-restore`
- `Tau.slnx` 已可通过 `dotnet build Tau.slnx --verbosity minimal`
- `Tau.CodingAgent.Tests` 已通过 172 个测试
- `Tau.Agent.Tests` 已通过 54 个 Mom/Slack channel runtime 测试
- `Tau.Pods.Tests` 已通过 32 个测试
- `Tau.Tui.Tests` 已通过 56 个测试
- `Tau.Ai.Tests` 已通过 191 个测试
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
