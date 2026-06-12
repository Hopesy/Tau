## [2026-06-12 01:03] | Task: CodingAgent CLI session flag baseline

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI on Windows PowerShell`

### User Query

> 接续当前 handoff，继续 Tau `GOAL.md` / pi-mono parity 迁移，关闭一个有边界的 CodingAgent session CLI 缺口，并同步 plan / quality / history。

### Changes Overview

**Scope:** `Tau.CodingAgent`、相邻 WebUi 持久化 helper、验证脚本、parity 文档。

**Key Actions:**

* **CLI parser/runtime**: `CodingAgentCliArguments` 现在捕获 `--session <path>` 和 `--session=<path>`，不再把 `--session` 当作只吞掉的 known option。
* **Session target resolution**: 新增 `CodingAgentSessionTarget`，让 `Program.cs` 在 host 启动前决定显式 session 目标。显式 `.jsonl` 路径使用 `CodingAgentTreeSessionController` 并优先 tree snapshot；显式非 `.jsonl` 路径使用 `CodingAgentSessionStore`；默认 env/path 行为继续兼容既有 flat + tree fallback。
* **Regression coverage**: 新增 parser tests 和 session-target tests，覆盖显式 flat path、显式 JSONL path、默认 tree empty 时保留 flat fallback。
* **Windows persistence fix**: 完整 CodingAgent/WebUi 验证暴露当前 Windows 临时目录下 `File.Replace(temp, dest, null)` 的 access denied 行为。`WebChatStore` 和 `WebArtifactStore` 现在改为同目录 temp write 后 `File.Move(..., overwrite: true)`。
* **Validation stability**: `scripts/verify-dotnet.ps1` 现在给 build/test 命令传 `-m:1`，匹配仓库“不并行写同一 `bin`/`obj`”的验证规则，并避免观察到的 `Tau.CodingAgent` build 0 warning / 0 error 失败。
* **Browser fixture boundary**: `WebUiBrowserFixture` 不再在 `dotnet test` 进程内执行 `playwright install chromium --with-deps`，避免受限 Windows 环境下 Playwright OS dependency install script 的 WMI access denied 或下载阻塞。缺 Chromium cache 时现在快速给出一次性手动安装命令。
* **Sandbox validation mode**: `scripts/verify-dotnet.ps1 -SkipWebUiBrowserTests` 会在 `Tau.WebUi.Tests` 阶段显式排除 `WebUiBrowserFlowTests`，用于浏览器启动受限环境中收集非浏览器 WebUi gate 信号；默认不带该开关时仍运行完整 WebUi browser tests。
* **Model CLI flags**: 同一 CodingAgent top-level CLI parity 线继续补 `--models` / `--list-models` / `--verbose`。`--list-models` 在打开 session 前输出 provider/model/context/max-out/thinking/images 表并退出；`--models` 支持 exact id、`:thinking` 后缀和 `*` / `?` 通配展开，作为 per-process scope 覆盖 settings `enabledModels` 参与启动模型选择、interactive model cycling 和 RPC `cycle_model`，但不写回 settings；`--verbose` 会覆盖 `quietStartup` 显示 model scope notice。
* **Session directory / continue flags**: 同一 CodingAgent top-level CLI parity 线继续补 `--continue` / `-c` 与 `--session-dir <dir>` / `--session-dir=<dir>`。显式 session-dir 会进入 JSONL tree session startup 路径：默认创建新的 per-run session，组合 `--continue` 时选择该目录中最近有效 JSONL session，目录为空则新建；无 session-dir 的 `--continue` 优先选择默认 tree session/search 结果，找不到则创建默认 tree session；无 session flags 的 flat + tree fallback 不变。
* **Docs sync**: 更新 `GOAL.md`、`next.md`、`docs/QUALITY_SCORE.md` 和 active parity plans，只把显式 `--session <path>` 文件路径 baseline、`--continue` / `--session-dir` 本地 JSONL startup selection baseline 与 `--models` / `--list-models` / `--verbose` 启动期 model flag baseline 标为本地关闭，保留 broader upstream session manager、完整 fuzzy scope、真实 e2e 与 package/bin identity 缺口。

### Design Intent

上游 `--session` 的低歧义子合同是：参数看起来像路径或以 `.jsonl` 结尾时，直接解析成 session 文件并打开。Tau 已经具备 flat session store 和 JSONL tree session store，因此本轮选择解析 flag 并集中 startup target 选择，而不是提前扩大完整 SessionManager surface。

这先把 `--resume`、`--fork`、session id lookup、global search、cross-project fork prompt 明确留作后续工作，避免把启动期 session 文件/目录 baseline 夸成完整 parity；同一任务后续继续关闭了 direct fork、本地 id-prefix lookup 和本地 `--resume` selector baseline，但全局 lookup / cross-project prompt / 完整 schema 仍保持 open。

WebUi store 和验证脚本改动来自验证失败，不是功能扩张。两者都是窄基础设施修复，用来让当前 Windows gate 行为可诊断、可复现。

`--models` 选择做成 per-process override，而不是保存进 `coding-agent-settings.json`，是为了对齐上游 CLI flag 的启动期 scope 语义：命令行参数只影响本次启动的 model selection / cycling，不改用户持久设置。`--list-models` 则提前到 session target 打开之前处理，避免只读列表命令产生 session 文件副作用。

`--session-dir` 和 `--continue` 选择继续落在 `CodingAgentSessionTarget`，是为了把 startup session selection 集中在一个地方：Tau 现有默认 flat + tree fallback 不变，显式 session-dir 则按上游 `SessionManager.create/continueRecent` 的方向进入 JSONL tree session 目录。后续 `--fork`、本地 id-prefix lookup 和本地 `--resume` selector 也沿用这个集中入口；跨项目 fork prompt、全局 lookup 和更完整 schema 继续保持 open。

### Files Modified

* `src/Tau.CodingAgent/Program.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentInitialMessageBuilder.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentModelAvailability.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentModelListFormatter.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentSessionTarget.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentStartupResumeResolver.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentHost.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentRpcHost.cs`
* `src/Tau.WebUi/Services/WebArtifactStore.cs`
* `src/Tau.WebUi/Services/WebChatStore.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCliModelScopeTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentInitialMessageBuilderTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentModelListFormatterTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentSessionTargetTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentStartupResumeResolverTests.cs`
* `tests/Tau.WebUi.Tests/WebUiBrowserFixture.cs`
* `scripts/verify-dotnet.ps1`
* `GOAL.md`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`

### Validation

* `dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal -m:1` 通过，0 warning / 0 error。
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --filter "CodingAgentInitialMessageBuilderTests|CodingAgentSessionTargetTests" --no-restore --verbosity minimal -m:1` 通过 39/39。
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --filter "CodingAgentInitialMessageBuilderTests|CodingAgentModelListFormatterTests|CodingAgentCliModelScopeTests" --no-restore --verbosity minimal -m:1` 通过 46/46。
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --filter "CodingAgentInitialMessageBuilderTests|CodingAgentSessionTargetTests" --no-restore --verbosity minimal -m:1` 在 `--continue` / `--session-dir` 回归加入后通过 50/50。
* `dotnet run --project src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-build -- --list-models gemini` 通过，输出 model list 表。
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --filter "FullyQualifiedName~SendMessageStreamAsync_PersistsAssistantAndBuildsAttachmentPrompt" --no-restore --verbosity minimal -m:1` 在 `WebChatStore` 写入路径修复后通过 1/1。
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal -m:1` 在 `--continue` / `--session-dir` 回归加入后通过 594/594。
* `dotnet test tests\Tau.WebUi.Tests\Tau.WebUi.Tests.csproj --filter "ArtifactsTool|ArtifactEndpoints" --no-restore --verbosity minimal -m:1` 在 `WebArtifactStore` 写入路径修复后通过 5/5。
* `dotnet test tests\Tau.WebUi.Tests\Tau.WebUi.Tests.csproj --filter "FullyQualifiedName!~WebUiBrowserFlowTests" --no-restore --verbosity minimal -m:1` 通过 53/53。
* 完整 `Tau.WebUi.Tests` 在最终复跑时通过 61/61；旧的 `spawn EPERM` 浏览器启动限制没有在最终 gate 中复现。此前 detailed/blame run 已确认旧症状来自 fixture 在测试进程内执行 `playwright install chromium --with-deps`，该路径触发 `install_media_pack.ps1` 的 `Get-WmiObject : Access denied` 并导致 testhost 崩溃/挂起；本轮已移除测试内 install，后续缺 Chromium cache 或浏览器启动受限时会快速给出明确诊断。
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` 最终复跑通过。计数：Ai 287、Agent 123、Tui 251、CodingAgent 615、WebUi 61、Pods 216。
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore -SkipWebUiBrowserTests` 也曾作为受限浏览器环境兜底通过；该开关不替代默认完整 project gate，也不能关闭 WebUi release/static smoke。

### Continuation: `--fork <path>` Path-Only Baseline

**Key Actions:**

* **CLI parser/runtime**: `CodingAgentCliArguments` 现在捕获 `--fork <path>` / `--fork=<path>` 和 `--resume` / `-r`。`--fork` 缺值、空 inline 值或后继 token 是 option / `@file` 时会直接报 `error: --fork requires an argument`，避免误把 `--continue` 等 option 当成 session 路径。
* **Startup conflict diagnostics**: `Program.cs` 现在拒绝 `--fork` 与 `--session`、`--continue`、`--resume` 组合；同任务后续已把 `--resume` 接到本地交互 selector。
* **Fork target resolution**: `CodingAgentSessionTarget.Resolve(..., forkSessionPath)` 要求源 session 文件存在，使用 `CodingAgentTreeSessionStore.ExportCurrentBranch(...)` 把源 JSONL tree session 当前 branch 复制到新的 JSONL session；目标目录优先使用显式 `--session-dir`，否则使用默认 tree session 目录旁的 `coding-agent-sessions`。fork 后返回 tree session controller 并优先 tree snapshot。
* **Regression coverage**: parser tests 覆盖 `--fork` / inline `--fork=`、缺值、空值、option-as-value、`--resume` / `-r`；session target tests 覆盖默认 fork 目录、显式 session-dir、missing source 和 `parentSession` metadata。
* **Docs sync**: 更新 `GOAL.md`、`next.md`、`docs/QUALITY_SCORE.md` 和 active parity plan/matrix，只把 direct JSONL path fork baseline 标成本地关闭，保留 session id / partial id local/global lookup、cross-project fork prompt、完整 upstream `SessionManager` schema 和真实 e2e 缺口。

**Design Intent:**

上游 `--fork` 支持 path、partial UUID lookup、local/global session lookup 和跨项目 fork prompt。Tau 当前已经有 JSONL tree session store 和 `ExportCurrentBranch(...)`，因此本次只领取低歧义的 direct path branch-copy 子合同，把更宽的 lookup/selector/prompt 语义显式留在 parity backlog 中。

### Additional Validation

* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --filter "CodingAgentInitialMessageBuilderTests|CodingAgentSessionTargetTests" --no-restore --verbosity minimal -m:1` 在 `--fork` 回归加入后通过 60/60。
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal -m:1` 在 `--fork` 回归加入后通过 604/604。
* CLI fork smoke 通过：先用 `--session-dir <src> --help` 创建源 JSONL，再用 `--fork <source> --session-dir <forks> --help` 创建 fork 目标，最终 `sourceCount=1` / `forkCount=1`。
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore -SkipWebUiBrowserTests` 在 `--fork` 回归加入后通过。计数：Ai 287、Agent 123、Tui 251、CodingAgent 604、WebUi 非浏览器 53、Pods 216。该命令仍是当前沙箱内非浏览器 gate，不替代完整 WebUi browser/release/static smoke。

### Continuation: Session ID Prefix Lookup Baseline

**Key Actions:**

* **Session reference resolution**: `CodingAgentResumeSessionInfo` 现在暴露 JSONL header 的 `SessionId`，用于启动期 session lookup。
* **Local id-prefix lookup**: `CodingAgentSessionTarget` 现在把带路径分隔符或扩展名的 `--session` / `--fork` 参数继续视为文件路径；无扩展、非路径参数按 JSONL session id 前缀解析。
* **Session-dir priority**: id-prefix lookup 会先查显式 `--session-dir` 中的 JSONL sessions，再查当前可发现的默认/clone session 列表，匹配顺序保持最近修改优先。
* **Open/fork behavior**: `--session <prefix>` 匹配后打开 JSONL tree session 并优先 tree snapshot；`--fork <prefix>` 匹配后复用 current-branch export 生成新 JSONL session，并保留 `parentSession` metadata。
* **Regression coverage**: session target tests 覆盖 matched open、missing prefix diagnostic、matched fork copy 和 `parentSession`。
* **Docs sync**: 更新 `GOAL.md`、`next.md`、`docs/QUALITY_SCORE.md` 和 active parity plan/matrix，只把本地/session-dir 可发现 JSONL id-prefix lookup 标成本地关闭，保留 global lookup、cross-project fork prompt 和完整 upstream `SessionManager` schema/selection 缺口。

**Design Intent:**

上游 `resolveSessionPath(...)` 的非路径分支会先用 `SessionManager.list(cwd, sessionDir)` 做 session id 前缀匹配，再做 `listAll()` 全局查找。Tau 当前已有 JSONL tree session summary 和可发现 session 列表，因此本次只关闭低风险的 local/session-dir lookup：路径语义保持兼容，有扩展名的 `session.json` 仍是 flat session path；无扩展的参数按上游方向解释为 session id 前缀。

本轮没有实现全局跨项目 `listAll()` 或跨项目 fork prompt，避免把需要跨项目 cwd 语义的更大合同混入这个切片；本地 `--resume` selector 由同任务后续子切片单独关闭。

### Additional Validation: ID Prefix Lookup

* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --filter "CodingAgentInitialMessageBuilderTests|CodingAgentSessionTargetTests" --no-restore --verbosity minimal -m:1` 在 id-prefix 回归加入后通过 63/63。
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal -m:1` 在 id-prefix 回归加入后通过 607/607。
* CLI id-prefix smoke 通过：先用临时 `--session-dir` 创建 JSONL，读取 header id 前缀后运行 `--session <prefix> --session-dir <dir> --help`，session 数保持 1；再运行 `--fork <prefix> --session-dir <dir> --help`，session 数变为 2。
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore -SkipWebUiBrowserTests` 在 id-prefix 回归加入后通过。计数：Ai 287、Agent 123、Tui 251、CodingAgent 607、WebUi 非浏览器 53、Pods 216。该命令仍是当前沙箱内非浏览器 gate，不替代完整 WebUi browser/release/static smoke。

### Continuation: `--resume` Local Interactive Selector Baseline

**Key Actions:**

* **Startup resolver**: 新增 `CodingAgentStartupResumeResolver`，在 `--resume` / `-r` 且处于交互式终端时构造 `CodingAgentResumeSelectorState`，列出显式 `--session-dir` 与当前默认/clone 可发现 JSONL sessions。
* **Program wiring**: `Program.cs` 在 session target resolve 前调用 resolver；选择 session 后复用同一 tree session startup path，print/RPC 或没有 selector 时返回明确诊断，空列表或取消选择时以 0 退出且不创建 session。
* **Regression coverage**: 新增 resolver tests，覆盖非 resume、显式 session bypass、print mode error、空列表 0 exit、取消选择和显式 session-dir 选择。
* **Docs sync**: 更新 `GOAL.md`、`next.md`、`docs/QUALITY_SCORE.md` 和 active parity plan/matrix，只把本地可发现 sessions 的交互选择 baseline 标为关闭，保留全局 `SessionManager.listAll()`、cross-project fork prompt、完整 upstream SessionManager schema/selection、真实 auth/provider e2e 与 package/bin identity 缺口。

**Design Intent:**

上游 `--resume` 的完整路径包含 local list、global list、TUI selector 和跨项目语义。Tau 当前已有 resume selector 与 JSONL session summary，因此本次只关闭不需要跨项目 prompt 的本地交互选择合同；print/RPC 模式下拒绝 `--resume`，避免非交互命令意外进入 selector 或创建 session。

### Additional Validation: Resume Selector

* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --filter "CodingAgentInitialMessageBuilderTests|CodingAgentSessionTargetTests|CodingAgentStartupResumeResolverTests|CodingAgentModelListFormatterTests|CodingAgentCliModelScopeTests" --no-restore --verbosity minimal -m:1` 通过 77/77。
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal -m:1` 通过 615/615。
* `dotnet run --project src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-build -- --list-models gemini` 通过，输出 model list 表。
* `dotnet run --project src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-build -- --resume --print` 返回 1，并输出 `error: --resume requires an interactive terminal; use --session <path> or --continue.`。
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` 通过。计数：Ai 287、Agent 123、Tui 251、CodingAgent 615、WebUi 61、Pods 216。
