# CodingAgent JSONL session tree baseline

## 用户诉求

继续从 `pi-mono-main` 完整移植剩余功能，并把推进重点从低价值单元测试切回真实功能迁移和 smoke/build 验证。

## 本次改动

- 新增 `CodingAgentTreeSessionStore` / `CodingAgentTreeSessionController`，为 `Tau.CodingAgent` 增加上游风格 JSONL session tree 基线。
- JSONL session 默认写入 `./.tau/coding-agent-session.jsonl`，可通过 `TAU_CODING_AGENT_TREE_SESSION_FILE` 覆盖；如果 `TAU_CODING_AGENT_SESSION_FILE` 指向 `.jsonl`，则作为 tree session 路径使用并关闭 flat snapshot 写入。
- 保留现有 `CodingAgentSessionStore` flat JSON 兼容入口；非 `.jsonl` 的 `/export` / `/import` 行为保持原有 Tau snapshot JSON 语义。
- JSONL 文件写入 `type=session` header，并 append-only 写入 `message`、`model_change`、`session_info`、`label` entries；每个 entry 带 `id`、`parentId`、`timestamp`。
- `CodingAgentHost` 通过 runner message count diff 把新 runtime messages 追加到当前 branch，避免每次重写历史。
- 新增 `CodingAgentHtmlSessionExporter`，为 `/export` 默认路径和 `/export <path.html|path.htm>` 增加 standalone HTML transcript 导出。
- HTML 导出会优先同步并导出当前 JSONL branch；没有 tree controller 时导出当前 runner messages。导出内容覆盖 session header、provider/model、消息统计、user/assistant/tool result、thinking、tool call、图片内容和基础错误信息。
- HTML 内嵌当前 branch JSONL，并提供 `Download JSONL` 按钮；tree session 会嵌入真实 branch JSONL，flat-only 场景会生成可 resume 的最小 JSONL。
- HTML 新增 `Branch Outline` 侧栏，从内嵌 JSONL 构建当前 branch 的 entry 列表，点击 message entry 可以滚动到对应 transcript 消息。
- 手动 `/compact` 现在会在压缩前同步当前 runner messages，压缩后向 JSONL tree 追加 `type=compaction` entry，记录 `summary`、`firstKeptEntryId`、估算 `tokensBefore` 和 `fromHook` baseline；恢复 branch 时由 compaction entry 重建 summary message，后续消息继续追加到 compaction 之后。
- 新增 `CodingAgentCompactionMessages`，让 runtime compaction summary message 和 tree restore 使用同一段 prefix/suffix/prompt 文本，避免 compaction 摘要格式分叉。
- 新增 `CodingAgentAutoCompactionOptions` / `CodingAgentTokenEstimator`；当 `TAU_CODING_AGENT_AUTO_COMPACT_TOKENS` 设置为正整数时，host 会在普通消息进入 runner 前估算当前 session + 待发送输入，超过阈值就先调用 compaction，并把 JSONL compaction entry 标记为 `fromHook=true`。`TAU_CODING_AGENT_AUTO_COMPACT_INSTRUCTIONS` 可补充自动摘要指令。
- 新增 `CodingAgentPromptTemplateStore`，加载用户 `~/.tau/prompts`、项目 `./.tau/prompts` 和 `TAU_CODING_AGENT_PROMPT_PATHS` 指定的 `.md` prompt template；支持 frontmatter `description` / `argument-hint`，以及 `$1`、`$@`、`$ARGUMENTS`、`${@:N}`、`${@:N:L}` 参数替换。
- `CodingAgentHost` 会让内置 slash command 继续优先；非内置 slash 输入如果命中 prompt template，会先展开再发送给 runner。`/prompts` 用于列出当前可用模板。
- 新增 `CodingAgentSkillStore`，加载用户 `~/.tau/skills`、项目 `./.tau/skills` 和 `TAU_CODING_AGENT_SKILL_PATHS` 指定的 `SKILL.md`；支持 frontmatter `name`、`description` 和 `disable-model-invocation`。
- `CodingAgentHost` 会在内置 slash command 之后优先展开 `/skill:<name> args`，把 skill body 包装成上游风格 `<skill name="..." location="...">` block 并附加用户参数后发送给 runner；`/skills` 用于列出当前可用技能命令。
- `RuntimeCodingAgentRunner.Create(...)` 会把可见 skill 注入默认 system prompt 的 `<available_skills>` inventory；`disable-model-invocation: true` 的 skill 不进入 system prompt，但仍可显式 `/skill:<name>` 调用。
- 新增 `CodingAgentExtensionCommandStore`，加载用户 `~/.tau/extensions`、项目 `./.tau/extensions` 和 `TAU_CODING_AGENT_EXTENSION_PATHS` 指定的 `.json` extension command definitions；支持单命令 JSON 或 `commands[]`。
- JSON extension command 支持 `name`、`description`、`argumentHint` / `argument-hint`、`response`、`prompt`、`sendToRunner` / `send-to-runner`；`response/prompt` 复用 prompt template 参数替换语义，重复 command name 按上游规则解析为 `name:1`、`name:2`。
- `CodingAgentHost` 现在按上游顺序处理非内置 slash input：extension command 先于 skill/prompt；status-only command 直接输出 `status>`，`sendToRunner=true` command 展开后进入 runner。
- `/extensions` 会列出当前已发现的 extension commands，并标明 scope 和 runner mode。
- 扩展 slash commands：
  - `/session` 同时报告估算 token、模型 context window、auto-compaction threshold budget、tree file、leaf、entries、branch messages。
  - `/tree [max entries]` 输出当前 JSONL tree 的 entry 摘要、label 和当前 branch 标记。
  - `/label <entry-id> [label | clear]` 查看、设置或清空 entry label。
  - `/fork <entry-id>` 从历史 entry 切出新 branch 并恢复该 branch messages。
  - `/resume [latest | path.jsonl]` 恢复 JSONL session。
  - `/export <path.jsonl>` 导出当前 branch 为独立 JSONL session。
  - `/export` 默认导出当前会话/branch 为 standalone HTML transcript。
  - `/export <path.html|path.htm>` 显式导出当前会话/branch 为 standalone HTML transcript。
  - `/import <path.jsonl>` 作为 JSONL resume 入口。
  - `/skills` 列出当前已发现的 `/skill:<name>` 命令。
  - `/extensions` 列出当前已发现的 JSON extension commands。
- 更新 `README.md`、`docs/ARCHITECTURE.md`、`docs/QUALITY_SCORE.md`、完整移植 active plan 和 `next.md`，明确已完成的是 JSONL tree baseline、label/stat 基线、`/session` token/context budget 基线、HTML transcript baseline、手动 compaction entry metadata baseline、opt-in auto-compaction threshold baseline、prompt template baseline、skill command baseline 和 JSON extension command baseline；interactive tree navigator、share/Gist export、richer HTML template、完整 TypeScript extension runtime、custom tools/events、resource selector/diagnostics 仍未完成。

## 设计取舍

- 先做可运行的 tree baseline，而不是一次性迁入上游 session-manager 的所有细节。原因是 `resume/fork/tree/.jsonl export-import` 是当前最影响用户可见 parity 的核心缺口。
- 保留 flat JSON session store。原因是当前 WebUi/Mom 和已有 CLI tests 已依赖 Tau snapshot 语义，直接替换会扩大风险，也会让迁移链难以评审。
- tree 同步采用 host 侧 message diff。原因是当前 runner 还没有细粒度 session event hook；先按消息数量追加能快速形成可用 branch/resume 基线，后续可再提升到 runtime event-level append。
- HTML export 先做 Tau-native standalone transcript，而不是直接搬上游带 theme、template JS、share/Gist 和 custom tool renderer 的完整导出系统。原因是当前 Tau 还没有上游 TUI renderer/extension renderer 生态；先交付可打开、可审查、可 smoke 的 transcript，并把无参 `/export` 对齐为 HTML 默认导出。HTML branch outline 和内嵌 JSONL 是本地 share baseline，不等同于上游 Gist/share 发布能力。
- 手动 compaction 先写 JSONL `compaction` entry，而不是继续把压缩后的 summary 当普通 message 追加。原因是上游 session-manager 把 compaction 作为审计边界和后续 auto-compaction 输入；Tau 先保留 metadata 和恢复语义，token threshold、recent-message retention 和 retry/rollback 后续接在同一 entry 类型上。
- auto-compaction 先做成 opt-in threshold，而不是默认开启。原因是当前摘要仍需要真实 provider 调用，默认开启会让无凭证本地 CLI 在普通消息前先失败；显式环境变量能让本地验证和真实使用都可控，同时 `fromHook=true` 保留了和手动 `/compact` 不同的审计事实。
- token budget UI 先复用 `/session`，而不是新增实时状态栏。原因是当前 Tau TUI 仍是最小交互层，`/session` 已经是 flat/tree 统计事实源；用同一个 `CodingAgentTokenEstimator` 输出当前估算 token、模型 context window 和 auto threshold remaining，可以避免 compaction 触发判断和用户可见预算两套算法分叉。
- prompt template 先做文件型 baseline，而不是直接实现完整 extension runtime。原因是上游 prompt template 是动态 slash command 的本地可验证子集；先让 `.md` 模板发现、frontmatter、参数替换和 runner 调用路径闭合，skill command 与 extension command registry 后续继续接在同一命令发现边界上。
- skill command 先做 `SKILL.md` 发现和 `/skill:<name>` 展开 baseline，而不是等待完整 extension/resource loader。原因是上游 skill command 的核心用户可见行为可以本地验证；Tau 先对齐 skill 目录、frontmatter、system prompt inventory 和显式命令调用，extension command registry、resource selector 和 diagnostics 继续后置。
- extension command 先做 Tau-native JSON 声明式 baseline，而不是引入上游 TypeScript/Jiti extension runtime。原因是上游 extension 能执行任意代码并注册 tools/events/UI/resource discovery；Tau 当前先交付可审计、可本地验证的 command surface，并保持上游“extension command 先于 skill/template”的用户可见顺序。完整 TS runtime、custom tools/events、resource selector 和 diagnostics 后续单独收口。

## 验证

- `dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal`
- `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal`
- 临时 `TAU_CODING_AGENT_TREE_SESSION_FILE` / `TAU_CODING_AGENT_SESSION_FILE` 下执行真实 CLI smoke：`/session`、`/tree`、`/quit`
- 临时 session 下执行真实 CLI smoke：`/export` 和 `/export <session.html>`，检查 HTML 文件存在且包含 session name / Tau header / empty-state / Branch Outline / Download JSONL。
- 临时 flat snapshot 下执行真实 CLI smoke：`/import <snapshot.json>`、`/export <session.html>`，检查 HTML 包含 user text、assistant text、thinking、tool call、tool result 和 HTML escaping。
- 临时 JSONL session 下执行真实 CLI smoke：手写 `message -> compaction -> message` branch，执行 `/resume <session.jsonl>`、`/session`、`/tree`、`/export <session.html>`，检查 `/tree` 输出 `compaction 12 tokens summary smoke` 且 HTML 包含 compaction summary 和后续消息。
- `dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal`：0 warnings / 0 errors。
- `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal`：79/79 passed，覆盖 JSONL `compaction` entry 写入、tokens metadata、summary restore、compaction 后继续追加消息、auto-compaction threshold / `fromHook=true` / env option parsing / token estimate、`/session` token/context budget、prompt template 加载/参数替换、skill store 加载/system prompt inventory/命令展开、extension command 加载/重复 invocation/status-only/runner expansion、`/prompts`、`/skills`、`/extensions` 和 host 展开后调用 runner。
- `powershell -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore -RunSmoke`：src/test build 全部通过；`Tau.Ai.Tests` 79/79、`Tau.Agent.Tests` 54/54、`Tau.Tui.Tests` 4/4、`Tau.CodingAgent.Tests` 79/79、`Tau.Pods.Tests` 7/7；WebUi smoke 和 Mom `--once` smoke 通过。
- `dotnet build Tau.slnx --verbosity minimal`：0 warnings / 0 errors。
- `git diff --check`：退出码 0；仅有 CRLF normalization warnings，无 whitespace error。
- 临时 `TAU_CODING_AGENT_SKILL_PATHS` 下执行真实 CLI smoke：`/skills`、`/quit`，确认真实进程能列出 `/skill:reviewer - Review smoke skill`。
- 临时 `TAU_CODING_AGENT_EXTENSION_PATHS` 下执行真实 CLI smoke：`/extensions`、`/hello Tau`、`/quit`，确认真实进程能列出 `/hello <name> - Say hello from smoke (path)` 并输出 `Hello Tau`。

## 后续

- 补 interactive tree navigator 和更完整 session metadata。
- 补 auto-compaction retained-message cut-point、retry/rollback。
- 补完整 TypeScript extension runtime、custom tools/events、resource selector 和 diagnostics。
- 补 share/Gist export parity、richer HTML template 和上游 custom tool renderer parity。
