# pi-mono → Tau.CodingAgent 功能差距分析与移植路线

## 目标

以 `pi-mono-main` 为参考源，系统梳理 Tau.CodingAgent 尚未移植的功能模块，按优先级分层排列，为后续逐切片推进提供可追溯的路线图。

## 范围

- 包含：Tau.CodingAgent 与上游 `packages/coding-agent/` 的功能差距、Tau.Agent runtime 层缺口、Tau.Tui 组件层缺口
- 不包含：Tau.Mom、Tau.Pods、Tau.WebUi、Tau.Ai provider/auth 层（这些已在 `2026-05-10-tau-complete-pi-mono-port.md` 中跟踪）

## 背景

- 相关文档：`docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`（总路线图）
- 参考项目：`C:\Users\zhouh\Desktop\pi-mono-main`（TypeScript monorepo，7 packages）
- 相关代码路径：`src/Tau.CodingAgent/`、`src/Tau.Agent/`、`src/Tau.Tui/`
- 已知约束：Tau 保持 AOT/source-gen/零 provider SDK 边界；不机械翻译，每个能力要有 Tau-native 数据模型和本地可验证 test

## 风险

- 风险：Extension Runtime 工作量巨大，可能阻塞其他功能
- 缓解方式：先推进不依赖 extension runtime 的独立切片（Print Mode、Steering、Branch Summarization）

## 里程碑

### Tier 1：缺失的运行模式

#### 1.1 Print Mode（完全缺失）
- **上游**: `packages/coding-agent/src/modes/print-mode.ts`
- **功能**: 非交互式单次执行，接收 prompt 输出结果后退出
- **支持**: text 输出 / JSON event stream、多消息、图片附件、exit code
- **Tau 现状**: Program.cs 只有 interactive 入口，无 `--print` / `-p` 参数
- **影响**: 无法用于 CI/CD pipeline、脚本自动化、批量处理

#### 1.2 RPC Mode（baseline 已接入，完整 parity 未完成）
- **上游**: `packages/coding-agent/src/modes/rpc/`
- **功能**: JSON stdin/stdout 协议，允许外部进程嵌入 coding agent
- **协议**: prompt, steer, follow_up, abort, get_state, set_model, set_thinking_level, cycle_thinking_level, compact, fork, clone, export_html, get_messages, get_commands 等 30+ 命令
- **Tau 现状**: 已新增 `--mode rpc` 与 `CodingAgentRpcHost` baseline，走 LF-delimited JSONL stdin/stdout，覆盖 prompt / steer / follow_up / abort / new_session（含可选 parentSession metadata） / get_state / get_settings / update_settings / set_model / cycle_model / get_available_models / set_thinking_level / cycle_thinking_level / set_auto_retry / abort_retry / bash / abort_bash / set_steering_mode / set_follow_up_mode / set_auto_compaction / switch_session / get_fork_messages / compact / fork / clone / get_session_stats / get_messages / get_commands / export_html / get_last_assistant_text / set_session_name
- **剩余影响**: extension UI sub-protocol、streamed bash output、上游 settings selector / theme / terminal / packages 等全量配置面和 full command parity 仍未完成

### Tier 2：缺失的核心子系统

#### 2.1 完整 Extension Runtime（仅有声明式 JSON baseline）
- **上游**: `packages/coding-agent/src/core/extensions/`
- **缺失**: 动态代码加载、事件订阅（40+ 事件类型）、自定义 tool/shortcut/provider/message renderer 注册、Extension UI Context、Resource discovery、Input transformation、Tool call blocking/result modification、Context message modification

#### 2.2 Branch Summarization（显式 `/fork --summarize` baseline 已接入）
- **上游**: `packages/coding-agent/src/core/compaction/branch-summarization.ts`
- **Tau 现状**: `/fork <entry-id> --summarize [instructions]` 会对被离开的 branch 生成结构化摘要，写 JSONL `branch_summary` entry，并在 branch restore 时注入 summary context；完整自动 branch switching hooks、extension events 与 cancellation UI 仍未完成

#### 2.3 Event Bus（完全缺失）
- **上游**: `packages/coding-agent/src/core/event-bus.ts`
- **功能**: 发布/订阅系统，extension 间通信

#### 2.4 Steering/FollowUp 用户面暴露（CLI + RPC baseline 已接入，完整 TUI 未完成）
- **上游**: Enter（steering）和 Alt+Enter（follow-up）在 agent 运行中注入消息
- **Tau 现状**: `AgentRuntime` 已有 `Steer()` / `FollowUp()`，`ICodingAgentRunner`、`CodingAgentHost` active-turn input source 和 `CodingAgentRpcHost` `steer` / `follow_up` 命令已接入 baseline；完整 TUI overlay、快捷键提示仍未完成

### Tier 3：缺失的 Slash 命令

| 命令 | 上游功能 | Tau 现状 |
|------|----------|----------|
| `/settings` | settings 选择器 UI | Read-only CLI/settings summary baseline 已接入：`/settings [current|path]` 可查看 settings 路径、当前 model/thinking、默认 model、tree filter、retry、default thinking、steering/follow-up mode、auto-compaction 设置和 enabledModels scope；完整 settings selector UI / TUI edit parity 仍未完成 |
| `/scoped-models` | Ctrl+P 快速切换模型子集 | CLI/settings `enabledModels` baseline 已接入：`/scoped-models [set|add|remove|clear|all]` 可查看和维护持久化模型 scope；完整 selector UI 与 Ctrl+P model cycling 仍未完成 |
| `/changelog` | 版本更新日志 | Tau-native release notes baseline 已接入：读取 `docs/releases/feature-release-notes.md` 或 `TAU_CODING_AGENT_CHANGELOG_FILE` 指定文件并输出最近条目；启动 changelog 渲染、`collapseChangelog` 设置和 install/update telemetry 仍未完成 |
| `/hotkeys` | 所有快捷键 | 当前 editor keybinding listing baseline 已接入，会显示运行时注入的 `IKeyBindingMap` 当前绑定；完整上游 app/session/tree/extension shortcut registry 仍未完成 |
| `/logout` | OAuth provider 登出 | `/logout [provider]` auth.json credential removal baseline 已接入；当前只删除本地 `auth.json` provider entry，不修改环境变量或 `models.json` credential，完整 OAuth selector / login-session parity 仍未完成 |
| `/reload` | 重新加载 keybindings/extensions/skills/prompts/themes/context files | settings / extension resources / prompts / skills / context files / keybindings reload baseline 已接入，settings reload 会同步 retry、thinking 和 queue mode；theme loader、完整 TypeScript extension runtime reload 和 full resource selector 仍未完成 |

### Tier 4：缺失的 TUI 组件层

- 差分渲染引擎（组件树、布局系统、message area、status area、footer）
- Interactive Selectors（model/session/settings/theme/thinking/config/extension/scoped-models）
- Rich Message Rendering（结构化消息渲染、diff 高亮、tool execution timeline）
- Keybinding Hints / Footer

### Tier 5：缺失的 Rendering / Export 能力

- 语法高亮（highlight.js 等价物）
- Custom Tool Renderer（per-tool renderCall/renderResult）
- Theme System（主题加载/切换/extension 贡献）
- Tau 专属 Share Viewer 服务

### Tier 6：缺失的 Agent Runtime 能力

- LLM-Generated Split-Turn Summarization
- Per-tool execution mode（sequential/parallel 声明）
- Tool Argument Preparation（prepareArguments）

### Tier 7：已有 baseline 但未完成 parity

| 功能 | 仍缺 |
|------|------|
| Interactive tree navigator | 多选、fold 持久化 |
| Auto-compaction | LLM split-turn summarization、compaction events/cancellation UI |
| Retry | settings UI parity、完整 cancellation UI |
| Extension system | 完整 runtime |
| HTML export | 语法高亮、custom tool renderer、richer template |

## 建议执行顺序

1. **Print Mode** — 最小投入最大收益，解锁 CI/脚本自动化
2. **Thinking Level 用户控制** — 已完成 `/thinking` 命令 + settings 持久化
3. **Steering/FollowUp CLI 接入** — 已完成 runner seam + host 运行中输入 listener baseline
4. **RPC Mode** — 解锁 IDE 集成和 WebUi 进程嵌入
5. **Branch Summarization** — 提升长 session 切换体验
6. **Slash 命令 baseline closure**（当前列出的缺失 slash 命令均已有 Tau-native baseline；`/settings`、`/scoped-models`、`/changelog`、`/logout` 与 `/reload` 的完整 selector/startup/runtime parity 仍缺）
7. **Extension Runtime** — 最大工作量，解锁生态
8. **TUI 组件层** — 长期投入，逐步推进

## 验证方式

- `dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore`
- `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore`
- 每个切片完成后补 targeted tests 和 history

## 进度记录

- [x] 差距分析完成，plan 落到 `docs/exec-plans/active/`
- [x] Print Mode baseline（`CodingAgentPrintMode` + `--print/-p` CLI 参数 + 4 个 targeted tests，185/185 通过）
- [x] Thinking Level 用户控制（`/thinking` 命令 + settings 持久化 + targeted tests，CodingAgent.Tests 189/189 通过）
- [x] Steering/FollowUp CLI 接入（`ICodingAgentRunner.Steer/FollowUp` + Host turn input source + targeted tests，CodingAgent.Tests 191/191 通过）
- [x] RPC Mode baseline（`CodingAgentRpcHost` + `--mode rpc` + 6 个 targeted tests，197/197 通过）
- [x] Branch Summarization baseline（`/fork --summarize` + JSONL `branch_summary` + HTML timeline/readFiles/modifiedFiles + targeted test，CodingAgent.Tests 198/198 通过）
- [x] RPC session utility baseline（`export_html` / `get_last_assistant_text` / `set_session_name` + 4 个 targeted tests，CodingAgent.Tests 202/202 通过）
- [x] Hotkeys listing baseline（`/hotkeys` + 当前 editor keybinding map 注入 + 3 个 targeted tests，CodingAgent.Tests 205/205 通过）
- [x] Reload command baseline（`/reload` + settings/resources/prompts/skills/keybindings reload + 2 个 targeted tests，CodingAgent.Tests 207/207 通过）
- [x] Logout command baseline（`/logout [provider]` + auth.json provider credential removal + Tau.Ai/CodingAgent targeted tests，Tau.Ai.Tests 194/194、CodingAgent.Tests 211/211 通过）
- [x] Changelog command baseline（`/changelog [count|all]` + release notes table parser + 4 个 targeted tests，CodingAgent.Tests 215/215 通过）
- [x] Scoped models command baseline（`/scoped-models [set|add|remove|clear|all]` + settings `enabledModels` + 3 个 targeted tests，CodingAgent.Tests 218/218 通过）
- [x] Settings command baseline（`/settings [current|path]` + read-only settings summary + 2 个 router tests + 1 个 host test，CodingAgent.Tests 221/221 通过）
- [x] RPC thinking controls baseline（`set_thinking_level` / `cycle_thinking_level` + 2 个 targeted tests，CodingAgent.Tests 223/223 通过）
- [x] RPC cycle model baseline（`cycle_model` + settings `enabledModels` scope + 3 个 targeted tests，CodingAgent.Tests 226/226 通过）
- [x] RPC auto retry baseline（`set_auto_retry` / `abort_retry` + retry rollback/audit + 4 个 targeted tests，CodingAgent.Tests 230/230 通过）
- [x] RPC session switch / fork messages baseline（`switch_session` / `get_fork_messages` + 3 个 targeted tests，CodingAgent.Tests 233/233 通过）
- [x] RPC queue / auto-compaction settings controls baseline（`set_steering_mode` / `set_follow_up_mode` / `set_auto_compaction` + settings-backed `get_state` + 5 个 RPC/settings tests，CodingAgent.Tests 238/238 通过；Agent.Tests 58/58 覆盖 queue drain）
- [x] RPC bash / abort_bash baseline（`bash` / `abort_bash` + 可注入 shell runner + 4 个 targeted tests，CodingAgent.Tests 242/242 通过）
- [x] RPC settings baseline（`get_settings` / `update_settings` + Tau-supported settings snapshot/update + 3 个 targeted tests，CodingAgent.Tests 245/245 通过）
- [x] RPC new_session parent metadata baseline（`new_session.parentSession` 写入 JSONL header `parentSession` + 1 个 targeted test，CodingAgent.Tests 246/246 通过）
- [x] Context files baseline（`~/.tau/AGENTS.md|CLAUDE.md` + parent-to-cwd `AGENTS.md|CLAUDE.md` + `--no-context-files` / `-nc` + `/reload` refresh，CodingAgent.Tests 252/252 通过）
- [~] Slash 命令 full parity（当前列出的缺失 slash 命令均已有 baseline；`/settings`、`/scoped-models`、`/changelog`、`/logout` 与 `/reload` 的完整 selector/startup/runtime parity 仍缺）
- [ ] Extension Runtime
- [ ] TUI 组件层

## 已完成切片：Settings command baseline

### 已完成的代码与测试改动

| 文件 | 改动 |
|------|------|
| `src/Tau.CodingAgent/Runtime/CodingAgentCommandCatalog.cs` | 新增 `/settings [current|path]` 命令定义，让 `/help` 与 usage 错误共用 catalog |
| `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs` | 新增只读 settings handler：`/settings` / `/settings current` 输出 settings path、当前 model/thinking、默认 model、tree filter、retry policy、default thinking 和 scoped models；`/settings path` 只输出路径；无 settings store 或无效参数返回明确错误 |
| `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs` | 新增 settings summary、path/current/unavailable/usage 回归，并确认命令不调用 runner |
| `tests/Tau.CodingAgent.Tests/CodingAgentHostTests.cs` | 更新 `/help` 输出，并补 host `/settings` status rendering 回归 |
| `README.md`、`docs/ARCHITECTURE.md`、`docs/QUALITY_SCORE.md`、`next.md`、`docs/releases/feature-release-notes.md`、两份 active plan | 同步 `/settings [current|path]` read-only baseline、221 个 CodingAgent tests，以及完整 settings selector UI 仍未完成的边界 |
| `docs/histories/2026-05/20260521-0800-coding-agent-settings-command.md` | 记录本切片设计意图、改动范围与验证结果 |

验证通过：`dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal` 0 警告 0 错误；`dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal` 221/221 通过。

### 设计要点（避免接手 agent 重新决策）

- `/settings` 第一刀选择 read-only CLI/settings summary，因为 Tau 已有 `CodingAgentSettingsStore`，但还没有完整上游 settings selector / TUI edit 层。
- 命令只读取 settings store 和当前 runner 状态，不保存 settings，不切换模型，不调用 runner。
- 输出聚合当前 settings 事实：文件路径、当前 provider/model、当前 thinking、默认 provider/model、tree filter、retry policy、default thinking 和 scoped models scope。完整可编辑 selector UI 后续在 TUI/selector 层补齐。

## 已完成切片：Scoped models command baseline

### 已完成的代码与测试改动

| 文件 | 改动 |
|------|------|
| `src/Tau.CodingAgent/Runtime/CodingAgentSettingsStore.cs` | 在 settings snapshot / document 中新增 `EnabledModels`；读写 `enabledModels` 时 trim、去空、大小写不敏感去重，空数组归一为 `null` |
| `src/Tau.CodingAgent/Runtime/CodingAgentCommandCatalog.cs` | 新增 `/scoped-models [set|add|remove|clear|all] [provider/model ...]` 命令定义 |
| `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs` | 新增 scoped model handler：支持查看 current/all、`set`、`add`、`remove`、`clear` / `all`；支持 `provider/model` 和唯一 model id 解析，ambiguous / unknown model 返回明确错误 |
| `tests/Tau.CodingAgent.Tests/CodingAgentSettingsStoreTests.cs` | 覆盖 `enabledModels` round-trip、`SaveDefaultModel()` 保留该字段、invalid JSON 回退为 `null` |
| `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs` | 覆盖默认 all enabled 展示、set/add/remove/clear 持久化语义、保留其他 settings 字段、usage 和 model 错误 |
| `tests/Tau.CodingAgent.Tests/CodingAgentHostTests.cs` | 更新 `/help` 输出，确保 host 命令面包含 `/scoped-models` |
| `README.md`、`docs/ARCHITECTURE.md`、`docs/QUALITY_SCORE.md`、`next.md`、`docs/releases/feature-release-notes.md`、两份 active plan | 同步 `/scoped-models` baseline、settings `enabledModels` 语义，以及完整 selector / Ctrl+P model cycling 仍未完成的边界 |
| `docs/histories/2026-05/20260521-0700-coding-agent-scoped-models-command.md` | 记录本切片设计意图、改动范围与验证结果 |

验证通过：`dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal` 0 警告 0 错误；`dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal` 218/218 通过。

### 设计要点（避免接手 agent 重新决策）

- `/scoped-models` 第一刀选择 CLI/settings baseline，因为 Tau 还没有完整 TUI scoped model selector，也没有 Ctrl+P model cycling；先固定持久化语义和错误边界。
- `enabledModels=null` 或缺失表示 all enabled / no filter；显式数组表示有序模型 scope。
- `set/add/remove` 的结果如果等于全部 available models，保存为 `null`，避免把“全量启用”写成冗余过滤列表。
- 当前命令只维护 settings scope，不调用 runner，不切换当前模型；完整上游 selector 和 Ctrl+P cycling 后续在 TUI/selector 层补齐。

## 已完成切片：Changelog command baseline

### 已完成的代码与测试改动

| 文件 | 改动 |
|------|------|
| `src/Tau.CodingAgent/Runtime/CodingAgentChangelogStore.cs` | 新增 release notes store，默认从当前目录向上查找 `docs/releases/feature-release-notes.md`，支持 `TAU_CODING_AGENT_CHANGELOG_FILE` 覆盖；解析 Markdown table 并输出日期、功能域、用户价值、变更摘要 |
| `src/Tau.CodingAgent/Runtime/CodingAgentCommandCatalog.cs` | 新增 `/changelog [count|all]` 命令定义，让 `/help` 与 usage 错误共用 catalog |
| `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs` | 新增 `HandleChangelogCommand`，支持默认最近 20 条、显式 count 和 `all`，参数错误返回 catalog usage |
| `src/Tau.CodingAgent/Runtime/CodingAgentHost.cs` | 允许注入 `CodingAgentChangelogStore`，便于 host tests 使用临时 release notes 文件 |
| `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs` | 新增 `/changelog 1`、`/changelog all`、invalid arguments 回归，并更新 help/catalog 预期 |
| `tests/Tau.CodingAgent.Tests/CodingAgentHostTests.cs` | 更新 `/help` 预期，并补 host `/changelog` status rendering 回归 |
| `docs/releases/feature-release-notes.md` | 新增 2026-05 release note，作为 `/changelog` 默认数据源的首个真实 Tau 功能记录 |
| `README.md`、`docs/ARCHITECTURE.md`、`docs/QUALITY_SCORE.md`、`next.md`、两份 active plan | 同步 `/changelog [count|all]` baseline 与启动 changelog / collapse / telemetry parity 仍未完成的边界 |
| `docs/histories/2026-05/20260521-0600-coding-agent-changelog-command.md` | 记录本切片设计意图、改动范围与验证结果 |

验证通过：`dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal` 0 警告 0 错误；`dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal` 215/215 通过。

### 设计要点（避免接手 agent 重新决策）

- `/changelog` 第一刀对齐上游“用户可显式查看 changelog”的命令价值，但数据源改为 Tau 仓库已有的 `docs/releases/feature-release-notes.md`，不新增根级 `CHANGELOG.md`。
- 解析器只处理 release notes Markdown table，保持输出为 CLI 纯文本；完整 TUI Markdown 渲染、启动时“What's New”区块、`lastChangelogVersion`、`collapseChangelog` 和 install/update telemetry 均继续后置。
- 默认查找当前目录向上的 `docs/releases/feature-release-notes.md`，同时提供 `TAU_CODING_AGENT_CHANGELOG_FILE` 供打包产物或测试固定来源。

## 已完成切片：Logout command baseline

### 已完成的代码与测试改动

| 文件 | 改动 |
|------|------|
| `src/Tau.Ai/Auth/OAuth/OAuthCredentialStore.cs` | 新增 `Remove(providerId)`，从首个存在的 auth file 删除大小写不敏感匹配的 provider entry；文件不存在、非 JSON object、不可读、JSON invalid 或 provider 缺失时返回 `false` 且不重写文件 |
| `src/Tau.Ai/Auth/ProviderAuthResolver.cs` | 新增 `Logout(providerId)`，调用 credential store 删除并写入不含 secret 的 `auth/logout` 观测事件 |
| `src/Tau.CodingAgent/Runtime/ICodingAgentRunner.cs` | 新增 `Logout(string providerId)`，让命令层不直接依赖 Tau.Ai store |
| `src/Tau.CodingAgent/Runtime/RuntimeCodingAgentRunner.cs` | 实现 `Logout(providerId)`，委托 `ProviderAuthResolver` |
| `src/Tau.CodingAgent/Runtime/CodingAgentCommandCatalog.cs` | 新增 `/logout [provider]` 命令定义 |
| `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs` | 新增 `HandleLogoutCommand`；未传 provider 时使用当前 runner provider，显式 provider 时先解析对应 auth status；成功/未命中都会明确说明 environment variables 和 `models.json` credentials 不变 |
| `tests/Tau.Ai.Tests/OAuthCredentialStoreTests.cs` | 新增删除匹配 provider 并保留其他 entries、provider 缺失不改写文件的回归 |
| `tests/Tau.Ai.Tests/ProviderAuthResolverTests.cs` | 新增 `Logout` 删除 auth file credential 的回归 |
| `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs` | 新增 `/logout` 默认 provider、显式 provider missing、额外参数 usage 回归，并更新 help/catalog 预期 |
| `tests/Tau.CodingAgent.Tests/CodingAgentHostTests.cs` | 更新 `/help` 预期，并补 host `/logout` status rendering 回归 |
| `tests/Tau.CodingAgent.Tests/FakeCodingAgentRunner.cs`、`tests/Tau.Agent.Tests/RuntimeDelegationAgentRunnerTests.cs`、`tests/Tau.WebUi.Tests/FakeWebUiRunner.cs` | 补齐 runner interface 新成员，避免 fake runner drift |
| `README.md`、`docs/ARCHITECTURE.md`、`docs/QUALITY_SCORE.md`、`next.md`、两份 active plan | 同步 `/logout [provider]` baseline 与完整 OAuth selector / login-session parity 仍未完成的边界 |
| `docs/histories/2026-05/20260521-0500-coding-agent-logout-command.md` | 记录本切片设计意图、改动范围与验证结果 |

验证通过：`dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal` 0 警告 0 错误；`dotnet test tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --no-restore --verbosity minimal` 194/194 通过；`dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal` 211/211 通过。

### 设计要点（避免接手 agent 重新决策）

- `/logout` 第一刀只对齐上游 `AuthStorage.logout(provider)` 的本地 credential removal 核心语义，不移植完整 OAuth provider selector UI。
- 删除范围限定为 `auth.json` provider entry；环境变量凭证和 `models.json` credential 配置不是 auth storage，不做删除或改写，避免误删用户外部配置。
- auth file 缺失、malformed、非 object 或 provider 未命中时返回友好状态并保持原文件不变。
- 命令输出只说明 provider 和删除状态，不回显 access/refresh/API key 或 credential header 值。

## 已完成切片：Thinking Level 用户控制

### 已完成的代码与测试改动

| 文件 | 改动 |
|------|------|
| `src/Tau.CodingAgent/Runtime/ICodingAgentRunner.cs` | 新增 `ThinkingLevel? ThinkingLevel { get; set; }` 接口属性 |
| `src/Tau.CodingAgent/Runtime/RuntimeCodingAgentRunner.cs` | 实现 `ThinkingLevel` 属性，通过 `_config.StreamOptions.Reasoning` 生效 |
| `src/Tau.CodingAgent/Runtime/CodingAgentSettingsStore.cs` | `CodingAgentSettingsSnapshot` / `CodingAgentSettingsDocument` 新增 `DefaultThinkingLevel` 字段 |
| `src/Tau.CodingAgent/Runtime/CodingAgentCommandCatalog.cs` | 新增 `/thinking [current\|cycle\|off\|minimal\|low\|medium\|high\|xhigh]` 命令定义 |
| `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs` | 新增 `HandleThinkingCommand` + `TryParseThinkingLevel` + `CycleThinkingLevel` + `FormatThinkingLevel` + `SaveThinkingLevel`（+88 行） |
| `src/Tau.CodingAgent/Program.cs` | 启动时从 `settings.DefaultThinkingLevel` 加载到 `runner.ThinkingLevel` |
| `tests/Tau.CodingAgent.Tests/FakeCodingAgentRunner.cs` | 实现 `ThinkingLevel` 属性（auto-property） |
| `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs` | 更新 `/help` 预期，并补 `/thinking` 默认 off、显式 high、cycle、off、invalid usage 和 settings round-trip 回归 |
| `tests/Tau.CodingAgent.Tests/CodingAgentHostTests.cs` | 更新 host `/help` 命令列表预期 |
| `docs/histories/2026-05/20260520-1300-coding-agent-thinking-level.md` | 记录本切片设计意图、改动范围与验证结果 |

验证通过：`dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal` 0 警告 0 错误；`dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal` 189/189 通过；真实 CLI smoke `/thinking`、`/thinking high`、`/thinking cycle`、`/thinking off`、`/quit` 退出码 0。

### 设计要点（避免接手 agent 重新决策）

- `/thinking cycle` 顺序：`null → Low → Medium → High → ExtraHigh → null`（跳过 Minimal，因为它和 Low 区分度低；Minimal 仅在显式 `/thinking minimal` 时使用）。这与上游 `thinking-selector.ts` 略有不同（上游含 `off` 作为枚举值），但保持 Tau 现有 `ThinkingLevel` 枚举不变（`Minimal/Low/Medium/High/ExtraHigh`），用 `null` 表示 off。
- settings 字段类型用 `string?`（"low"/"medium"/...）而不是直接序列化 enum，避免后续 enum 重命名破坏旧 settings 文件。
- `/thinking off` 既清空 runtime 也清空 settings；`/thinking cycle → off` 同理。
- 不实现 thinking-selector UI（依赖 TUI 组件层），CLI 命令面已足够。

## 已完成切片：Steering/FollowUp CLI 接入

### 已完成的代码与测试改动

| 文件 | 改动 |
|------|------|
| `src/Tau.CodingAgent/Runtime/ICodingAgentRunner.cs` | 新增 `Steer(string input)` / `FollowUp(string input)`，把 runtime 队列语义暴露给 host / 后续 RPC |
| `src/Tau.CodingAgent/Runtime/RuntimeCodingAgentRunner.cs` | `Steer` / `FollowUp` 分别包装为 `UserMessage` 后转发给 `AgentRuntime.Steer()` / `AgentRuntime.FollowUp()` |
| `src/Tau.CodingAgent/Runtime/CodingAgentTurnInputSource.cs` | 新增 `ICodingAgentTurnInputSource`、`CodingAgentTurnInput` 与 `SystemConsoleCodingAgentTurnInputSource`；生产 source 轮询 `Console.KeyAvailable`，Enter 提交 steering，Alt+Enter 提交 follow-up |
| `src/Tau.CodingAgent/Runtime/CodingAgentHost.cs` | `RunSingleTurnAttemptAsync` 在 active runner turn 期间启动后台 listener，turn 结束后取消并等待收敛；listener 只把输入转发到 runner，不改变 slash command 主输入循环 |
| `src/Tau.CodingAgent/Program.cs` | 仅在真实交互式 editor 启用时给 host 注入 `SystemConsoleCodingAgentTurnInputSource`，redirected stdin/stdout 和 print mode 不启用 |
| `tests/Tau.CodingAgent.Tests/FakeCodingAgentRunner.cs` | 记录 `SteeringInputs` / `FollowUpInputs`，提供 observer 让 host tests deterministic 等待转发 |
| `tests/Tau.CodingAgent.Tests/CodingAgentHostTests.cs` | 新增 steering / follow-up 两个 targeted tests，覆盖 active turn 期间的输入转发 |
| `tests/Tau.Agent.Tests/RuntimeDelegationAgentRunnerTests.cs`、`tests/Tau.WebUi.Tests/FakeWebUiRunner.cs` | 补齐 `ICodingAgentRunner` 新成员，避免测试 fake runner 漂移 |
| `docs/histories/2026-05/20260520-1400-coding-agent-steering-followup.md` | 记录本切片设计意图、改动范围与验证结果 |

验证通过：`dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal` 0 警告 0 错误；`dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal` 191/191 通过；`dotnet test tests\Tau.Agent.Tests\Tau.Agent.Tests.csproj --no-restore --verbosity minimal` 54/54 通过；`dotnet build tests\Tau.WebUi.Tests\Tau.WebUi.Tests.csproj --no-restore --verbosity minimal` 0 警告 0 错误。

### 设计要点（避免接手 agent 重新决策）

- 不把 listener 绑死到 `Console.ReadKey` 阻塞读取。生产 source 使用 `Console.KeyAvailable` + 短轮询，避免 runner turn 已结束但后台 task 仍卡在不可取消的 `Console.ReadKey`。
- Host 只在 `RunSingleTurnAttemptAsync` 内开启 listener，不改变主输入循环、slash command 解析、retry/rollback 或 session 持久化路径；转发后的消息是否在本 turn 被消费由 `AgentRuntime` 现有 inner/outer loop 决定。
- Enter / Alt+Enter 的 production 映射先做最小 baseline；完整 TUI overlay、输入回显、快捷键提示和 RPC steering/follow_up 命令继续后置。

## 后续切片接手指引

### Steering/FollowUp CLI 接入（已完成 baseline）
- `AgentRuntime.Steer()` / `AgentRuntime.FollowUp()` 已通过 `ICodingAgentRunner.Steer(string)` / `FollowUp(string)` 暴露到 host 层。
- `CodingAgentHost.RunSingleTurnAttemptAsync` 会在每个 active runner turn 期间启动可注入的 `ICodingAgentTurnInputSource`，收到 `Steering` 输入时调用 runner `Steer`，收到 `FollowUp` 输入时调用 runner `FollowUp`，turn 完成后取消并收敛 listener。
- 生产入口只在真实交互式 editor 启用时接 `SystemConsoleCodingAgentTurnInputSource`；该 source 用非阻塞 `Console.KeyAvailable` 轮询，Enter 提交 steering，Alt+Enter 提交 follow-up，Backspace/Escape 提供最小缓冲编辑。
- 当前仍只是 CLI baseline，不等于完整上游 TUI input overlay / keybinding hint / RPC steering parity。

### RPC Mode（已完成 baseline）
- `CodingAgentRpcHost` 直接消费 `TextReader` / `TextWriter`，使用严格 LF JSONL 作为 stdin/stdout framing。
- 生产入口 `--mode rpc` 不创建交互式 editor，不写 welcome / prompt / TUI 文本到 stdout，避免污染 JSONL。
- 当前命令覆盖 `prompt`、`steer`、`follow_up`、`abort`、`new_session`（含可选 `parentSession` 写入 JSONL header metadata）、`get_state`、`set_model`、`cycle_model`、`get_available_models`、`set_thinking_level`、`cycle_thinking_level`、`set_auto_retry`、`abort_retry`、`bash`、`abort_bash`、`set_steering_mode`、`set_follow_up_mode`、`set_auto_compaction`、`switch_session`、`get_fork_messages`、`compact`、`fork`、`clone`、`get_session_stats`、`get_messages`、`get_commands`、`export_html`、`get_last_assistant_text`、`set_session_name`。
- 当前仍不是完整上游 RPC parity：extension UI request/response sub-protocol、streamed bash output、full settings RPC 和更多 command discovery provenance 仍未完成。

### Branch Summarization（baseline 已完成，剩余自动 hook / UI parity）
- 上游：`packages/coding-agent/src/core/compaction/branch-summarization.ts`
- 已完成触发：`/fork <entry-id> --summarize [instructions]` / `--summary` / `-s`
- 已完成实现：用 LLM 对离开的 branch 生成结构化摘要（Goal/Constraints/Progress/Key Decisions/Next Steps），写 JSONL `branch_summary` entry，并在 branch restore 时转成 context user message
- 已完成导出：`/tree --search` 能搜索 branch summary，HTML transcript timeline / branch outline 能渲染 branch summary、read files 和 modified files
- 剩余：`/resume` / `/tree --interactive` navigation 的自动 summarization hook、extension event、cancellation UI 和完整上游 TreeSelector 集成仍未完成

## 决策记录

- 2026-05-20：决定把差距分析作为独立 exec plan 落到 active 目录，而不是直接追加到已有的 `2026-05-10-tau-complete-pi-mono-port.md`。原因是总路线图已经很长（275 行决策记录），独立文件更便于聚焦当前执行切片和追踪进度；两份 plan 通过"背景"互相引用。
- 2026-05-20：决定 Print Mode 作为第一个执行切片。原因是它不依赖 extension runtime、TUI 组件或 event bus，只需要在 Program.cs 增加参数解析和一个非交互式 host 路径，投入最小但解锁 CI/脚本自动化场景。
- 2026-05-20：决定 Thinking Level 作为第二个执行切片，先做命令面（`/thinking`）和 settings 持久化，跳过 thinking-selector UI（依赖 TUI 组件层）。原因是 Tau 现有 provider 层已支持 `SimpleStreamOptions.Reasoning`，命令行控制即可让用户在 Anthropic/Bedrock/Google 等 thinking-capable 模型上调档；selector UI 后置不影响功能可用性。
- 2026-05-20：决定 settings 中 `DefaultThinkingLevel` 用 `string?` 而非 enum 序列化。原因是后续 enum 名称变化（如增加 `Off` 值）不会破坏旧 settings 文件，且 Tau 其他 settings 字段（`TreeFilterMode`）也是同样取舍。
- 2026-05-20：决定 Steering/FollowUp 先做 Host 层可注入 input source baseline，而不是直接改造完整 TUI editor。原因是 `AgentRuntime` 已有队列语义，最小缺口是 runner seam 与 active turn 期间并发输入转发；用 `ICodingAgentTurnInputSource` 可以本地 deterministic test，生产端再用非阻塞 `Console.KeyAvailable` 避免 `Console.ReadKey` 在 turn 结束后悬挂。
- 2026-05-20：决定 RPC Mode 第一刀只做 Tau-native headless JSONL baseline，而不是一次性搬上游完整 RPC mode。原因是上游协议包含 extension UI、bash、retry/settings、session switch 和大量命令；Tau 当前最关键缺口是让外部进程可用 JSONL 驱动 runner 和现有 session/tree/settings seam。先交付 prompt / steer / follow_up / abort / state / model / compact / fork / clone / messages / commands，可以解锁 IDE/WebUi 进程嵌入的最小合同，同时不把完整上游 RPC parity 写成已完成。
- 2026-05-21：决定 Branch Summarization 第一刀接到显式 `/fork --summarize`，而不是自动挂到所有 `/resume`、`/tree --interactive` navigation 或完整 TreeSelector。原因是 Tau 当前 tree navigation 仍是命令/console baseline，显式 fork 是最稳定、可测试且不意外触发 LLM 调用的 branch switch 入口；先把 `branch_summary` JSONL entry、restore context 和 HTML timeline 固定下来，后续自动 hook / extension event / cancellation UI 可以复用同一 entry 语义。
- 2026-05-21：决定 RPC session utility 第二刀只补 `export_html` / `get_last_assistant_text` / `set_session_name`，而不是继续扩到 bash、settings UI 或 session switch。原因是这三条命令都能复用现有 HTML exporter、runner messages、session name 和 flat/tree store seam，风险低且直接对齐上游 headless client 常用 session utilities；bash/abort_bash、settings RPC、switch_session 和 extension UI 仍需要单独协议与安全边界。
- 2026-05-21：决定 RPC thinking controls 第三刀只补 `set_thinking_level` / `cycle_thinking_level`，而不是同时扩到 settings RPC 或 session switch。原因是 Tau 现有 `/thinking` 命令、runner `ThinkingLevel` 和 settings `defaultThinkingLevel` 已经固定 runtime/持久化语义；headless client 需要的是同一事实源的 RPC 入口，其他 RPC 子协议继续独立切片。
- 2026-05-21：决定 RPC cycle model 第四刀只补 `cycle_model`，复用 settings `enabledModels` scope 和 runner `SelectModel()`，不扩 runner interface。原因是 Tau 已有 `/model`、`/scoped-models` 与 settings scope 合同，RPC client 需要的是按 scope 可审计切换到 next model；settings RPC、switch_session、bash/abort_bash 和 extension UI 仍按独立协议边界后置。
- 2026-05-21：决定 RPC auto retry 第五刀只补 `set_auto_retry` / `abort_retry`，复用现有 host-level retry policy、settings retry 字段和 JSONL retry audit，不引入第二套 retry 状态。原因是上游 RPC 也只是把这两条命令委派给 session retry 开关和 retry abort；Tau 最小缺口是让 headless prompt 复用同一 rollback/retry/audit 语义。完整 settings selector/UI、bash/abort_bash、session switch 和 extension UI 继续后置。
- 2026-05-21：决定 RPC session switch 第六刀只补 `switch_session` / `get_fork_messages`，复用现有 JSONL tree session `Resume()`、flat snapshot persist 和 user message extraction，不扩展到 bash、full settings RPC 或 extension UI。原因是这两条都是上游 headless session utility 的低耦合入口：`switch_session` 只需要恢复指定 JSONL session，`get_fork_messages` 只需要列出可 fork 的 user messages；bash/abort_bash 和 full settings RPC 仍需要独立安全/配置边界。
- 2026-05-21：决定 RPC queue / auto-compaction settings controls 第七刀只补 `set_steering_mode` / `set_follow_up_mode` / `set_auto_compaction` 与 settings-backed `get_state`，不把 RPC prompt 自动 compaction 运行路径并入同一切片。原因是 queue mode 可以直接落到 `AgentRuntime` drain 语义和 settings；而 Tau 的 auto-compaction 运行触发仍由 env threshold budget 控制，RPC boolean 只能表达启停状态，不能凭空发明 threshold。完整 bash/extension UI/full settings RPC 继续后置。
- 2026-05-21：决定 RPC bash 第八刀只补 `bash` / `abort_bash` 的阻塞式 response baseline，而不是一次性搬完整 terminal subsystem 或 streamed bash event。原因是上游 RPC response 的稳定合同是 `BashResult`，Tau 先用可注入 `ICodingAgentShellRunner` 固定 output/exitCode/cancelled/truncated/fullOutputPath、并发拒绝和取消边界；stdout/stderr 分块 event、terminal UI 和 extension UI 继续后置。
- 2026-05-21：决定 RPC settings 第九刀只补 `get_settings` / `update_settings` 的 Tau-supported settings baseline，而不是宣称完整上游 settings runtime parity。原因是 Tau 当前 settings store 只承载默认模型、tree filter、retry、default thinking、enabledModels、queue mode 和 auto-compaction boolean；先固定 headless snapshot/update 合同并同步 runner runtime 状态，theme、terminal、packages、markdown、images、transport 等上游全量配置面继续按后续真实实现推进。
- 2026-05-21：决定 `/hotkeys` 第一刀只列出当前交互式 editor 的 `IKeyBindingMap`，而不是实现完整上游 app/session/tree/extension shortcut registry。原因是 Tau 已经有 `KeyBindingMap` 与 keybinding JSON override，最小可验证缺口是把当前实际绑定暴露到 slash command；全局 shortcut registry、tree/session-specific shortcut provenance 和 footer keybinding hints 依赖完整 TUI/extension runtime，继续后置。
- 2026-05-21：决定 `/reload` 第一刀只重载 Tau 当时已经存在的可变事实源：settings、JSON extension resources、prompt templates、skills 和交互式 editor keybindings。原因是上游 reload 同时覆盖 keybindings、extensions、skills、prompts、themes/context files 和 extension runtime lifecycle；当时 Tau 尚无 theme/context file loader 或 TypeScript extension runtime，先把现有 settings/resource stores 做成可验证的当前进程 reload，避免把未移植 runtime 伪装成完成。context files baseline 后续已独立补齐，theme loader、完整 TypeScript extension runtime 和 full resource selector 仍后置。
- 2026-05-21：决定 CodingAgent context files 先落 Tau-native `~/.tau` + ancestor `AGENTS.md` / `CLAUDE.md` baseline，而不是直接搬完整上游 theme/context/resource loader。原因是该切片最小用户价值是自动注入仓库协作规则，且可用本地文件系统测试完整验证；完整 theme loader、TypeScript extension runtime 和 full resource selector 继续作为后续 parity。
- 2026-05-21：决定 `/settings` 第一刀做 read-only CLI/settings summary，不做 selector UI。原因是 Tau 现有 `CodingAgentSettingsStore` 已覆盖默认模型、tree filter、retry、default thinking、queue modes、auto-compaction boolean 和 enabledModels scope，先固定可 inspect 的 settings contract；完整可编辑 selector UI 后置到 TUI/selector 层。
- 2026-05-21：决定 `/logout` 第一刀只删除本地 `auth.json` provider credential entry，而不是移植完整 OAuth selector UI 或清理所有可能的 credential 来源。原因是上游 `AuthStorage.logout(provider)` 的核心语义是删除 auth storage 中该 provider 的本地 entry；环境变量和 `models.json` credential 配置不是同一个 store，自动删除会越界且可能破坏用户外部配置。完整 OAuth selector / login-session parity 后续单独推进。
- 2026-05-21：决定 `/changelog` 第一刀只读取 Tau 本地 release notes 表，而不是移植完整上游启动 changelog、`collapseChangelog` 设置或 install/update telemetry。原因是 Tau 当前没有根级 `CHANGELOG.md`，但已有 `docs/releases/feature-release-notes.md` 作为用户可感知变更记录；先把显式命令接到这个仓库事实源，可以低风险补齐命令面，同时不引入启动副作用或网络遥测。

## 已完成切片：RPC Mode baseline

### 已完成的代码与测试改动

| 文件 | 改动 |
|------|------|
| `src/Tau.CodingAgent/Runtime/CodingAgentRpcHost.cs` | 新增 headless JSONL RPC host，支持 prompt、steer、follow_up、abort、get_state、set_model、compact、fork、clone、get_session_stats、get_messages、get_commands，并把 AgentEvent 转成 JSONL event |
| `src/Tau.CodingAgent/Program.cs` | 新增 `--mode rpc` / `--mode=rpc` 检测，RPC 模式不启用交互式 editor，直接以 Console.In / Console.Out 运行 `CodingAgentRpcHost` |
| `tests/Tau.CodingAgent.Tests/CodingAgentRpcHostTests.cs` | 新增 6 个 targeted tests，覆盖 prompt response + event stream、LF JSONL framing、steer/follow_up/abort、state/model/messages/commands、compact 持久化、invalid JSON 错误响应 |
| `README.md`、`docs/ARCHITECTURE.md`、`docs/QUALITY_SCORE.md`、`next.md`、两份 active plan | 同步 RPC baseline 与剩余完整 parity 边界 |
| `docs/histories/2026-05/20260520-2356-coding-agent-rpc-mode.md` | 记录本切片设计意图、改动范围与验证结果 |

验证通过：`dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal` 0 警告 0 错误；`dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal` 197/197 通过。

### 设计要点（避免接手 agent 重新决策）

- RPC stdout 只能输出 JSONL；因此 `--mode rpc` 不复用 `CodingAgentHost` / `InteractiveConsoleSession` 的 welcome、status 和 TUI 渲染。
- prompt 命令先返回一次 authoritative accepted response，再异步输出 agent events；stdin EOF 后 host 会等待 active prompt 完成，避免子进程过早退出。
- `steer` / `follow_up` 只在 active prompt 期间接受；普通 `prompt` 在 active turn 中需要 `streamingBehavior=steer|followUp`，避免无意并发启动多个 runner turn。
- JSONL framing 仍使用 .NET `ReadLineAsync` 的 LF/CRLF 行输入；测试固定 payload 中的 U+2028 不会被拆成多条命令。
- 当前 event DTO 是 Tau-native 简化形态，足够让 headless client 跟踪 agent/text/tool lifecycle；完整上游 event payload、extension UI request/response、bash execution event 和 command provenance 后置。

## 已完成切片：Branch Summarization baseline

### 已完成的代码与测试改动

| 文件 | 改动 |
|------|------|
| `src/Tau.CodingAgent/Runtime/CodingAgentCompactionResult.cs` | 新增 `CodingAgentBranchSummaryResult`，携带摘要、entry 数、估算 token、read files、modified files 与 `fromHook` |
| `src/Tau.CodingAgent/Runtime/ICodingAgentRunner.cs` | 新增 `SummarizeBranchAsync(...)`，让 router 能显式请求当前模型摘要离开的 branch |
| `src/Tau.CodingAgent/Runtime/RuntimeCodingAgentRunner.cs` | 复用 `StreamFunctions.CompleteSimpleAsync(...)` 实现 branch summary prompt，并接入 file operation tracking |
| `src/Tau.CodingAgent/Runtime/CodingAgentCompactionMessages.cs` | 新增 branch summary prompt / preamble / restore context message |
| `src/Tau.CodingAgent/Runtime/CodingAgentTreeSessionStore.cs` | 新增 JSONL `branch_summary` entry、`fromId/readFiles/modifiedFiles` 字段、abandoned branch message collection、branch restore summary context |
| `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs` | `/fork <entry-id> [--summarize [instructions]]` 改为异步路径，显式 summary 时先生成摘要再 branch |
| `src/Tau.CodingAgent/Runtime/CodingAgentCommandCatalog.cs` | 更新 `/fork` usage |
| `src/Tau.CodingAgent/Runtime/CodingAgentHtmlSessionExporter.cs` | HTML timeline 和 branch outline 支持 `branch_summary`，展示 summary、read files、modified files |
| `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs` | 新增 targeted test 覆盖 `/fork --summarize`、JSONL entry、restore context、`/tree --search` 和 HTML export |
| `tests/Tau.CodingAgent.Tests/FakeCodingAgentRunner.cs`、`tests/Tau.Agent.Tests/RuntimeDelegationAgentRunnerTests.cs`、`tests/Tau.WebUi.Tests/FakeWebUiRunner.cs` | 补齐新 runner interface 成员，避免 fake runner drift |
| `README.md`、`docs/ARCHITECTURE.md`、`docs/QUALITY_SCORE.md`、`next.md`、两份 active plan | 同步 branch summary baseline 与剩余完整 parity 边界 |
| `docs/histories/2026-05/20260521-0100-coding-agent-branch-summarization.md` | 记录本切片设计意图、改动范围与验证结果 |

验证通过：`dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal` 0 警告 0 错误；`dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal` 198/198 通过；`dotnet test tests\Tau.Agent.Tests\Tau.Agent.Tests.csproj --no-restore --verbosity minimal` 54/54 通过；`dotnet build tests\Tau.WebUi.Tests\Tau.WebUi.Tests.csproj --no-restore --verbosity minimal` 0 警告 0 错误。

### 设计要点（避免接手 agent 重新决策）

- 当前只在显式 `/fork --summarize` 下触发 LLM 调用，普通 `/fork` 行为保持不变，避免 branch navigation 产生不可预期的 token 成本和延迟。
- 被摘要内容来自 old leaf 回溯到 common ancestor 的 abandoned branch entries，按时间顺序转回 runtime messages；tool result 不参与摘要，已有 compaction / branch summary entries 会转成上下文 message。
- `branch_summary` entry 挂在目标 entry 下，`parentId = targetId`，`fromId = targetId ?? root`，保持与上游 `branchWithSummary(branchFromId, ...)` 的核心语义一致。
- Branch restore 会把 `branch_summary` 转成 `Branch summary from ...` user message 注入 runtime context，避免切回目标 branch 后丢掉刚离开的 branch 决策。
- HTML export 只做 Tau-native timeline/outline rendering baseline；完整上游 renderer、自动 hook、extension event 和 cancellation UI 后置。

## 已完成切片：RPC session utility baseline

### 已完成的代码与测试改动

| 文件 | 改动 |
|------|------|
| `src/Tau.CodingAgent/Runtime/CodingAgentRpcHost.cs` | 新增 `export_html`、`get_last_assistant_text`、`set_session_name` RPC 命令；HTML 导出复用现有 tree-aware transcript exporter，session name 设置会同步 flat JSON 和 JSONL tree |
| `tests/Tau.CodingAgent.Tests/CodingAgentRpcHostTests.cs` | 新增 4 个 targeted tests，覆盖 HTML 输出文件与响应 path、last assistant text 的文本/null 形态、session name 对 runner / flat store / tree store 的持久化，以及 active prompt 期间拒绝导出/改名 |
| `README.md`、`docs/ARCHITECTURE.md`、`docs/QUALITY_SCORE.md`、`next.md`、两份 active plan | 同步 RPC session utility baseline 与剩余完整 RPC parity 边界 |
| `docs/histories/2026-05/20260521-0200-coding-agent-rpc-session-utilities.md` | 记录本切片设计意图、改动范围与验证结果 |

验证通过：`dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal` 202/202 通过。

### 设计要点（避免接手 agent 重新决策）

- `export_html` 只导出 HTML，不复用 `/export` 的 `.jsonl` / flat JSON 多格式分支；这是按上游 RPC command type 的窄语义实现。
- tree session 存在时，RPC export 会先 `SyncFromRunner()`，再使用 current branch snapshot、tree summary 和 `ExportCurrentBranchText()`，因此 HTML 继续包含 branch outline、cwd/parent metadata 和内嵌 JSONL。
- `get_last_assistant_text` 使用和 `/copy` 相同的语义：只取最后一条 assistant message 的 `TextContent`，多个 text block 以空行拼接，没有可用文本时返回 JSON null。
- `set_session_name` 对 `name` 做 trim 后写入 runner，并调用同一个 `PersistSession()` seam；当前不把空字符串当作 clear 命令，RPC clear/session switch 后续按上游完整 session utility parity 单独处理。

## 已完成切片：RPC thinking controls baseline

### 已完成的代码与测试改动

| 文件 | 改动 |
|------|------|
| `src/Tau.CodingAgent/Runtime/CodingAgentRpcHost.cs` | 新增 `set_thinking_level` 与 `cycle_thinking_level` RPC 命令；复用 runner `ThinkingLevel` 和 settings `DefaultThinkingLevel`；active prompt 期间拒绝变更；`cycle` 到 off 时返回显式 `data: null` |
| `tests/Tau.CodingAgent.Tests/CodingAgentRpcHostTests.cs` | 新增 2 个 targeted tests，覆盖 set/cycle settings 持久化、`get_state.thinkingLevel`、`data:null` 响应、invalid level 错误和 active prompt 拒绝 |
| `README.md`、`docs/ARCHITECTURE.md`、`docs/QUALITY_SCORE.md`、`next.md`、两份 active plan、`docs/releases/feature-release-notes.md` | 同步 RPC thinking controls baseline、223 个 CodingAgent tests，以及完整 RPC parity 剩余边界 |
| `docs/histories/2026-05/20260521-0900-coding-agent-rpc-thinking-controls.md` | 记录本切片设计意图、改动范围与验证结果 |

验证通过：`dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal` 0 警告 0 错误；`dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal` 223/223 通过。

### 设计要点（避免接手 agent 重新决策）

- RPC thinking controls 复用 CLI `/thinking` 与 settings `defaultThinkingLevel` 语义，不新增第二套 reasoning 状态。
- `set_thinking_level` 接受 `off/none/minimal/low/medium/med/high/xhigh/extrahigh/extra-high`，无效值返回 RPC error。
- `cycle_thinking_level` 顺序沿用 Tau CLI：`off -> low -> medium -> high -> xhigh -> off`；`minimal` 只在显式设置时保留，cycle 时进入 `low`。
- active prompt 期间拒绝变更，保持和当前 `set_model` / `export_html` / `set_session_name` 等 RPC safety pattern 一致。
- 本切片当时不宣称 retry/settings RPC、session switch、bash/extension UI 或完整 command provenance 已完成。

## 已完成切片：RPC cycle model baseline

### 已完成的代码与测试改动

| 文件 | 改动 |
|------|------|
| `src/Tau.CodingAgent/Runtime/CodingAgentRpcHost.cs` | 新增 `cycle_model` RPC 命令；按 settings `enabledModels` 有序 scope 或全部可用模型选择下一个模型，复用 runner `SelectModel()`，持久化默认 provider/model，同步 flat/tree session；候选不足两个时返回显式 `data:null`，active prompt 期间拒绝切换 |
| `tests/Tau.CodingAgent.Tests/CodingAgentRpcHostTests.cs` | 新增 3 个 targeted tests，覆盖 scoped cycle + settings 保存 + thinkingLevel/isScoped 响应、单模型 scope explicit null、active prompt 拒绝 |
| `README.md`、`docs/ARCHITECTURE.md`、`docs/QUALITY_SCORE.md`、`next.md`、两份 active plan、`docs/releases/feature-release-notes.md` | 同步 RPC cycle model baseline、226 个 CodingAgent tests，以及剩余完整 RPC parity 边界 |
| `docs/histories/2026-05/20260521-1000-coding-agent-rpc-cycle-model.md` | 记录本切片设计意图、改动范围与验证结果 |

验证通过：`dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal` 0 警告 0 错误；`dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal` 226/226 通过。

### 设计要点（避免接手 agent 重新决策）

- `cycle_model` 复用 settings `enabledModels`，不在 RPC host 内发明第二套 scope 存储；scope 缺失/null 时使用全部 `GetProviders()` + `GetModels()` 可用模型。
- settings scope 中无效或漂移的模型引用会被跳过；如果显式 scope 全部无效，RPC 回退到全部可用模型，避免手工坏 settings 让 headless client 卡死。
- 命令保存默认 provider/model，返回 `{ model, thinkingLevel, isScoped }`；thinking level 只报告当前值，不在 model cycle 中单独改档。
- active prompt 期间拒绝切换，保持和 `set_model` / thinking controls / session utility RPC safety pattern 一致。
- 本切片当时不宣称 retry/settings RPC、session switch、bash/extension UI、queue modes 或 full command provenance 已完成。

## 已完成切片：RPC auto retry baseline

### 已完成的代码与测试改动

| 文件 | 改动 |
|------|------|
| `src/Tau.CodingAgent/Runtime/CodingAgentRpcHost.cs` | 新增 `set_auto_retry` / `abort_retry` RPC 命令；RPC prompt 现在复用 host-level retry classifier、rollback snapshot、settings retry policy 和 JSONL retry audit；retry 成功时同步成功 attempt messages 后写 retry end，取消 pending retry delay 时写 `Retry cancelled` end audit 并恢复失败前 snapshot |
| `src/Tau.CodingAgent/Program.cs` | RPC mode 构造 `CodingAgentRpcHost` 时传入 `CodingAgentRetryOptions.FromSettingsOrEnvironment(settings)`，保持 CLI 与 headless 入口同一 retry 默认来源 |
| `tests/Tau.CodingAgent.Tests/CodingAgentRpcHostTests.cs` | 新增 4 个 targeted tests，覆盖 `set_auto_retry` settings 持久化与 `get_state.autoRetryEnabled`、关闭 retry、retryable prompt rollback/retry audit，以及 `abort_retry` 取消 pending delay |
| `README.md`、`docs/ARCHITECTURE.md`、`docs/QUALITY_SCORE.md`、`next.md`、两份 active plan、`docs/releases/feature-release-notes.md` | 同步 RPC auto retry baseline、230 个 CodingAgent tests，以及剩余完整 RPC parity 边界 |
| `docs/histories/2026-05/20260521-1100-coding-agent-rpc-auto-retry.md` | 记录本切片设计意图、改动范围与验证结果 |

验证通过：`dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal` 0 警告 0 错误；`dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal` 230/230 通过。

### 设计要点（避免接手 agent 重新决策）

- `set_auto_retry` 不新增独立 RPC settings store：开启时优先复用当前 settings 中有效 retry attempts/base delay，缺失或为 0 时回到 Tau 默认 `3` 次、`2000ms` base delay；关闭时写入 settings retry `0/0`。
- RPC prompt retry 复用 CLI host 的 rollback 思路：每次 retry 前恢复 prompt 前 snapshot，成功 attempt 才进入 flat/tree session；失败、耗尽或取消只保留 retry audit，不把失败输入落盘。
- `abort_retry` 只取消 pending retry delay，不等同于 `abort` 当前 agent turn；这与上游 `abortRetry()` 边界一致，也避免 headless client 误杀正在运行的模型请求。
- 本切片当时不宣称完整 settings RPC、session switch、bash/extension UI、queue modes 或 full command provenance 已完成。

## 已完成切片：RPC session switch / fork messages baseline

### 已完成的代码与测试改动

| 文件 | 改动 |
|------|------|
| `src/Tau.CodingAgent/Runtime/CodingAgentTreeSessionStore.cs` | 新增 `CodingAgentForkMessage` 与 `GetUserMessagesForForking()`，从 JSONL tree entries 中提取 user message 的 `{ entryId, text }`，只拼接 text content，忽略图片、工具结果等非文本内容 |
| `src/Tau.CodingAgent/Runtime/CodingAgentRpcHost.cs` | 新增 `switch_session` / `get_fork_messages` RPC 命令；`switch_session` 复用 tree session `Resume()` 恢复指定 JSONL session、同步 runner snapshot 并持久化 flat session；`get_fork_messages` 先从 runner 同步 tree，再返回可 fork 的 user messages |
| `tests/Tau.CodingAgent.Tests/CodingAgentRpcHostTests.cs` | 新增 3 个 targeted tests，覆盖 JSONL session restore、flat snapshot 持久化、active prompt 拒绝切换，以及 fork selector user message entry id / text extraction |
| `README.md`、`docs/ARCHITECTURE.md`、`docs/QUALITY_SCORE.md`、`next.md`、两份 active plan、`docs/releases/feature-release-notes.md` | 同步 RPC session switch / fork messages baseline、233 个 CodingAgent tests，以及剩余完整 RPC parity 边界 |
| `docs/histories/2026-05/20260521-1200-coding-agent-rpc-session-switch.md` | 记录本切片设计意图、改动范围与验证结果 |

验证通过：`dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal` 233/233 通过；`powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` 通过，测试计数为 `Tau.Ai.Tests` 194、`Tau.Agent.Tests` 54、`Tau.Tui.Tests` 56、`Tau.CodingAgent.Tests` 233、`Tau.Pods.Tests` 32；`git diff --check` 退出码 0，仅有既有 CRLF normalization warnings。

### 设计要点（避免接手 agent 重新决策）

- `switch_session` 只在没有 active prompt 时接受；运行中切换会拒绝，避免 runner messages、flat snapshot 和 JSONL tree 观察到半更新状态。
- `switch_session` 不新增第二套 session loader，而是复用 `CodingAgentTreeSessionController.Resume(sessionPath)`，再用 current branch snapshot 恢复 runner，最后走现有 `PersistSession()` 同步 flat JSON。
- `get_fork_messages` 对齐上游 `getUserMessagesForForking()` 的核心语义：遍历整棵 session entries，而不是只看 current branch；只返回 user message；array content 中只拼接 text content。
- 本切片不扩展到 bash/abort_bash、full settings RPC、extension UI sub-protocol 或完整 command provenance；这些仍需要独立安全和协议边界。

## 已完成切片：RPC queue / auto-compaction settings controls baseline

### 已完成的代码与测试改动

| 文件 | 改动 |
|------|------|
| `src/Tau.Agent/Runtime/AgentQueueMode.cs` | 新增 `AgentQueueMode.All` / `OneAtATime`，作为 steering/follow-up drain 策略的共享 enum |
| `src/Tau.Agent/Runtime/AgentRuntime.cs` | `SteeringMode` / `FollowUpMode` 默认 `OneAtATime`；steering/follow-up queue drain 支持 all vs one-at-a-time，并避免本轮已 drain 的 steering message 触发无输入的额外 LLM turn |
| `src/Tau.CodingAgent/Runtime/CodingAgentSettingsStore.cs` | settings snapshot/document 新增 `SteeringMode`、`FollowUpMode`、`AutoCompactionEnabled`；读取旧 `queueMode` 时迁移到 `steeringMode`；保存时保留其他 settings 字段 |
| `src/Tau.CodingAgent/Runtime/CodingAgentRpcHost.cs` | 新增 `set_steering_mode` / `set_follow_up_mode` / `set_auto_compaction`；active prompt 期间拒绝变更；`get_state` 返回 runner/settings backed `steeringMode`、`followUpMode`、`autoCompactionEnabled` |
| `src/Tau.CodingAgent/Program.cs` | 启动时把 settings queue modes 恢复到 runner；interactive host 的自动 compaction 运行触发会尊重 settings `autoCompactionEnabled=false`，但 threshold budget 仍来自 env |
| `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs` | `/reload` 同步 settings queue modes 到当前 runner；`/settings` summary 展示 steering/follow-up mode 与 auto-compaction 设置 |
| `tests/Tau.Agent.Tests/AgentRuntimeQueueModeTests.cs` | 覆盖 steering/follow-up `all` 与 `one-at-a-time` drain 语义，防止 queue mode 引入空转 LLM turn |
| `tests/Tau.CodingAgent.Tests/CodingAgentRpcHostTests.cs`、`tests/Tau.CodingAgent.Tests/CodingAgentSettingsStoreTests.cs`、`tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs` | 覆盖 RPC settings 持久化、`get_state` 状态、invalid mode、active prompt 拒绝、旧 `queueMode` 迁移、settings 字段保留和 settings/reload 文案 |
| `README.md`、`docs/ARCHITECTURE.md`、`docs/QUALITY_SCORE.md`、`next.md`、两份 active plan、`docs/releases/feature-release-notes.md` | 同步 RPC queue/auto-compaction controls baseline、238 个 CodingAgent tests、58 个 Agent tests，以及完整 bash/extension/full settings RPC 仍未完成的边界 |
| `docs/histories/2026-05/20260521-1952-coding-agent-rpc-queue-auto-compaction.md` | 记录本切片设计意图、改动范围与验证结果 |

验证通过：`dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal` 0 警告 0 错误；`dotnet test tests\Tau.Agent.Tests\Tau.Agent.Tests.csproj --no-restore --verbosity minimal` 58/58 通过；`dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal` 238/238 通过；`powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` 通过，测试计数为 `Tau.Ai.Tests` 194、`Tau.Agent.Tests` 58、`Tau.Tui.Tests` 56、`Tau.CodingAgent.Tests` 238、`Tau.Pods.Tests` 32；`git diff --check` 退出码 0，仅有既有 CRLF normalization warnings。

### 设计要点（避免接手 agent 重新决策）

- queue mode 不是只写 settings：`RuntimeCodingAgentRunner` 把模式转发到 `AgentRuntime`，RPC 命令会立即影响当前 runner。
- `OneAtATime` 是默认值，对齐上游 settings default；旧 `queueMode` 只作为读取迁移来源，保存时写 `steeringMode`。
- `set_auto_compaction` 保存 boolean 状态并让 `get_state.autoCompactionEnabled` 反映 settings；Tau 的实际自动 compaction 触发仍需要 `TAU_CODING_AGENT_AUTO_COMPACT_TOKENS` 提供 threshold budget。
- 本切片不把 RPC prompt 自动 compaction hook 接入同一刀，避免 retry rollback snapshot 与 compaction 写入顺序交错造成 session/JSONL 不一致；完整 compaction extension events/cancellation UI 继续独立切片。

## 已完成切片：Hotkeys listing baseline

### 已完成的代码与测试改动

| 文件 | 改动 |
|------|------|
| `src/Tau.CodingAgent/Runtime/CodingAgentHotkeysFormatter.cs` | 新增当前 editor keybinding formatter，按 action 分组输出 action 名、按键组合和说明，并隐藏已禁用 binding |
| `src/Tau.CodingAgent/Runtime/CodingAgentCommandCatalog.cs` | 新增 `/hotkeys` 命令定义，让 `/help` 与 usage 错误共用 catalog |
| `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs` | 新增 `HandleHotkeysCommand`，只读取注入的 `IKeyBindingMap`，不调用 runner；无 editor 时返回 unavailable |
| `src/Tau.CodingAgent/Runtime/CodingAgentHost.cs` | 接收 host 传入的 `IKeyBindingMap` 并注入 router |
| `src/Tau.CodingAgent/Program.cs` | 生产入口把交互式 editor 的 `KeyBindings` 传入 host；print/RPC/redirected 模式不创建 editor，因此不会暴露 hotkeys |
| `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs` | 新增 3 个 targeted tests，覆盖自定义 binding 输出、`EditorAction.None` 禁用默认 binding、无 editor 返回 unavailable 和参数 usage |
| `README.md`、`docs/ARCHITECTURE.md`、`docs/QUALITY_SCORE.md`、`next.md`、两份 active plan | 同步 hotkeys baseline 与完整 shortcut registry 仍未完成的边界 |
| `docs/histories/2026-05/20260521-0300-coding-agent-hotkeys-command.md` | 记录本切片设计意图、改动范围与验证结果 |

验证通过：`dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal` 0 警告 0 错误；`dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal` 205/205 通过。

### 设计要点（避免接手 agent 重新决策）

- `/hotkeys` 显示的是当前 `InteractiveInputEditor.KeyBindings`，不是静态默认表；因此自定义 keybinding JSON、覆盖默认 action 和 `action: "None"` 禁用默认绑定都会体现在输出里。
- formatter 只关心 `IKeyBindingMap.Bindings` 的当前事实，不直接读取 `TAU_CODING_AGENT_KEYBINDINGS_FILE`，避免 command router 再建一条配置加载路径。
- print mode、RPC mode 和 redirected stdin/stdout 不创建 editor，`/hotkeys` 在这些模式下返回 unavailable；这样不会把不存在的运行时快捷键伪装成可用。
- 完整上游 app/session/tree/extension shortcut registry、footer hints 和 shortcut provenance 仍属于后续 TUI/extension runtime parity。

## 已完成切片：Reload command baseline

### 已完成的代码与测试改动

| 文件 | 改动 |
|------|------|
| `src/Tau.Tui/Runtime/InteractiveInputEditor.cs` | 允许替换当前 `IKeyBindingMap`，供 `/reload` 热更新交互式 editor keybindings |
| `src/Tau.CodingAgent/Runtime/ICodingAgentRunner.cs` | 新增 `RefreshSkills(...)`，让命令层可请求 runner 刷新 skill inventory |
| `src/Tau.CodingAgent/Runtime/RuntimeCodingAgentRunner.cs` | 对生成 system prompt 的 runner 支持按最新 skills 重建 prompt；自定义 system prompt 不被覆盖 |
| `src/Tau.CodingAgent/Runtime/CodingAgentExtensionResourceState.cs` | 新增 extension-contributed prompt/skill resource paths 的当前进程状态容器 |
| `src/Tau.CodingAgent/Runtime/CodingAgentPromptTemplateStore.cs`、`src/Tau.CodingAgent/Runtime/CodingAgentSkillStore.cs` | 支持从动态 provider 合并最新 extension-contributed prompt/skill paths |
| `src/Tau.CodingAgent/Runtime/CodingAgentCommandCatalog.cs` | 新增 `/reload` 命令定义，让 `/help` 与 usage 错误共用 catalog |
| `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs` | 新增 `HandleReloadCommand`，重读 settings、extension status/resources、prompts、skills、context files 和 keybindings；context files baseline 已完成，themes 仍明确输出 not implemented |
| `src/Tau.CodingAgent/Runtime/CodingAgentHost.cs`、`src/Tau.CodingAgent/Program.cs` | 生产入口注入 `CodingAgentExtensionResourceState` 和 keybinding reload callback；prompt/skill stores 使用 extension resource state 动态路径 |
| `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs` | 新增 2 个 targeted tests，覆盖 reload settings/resources/skills/keybindings 和额外参数 usage |
| `tests/Tau.CodingAgent.Tests/FakeCodingAgentRunner.cs`、`tests/Tau.Agent.Tests/RuntimeDelegationAgentRunnerTests.cs`、`tests/Tau.WebUi.Tests/FakeWebUiRunner.cs` | 补齐 runner interface 新成员，避免 fake runner drift |
| `README.md`、`docs/ARCHITECTURE.md`、`docs/QUALITY_SCORE.md`、`next.md`、两份 active plan | 同步 `/reload` baseline 与 context files baseline；theme loader、完整 TypeScript extension runtime 和 full resource selector 仍未完成 |
| `docs/histories/2026-05/20260521-0400-coding-agent-reload-command.md` | 记录本切片设计意图、改动范围与验证结果 |

验证通过：`dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal` 0 警告 0 错误；`dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal` 207/207 通过。

### 设计要点（避免接手 agent 重新决策）

- `/reload` 重读 settings 后会立即同步当前 host retry policy、runner thinking level 和 runner queue mode，不需要重启 CLI。
- extension resources 通过 `CodingAgentExtensionResourceState` 更新，prompt/skill stores 每次 `Load()` 读取动态 provider，因此不需要重建 stores。
- `RuntimeCodingAgentRunner.RefreshSkills(...)` 只刷新 Tau 生成的 system prompt；如果 runner 构造时传入自定义 `systemPromptOverride`，reload 不覆盖用户自定义 prompt。
- keybindings 只在交互式 editor 存在时可重载；print/RPC/redirected 模式没有 editor，会返回 `keybindings: unavailable`。
- 当前 reload 会输出 `context files: N, runner prompt refreshed|unchanged`，并继续明确输出 `themes: not implemented`。完整上游 theme loader、extension runtime lifecycle events、TypeScript extension runtime 和 full resource selector 仍属于后续 parity。
