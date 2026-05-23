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
- [~] secret 持久化边界和脱敏规则（已补默认 `./.tau/auth.json` / `./.tau/models.json` / JSONL session 本地状态忽略、`auth.json` Unix 0600 写入、OAuth metadata 保留字段过滤、auth status 不回显密钥、models.json credential header 状态识别、command-backed `apiKey` 状态检查不执行 `!command` 的回归、HTML transcript 导出（`/export` / `/share`）默认对常见 AWS / GitHub / Slack / Anthropic / OpenAI / Bearer / JWT secret 模式做 `[redacted]` 替换（`TAU_CODING_AGENT_REDACT_SECRETS=0` 可关闭），以及 `TauSecretRedactor` 提升到 `Tau.Ai` 并被 `Tau.WebUi GET /api/sessions/{id}/export.html` 使用（`TAU_WEBUI_REDACT_SECRETS=0` 可关闭）；仍需继续梳理 JSONL 流式 export / runtime 日志输出脱敏边界）
- [x] provider-specific headers 支持（models.json 已能合并静态 provider/model headers，并在 StreamFunctions 层解析 provider/model request headers）
- [x] Bedrock AWS SSO / AssumeRole / credential_process / IMDS / ECS / web identity credential chain（已覆盖全部六个源 + AssumeRole 的 source_profile 与 credential_source 两种触发方式（Environment / EcsContainer / Ec2InstanceMetadata）；SigV4 签名器泛化到任意 service，STS XML 解析共享，token cache 路径 / portal endpoint / sts endpoint 均可在 BedrockOptions 中显式覆盖；剩余增量：SSO token 自动刷新和真实云端 e2e）
- [x] 自定义 provider / custom model 配置入口（`TAU_MODELS_FILE`、`./.tau/models.json`、`~/.tau/models.json`，支持 Tau 已注册 API 的 `providers/baseUrl/api/apiKey/authHeader/headers/compat/models/modelOverrides` 子集）
- [x] models.json 的 `apiKey/authHeader`、shell/env value resolution、运行时 request auth 合并

### Tau.CodingAgent

- [x] flat session 持久化（`TAU_CODING_AGENT_SESSION_FILE` 或 `./.tau/coding-agent-session.json`，启动自动 rehydrate，回合后保存）
- [x] JSONL session tree baseline（默认 `./.tau/coding-agent-session.jsonl`，`TAU_CODING_AGENT_TREE_SESSION_FILE` 或 `.jsonl` 形式的 `TAU_CODING_AGENT_SESSION_FILE` 可覆盖；已支持 header、append-only message/model/session-info/label/branch_summary entries、entry id/parentId/timestamp、runner diff 同步、current branch restore）
- [~] session lifecycle（已补 `/new`、`/session` tree stats、估算 token/context usage、auto-compaction threshold budget、retry policy display、JSONL `cwd/parentSession` metadata、`/name`、`/tree` 过滤/搜索模式与 label timestamp、`/tree --interactive` navigator、`/label`、`/fork`、`/fork <entry-id> --summarize [instructions]`、`/clone`、`/resume`、`.jsonl` export/import；`/fork --summarize` 会写 JSONL `branch_summary`，恢复 branch 时作为 context message 注入 runtime；仍缺更完整 session metadata inspector、自动 branch switching summarization hooks 和上游 TreeSelector parity）
- [x] settings / model selection / provider selection / thinking level / scoped models / theme setting（`/settings`、`/theme`、`/model`、`/provider`、`/models`、`/providers`、`/thinking`、`/scoped-models`，默认写入 `TAU_CODING_AGENT_SETTINGS_FILE` 或 `./.tau/coding-agent-settings.json`；`/settings [current|path|select]` 可查看 settings 路径/摘要，并在真实交互式 editor 会话中打开 TUI settings selector baseline；selector 当前可切换 auto-compaction、steering/follow-up mode、tree filter、thinking level、scoped models 和 theme；`/thinking [current|select|cycle|off|minimal|low|medium|high|xhigh]` 可查看、用 TUI selector 选择、循环、设置或关闭 reasoning level，并写回 settings `defaultThinkingLevel`；`/theme [current|list|select|set|clear] [name]` 可查看、列出、用 TUI selector 选择、校验保存或清空当前主题；`/scoped-models [current|select|set|add|remove|clear|all] [provider/model ...]` 可查看、用 TUI multi-select 选择、命令式维护或清空模型 scope；`/model select [search]`、交互式裸 `/model` 或 Ctrl+L 会按当前 `enabledModels` scope 或全部模型打开 TUI 单选模型 selector，选择后保存默认 provider/model；空闲输入 prompt 的 Ctrl+P / Ctrl+Shift+P 会按 settings `enabledModels` scope 或全部可用模型切换下一个/上一个模型，并保存默认模型；同一 settings 文件支持上游兼容 `treeFilterMode` 作为 `/tree` 默认过滤模式，支持 retry attempts/base delay 字段供 `/retry` 和生产入口使用，支持 default thinking level、steering/follow-up queue mode 启动恢复，支持 `theme` 当前主题名，并支持 `enabledModels` 有序数组作为模型切换 scope；`autoCompactionEnabled` 保存 boolean 开关，旧 `queueMode` 读入时迁移为 `steeringMode`；`enabledModels` 缺失/null 表示 all enabled / no filter）
- [x] steering / follow-up CLI baseline（`ICodingAgentRunner.Steer/FollowUp` 暴露 `AgentRuntime` 现有队列；`CodingAgentHost` 在 active runner turn 期间消费可注入 `ICodingAgentTurnInputSource`；交互式 console 下 Enter 提交 steering、Alt+Enter 提交 follow-up；完整 TUI overlay、快捷键提示仍属后续）
- [x] RPC mode baseline（`--mode rpc` 走 LF-delimited JSONL stdin/stdout，`CodingAgentRpcHost` 覆盖 `prompt`、`steer`、`follow_up`、`abort`、`new_session`（含可选 `parentSession` 写入 JSONL header metadata）、`get_state`、`get_settings`、`update_settings`、`set_model`、`cycle_model`、`get_available_models`、`set_thinking_level`、`cycle_thinking_level`、`set_auto_retry`、`abort_retry`、`bash`、`abort_bash`、`set_steering_mode`、`set_follow_up_mode`、`set_auto_compaction`、`switch_session`、`get_fork_messages`、`compact`、`fork`、`clone`、`get_session_stats`、`get_messages`、`get_commands`、`export_html`、`get_last_assistant_text`、`set_session_name`；完整 extension UI sub-protocol、streamed bash output、上游 settings selector / theme selector / terminal / packages 等全量配置面和 richer command set 仍属后续）
- [~] auth 管理入口（已补 `/auth [current|select|provider]` provider auth 状态查看与交互式 TUI status selector baseline、`/login [select|provider]` OAuth provider selector baseline 和 `/logout [select|provider]` OAuth provider selector / 本地 `auth.json` credential 清理；`/auth select` 只检查 provider credential 状态，不写凭证、不执行 OAuth login；交互式裸 `/login` 或 `/login select` 会筛选当前注册且有 OAuth provider 的 provider，选择后调用现有 OAuth login flow 并保存到 `auth.json`；交互式裸 `/logout` 或 `/logout select` 会筛选当前有本地 OAuth credential 且注册了 OAuth provider 的 provider，选择后只删除对应 `auth.json` entry；`/logout` 不修改环境变量或 `models.json` credential 配置；完整上游 OAuth login dialog/session parity、credential refresh UX 和真实 OAuth e2e 仍在 Tau.Ai OAuth backlog）
- [x] slash command router 抽离（`CodingAgentCommandRouter`；当前命令行为不变，为 `/compact` / login flow 等后续命令留 seam）
- [x] local quit command（`/quit` 结束当前 CLI loop，不调用 runner，不进入 LLM conversation）
- [x] local help command（`/help` 列出当前 Tau 已支持命令，已纳入 `/prompts`、`/skills` 与 `/extensions`）
- [x] local hotkeys command（`/hotkeys` 列出当前交互式 editor 注入的 `IKeyBindingMap` 绑定；默认包含 Ctrl+P / Ctrl+Shift+P model cycle 和 Ctrl+L model selector；自定义 keybindings 和 `action: "None"` 禁用默认绑定会反映到输出；无 editor 的 print/RPC/redirected 模式返回未启用错误；完整上游 app/session/tree/extension shortcut registry 仍属后续）
- [x] local reload command（`/reload` 重读 settings 并同步 retry policy / default thinking level，重读 JSON extension resources 并刷新 extension-contributed prompt/skill/theme paths，重载 prompts、skills、context files、theme status 和交互式 editor keybindings；runner 使用生成 system prompt 时会刷新 skill/context inventory；完整 theme selector、TUI theme rendering、theme file watcher、TypeScript extension runtime 和 full resource selector 仍属后续）
- [x] local changelog command（`/changelog [count|all]` 读取 Tau 本地 release notes 表，默认从当前目录向上查找 `docs/releases/feature-release-notes.md`，也可由 `TAU_CODING_AGENT_CHANGELOG_FILE` 覆盖；这是 Tau-native CLI baseline，启动 changelog 渲染、`collapseChangelog` 设置和 install/update telemetry 仍属后续）
- [x] local settings command（`/settings [current|path|select]` 展示 settings 文件路径/摘要，或在真实交互式 editor 会话中打开 TUI settings selector baseline；selector 可写回 auto-compaction、steering/follow-up mode、tree filter、thinking level、scoped models 和 theme；完整上游 SettingsList/submenu、images/terminal/transport/packages 等全量配置面仍属后续）
- [x] local thinking command selector（`/thinking [current|select|cycle|off|minimal|low|medium|high|xhigh]` 保留查询/显式设置/循环语义，并在真实交互式 editor 会话中通过 `CodingAgentThinkingSelector` 打开上游同序 `off/minimal/low/medium/high/xhigh` 列表；选择后立即同步 runner `ThinkingLevel` 和 settings `defaultThinkingLevel`；模型能力 clamp 已接入：非 reasoning 模型只允许 off，不支持 xhigh 的 reasoning 模型把 xhigh 降到 high；完整上游 settings 子菜单 parity 仍属后续）
- [x] local theme command（`/theme [current|list|select|set|clear] [name]` 复用 theme loader/status，列出 built-in、用户、项目、env/CLI 和 extension-contributed themes，`select` 通过 `TuiSelectorSession` 交互式选择并写入 settings `theme`，`set` 校验存在后写入 settings `theme`，`clear` 回到默认 `dark`；完整上游 theme selector parity、TUI theme rendering 和 theme file watcher 仍属后续）
- [x] local model selector / scoped models / model cycle auth filtering + footer/scope/detail/search chrome + per-entry thinking + 模型能力 clamp baseline（`/scoped-models [current|select|set|add|remove|clear|all] [provider/model[:thinking] ...]` 查看或维护 settings `enabledModels`；该配置入口仍基于全部注册模型，允许提前配置尚未登录的 provider；`enabledModels` 条目可用 `:off|minimal|low|medium|high|xhigh` 为单个 scoped model 指定 thinking override；真实交互式 editor 会话中裸 `/scoped-models` 或 `/scoped-models select` 会打开 TUI multi-select selector，支持 filter、toggle、provider toggle、enable all、clear、reorder、save/cancel，并保留既有 per-entry thinking suffix 但暂不提供逐项编辑；`clear` / `all` 回到 all enabled / no filter；`/model select [search]`、交互式裸 `/model` 或 Ctrl+L 会按当前 scope 或全部已配置凭证模型打开 TUI 单选 selector，有 scoped 候选时默认 scoped，可用 Tab 在 `scoped` / `all` 候选间切换，顶部显示 `Model Selector` / `Search:` 轻量 chrome，普通字符输入会更新过滤，Backspace 会回退过滤，列表下方显示 `Model Name: ...`，并在底部提示 `Only showing models with configured auth`，选择后保存默认 provider/model 并保留 draft；空闲输入 prompt 的 Ctrl+P / Ctrl+Shift+P 和 RPC `cycle_model` 会按当前 scope 中已配置凭证的模型或全部已配置凭证模型循环切换，遇到 per-entry thinking override 时同步 runner thinking level 和默认 thinking setting，并按目标模型能力 clamp；显式 `/model`、`/provider` 和 RPC `get_available_models` / `set_model` / `cycle_model` / `update_settings.settings.model` 同样拒绝未配置凭证模型；完整上游 theme/dynamic-border/terminal-host parity 和 per-entry thinking UI editor 仍属后续）
- [x] slash command catalog（`CodingAgentCommandCatalog` 统一当前本地命令 name/usage/description，`/help` 和 usage 错误共用）
- [x] local session name command（`/name [display name | clear]` 查看、设置或清空当前 session display name，并写入 session store）
- [x] local copy command（`/copy` 复制最后一条 assistant 文本到系统剪贴板，clipboard 写入通过 `ICodingAgentClipboard` 隔离）
- [x] local export command（`/export` 默认导出 standalone HTML transcript；`/export <path>` 对 `.html/.htm` 路径导出 HTML，对 `.jsonl` 路径导出当前 branch JSONL，其他路径导出 Tau 平面 session snapshot JSON；HTML 提供 branch outline、filter/search、current-branch JSONL timeline、branch_summary timeline/read files/modified files、文本 code fence rendering、inline code span rendering、plaintext link rendering、Markdown heading/list/blockquote block rendering、Markdown strong/emphasis span rendering、Markdown pipe table rendering、Markdown task list rendering、image metadata caption、长 tool result folding、tool call JSON argument rendering、长 tool call arguments folding 并内嵌可下载 JSONL）
- [x] local history command（`/history [count|all]` 在 editor 启用时列出最近输入；non-interactive 模式返回未启用错误；持久化结合 `FileInputHistoryStore`）
- [x] local clear command（`/clear` 通过 InteractiveConsoleSession.ClearScreen 发送 ANSI clear-screen + cursor-home 序列；session/runtime state 保持不变；router 在 non-interactive 模式返回未启用错误）
- [x] local find command（`/find <pattern>` 在 _runner.Messages 上 case-insensitive 搜索 TextContent 与 ToolCallContent.Arguments，输出 role + 序号 + 上下文截断的匹配行；缺 pattern 报 usage；无匹配返回友好提示）
- [x] local import command（`/import <path>` 严格导入 Tau snapshot JSON 或 resume JSONL session，并恢复 messages/provider/model/display name；仍缺上游 share/import richer metadata）
- [~] manual / auto compaction（已补 `/compact [instructions]`，当前使用当前模型生成摘要并把 flat session 压成 summary message；JSONL tree 会追加 `compaction` entry，记录 `summary`、`firstKeptEntryId`、估算 `tokensBefore`、`fromHook`、`isSplitTurn` 和 `turnPrefixSummary` baseline；manual / auto compaction 默认优先按 `TAU_CODING_AGENT_COMPACT_KEEP_RECENT_TOKENS` 的 token budget 保留 recent messages，再回落到最近 4 条 message，可用 `TAU_CODING_AGENT_COMPACT_KEEP_RECENT_MESSAGES` 调整 fallback，branch restore / resume / clone export 会重建 summary + retained messages + post-compaction messages；如果 retained cut point 落在一个 user turn 中间，恢复 runtime 时会把 split-turn prefix context 拼入 summary message，HTML transcript timeline 也会展示并可搜索该字段；已补 `TAU_CODING_AGENT_AUTO_COMPACT_TOKENS` / `TAU_CODING_AGENT_AUTO_COMPACT_INSTRUCTIONS`，普通消息执行前超过阈值会自动 compact 并写 `fromHook=true`，context overflow 会恢复回合前 snapshot、compact、记录 `fromHook=true` boundary 并重试同一输入；settings/RPC 已补 `autoCompactionEnabled` boolean control，但实际自动触发仍需要 threshold budget；`/session` 会显示当前估算 token、模型 context window 和 auto threshold 剩余量；已升级为上游结构化 summarization prompt（Goal/Progress/Decisions/Next Steps）和 iterative update summarization（前一次 compaction summary 作为 previous-summary 合并新信息）；仍缺上游 LLM-generated split-turn summarization 的独立 LLM 调用、compaction extension events 和 cancellation UI 语义）
- [~] retry / rollback（已补普通回合失败/取消 rollback baseline：runner exception、取消或错误型 `AgentEndEvent` 会恢复回合前 snapshot，并避免 flat JSON / JSONL tree 持久化失败输入；已补 host-level retryable error auto-retry baseline：settings 优先、`TAU_CODING_AGENT_AUTO_RETRY_ATTEMPTS` / `TAU_CODING_AGENT_AUTO_RETRY_BASE_DELAY_MS` env 兜底控制 429/5xx/rate limit/timeout/network 等 transient error 的有限重试，重试前恢复 snapshot；已补 `/retry [current|default|off|<max attempts> [base delay ms]]` settings command baseline，可持久化并同进程热更新；已补 RPC `set_auto_retry` / `abort_retry` baseline，headless client 可开关 retry 并取消 pending retry delay；已补 Tau-native JSONL `auto_retry_start` / `auto_retry_end` audit entries，成功时先持久化成功 attempt messages 再写 retry end，失败或耗尽时只保留 retry audit；已补 retry delay cancellation visibility baseline，取消 delay 时显示明确状态、写 `Retry cancelled` end audit，并保持失败输入不落盘；context overflow 已单独走 compact-and-retry baseline；仍缺完整 settings UI 控制和完整 retry cancellation UI）
- [~] JSONL tree navigator / richer session metadata（已补命令行 tree filter/search：`default/no-tools/user-only/labeled-only/all`、settings `treeFilterMode`、`--search query` 与 `--label-time`；已补 `/tree --interactive`（`-i`）j/k/↑/↓/g/G/Home/End/Enter/q/Esc 的交互式 navigator baseline，复用 `IConsoleKeyReader`，输出 `selected entry <id>`/`tree navigator cancelled` 状态；已补 overlay `/` search（incremental filter + Backspace + Esc clear）、`f` filter cycling（default→no-tools→user-only→labeled-only→all→default）、`n`/`N` next/prev match、普通 Left/Right 分页、Ctrl/Alt+Left 折叠或跳到上一个分支段、Ctrl/Alt+Right 展开或跳到下一个分支段、filter/search 切换时保持 selected entry id 稳定、Space 折叠/展开当前 entry descendants、selected entry type/depth/branch/leaf metadata，以及无匹配时 Enter 不踩空；仍缺多选、fold 持久化和完整 metadata inspector）
- [~] dynamic slash command registry / prompt registry / skills/extensions discovery（已补 prompt template discovery/expansion baseline：`~/.tau/prompts`、`./.tau/prompts`、`TAU_CODING_AGENT_PROMPT_PATHS`、`/prompts`、`$1/$@/$ARGUMENTS/${@:N[:L]}` 参数替换；已补 skill command discovery/expansion baseline：`~/.tau/skills`、`./.tau/skills`、`TAU_CODING_AGENT_SKILL_PATHS`、`/skills`、`/skill:<name>`、`disable-model-invocation` 和默认 system prompt inventory；已补 context files baseline：`~/.tau/AGENTS.md|CLAUDE.md` + parent-to-cwd `AGENTS.md|CLAUDE.md` + `--no-context-files` / `-nc` + `/reload` refresh；已补 theme loader/status + CLI/TUI selection baseline：built-in `dark/light`、`~/.tau/themes`、`./.tau/themes`、`TAU_CODING_AGENT_THEME_PATHS`、repeatable `--theme`、`--no-themes/-nt`、上游 required color tokens、`vars` 递归引用、`export.pageBg/cardBg/infoBg`、load diagnostics 和 `/theme` settings 持久化；已补 JSON extension command/resource/load diagnostics baseline：`~/.tau/extensions`、`./.tau/extensions`、`TAU_CODING_AGENT_EXTENSION_PATHS`、`/extensions`、`response/prompt` 参数替换、`sendToRunner`、重复命令 `name:1/name:2`、`promptPaths/skillPaths/themePaths` 资源贡献、extension 文件/resource 明细、坏 JSON 和缺失显式路径诊断；已补 `/reload` 对 settings / extension resources / prompts / skills / context files / theme status / keybindings 的当前进程重载 baseline；仍缺完整 TypeScript extension runtime、custom tools/events、完整 theme selector parity、TUI theme rendering、theme file watcher、interactive resource selector、full resource selector 和 richer runtime diagnostics）
- [x] standalone HTML transcript export（`/export` 默认 HTML，`/export <path.html|path.htm>` 显式 HTML，覆盖 text/thinking/tool call/tool result/image 内容，并提供 branch outline、filter/search、cwd/parent metadata、current-branch JSONL timeline、label/model/compaction/branch-summary events、本地 Download JSONL、branch summary read/modified file 列表、文本 fenced code block 的独立 code rendering baseline、inline code span rendering baseline、plaintext link rendering baseline、Markdown heading/list/blockquote block rendering baseline、Markdown strong/emphasis span rendering baseline、Markdown pipe table rendering baseline、Markdown task list rendering baseline、image metadata caption baseline、长 tool result 默认折叠 baseline、tool call JSON arguments 格式化 baseline 和长 tool call arguments 默认折叠 baseline）
- [~] share/Gist export parity 和上游 richer HTML template（已补 `/share` secret Gist baseline：复用 HTML transcript export、检查 `gh auth status`、执行 `gh gist create --public=false`，并支持 `TAU_SHARE_VIEWER_URL` 覆盖预览 URL；HTML transcript 已支持 message deep-link/copy-link、branch outline filter/search、label badge、model/label/compaction/branch-summary timeline entries 和 `targetId` 定位；仍缺真实 `gh` smoke、Tau 专属 share viewer 和完整上游 richer HTML template）
- [~] richer rendering（已补 HTML transcript 文本 fenced code block -> `code-block` / `<code>` baseline、普通文本 backtick inline code span -> `<code class="inline-code">` baseline、普通文本 Markdown-style `[label](http/https...)` 与裸 `http(s)` URL 外链 baseline、普通文本 angle-bracket autolink `<http(s)://...>` baseline、普通文本 heading/list/blockquote block rendering baseline、嵌套列表（缩进升级 `<ul>` / `<ol>` 栈）baseline、普通文本 horizontal rule (`---`/`***`/`___`) -> `<hr>` baseline、普通文本 strong/emphasis span rendering baseline、普通文本 strikethrough (`~~text~~`) -> `<del>` baseline、普通文本 Markdown pipe table -> 可横向滚动 `<table>` baseline、普通文本 task list -> disabled checkbox baseline、image mime type / byte count caption baseline、长 tool result -> `<details class="tool-result-fold">` 默认折叠 baseline、tool call JSON arguments -> `code-block` / `<code data-language="json">` 格式化 baseline，以及长 tool call arguments -> `<details class="tool-call-arguments-fold">` 默认折叠 baseline；仍缺完整 Markdown/highlight renderer、custom tool renderer 和上游 richer HTML template）
- [x] 显式 `Create(provider, model, history)` runner 工厂
- [x] 与 `ModelCatalog` 对齐的默认模型解析层继续收口
- [x] 把当前 `Tau.CodingAgent` / `Tau.WebUi` / `Tau.Mom` / `Tau.CodingAgent.Tests` / `Tau.Agent.Tests` 的 DLL `HintPath` workaround 收回到更正常的 `ProjectReference` 结构
- [x] 解决当前本机上 `Tau.slnx` / metaproj / workload resolver 的 build 异常（`dotnet build Tau.slnx --verbosity minimal` 已通过）

### Tau.Tui

- [~] 真正的输入编辑器（`InteractiveInputEditor` baseline 已落：key-by-key 读取、char append、backspace/delete、左/右光标、Home/End、Ctrl+Left/Right 词级跳转、Ctrl+Backspace 删除前一个词、Ctrl+Delete 删除下一个词、Ctrl-A/E 行首行尾跳转、Ctrl-K/U kill-to-end/start、Ctrl-R 反向 history 搜索（再按 R 切到更旧匹配，Esc/Ctrl-G 取消，Enter 提交）、Enter 提交、Ctrl-C 取消、`InputHistory` Up/Down 历史回放（去重 + capacity）；`Tau.CodingAgent` 在交互式 console（无 redirected stdin/stdout、未设 `TAU_CODING_AGENT_DISABLE_INPUT_EDITOR=1`）下默认使用 editor，非交互回退 `Console.ReadLine`；history 通过 `FileInputHistoryStore` 持久化到 `~/.tau/coding-agent-history`（可被 `TAU_CODING_AGENT_HISTORY_FILE` 覆盖），启动加载 / 提交追加 / 超 capacity 截断；通过 `IConsoleKeyReader`/`IInteractiveRenderer` seam 测试；仍缺多行/wrap 渲染）
- [~] 组件系统（已补 `ITuiComponent` / `ITuiInputComponent`、`TuiContainer`、`TuiBox`、`TuiTextBlock` 和 `TuiText` 宽度/截断/wrap helper；当前是库内基础组件树，不等于完整上游 focus stack、terminal lifecycle 或 theme rendering）
- [~] selector foundation / session host（已补 `TuiSelectList`：过滤、选中态、j/k/方向键/Home/End/PageUp/PageDown/Enter/Esc/Ctrl-C、描述列对齐、滚动提示和 footer hint 行；已补 `TuiMultiSelectList` / `TuiMultiSelectSession`：filter、toggle、provider toggle、enable all、clear、Alt+Up/Down reorder、save/cancel；已补 `ITuiRenderSurface`、`TuiOverlayHost` 和 `TuiSelectorSession`，可测试初始渲染、按键分发、diff apply、Enter 选择和 Esc/Ctrl-C 取消；已接入 `Tau.CodingAgent` `/theme select`、交互式 `/settings`、`/scoped-models`、`/model select`、`/auth select` status selector、`/login` OAuth provider selector、`/logout` OAuth provider selector 和 `/thinking select` thinking level selector baseline；仍未接入完整 OAuth login dialog/session 或 resource selector UI）
- [ ] 消息区 / 状态区
- [x] 键盘体系（`SystemConsoleKeyReader` + `InteractiveInputEditor` 已支持主要导航/编辑键 + 词级跳转/删除 + Ctrl-A/E/K/U readline 习惯 + Ctrl-R 反向搜索；自定义绑定层已落：`IKeyBindingMap` + `EditorAction` 枚举驱动 dispatch，`KeyBindingMap.Default` 保留既有行为并新增 Ctrl+P / Ctrl+Shift+P model cycle action 与 Ctrl+L `SelectModel` action，`KeyBindingFileStore` 从 `TAU_CODING_AGENT_KEYBINDINGS_FILE` 或 `~/.tau/coding-agent-keybindings.json` 加载 `{ key, modifiers, action }` JSON，`action: "None"` 用来 disable 默认绑定）
- [~] 更稳定的差分渲染层（已补 `TuiDiffRenderer` / `TuiRenderFrame` / `TuiRenderOperation` 纯函数式 diff 计划器：首帧/强制/宽高变化 full redraw，稳定尺寸下只返回 changed/cleared lines；`TuiOverlayHost` 已能把 diff 交给可注入 render surface；`TuiAnsiRenderSurface` 已能输出 synchronized ANSI full redraw 和 line diff；仍未实现 viewport/scrollback 管理、overlay compositing 和硬件 cursor）

## P1：后续应用面

### Tau.WebUi

- [x] 最小聊天 UI
- [x] session 持久化（`output/webui-sessions.json`）
- [x] provider/model 选择入口（`/api/catalog` + 会话设置）
- [x] 流式消息绑定（NDJSON streaming endpoint + 前端 ReadableStream 增量渲染）
- [x] richer rendering / thinking / tool timeline 展示（fenced code blocks、inline code、links、headings/lists/blockquotes、strong/emphasis、tables、task lists、thinking details、tool call cards with status/input/output）
- [x] auth/settings UX（`/api/auth/{provider}` 状态查询 + 前端 provider/model 切换时刷新 auth 状态；真实 login flow 仍在 Tau.Ai OAuth backlog）
- [x] 附件体系（`WebChatAttachmentDto` + 前端 file picker/preview/remove + 发送时 base64 content + text extraction）
- [x] session lifecycle（session delete / export JSON download / export.html / export.md / export.jsonl 线性 transcript / import.jsonl 线性 transcript roundtrip / clone / search by title / import file upload / title rename / last-opened session restore baseline / clear messages 端点）
- [x] 更高层的 WebUi 行为测试（已补 `WebChatService` fake-runner 流式消息、附件 prompt 和持久化行为测试、Minimal API endpoint 的 NDJSON streaming/session 持久化/export/import/delete/rename/restore/错误状态回归，以及 `tests/Tau.WebUi.Tests` 用 Microsoft.Playwright 真实 headless Chromium 跑 create session/streaming send/rename 的 browser-级 flow 测试）

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
- [x] Mom sandbox/tool delegation seam（`MomSandboxConfig` / `IMomSandboxExecutor` / `MomToolSet`，默认 host sandbox，配置层支持 `docker:<container>`，新增 `--validate-sandbox` 显式 sandbox validation 入口，Docker validate/exec 路径已可通过注入 `IMomSandboxProcessRunner` 本地验证，runner 默认工具切到 `bash/read/write/edit/attach`，attach 产物进入 execution attachments）
- [ ] real Slack smoke
- [~] workspace / sandbox / tool delegation（已补 workspace memory context、本地 attachment staging、scratch 目录、SYSTEM.md、Agent Skills prompt inventory、host sandbox executor、docker path/command construction seam、`--validate-sandbox` 显式 validation 入口、Docker validate/exec 可测试 seam 和 `bash/read/write/edit/attach` tools；仍缺真实 Docker sandbox smoke）
- [~] message / runtime flow（已补最小 `log.jsonl` channel history 注入、本地 request/result 写回、`context.json` runtime messages、`last_prompt.jsonl` prompt debug snapshot、`status.json` runtime 状态、本地 busy-state guard、Slack-compatible envelope、Slack event mapper、Slack Socket Mode transport seam、Slack startup backfill seam、channel processor busy/stop/typing/thread response seam、true cancellable stop seam、Slack Web API responder seam、Slack private file download seam 与 per-channel queue seam，仍缺真实 Slack session sync / 多消息 runtime flow）
- [ ] 更高层 delegation flow 与端到端测试

### Tau.Pods

- [x] config init/list/validate/status
- [x] probe（HTTP endpoint / TCP ssh target）
- [x] exec（SSH pod remote command execution）
- [x] deploy / stop / restart / health / logs / deployments（`PodLifecycleService` + CLI commands，SSH-based deploy/stop/restart/logs/deployments 和 HTTP/SSH health check；logs 通过 journalctl 拉 `tau-pod-<name>` unit，回退 `~/.tau_pods/<name>.log`，可配置 tail；deployments 通过 SSH 列出并解析 `~/.tau_pods/*.json` metadata，输出 name/model/status/ts）
- [~] 真正的 CLI 运维命令体系（已补 `health/deploy/stop/restart/logs/deployments`、可省略 path 的 target-command 参数解析、SSH lifecycle metadata 命令转义、SSH exec `ArgumentList` argv hardening、本地 ssh 启动失败/runner 异常/cancellation 结构化返回和 CLI 参数回归测试；仍缺更完整模型生命周期、更完整远端 transport hardening 和真实运维 smoke）

## P2：工程化

- [ ] release 产物改为真实 Tau 可执行产物
- [ ] solution build 的环境异常诊断文档化
- [ ] provider e2e 测试（当前 Bedrock 已有 StubHandler 级 bearer/SigV4/shared profile/eventstream 回归，Vertex 已有 ADC token/SSE 回归，Gemini CLI/Antigravity 已有 headers/fallback/retry/empty-stream 回归，仍缺真实云端 e2e）
- [ ] coding-agent 默认路径的更高层回归测试
- [~] CodingAgent RPC mode（已复用 runner `Steer/FollowUp` seam 落地 `--mode rpc` JSONL baseline，覆盖 prompt、steer、follow_up、abort、new_session（含上游同名 `parentSession` metadata）、get_state、get_settings、update_settings、set_model、cycle_model、get_available_models、set_thinking_level、cycle_thinking_level、set_auto_retry、abort_retry、bash、abort_bash、set_steering_mode、set_follow_up_mode、set_auto_compaction、switch_session、get_fork_messages、compact、fork、clone、get_session_stats、get_messages、get_commands、export_html、get_last_assistant_text、set_session_name；仍缺完整上游 RPC extension UI、streamed bash output、上游 settings selector / 全量配置面和 full command parity）
- [~] 可观测性：provider 调用、auth、tool execution、session / delegation / pod probe 的最小日志（已补 `Tau.Ai.Observability.ITauLogSink`/`JsonlTauLogSink`/`NullTauLogSink` baseline，默认写 `./.tau/log.jsonl`，可被 `TAU_LOG_FILE` 覆盖或 `TAU_LOG_DISABLED=1` 关闭；`RuntimeCodingAgentRunner.RunAsync` 已 emit `agent/run.start|run.end|run.cancel|run.error` 含 provider/model/inputBytes/elapsedMs/error 字段；仍缺 auth status、tool execution、session 持久化、Mom delegation、Pod probe 几个面的事件）
- [ ] `scripts/verify-dotnet.sh` 对运行态 smoke 的进一步自动化
- [x] `scripts/verify-dotnet.ps1` 对运行态 smoke 的进一步自动化（`-RunSmoke` 已覆盖 `WebUi` 与 `Mom --once`）
- [ ] 把 `verify-dotnet.ps1 -RunSmoke` 接到 CI 或补 bash 等价 smoke

## 当前已知环境现实

- [ ] 当前 Windows 环境下 `bash scripts/verify-dotnet.sh --skip-restore` 会落到 WSL 并失败于缺少 `/bin/bash`
- [x] Windows 本机已补 `scripts/verify-dotnet.ps1` 作为等价项目级验证入口
- [ ] 本地标准命令继续保持 bash 形式，但现场执行要接受 PowerShell 脚本或等价顺序 `dotnet` 验证作为兜底
- [x] `Tau.Mom` 也已收回 `ProjectReference`
