## [2026-05-24 16:03] | Task: Composition 多行输入视窗

### Execution Context

* **Agent ID**: `Codex 主控`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 下一轮

### Changes Overview

**Scope:** `Tau.CodingAgent`, `Tau.Tui`

### Key Actions

* **Tau.CodingAgent**: `model selector` 在 composition 主屏启用时也改走 composition overlay，至此高频 selector 主路径（theme/settings/thinking/auth/scopedModels/model selector）都已接入同一块 surface。
* **Tau.Tui**: `TuiCompositionInteractiveRenderer` 从单行输入窗口推进到多行输入视窗：普通输入最多显示 3 行，reverse-search 最多显示 2 行。
* **Tau.Tui**: 输入与 reverse-search 的可见窗口都改成“wrap 全量内容，再取包含光标的末尾窗口”，而不是继续用单行硬截。

### Design Intent (Why)

主屏替换要继续往可用性收口，下一层最明显的问题就是长输入和 reverse-search 在 composition 模式下仍然难看、难用。本轮先不动 editor 协议，而是在 renderer 侧把单行窗口推进到多行视窗，并让最常用 selector 主路径彻底回到当前主屏 surface。

### Validation

* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。
* `git diff --check` 通过；仅出现 Git CRLF normalization warning。
* 按当前快速移植策略未跑单元测试。

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentModelSelector.cs`
* `src/Tau.CodingAgent/Program.cs`
* `src/Tau.Tui/Runtime/TuiCompositionInteractiveRenderer.cs`

## [2026-05-24 16:12] | Task: Composition 光标反显与 reverse-search 语义收口

### Execution Context

* **Agent ID**: `Codex 主控`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 下一轮

### Changes Overview

**Scope:** `Tau.Tui`

### Key Actions

* **Tau.Tui**: `TuiCompositionInteractiveRenderer` 不再把输入光标渲染成字面量 `|`，改成 ANSI 反显块，普通输入和 reverse-search 都共用同一套 placeholder + wrap + 视窗裁切逻辑。
* **Tau.Tui**: reverse-search 从“前缀和整条命中揉成一行”改成两层语义：第一行固定展示 query，第二行单独展示命中内容或 `[no match]`，并让光标跟着命中位置落在第二行。
* **Tau.Tui**: reverse-search 第二行会把命中的 query 片段单独做下划线标记，避免只看到整条历史命令却分不清这次是匹配到了哪一段。
* **Tau.Tui**: 光标位于行尾时会渲染成反显空格，而不是把一个额外的可见分隔符插进正文。

### Design Intent (Why)

上一轮已经把输入窗口推进到多行可视窗，但视觉上仍然像“在文本里插了个管道符”，reverse-search 也看不清当前是在看 query 还是命中内容。本轮只动 renderer，把 composition 主屏最直接影响交互可用性的两个问题先收口，不扩到 editor 协议或更多模块。

### Validation

* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。
* `git diff --check` 通过；仅出现既有 Git CRLF normalization warning。
* 按当前快速移植策略未跑单元测试。

### Files Modified

* `src/Tau.Tui/Runtime/TuiCompositionInteractiveRenderer.cs`

## [2026-05-24 16:24] | Task: Composition 运行中输入链接回主屏

### Execution Context

* **Agent ID**: `Codex 主控`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 继续不断移植，在移植进度100%之前不要询问我，你继续制定下一步计划，不断往复执行

### Changes Overview

**Scope:** `Tau.CodingAgent`, `Tau.Tui`

### Key Actions

* **Tau.CodingAgent**: `Program.cs` 创建 `TuiCompositionSession` 时正式注入同一个 `SystemConsoleKeyReader`，把此前 composition overlay/selector 运行时读不到键盘的缺口补上。
* **Tau.CodingAgent**: 新增 `CompositionCodingAgentTurnInputSource`，运行中的 `steer/follow-up` 输入不再走隐藏式 `Console.ReadKey` 缓冲，而是复用 `InteractiveInputEditor + TuiCompositionInteractiveRenderer`，把可见输入 overlay 接回当前主屏。
* **Tau.CodingAgent**: `Alt+Enter` 仍保持 `follow-up` 语义，普通 `Enter` 继续是 `steer`；`Escape` 会清掉当前运行中草稿，且 turn-input 模式下会屏蔽 `Ctrl+P`/`Ctrl+Shift+P`/`Ctrl+L` 这类只属于主 prompt 的模型切换动作。
* **Tau.CodingAgent**: `/tree --interactive` 仍然还是 raw navigator，但现在会先临时停止 composition session、跑完 raw tree，再恢复 transcript 主屏，避免两套渲染直接抢同一块终端。
* **Tau.Tui**: `TuiCompositionHost` 增加最小串行化，避免 transcript 更新、overlay 输入和 status/render 在运行中并发踩同一块 composition surface。

### Design Intent (Why)

上一轮虽然把主 prompt 和 selector 接到了 composition surface，但当前真实运行链里仍有两个硬缺口：一是 composition session 自己没有 key reader，二是 agent 正在输出时的 steer/follow-up 仍然是不可见的 raw console 输入。本轮把这两条链一起接回同一块 surface，先守住主屏行为闭环，再继续推下一批 parity 缺口。

### Validation

* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。
* `git diff --check` 通过；仅出现既有 Git CRLF normalization warning。
* 按当前快速移植策略未跑单元测试。

### Files Modified

* `src/Tau.CodingAgent/Program.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentTurnInputSource.cs`
* `src/Tau.Tui/Runtime/TuiCompositionHost.cs`

## [2026-05-24 16:38] | Task: Composition tree navigator 接回主屏

### Execution Context

* **Agent ID**: `Codex 主控`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 继续不断移植，在移植进度100%之前不要询问我，你继续制定下一步计划，不断往复执行

### Changes Overview

**Scope:** `Tau.CodingAgent`

### Key Actions

* **Tau.CodingAgent**: 新增 `CodingAgentTreeCompositionNavigator`，把 `/tree --interactive` 从 raw console navigator 迁到 composition overlay；overlay 使用全宽 surface，不再靠“停止 composition 再硬刷控制台”的过渡桥。
* **Tau.CodingAgent**: tree component 保留当前高频语义：`j/k`、`g/G`、`Home/End`、`PageUp/PageDown`、普通 `Left/Right` 翻页、`Ctrl/Alt+Left/Right` branch navigation、`f` filter cycle、`/` search、`n/N`、`Space` fold、`i` inspector、`Enter` select、`q/Esc` cancel/clear search。
* **Tau.CodingAgent**: `Program.cs` 的 tree navigator factory 现在按是否存在 `compositionSession` 选择 composition runner 或 raw fallback；非 composition 模式仍保留原始 `CodingAgentTreeInteractiveNavigator`。

### Design Intent (Why)

上一轮只是把 `/tree -i` 做成“临时退出 composition 再恢复”的过渡态，那只能止血，不能算真正把主屏交互迁回来。本轮直接把 tree 交互本体接到 composition overlay，先把 CodingAgent 主屏里最显眼的一条 raw console 分叉收掉，后续再继续打磨视觉和细节 parity。

### Validation

* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。
* `git diff --check` 通过；仅出现既有 Git CRLF normalization warning。
* 按当前快速移植策略未跑单元测试，也未做真实交互式 console smoke。

### Files Modified

* `src/Tau.CodingAgent/Program.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentTreeCompositionNavigator.cs`

## [2026-05-24 16:47] | Task: OAuth login callback 输出接回主屏

### Execution Context

* **Agent ID**: `Codex 主控`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 继续不断移植，在移植进度100%之前不要询问我，你继续制定下一步计划，不断往复执行

### Changes Overview

**Scope:** `Tau.CodingAgent`

### Key Actions

* **Tau.CodingAgent**: 新增 `InteractiveOAuthLoginCallbacks`，把 OAuth login 的授权 URL、进度消息和后续文本 prompt 接回 `InteractiveConsoleSession`，不再固定走裸 `Console.WriteLine/ReadLine`。
* **Tau.CodingAgent**: `CodingAgentCommandRouter` 现在接受可注入 `IOAuthLoginCallbacks` factory；默认仍回退 `ConsoleOAuthLoginCallbacks`，生产入口则改为使用 `InteractiveOAuthLoginCallbacks(ui)`。
* **Tau.CodingAgent**: 这样 `/login` 在 composition 主屏下至少会把浏览器认证提示、token exchange/progress 和回退 prompt 保持在同一条 transcript/UI 路径里，减少“命令执行在主屏里，OAuth 提示却飘到裸控制台”的分叉。

### Design Intent (Why)

把 `/tree -i` 迁回主屏之后，下一条显眼分叉就是 `/login`。这一轮先不改 OAuth provider 协议，也不做完整 dialog/overlay，只把 callback 输出面切回 `InteractiveConsoleSession`，让主屏至少能承载 login 过程中的说明和进度，再为下一轮真正的 interactive login prompt 收口打底。

### Validation

* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。
* `git diff --check` 通过；仅出现既有 Git CRLF normalization warning。
* `dotnet test tests/Tau.Tui.Tests/Tau.Tui.Tests.csproj --no-restore --filter InteractiveConsoleSessionTests --verbosity minimal` 通过（10 tests）。
* `dotnet test tests/Tau.CodingAgent.Tests/Tau.CodingAgent.Tests.csproj --no-restore --filter InteractiveOAuthLoginCallbacksTests --verbosity minimal` 通过（3 tests）。
* 仍未做真实 OAuth 浏览器回环 smoke。

### Files Modified

* `src/Tau.CodingAgent/Program.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentHost.cs`
* `src/Tau.CodingAgent/Runtime/InteractiveOAuthLoginCallbacks.cs`

## [2026-05-24 16:58] | Task: OAuth manual-code 输入与自定义 prompt 收口

### Execution Context

* **Agent ID**: `Codex 主控`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 继续不断移植，在移植进度100%之前不要询问我，你继续制定下一步计划，不断往复执行

### Changes Overview

**Scope:** `Tau.Ai`, `Tau.CodingAgent`, `Tau.Tui`

### Key Actions

* **Tau.Ai / Tau.CodingAgent**: 新增 `IOAuthManualCodeInputController`，并让 OpenAI Codex / Anthropic / Gemini CLI / Antigravity 这几条支持 browser-callback 与 manual input 竞争的 OAuth provider 在 callback 先成功时显式取消 manual input task，避免 composition 主屏里遗留后台读输入任务。
* **Tau.CodingAgent**: `InteractiveOAuthLoginCallbacks` 现在真的实现了 `OnManualCodeInputAsync()`，会在主屏里等待用户粘贴 authorization code 或 redirect URL；浏览器回调先完成时会被 provider 主动取消。
* **Tau.Tui**: `InteractiveConsoleSession` 新增可定制 prompt 的输入重载；OAuth prompt 改为显式 `oauth> `，不再和普通消息输入共用同一个 `> ` 视觉语义。

### Design Intent (Why)

上一轮虽然把 `/login` 的输出和进度拉回了主屏，但 manual code 仍然是空实现，provider 一旦并发等待浏览器回调和手动输入，就存在“主屏没有手动输入入口”以及“浏览器成功后后台输入任务继续挂着”的问题。本轮把这条并发输入路径真正补实，并顺手把 prompt 语义从普通输入里分离出来。

### Validation

* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。
* `git diff --check` 通过；仅出现既有 Git CRLF normalization warning。
* `dotnet test tests/Tau.Tui.Tests/Tau.Tui.Tests.csproj --no-restore --filter InteractiveConsoleSessionTests --verbosity minimal` 通过（10 tests）。
* `dotnet test tests/Tau.CodingAgent.Tests/Tau.CodingAgent.Tests.csproj --no-restore --filter InteractiveOAuthLoginCallbacksTests --verbosity minimal` 通过（3 tests）。
* 仍未做真实 OAuth 浏览器回环 smoke。

### Files Modified

* `src/Tau.Ai/Auth/OAuth/OAuthLoginCallbacks.cs`
* `src/Tau.Ai/Auth/OAuth/Providers/AnthropicOAuthProvider.cs`
* `src/Tau.Ai/Auth/OAuth/Providers/AntigravityOAuthProvider.cs`
* `src/Tau.Ai/Auth/OAuth/Providers/GeminiCliOAuthProvider.cs`
* `src/Tau.Ai/Auth/OAuth/Providers/OpenAICodexOAuthProvider.cs`
* `src/Tau.CodingAgent/Runtime/InteractiveOAuthLoginCallbacks.cs`
* `src/Tau.Tui/Runtime/InteractiveConsoleSession.cs`

## [2026-05-24 17:12] | Task: Composition metadata inspector baseline

### Execution Context

* **Agent ID**: `Codex 主控`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 继续不断移植，在移植进度100%之前不要询问我，你继续制定下一步计划，不断往复执行

### Changes Overview

**Scope:** `Tau.CodingAgent`

### Key Actions

* **Tau.CodingAgent**: 新增 `CodingAgentTreeMetadataSnapshot`、`CodingAgentTreeMetadataEntrySnapshot`、`CodingAgentTreeMetadataRelationSnapshot` 和 `CodingAgentTreeMetadataSectionSnapshot`，把 `/metadata` 的 session summary、latest metadata entries 和 entry-level detail 从纯字符串提升成结构化 snapshot + composition inspector。
* **Tau.CodingAgent**: composition 模式下的 `/metadata` 不再只是全量文本滚动，而是固定 session summary，列表模式可选择 recent metadata entry，`Enter` 进入 detail，`Esc/Enter` 返回或关闭，支持 `j/k`、方向键、`PageUp/PageDown`、`Home/End`。
* **Tau.CodingAgent**: entry detail 现在会分成 `Overview`、`Relations` 和按 entry type 拆分的 section（如 `Message`、`Session`、`Compaction`、`Branch Summary` 等）；detail 模式下可用数字键 `1-9` 跟随 relation drill-down，并通过返回栈回到上一个 detail 或 recent metadata 列表。
* **Tau.CodingAgent**: 非 composition 模式下 `/metadata` 仍保持原来的纯文本返回合同，避免破坏脚本化和既有测试路径。

### Design Intent (Why)

`next.md` 里剩余最显眼的 CodingAgent/TUI 缺口之一就是 “完整 TUI metadata inspector”。本轮把 `/metadata` 提升成有固定 summary + selectable recent metadata + structured entry detail 的 composition inspector，并先补了 relation drill-down 与返回栈；后续仍可继续补更完整的字段布局和跨 entry 浏览体验，但主路径已经不再只是纯文本 viewer。

### Validation

* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。
* `git diff --check` 通过；仅出现既有 Git CRLF normalization warning。
* `dotnet test tests/Tau.CodingAgent.Tests/Tau.CodingAgent.Tests.csproj --no-restore --filter "GetMetadataSnapshot_FocusedEntryIncludesRelationsAndSections|TryHandleAsync_MetadataCommand_WithViewer_InvokesViewerWithoutTranscriptMessage" --verbosity minimal` 通过（2 tests）。

### Files Modified

* `src/Tau.CodingAgent/Program.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCompositionMetadataViewer.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentHost.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentTreeMetadataSnapshot.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentTreeSessionStore.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`

## [2026-05-24 17:24] | Task: Metadata inspector 跨 entry 浏览增强

### Execution Context

* **Agent ID**: `Codex 主控`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 继续不断移植，在移植进度100%之前不要询问我，你继续制定下一步计划，不断往复执行

### Changes Overview

**Scope:** `Tau.CodingAgent`

### Key Actions

* **Tau.CodingAgent**: `CodingAgentTreeMetadataSnapshot` 继续从“summary + detail lines”推进到真正的结构化 inspector 数据：`VisibleEntryIds`、`EntriesById`、entry `OverviewLines`、`Relations` 和按类型拆分的 `Sections`。
* **Tau.CodingAgent**: `CodingAgentCompositionMetadataViewer` 的 detail 模式现在支持 `Tab/Shift+Tab` 在当前 recent metadata entries 之间切换，保持 detail 视图不退回列表；`1-9` relation jump 与返回栈仍保留。
* **Tau.CodingAgent**: detail 视图 footer/header 也同步更新，明确暴露 relation jump 和 entry browse 的键位。

### Design Intent (Why)

上一轮已经把 `/metadata` 拉到 structured inspector，但“跨 entry 浏览体验”还偏弱，用户需要退回列表才能换下一个 metadata entry。本轮先把 recent metadata entries 的 detail 内切换做实，同时把 snapshot 结构继续清理到适合后续 richer layout 的形状，避免继续依赖纯字符串拼接。

### Validation

* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。
* `git diff --check` 通过；仅出现既有 Git CRLF normalization warning。
* 本轮未新增 viewer 级专门测试；沿用上一轮的 metadata snapshot / viewer seam 定向测试作为当前结构回归基线。

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentCompositionMetadataViewer.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentTreeMetadataSnapshot.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentTreeSessionStore.cs`

## [2026-05-24 17:39] | Task: Metadata inspector detail 布局收口

### Execution Context

* **Agent ID**: `Codex 主控`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 下一轮

### Changes Overview

**Scope:** `Tau.CodingAgent`

### Key Actions

* **Tau.CodingAgent**: `CodingAgentCompositionMetadataViewer` 的 detail 视图不再只是线性拼接 `Overview/Relations/Sections`，而是增加 `Entry Summary` 顶部摘要块，明确展示当前 entry summary、type/path、relation/section 数和当前 entry 是否仍在 visible metadata scope 内。
* **Tau.CodingAgent**: relation 区块现在会把目标 entry 的 `SummaryLine` 一并显示出来，数字跳转不再只看到裸 `entryId`。
* **Tau.CodingAgent**: detail 模式新增 `h/l` 与已有 `Tab/Shift+Tab` 共同承担跨 entry 浏览；relation jump、backstack 返回和退出 detail 回到 list 时都会同步当前选中项，避免 list/detail 状态漂移。
* **Tau.CodingAgent**: recent metadata 列表项补了稳定编号，detail footer 也改成显式展示 `entry x/y`、relation 数、jump 范围和 backstack 深度。
* **Tau.CodingAgent**: 顺手把 `CodingAgentTreeSessionStore` 里两处对 `IReadOnlyList` 错用 `FindIndex(...)` 的编译阻塞改成局部 helper，恢复 solution build。

### Design Intent (Why)

上一轮已经把 `/metadata` 接到 structured inspector，但 detail 还是“结构化数据的线性打印”，relation drill-down 也缺少目标上下文。本轮不再扩 snapshot 协议，只在 viewer 层把最直接影响可读性的版式和跨 entry 浏览状态收口，保持这条 parity 线继续小步前进。

### Validation

* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。
* `git diff --check` 通过；仅出现既有 Git CRLF normalization warning。
* 按当前快速移植策略，本轮未新增单元测试。

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentCompositionMetadataViewer.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentTreeSessionStore.cs`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `docs/histories/2026-05/20260524-1603-composition-multiline-input.md`
* `next.md`

## [2026-05-24 18:00] | Task: Composition tree navigator 接 metadata inspector

### Execution Context

* **Agent ID**: `Codex 主控`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 继续不断移植，在移植进度100%之前不要询问我，你继续制定下一步计划，不断往复执行

### Changes Overview

**Scope:** `Tau.CodingAgent`

### Key Actions

* **Tau.CodingAgent**: `CodingAgentTreeCompositionNavigator` 新增 metadata inspection request 路径；composition `/tree --interactive` 中按 `i` 时，不再只是切换一条简陋 inline inspector，而是基于当前选中 entry id 打开同一套 `CodingAgentCompositionMetadataViewer`。
* **Tau.CodingAgent**: metadata viewer 关闭后会回到原 tree navigator overlay，保留原来的 tree selection / fold / filter / search 上下文，主屏不再需要在 tree 和 `/metadata` 之间来回切换命令。
* **Tau.CodingAgent**: `Program.cs` 的 composition tree navigator factory 现在把 `treeSessionController.GetMetadataSnapshot(entryId)` 直接接给 tree navigator，raw console fallback 仍保持原来的轻量 inspector，不扩大修改面。

### Design Intent (Why)

上一轮已经把 `/metadata` 做成 structured inspector，但 `/tree --interactive` 里的 `i` 还停留在一条本地 detail line，上下文割裂明显。本轮把 tree 主屏直接接到同一套 metadata viewer，让最常用的 session tree 浏览路径和 metadata inspect 路径合并到同一块 composition surface。

### Validation

* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。
* `git diff --check` 通过；仅出现既有 Git CRLF normalization warning。
* 按当前快速移植策略，本轮未新增单元测试。

### Files Modified

* `src/Tau.CodingAgent/Program.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentTreeCompositionNavigator.cs`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `docs/histories/2026-05/20260524-1603-composition-multiline-input.md`
* `next.md`

## [2026-05-24 18:16] | Task: Interactive tree 全量导航窗口

### Execution Context

* **Agent ID**: `Codex 主控`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 继续不断移植，在移植进度100%之前不要询问我，你继续制定下一步计划，不断往复执行

### Changes Overview

**Scope:** `Tau.CodingAgent`

### Key Actions

* **Tau.CodingAgent**: `CodingAgentCommandRouter` 的 interactive tree 路径不再直接复用文本 `/tree` 的 `MaxEntries` 截断窗口；`/tree --interactive` 现在会把 `EnumerateView(...)` 的 `MaxEntries` 提升为全量 filtered/search 结果，让 navigator 拿到完整可导航条目集。
* **Tau.CodingAgent.Tests**: 新增定向回归 `TryHandleAsync_TreeInteractiveCommand_UsesFullFilteredTreeInsteadOfDefaultWindow()`，固定 30 条消息时 interactive tree 实际收到 30 个 view items，而不是默认被压回 24 条。

### Design Intent (Why)

上一轮虽然把 composition tree 主屏和 metadata inspector 接上了，但 interactive tree 仍然建立在“最近窗口”而不是真正的完整 filtered/search 结果上，这会让 branch navigation、search 和 metadata drill-down 都停在不完整子集里。本轮优先修数据面，让 interactive tree 成为真正可浏览的树，而不是文本摘要的交互包装。

### Validation

* `dotnet test tests/Tau.CodingAgent.Tests/Tau.CodingAgent.Tests.csproj --no-restore --filter "TryHandleAsync_TreeInteractiveCommand_UsesFullFilteredTreeInsteadOfDefaultWindow|TryHandleAsync_TreeInteractiveCommand_InvokesNavigatorAndReturnsSelection" --verbosity minimal` 通过（2 tests）。
* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。
* `git diff --check` 通过；仅出现既有 Git CRLF normalization warning。

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `docs/histories/2026-05/20260524-1603-composition-multiline-input.md`
* `next.md`

## [2026-05-24 18:29] | Task: Metadata 返回后同步 tree 选中态

### Execution Context

* **Agent ID**: `Codex 主控`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 继续不断移植，在移植进度100%之前不要询问我，你继续制定下一步计划，不断往复执行

### Changes Overview

**Scope:** `Tau.CodingAgent`

### Key Actions

* **Tau.CodingAgent**: `CodingAgentCompositionMetadataViewer` 新增 `RunWithSelectionAsync(...)`，在 composition metadata inspector 关闭时返回最终停留的 entry id；现有 `RunAsync(...)` 继续保留为不关心返回值的兼容入口。
* **Tau.CodingAgent**: `CodingAgentTreeCompositionNavigator` 在 tree -> metadata -> tree 路径上消费这个返回 entry id，并尝试把 tree 选中态同步到 metadata 最终停留的 entry。
* **Tau.CodingAgent**: 如果 metadata 最终 entry 仍在当前 tree item 集内但只是被 fold 隐藏，tree navigator 会先展开相关 folded ancestors，再恢复到对应 entry；如果 entry 已被当前 filter/search 排除，则保持现有 tree 合同，不强行跨过滤器显示。
* **Tau.CodingAgent.Tests**: 新增 composition 级回归 `RunAsync_MetadataViewerSelectionUpdatesTreeSelection()`，固定 tree 中按 `i` 进入 metadata、用 relation jump 跳到 parent、关闭后再 `Enter`，最终返回的 tree selected entry 会变成 parent。

### Design Intent (Why)

前两轮已经把 composition tree navigator 和 structured metadata inspector 接到同一块主屏上，但 inspect 完回来后 tree 仍停在旧 entry，上下文仍然断一半。本轮把 metadata 的最终停留 entry 回传给 tree，让 relation drill-down 真正成为 tree 浏览的一部分，而不是“看完信息再手动回去找”。

### Validation

* `dotnet test tests/Tau.CodingAgent.Tests/Tau.CodingAgent.Tests.csproj --no-restore --filter "RunAsync_MetadataViewerSelectionUpdatesTreeSelection|TryHandleAsync_TreeInteractiveCommand_UsesFullFilteredTreeInsteadOfDefaultWindow" --verbosity minimal` 通过（2 tests）。
* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。
* `git diff --check` 通过；仅出现既有 Git CRLF normalization warning。

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentCompositionMetadataViewer.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentTreeCompositionNavigator.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentTreeCompositionNavigatorTests.cs`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `docs/histories/2026-05/20260524-1603-composition-multiline-input.md`
* `next.md`

## [2026-05-24 18:41] | Task: Interactive tree richer 搜索语义

### Execution Context

* **Agent ID**: `Codex 主控`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 继续不断移植，在移植进度100%之前不要询问我，你继续制定下一步计划，不断往复执行

### Changes Overview

**Scope:** `Tau.CodingAgent`

### Key Actions

* **Tau.CodingAgent**: `CodingAgentTreeViewItem` 新增可选 `SearchText`，由 `CodingAgentTreeSessionStore.BuildViewItems(...)` 直接复用现有 `BuildSearchText(...)` 填充，把 interactive tree 也接回同一套 richer search fact source。
* **Tau.CodingAgent**: raw / composition 两条 interactive tree 本地 `/` 搜索路径现在都会优先匹配 `item.SearchText`，没有时才回落到 `DisplayLine`，避免出现 `/tree --search` 能搜到而 interactive `/` 搜不到的语义分叉。
* **Tau.CodingAgent.Tests**: 新增 composition 级回归 `RunAsync_LocalSearchUsesSearchTextWhenDisplayLineDoesNotContainQuery()`，固定 query 只存在于 `SearchText`、不存在于 `DisplayLine`，确认 composition tree 本地搜索仍能命中目标 entry。

### Design Intent (Why)

前几轮已经把 interactive tree 做成完整 filtered/search 数据面，并把 metadata drill-down 接回主屏，但本地 `/` 搜索语义仍然比 `/tree --search` 弱，只看渲染行文本。这会让用户面对两套不同的搜索合同。本轮把 interactive 搜索重新挂回 tree store 的 richer search text，让 tree 浏览、metadata inspect 和 CLI 搜索继续收口到同一个事实源。

### Validation

* `dotnet test tests/Tau.CodingAgent.Tests/Tau.CodingAgent.Tests.csproj --no-restore --filter "RunAsync_LocalSearchUsesSearchTextWhenDisplayLineDoesNotContainQuery|RunAsync_MetadataViewerSelectionUpdatesTreeSelection" --verbosity minimal` 通过（2 tests）。
* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。
* `git diff --check` 通过；仅出现既有 Git CRLF normalization warning。

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentTreeSessionStore.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentTreeInteractiveNavigator.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentTreeCompositionNavigator.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentTreeCompositionNavigatorTests.cs`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `docs/histories/2026-05/20260524-1603-composition-multiline-input.md`
* `next.md`

## [2026-05-24 18:56] | Task: Interactive tree label timestamp toggle

### Execution Context

* **Agent ID**: `Codex 主控`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 继续不断移植，在移植进度100%之前不要询问我，你继续制定下一步计划，不断往复执行

### Changes Overview

**Scope:** `Tau.CodingAgent`

### Key Actions

* **Tau.CodingAgent**: `CodingAgentTreeViewItem` 新增 `BaseDisplayLine`、`LabelTimestampSuffix` 和 `LabelTimestampsEnabled` 元数据，tree store 在构造 view items 时会把“无时间戳基线”和“可选 label timestamp suffix”都带出来。
* **Tau.CodingAgent**: raw / composition 两条 interactive tree navigator 现在都支持 `Shift+T` 切换 label timestamp 显示，header 里会显示 `Shift+T label time`，开启后附加 `[+label time]` 状态提示。
* **Tau.CodingAgent.Tests**: 新增 raw navigator 回归 `NavigateAsync_ShiftTTogglesLabelTimestampsInRenderedLines()`，固定 `Shift+T` 后渲染结果里会出现 label timestamp 和 `[+label time]` 状态标记。

### Design Intent (Why)

上一轮已经把 interactive tree 搜索、metadata drill-down 和选中态回传都收口到主屏，但 label timestamp 仍然只能靠 `/tree --label-time` 这种命令参数一次性决定，和上游 in-place tree toggle 体验差了一截。本轮把这个 toggle 直接补到 interactive tree 里，让 tree 浏览路径继续朝上游 TreeSelector 靠近。

### Validation

* `dotnet test tests/Tau.CodingAgent.Tests/Tau.CodingAgent.Tests.csproj --no-restore --filter "NavigateAsync_ShiftTTogglesLabelTimestampsInRenderedLines|RunAsync_LocalSearchUsesSearchTextWhenDisplayLineDoesNotContainQuery|RunAsync_MetadataViewerSelectionUpdatesTreeSelection" --verbosity minimal` 通过（3 tests）。
* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。
* `git diff --check` 通过；仅出现既有 Git CRLF normalization warning。

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentTreeSessionStore.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentTreeInteractiveNavigator.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentTreeCompositionNavigator.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentTreeInteractiveNavigatorTests.cs`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `docs/histories/2026-05/20260524-1603-composition-multiline-input.md`
* `next.md`

## [2026-05-24 21:10] | Task: Resume selector rename parity baseline

### Execution Context

* **Agent ID**: `Codex 主控`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 下一轮

### Changes Overview

**Scope:** `Tau.CodingAgent`

### Key Actions

* **Tau.CodingAgent**: `CodingAgentResumeSelector` 新增 `CodingAgentResumeSelectionResult`，selector 返回值不再只是“选中的 session path”，还可以把“当前 session 在 selector 里被改名”的结果带回 router。
* **Tau.CodingAgent**: `CodingAgentResumeSelectorComponent` 新增 `rename` mode；在列表态下按 `Ctrl+R` 会切到 rename 输入面板，顶部改成 `Rename Session`，当前仅预填显式 session name，不会误把首条 user 文本 preview 当成真实名称。
* **Tau.CodingAgent**: console / composition 两条 `/resume` selector 路径现在都会在 overlay loop 里处理 rename request，成功后刷新 session 列表并显示 `Session renamed`；如果改名的是当前 session，即使用户随后取消 selector，router 也会把 `runner.SessionName` 更新成新名称，避免当前内存态和 JSONL tree 文件重新分叉。
* **Tau.CodingAgent.Tests**: `CodingAgentResumeSelectorTests` 新增真实 selector rename 流程回归，固定 `Ctrl+R -> 输入新名 -> Enter -> Esc` 会把当前 session 名称写回 JSONL，并把新名称带回 selector 结果；`CodingAgentCommandRouterTests` 也新增当前 session rename 后取消 selector 时仍会更新 runner name 的回归。

### Design Intent (Why)

上一轮已经把 `/resume` 收成真正可搜索的 session selector，但 `next.md` 里还明确留着 delete/rename parity。相比 delete，rename 直接作用在同一份 JSONL session metadata 上，而且已经有上游明确的 `Ctrl+R` 行为和现成 `appendSessionInfo(...)` 语义，性价比更高。这一轮顺手把“当前 session 在 selector 里改名后，取消返回会导致 runner 名称漂移”的隐藏分叉也一起收口，避免只做表面 UI 而把状态一致性留空。

### Validation

* `dotnet test tests/Tau.CodingAgent.Tests/Tau.CodingAgent.Tests.csproj --no-restore --filter "FullyQualifiedName~Resume" --verbosity minimal` 通过（7 tests）。
* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。
* `git diff --check` 通过；仅出现既有 Git CRLF normalization warning。

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentHost.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentResumeSelector.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentTreeSessionStore.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentResumeSelectorTests.cs`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`
* `docs/histories/2026-05/20260524-1603-composition-multiline-input.md`
* `next.md`

## [2026-05-24 21:23] | Task: Resume selector delete parity baseline

### Execution Context

* **Agent ID**: `Codex 主控`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 下一轮

### Changes Overview

**Scope:** `Tau.CodingAgent`

### Key Actions

* **Tau.CodingAgent**: `CodingAgentResumeSelectorComponent` 新增 delete confirm 状态，列表态下支持 `Ctrl+D`；确认中会拦截普通 selector 输入，`Enter` 真正提交 delete，`Esc` 取消。
* **Tau.CodingAgent**: delete 逻辑按上游 session selector 的真实边界执行：如果当前选中的是 active session，直接显示 `Cannot delete the currently active session`，不允许删除；非当前 session 才会继续确认和删除。
* **Tau.CodingAgent**: 删除成功后会刷新 `ListAvailableSessions(...)`、保留 selector 流程并显示状态；Windows 下会优先走 Recycle Bin，失败时再回退直接删除。
* **Tau.CodingAgent.Tests**: `CodingAgentResumeSelectorTests` 新增 `Ctrl+D` 删除非当前 session 的真实流程回归，以及当前 active session 删除被阻止的回归，`Resume` focused tests 从 7/7 提升到 9/9。

### Design Intent (Why)

上一轮已经把 `/resume` selector 的 rename parity 收了，剩下最直接的 session selector 缺口就是 delete。这里不能只想着“把文件删掉”就算完，因为 Tau 当前 tree session controller 仍绑定当前 JSONL 路径，删当前 active session 会直接把后续 append-only 写入带进坏状态。上游真实实现本来就禁止删当前 session，所以这轮直接对齐这个边界：先把 delete confirm 和 non-current delete 做实，把最危险的状态破坏点卡死；随后又把 Windows 本机路径提升成 Recycle Bin 优先，继续向上游 trash-first 语义靠拢。

### Validation

* `dotnet test tests/Tau.CodingAgent.Tests/Tau.CodingAgent.Tests.csproj --no-restore --filter "FullyQualifiedName~Resume" --verbosity minimal` 通过（9 tests）。
* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。
* `git diff --check` 通过；仅出现既有 Git CRLF normalization warning。

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentResumeSelector.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentResumeSelectorTests.cs`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`
* `docs/histories/2026-05/20260524-1603-composition-multiline-input.md`
* `next.md`

## [2026-05-24 22:19] | Task: Resume selector current/all scope baseline

### Execution Context

* **Agent ID**: `Codex 主控`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 下一轮

### Changes Overview

**Scope:** `Tau.CodingAgent`

### Key Actions

* **Tau.CodingAgent**: `CodingAgentResumeSelectorState` 现在显式携带 `CurrentWorkingDirectory`，供 selector 在 current/all scope 之间切换时按 session header `cwd` 做本地过滤。
* **Tau.CodingAgent**: `CodingAgentResumeSelectorComponent` 新增 `showAllSessions` 状态，按 `Tab` 可在 current/all scope 间切换；header 会显示 `Scope: current|all`。
* **Tau.CodingAgent**: selector 在 current scope 没有匹配项时不再直接退出，只要底层确实还有 session，就允许用户按 `Tab` 切到 all 继续浏览和选择。
* **Tau.CodingAgent.Tests**: `CodingAgentResumeSelectorTests` 新增组件级 scope toggle 回归，以及 current scope 为空时 `Tab -> Enter` 仍能在 all scope 选中 session 的回归，把 `Resume` focused tests 从 11/11 提升到 13/13。

### Design Intent (Why)

前几轮已经把 `/resume` selector 的搜索、rename、delete、named/path 浏览能力都补齐了，但如果当前 cwd 下没有 session，它仍然会直接返回，用户连切到 all scope 的机会都没有，这和上游 session selector 的 `Tab` 入口不一致。这一轮先把 current/all scope 切换补上，继续提高 session 浏览可用性，同时仍然避免把 Tau 当前 selector拉进更重的 threaded session browser 结构。

### Validation

* `dotnet test tests/Tau.CodingAgent.Tests/Tau.CodingAgent.Tests.csproj --no-restore --filter "FullyQualifiedName~Resume" --verbosity minimal` 通过（13 tests）。
* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。
* `git diff --check` 通过；仅出现既有 Git CRLF normalization warning。

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentResumeSelector.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentResumeSelectorTests.cs`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`
* `docs/histories/2026-05/20260524-1603-composition-multiline-input.md`
* `next.md`

## [2026-05-24 22:01] | Task: Resume selector named/path browsing baseline

### Execution Context

* **Agent ID**: `Codex 主控`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 下一轮

### Changes Overview

**Scope:** `Tau.CodingAgent`

### Key Actions

* **Tau.CodingAgent**: `CodingAgentResumeSelectorComponent` 新增两个本地浏览状态：`namedOnly` 和 `showPath`。列表头现在会明确显示 `Filter: all|named` 与 `Path: on|off`。
* **Tau.CodingAgent**: `/resume` selector 现在支持 `Ctrl+N` 切换“只看显式命名 session”，不会把仅靠首条 user 文本生成的 preview 当成 named session。
* **Tau.CodingAgent**: `/resume` selector 现在也支持 `Ctrl+P` 切换 description 里的路径显示；搜索能力仍然保留对 path 的匹配，不因为 path hidden 就丢掉 path 搜索。
* **Tau.CodingAgent.Tests**: `CodingAgentResumeSelectorTests` 新增 `Ctrl+N` 和 `Ctrl+P` 两条组件级回归，把 `Resume` focused tests 从 9/9 提升到 11/11。

### Design Intent (Why)

`/resume` 的 rename/delete 已经够用之后，再继续堆更重的 scope/threaded/all-sessions 结构，收益就没那么高了。上游 session selector 里还有两条很高频但数据面轻很多的浏览动作：只看 named session，和路径显示开关。这一轮优先把这两条补上，继续提高 session selector 的可用性，同时不把 Tau 当前 selector 拉进更复杂的树形 session browser。

### Validation

* `dotnet test tests/Tau.CodingAgent.Tests/Tau.CodingAgent.Tests.csproj --no-restore --filter "FullyQualifiedName~Resume" --verbosity minimal` 通过（11 tests）。
* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。
* `git diff --check` 通过；仅出现既有 Git CRLF normalization warning。

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentResumeSelector.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentResumeSelectorTests.cs`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`
* `docs/histories/2026-05/20260524-1603-composition-multiline-input.md`
* `next.md`

## [2026-05-24 20:53] | Task: Resume selector search baseline

### Execution Context

* **Agent ID**: `Codex 主控`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 下一轮

### Changes Overview

**Scope:** `Tau.CodingAgent`

### Key Actions

* **Tau.CodingAgent**: `CodingAgentResumeSelector` 不再只是包一层 plain `TuiSelectList`，现在有独立 `CodingAgentResumeSelectorComponent`，顶部会显示 `Resume Session` 和 `Search:`，支持在 selector 内直接输入过滤。
* **Tau.CodingAgent**: console 与 composition 两条 `/resume` selector 路径都改成跑这个 input component，而不是旧的纯列表 session；Enter/Esc 行为和当前 session 预选语义保持不变。
* **Tau.CodingAgent**: 搜索规则按 session name、provider、model、cwd 和 file path 做 case-insensitive contains 匹配，避免只支持 path/name 前缀过滤。
* **Tau.CodingAgent.Tests**: `CodingAgentResumeSelectorTests` 新增组件级搜索回归和 `SelectAsync(...)` 搜索选择回归，固定 `rev` 这类输入会把 session 列表收窄到目标 checkpoint，并保留渲染 diff 里的 `Search:` 文本更新。

### Design Intent (Why)

上一轮已经把裸 `/resume` 从“直接 latest”收成真正的 session selector，也把 sync-before-switch 和 sibling session discovery 补实了。接下来最明显的剩余缺口就是 selector 仍然只能靠上下键硬翻列表，和 `next.md` 里已经写明的 `/resume` search parity 不一致。这一轮继续复用 Tau 现有 selector/overlay 基础，只把搜索能力接进去，不顺手扩大到 delete/rename 或更高层 session 管理。

### Validation

* `dotnet test tests/Tau.CodingAgent.Tests/Tau.CodingAgent.Tests.csproj --no-restore --filter "FullyQualifiedName~Resume" --verbosity minimal` 通过（5 tests）。
* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。
* `git diff --check` 通过；仅出现既有 Git CRLF normalization warning。

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentResumeSelector.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentResumeSelectorTests.cs`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`
* `docs/histories/2026-05/20260524-1603-composition-multiline-input.md`
* `next.md`

## [2026-05-24 20:22] | Task: Tree summary replaceInstructions foundation

### Execution Context

* **Agent ID**: `Codex 主控`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 下一轮

### Changes Overview

**Scope:** `Tau.CodingAgent`

### Key Actions

* **Tau.CodingAgent**: `ICodingAgentRunner.SummarizeBranchAsync(...)` 现在正式接收 `replaceInstructions`，`RuntimeCodingAgentRunner` 也把它接进 branch summary prompt 生成逻辑。
* **Tau.CodingAgent**: 当 `replaceInstructions=true` 且存在 custom prompt 时，branch summary prompt 不再把用户指令挂到 `Additional focus:` 后面，而是直接用 custom prompt 替换默认 summary 模板；conversation envelope 和 Tau 现有 file-ops context 仍保持原样。
* **Tau.CodingAgent**: interactive tree summary path 现在会把 `CodingAgentTreeNavigationDecision.ReplaceInstructions` 透传到 runner，给后续 extension/runtime surface 复用同一条 tree navigation 合同。
* **Tau.CodingAgent.Tests**: 新增 focused 回归，分别固定 interactive tree 会把 `replaceInstructions` 传给 branch summarizer，以及 runtime runner 在 replace 模式下不会再拼默认 template / `Additional focus:`。

### Design Intent (Why)

上游 `navigateTree(options)` 明确支持 `customInstructions`、`replaceInstructions` 和 `label` 三件套。Tau 前几轮已经把 tree summary decision、label foundation 和 custom prompt 链打通，但 `replaceInstructions` 还停在缺口状态。这轮只补 foundation，不扩新 prompt UI，先把 runner 合同和 tree navigation 透传语义固定下来，后面无论是 extension hook 还是更完整的 tree summary 交互，都能直接站在同一层能力上继续推进。

### Validation

* `dotnet test tests/Tau.CodingAgent.Tests/Tau.CodingAgent.Tests.csproj --no-restore --filter "FullyQualifiedName~ReplaceInstructions" --verbosity minimal` 通过（2 tests）。
* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。
* `git diff --check` 通过；仅出现既有 Git CRLF normalization warning。

### Files Modified

* `src/Tau.CodingAgent/Runtime/ICodingAgentRunner.cs`
* `src/Tau.CodingAgent/Runtime/RuntimeCodingAgentRunner.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `tests/Tau.CodingAgent.Tests/FakeCodingAgentRunner.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `tests/Tau.CodingAgent.Tests/RuntimeCodingAgentRunnerTests.cs`
* `tests/Tau.WebUi.Tests/FakeWebUiRunner.cs`
* `tests/Tau.Agent.Tests/RuntimeDelegationAgentRunnerTests.cs`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`
* `docs/histories/2026-05/20260524-1603-composition-multiline-input.md`
* `next.md`

## [2026-05-24 20:43] | Task: Resume selector baseline

### Execution Context

* **Agent ID**: `Codex 主控`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 下一轮

### Changes Overview

**Scope:** `Tau.CodingAgent`

### Key Actions

* **Tau.CodingAgent**: 新增 `CodingAgentResumeSelector`，让交互式裸 `/resume` 在 selector 可用时改为打开 session selector，而不是直接跳 `latest`。
* **Tau.CodingAgent**: selector 会预选当前 session，并显示 session name；若没有显式 session name，则回退到当前 branch 的首条 user 文本。description 同时带上 `provider/model`、message count、mtime 和完整路径。
* **Tau.CodingAgent**: `/resume` 现在会在切换前先 `SyncFromRunner(_runner)`，避免当前 tree session 里还没落盘的 messages / session name 被切 session 覆盖掉。
* **Tau.CodingAgent**: session 发现逻辑现在也会覆盖“当前自定义 tree file 所在目录旁边的 sibling `coding-agent-sessions/`”，不再只盯默认 `.tau/coding-agent-sessions`。
* **Tau.CodingAgent.Tests**: 新增 selector 构造回归和 router 回归，固定 `/resume` selector 会恢复目标 session，并且切走前真的把当前 session 先 sync 回 JSONL tree。

### Design Intent (Why)

上游裸 `/resume` 是 session selector，不是“永远直接跳 latest”。Tau 之前虽然已经有 `/resume latest|path.jsonl` baseline，但既缺 interactive session picker，也有一个更实在的行为风险：当前 runner 的增量状态如果还没 sync 回 tree，就可能在 resume 时被静默覆盖。这一轮优先补最小可用 selector，并顺手把 sync-before-switch 收紧，让 session lifecycle 主线继续向上游可用行为靠拢。

### Validation

* `dotnet test tests/Tau.CodingAgent.Tests/Tau.CodingAgent.Tests.csproj --no-restore --filter "FullyQualifiedName~Resume" --verbosity minimal` 通过（3 tests）。
* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。
* `git diff --check` 通过；仅出现既有 Git CRLF normalization warning。

### Files Modified

* `src/Tau.CodingAgent/Program.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentHost.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentResumeSelector.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentTreeSessionStore.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentResumeSelectorTests.cs`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`
* `docs/histories/2026-05/20260524-1603-composition-multiline-input.md`
* `next.md`

## [2026-05-24 19:12] | Task: Interactive tree label edit baseline

### Execution Context

* **Agent ID**: `Codex 主控`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 下一轮

### Changes Overview

**Scope:** `Tau.CodingAgent`

### Key Actions

* **Tau.CodingAgent**: raw / composition 两条 interactive tree navigator 现在都支持 `Shift+L`。命中后不直接离开 tree 命令，而是返回 label edit request，让 `CodingAgentCommandRouter` 在同一条 `/tree --interactive` 流程里继续处理。
* **Tau.CodingAgent**: `CodingAgentHost` 新增最小 `label> ` prompt callback；空输入或 `clear` 会清 label，`cancel/q/quit` 保持原值。router 收到保存结果后会写 `AppendLabelChange(...)`，然后重开 tree。
* **Tau.CodingAgent**: interactive tree reopen 现在会显式携带 preferred selected entry id，所以 label 编辑后会尽量回到原来的选中项，而不是每次都掉回最后一个可见 entry。
* **Tau.CodingAgent.Tests**: 新增 raw/composition navigator 的 `Shift+L` 回归，以及 router 回归 `TryHandleAsync_TreeInteractiveCommand_LabelEditReopensTreeAtSameEntryAndPersistsLabel()`，固定 label 变更、tree reopen 和选中项延续语义。

### Design Intent (Why)

interactive tree 已经有 metadata、summary prompt、filter hotkeys 和 label timestamp toggle，但 label 仍然只能退回 `/label` 命令做，和上游 TreeSelector 的 in-place 体验差了一块。这一轮不引入新的 overlay/editor，只把 `Shift+L -> host prompt -> reopen tree` 这条最短闭环补齐，继续把 tree/metadata/composition 主线往上游交互习惯收口。

### Validation

* `dotnet test tests/Tau.CodingAgent.Tests/Tau.CodingAgent.Tests.csproj --no-restore --filter "FullyQualifiedName~CodingAgentCommandRouterTests.TryHandleAsync_TreeInteractiveCommand_" --verbosity minimal` 通过（7 tests）。
* `dotnet test tests/Tau.CodingAgent.Tests/Tau.CodingAgent.Tests.csproj --no-restore --filter "FullyQualifiedName~CodingAgentTreeInteractiveNavigatorTests|FullyQualifiedName~CodingAgentTreeCompositionNavigatorTests|FullyQualifiedName~CodingAgentTreeFoldStateTests" --verbosity minimal` 通过（33 tests）。
* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。
* `git diff --check` 通过；仅出现既有 CRLF normalization warning。

### Files Modified

* `src/Tau.CodingAgent/Program.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentHost.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentTreeCompositionNavigator.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentTreeInteractiveNavigator.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentTreeCompositionNavigatorTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentTreeFoldStateTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentTreeInteractiveNavigatorTests.cs`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`
* `docs/histories/2026-05/20260524-1603-composition-multiline-input.md`
* `next.md`

## [2026-05-24 19:28] | Task: Interactive tree filter key parity correction

### Execution Context

* **Agent ID**: `Codex 主控`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 下一轮

### Changes Overview

**Scope:** `Tau.CodingAgent`

### Key Actions

* **Tau.CodingAgent**: 重新对照上游 `keybindings.ts` 与 `tree-selector.ts` 后，interactive tree filter hotkeys 改为真实上游语义：`Ctrl+D` default、`Ctrl+T` no-tools、`Ctrl+U` user-only、`Ctrl+L` labeled-only、`Ctrl+A` all、`Ctrl+O/Shift+Ctrl+O` cycle forward/backward。
* **Tau.CodingAgent**: raw / composition 两条 navigator 的 header hint 一并更新，不再把 `Ctrl+O` 误写成 `all` toggle。
* **Tau.CodingAgent**: `labeled-only` 不再靠渲染文本里的 `[`/`]` 猜测 label，而是给 `CodingAgentTreeViewItem` 新增 `HasLabel` 元数据，由 tree store 基于真实 `LabelsById` 填充，避免 `[tool-only]` 这类普通文本被误筛进 labeled-only 结果。
* **Tau.CodingAgent.Tests**: raw/composition tree tests 新增热键语义回归，固定 labeled-only、default reset 和 cycle 行为。

### Design Intent (Why)

这轮不是新功能堆叠，而是纠偏。上一刀按 `tree.md` 文档把 `Ctrl+O` 接成了 `all`，但继续对照上游真实代码时发现 `tree.md` 已和实现漂移：真正的语义在 `keybindings.ts + tree-selector.ts`，不是文档表格。既然目标是 parity，就应该以实际运行代码为准。同时，labeled-only 如果继续靠字符串启发式，会把 `[tool-only]` 这种文本误当 label，过滤结果本身就不可信，所以一并收正到 label 元数据。

### Validation

* `dotnet test tests/Tau.CodingAgent.Tests/Tau.CodingAgent.Tests.csproj --no-restore --filter "FullyQualifiedName~CodingAgentTreeInteractiveNavigatorTests|FullyQualifiedName~CodingAgentTreeCompositionNavigatorTests" --verbosity minimal` 通过（31 tests）。
* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。
* `git diff --check` 通过；仅出现既有 CRLF normalization warning。

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentTreeCompositionNavigator.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentTreeInteractiveNavigator.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentTreeSessionStore.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentTreeCompositionNavigatorTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentTreeInteractiveNavigatorTests.cs`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`
* `docs/histories/2026-05/20260524-1603-composition-multiline-input.md`
* `next.md`

## [2026-05-24 19:40] | Task: Interactive tree summary cancel reopen

### Execution Context

* **Agent ID**: `Codex 主控`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 下一轮

### Changes Overview

**Scope:** `Tau.CodingAgent`

### Key Actions

* **Tau.CodingAgent**: interactive tree 选中一个会触发 summary prompt 的节点后，如果用户在 summary 选择阶段取消，router 不再直接返回 `tree navigation cancelled`，而是重开 tree，并尽量保持原来的选中 entry。
* **Tau.CodingAgent**: `CodingAgentHost.PromptForTreeNavigationAsync(...)` 现在把 `custom summary prompt` 的取消语义也往上游收：如果 `summary-prompt>` 输入被取消，会回到 `tree-summary>` 重新选择，而不是直接退出整条 tree 流程。
* **Tau.CodingAgent.Tests**: 新增 router 回归 `TryHandleAsync_TreeInteractiveCommand_CancelledSummaryPromptReopensTreeAtSameSelection()`，固定 summary choice 取消后 tree 会 reopen，且第二次 navigator 会收到与第一次相同的 preferred selected entry id。

### Design Intent (Why)

上一轮虽然已经把 summary prompt 接进 interactive tree 了，但取消路径仍然太“硬”：一旦从 summary prompt 退出，整条 tree 导航就结束，用户必须重新进一次 `/tree --interactive`。上游真实交互不是这样，它会把用户送回 tree 或 summary 选择本身。本轮继续只收这条交互缝，让 tree navigation 更接近真正连续的浏览流程。

### Validation

* `dotnet test tests/Tau.CodingAgent.Tests/Tau.CodingAgent.Tests.csproj --no-restore --filter "FullyQualifiedName~CodingAgentCommandRouterTests.TryHandleAsync_TreeInteractiveCommand_" --verbosity minimal` 通过（8 tests）。
* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。
* `git diff --check` 通过；仅出现既有 CRLF normalization warning。

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentHost.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`
* `docs/histories/2026-05/20260524-1603-composition-multiline-input.md`
* `next.md`

## [2026-05-24 19:49] | Task: Interactive tree current leaf no-op

### Execution Context

* **Agent ID**: `Codex 主控`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 下一轮

### Changes Overview

**Scope:** `Tau.CodingAgent`

### Key Actions

* **Tau.CodingAgent**: interactive tree 现在会把“选择当前 leaf”识别成真正的 no-op。命中时直接返回 `Already at this point`，不再继续走 branch/session_info 追加路径。
* **Tau.CodingAgent**: 这个修正同时收掉了一个更隐蔽的偏差：当当前 leaf 本身是 user message 时，Tau 之前会错误地把它当成普通 user selection，回退到 parent/root 并预填 draft；现在当前 leaf user 也会保持 no-op。
* **Tau.CodingAgent.Tests**: 新增 router 回归 `TryHandleAsync_TreeInteractiveCommand_SelectingCurrentLeafIsNoOp()`，固定三件事：不触发 summary prompt、不加载 draft、不增加 tree entry count。

### Design Intent (Why)

这刀是纯 parity 修正。上游 `interactive-mode.ts` 在 tree selector 里会先判 `entryId === realLeafId`，命中时直接显示 `Already at this point`。Tau 之前缺这个短路，导致选择当前 leaf 也会污染 session tree，甚至在 current-user-leaf 情况下误触发 rewind。既然目标是把 tree 从“能用”继续推进到“合同正确”，这个 no-op 必须先补。

### Validation

* `dotnet test tests/Tau.CodingAgent.Tests/Tau.CodingAgent.Tests.csproj --no-restore --filter "FullyQualifiedName~CodingAgentCommandRouterTests.TryHandleAsync_TreeInteractiveCommand_" --verbosity minimal` 通过（9 tests）。
* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。
* `git diff --check` 通过；仅出现既有 CRLF normalization warning。

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`
* `docs/histories/2026-05/20260524-1603-composition-multiline-input.md`
* `next.md`

## [2026-05-24 20:00] | Task: Tree navigation label foundation

### Execution Context

* **Agent ID**: `Codex 主控`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 下一轮

### Changes Overview

**Scope:** `Tau.CodingAgent`

### Key Actions

* **Tau.CodingAgent**: `CodingAgentTreeNavigationDecision` 现在可携带可选 `label`。这不是新的用户 prompt，而是给后续 extension/runtime surface 预留的 tree navigation 语义槽位。
* **Tau.CodingAgent**: tree navigation 在 summarizing 时会把该 label 绑定到新生成的 `branch_summary` entry；不 summarizing 时则绑定到目标 entry。实现方式仍遵守 Tau 当前 append-only JSONL 语义：不是改 entry 字段，而是在 branch / branch_summary 之后追加一条 `label` entry。
* **Tau.CodingAgent.Tests**: 新增两条 router 回归，分别固定
  - summary label 会落到 `branch_summary` entry
  - no-summary label 会落到被导航的目标 entry

### Design Intent (Why)

这刀是基础设施收口，不是 UI 扩张。上游 `navigateTree(options.label)` 明确支持把 label 带进 tree navigation，但 Tau 之前只有独立 `/label` 命令和 tree 内 `Shift+L` 编辑，没有“branch switch 本身携带 label”的能力。考虑到 Tau 还没接完整 extension/runtime surface，先把 JSONL tree 的底层语义补齐，比硬塞一个临时 CLI 参数更稳，也更接近后续真正要复用的核心合同。

### Validation

* `dotnet test tests/Tau.CodingAgent.Tests/Tau.CodingAgent.Tests.csproj --no-restore --filter "FullyQualifiedName~CodingAgentCommandRouterTests.TryHandleAsync_TreeInteractiveCommand_" --verbosity minimal` 通过（11 tests）。
* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。
* `git diff --check` 通过；仅出现既有 CRLF normalization warning。

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentTreeSessionStore.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`
* `docs/histories/2026-05/20260524-1603-composition-multiline-input.md`
* `next.md`

## [2026-05-24 19:24] | Task: Interactive tree in-place navigation baseline

### Execution Context

* **Agent ID**: `Codex 主控`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 继续不断移植，在移植进度100%之前不要询问我，你继续制定下一步计划，不断往复执行

### Changes Overview

**Scope:** `Tau.CodingAgent`, `Tau.Tui`

### Key Actions

* **Tau.CodingAgent**: `CodingAgentTreeViewItem` 新增 `MessageRole` 和 `NavigationDraftText` 元数据，tree store 会在构造 view item 时把 user message 的完整文本一并带出来，供 interactive tree selection 直接复用。
* **Tau.CodingAgent**: `CodingAgentTreeSessionStore` / controller 新增 `BranchTo(string?)` 语义，允许 interactive tree 直接切到某个 entry，或在选择根 user message 时切回 `root`。
* **Tau.CodingAgent**: `CodingAgentCommandRouter.HandleTreeCommand(...)` 不再把 interactive tree 结果只是当成 “selected entry” 字符串返回；现在会真的执行 in-place navigation：
  - 选中非-user entry：直接切到该点并恢复 session
  - 选中 user entry：回到 parent/root，并把原 user 文本预填回 draft
* **Tau.Tui**: `InteractiveConsoleSession` 新增 `SetDraft(...)`，同时同步 session buffer 和 editor buffer，保证 tree navigation 预填的 user draft 会进入下一轮输入。
* **Tau.CodingAgent.Tests**: 更新 interactive tree baseline 回归，并新增 `TryHandleAsync_TreeInteractiveCommand_SelectingUserMessageRewindsAndLoadsDraft()`，固定 user selection 会回退 branch 并预填 draft。

### Design Intent (Why)

前几轮已经把 interactive tree 的数据面、搜索、filter、metadata drill-down 和 tree<-metadata 选中态闭环做起来了，但主路径依然只是“看树”和“选中一个 id”。这轮把 `/tree --interactive` 推进到真正可用的 in-place navigation baseline，让 tree 不再只是 viewer，而是真正能在同一 session 内切换上下文。

### Validation

* `dotnet test tests/Tau.CodingAgent.Tests/Tau.CodingAgent.Tests.csproj --no-restore --filter "TryHandleAsync_TreeInteractiveCommand_InvokesNavigatorAndReturnsSelection|TryHandleAsync_TreeInteractiveCommand_UsesFullFilteredTreeInsteadOfDefaultWindow|TryHandleAsync_TreeInteractiveCommand_SelectingUserMessageRewindsAndLoadsDraft" --verbosity minimal` 通过（3 tests）。
* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。期间并行验证曾触发一次 `Tau.WebUi.dll` 文件锁；串行重跑后通过，说明是本地并行构建冲突，不是代码问题。
* `git diff --check` 通过；仅出现既有 Git CRLF normalization warning。

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentHost.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentTreeSessionStore.cs`
* `src/Tau.Tui/Runtime/InteractiveConsoleSession.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `docs/histories/2026-05/20260524-1603-composition-multiline-input.md`
* `next.md`

## [2026-05-25 00:08] | Task: Shared session switch coordinator baseline

### Execution Context

* **Agent ID**: `Codex 主控`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 继续不断移植，在移植进度100%之前不要询问我，你继续制定下一步计划，不断往复执行

### Changes Overview

**Scope:** `Tau.CodingAgent`

### Key Actions

* **Tau.CodingAgent**: 新增独立的 `CodingAgentSessionSwitchCoordinator`，把 session replacement 相关的统一链路从 `CommandRouter` / `RpcHost` 里抽出来：
  - 收集 current branch summary candidates
  - 构造 `sessionSwitchHook` state
  - 处理 hook cancel / decision override
  - 处理 `sessionSwitchPrompt`
  - 真正执行 `SummarizeBranchAsync(...)`
  - 写回 root-level `branch_summary` 并恢复 runner snapshot
* **Tau.CodingAgent**: `CommandRouter` 现在不再自己维护 `/new`、`/resume`、`.jsonl` `/import` 的 session switch summary 链；`RpcHost` 也不再自己维护 `new_session` / `switch_session` 的那一份拷贝逻辑。
* **Tau.CodingAgent**: session switch 相关 contract 也已随之从 `CommandRouter.cs` 里迁出到共享 coordinator 文件，让 shared types 和 shared logic 位于同一个边界，而不是继续嵌在 CLI router 文件末尾。
* **Tau.CodingAgent.Tests**: 跑 mixed focused 回归，确认共享 coordinator 接管后，CLI hook、RPC hook 和 tree interactive summary 三条路径都还保持正确行为。

### Design Intent (Why)

前几轮已经把 session switch 的 prompt seam、hook seam 和 target metadata 都做出来了，但真正的决策链还分散在 `CommandRouter` 和 `RpcHost` 两边，各自复制一套。这轮的目标就是把“同一条链两份实现”的状态收掉，真正形成一个单一入口，为后续继续收口到更像 runtime event payload/result 的 contract 做准备。

### Validation

* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --filter "TryHandleAsync_ResumeCommandWithoutArgs_CanUseSessionSwitchHookDecisionWithoutPrompt|RunAsync_NewSessionReturnsCancelledWhenSessionSwitchHookCancels|RunAsync_SwitchSessionReturnsCancelledWhenSessionSwitchHookCancels|TryHandleAsync_TreeInteractiveCommand_CanSummarizeAbandonedBranchBeforeNavigation" --verbosity minimal` 通过（4 tests）。
* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。
* `git diff --check` 通过；仅出现既有 Git CRLF normalization warning。

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentSessionSwitchCoordinator.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentRpcHost.cs`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`
* `docs/histories/2026-05/20260524-1603-composition-multiline-input.md`
* `next.md`

## [2026-05-24 22:18] | Task: Session switch hook target metadata baseline

### Execution Context

* **Agent ID**: `Codex 主控`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 继续不断移植，在移植进度100%之前不要询问我，你继续制定下一步计划，不断往复执行

### Changes Overview

**Scope:** `Tau.CodingAgent`

### Key Actions

* **Tau.CodingAgent**: `sessionSwitchHook` state 不再只给 `CurrentSessionPath/TargetSessionPath`。现在还会携带：
  - current 侧 `sessionName/provider/model`
  - target 侧 session metadata：`name/provider/model/messageCount`
* **Tau.CodingAgent**: target metadata 不重新发明读取逻辑，而是直接复用 `CodingAgentTreeSessionStore.TryGetResumeSessionInfo(...)`。这样 resume/import/switch hook 能基于真实 target session 事实做决定，而不是只拿到一个 path 字符串。
* **Tau.CodingAgent.Tests**: 扩了已有 hook-focused 回归，固定 CLI resume hook 和 RPC switch/new hook 都能看到 current 与 target metadata；不额外新开低收益测试文件。

### Design Intent (Why)

上一轮已经把 `sessionSwitchHook` seam 做成可 cancel / override，但如果 hook 只能看到 path，就还不够像上游实际会用的 `session_before_switch` 决策面。这轮把 target session 的最基本事实也补进来，让后续如果继续接 runtime/extension 逻辑，可以直接根据目标 session 的名字、模型和消息规模做判断，而不是再临时自己开文件读 JSONL。

### Validation

* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --filter "TryHandleAsync_ResumeCommandWithoutArgs_CanUseSessionSwitchHookDecisionWithoutPrompt|RunAsync_NewSessionReturnsCancelledWhenSessionSwitchHookCancels|RunAsync_SwitchSessionReturnsCancelledWhenSessionSwitchHookCancels" --verbosity minimal` 通过（3 tests）。
* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。
* `git diff --check` 通过；仅出现既有 Git CRLF normalization warning。

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentTreeSessionStore.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentRpcHost.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentRpcHostTests.cs`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`
* `docs/histories/2026-05/20260524-1603-composition-multiline-input.md`
* `next.md`

## [2026-05-24 22:02] | Task: Session switch hook seam baseline

### Execution Context

* **Agent ID**: `Codex 主控`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 继续不断移植，在移植进度100%之前不要询问我，你继续制定下一步计划，不断往复执行

### Changes Overview

**Scope:** `Tau.CodingAgent`

### Key Actions

* **Tau.CodingAgent**: 在 `sessionSwitchPrompt` 之外新增 `sessionSwitchHook` seam，允许内部 hook 对 `/new`、`/resume`、`.jsonl` `/import` 以及 RPC `new_session` / `switch_session` 做两类事：
  - `cancel`：阻止这次 session replacement
  - `decision override`：直接给出 `CodingAgentTreeNavigationDecision`，绕过用户 prompt
* **Tau.CodingAgent**: `CodingAgentCommandRouter` 现在会先跑 hook，再决定是否需要 `sessionSwitchPrompt`。这意味着没有 prompt 的情况下，hook 也能驱动 summary 行为；没有 tree session controller 的情况下，hook 也至少能 cancel switch。
* **Tau.CodingAgent**: `CodingAgentRpcHost` 也接上同一条 hook seam。命中 cancel 时不再报错，而是返回 success response 且 `cancelled=true`，更接近上游 `session_before_switch` 的取消合同。
* **Tau.CodingAgent.Tests**: 新增 hook-focused 回归，固定：
  - CLI `/new` hook cancel
  - CLI `/resume` hook override summary
  - RPC `new_session` hook cancel -> `cancelled=true`
  - RPC `switch_session` hook cancel -> `cancelled=true`
  - 同时保留一条 tree interactive summary 回归，确认 tree 路径没有被这条新 seam 污染

### Design Intent (Why)

前两轮已经把 session replacement 的 prompt context 和 seam 结构整理出来，但它仍然只是“提示用户选 summary 的地方”。上游 `session_before_switch` 的核心价值之一是：在真正切 session 前，内部 runtime/extension 可以直接取消或拦截。这轮就是把这层最小 hook 合同补出来，让 Tau 的 session lifecycle 不再只有 UI prompt，没有真正的内部切口。

### Validation

* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --filter "TryHandleAsync_NewCommand_WhenSessionSwitchHookCancels_KeepsCurrentSession|TryHandleAsync_ResumeCommandWithoutArgs_CanUseSessionSwitchHookDecisionWithoutPrompt|RunAsync_NewSessionReturnsCancelledWhenSessionSwitchHookCancels|RunAsync_SwitchSessionReturnsCancelledWhenSessionSwitchHookCancels|TryHandleAsync_TreeInteractiveCommand_CanSummarizeAbandonedBranchBeforeNavigation" --verbosity minimal` 通过（5 tests）。
* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。
* `git diff --check` 通过；仅出现既有 Git CRLF normalization warning。

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentRpcHost.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentRpcHostTests.cs`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`
* `docs/histories/2026-05/20260524-1603-composition-multiline-input.md`
* `next.md`

## [2026-05-24 21:41] | Task: Session switch prompt seam split baseline

### Execution Context

* **Agent ID**: `Codex 主控`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 继续不断移植，在移植进度100%之前不要询问我，你继续制定下一步计划，不断往复执行

### Changes Overview

**Scope:** `Tau.CodingAgent`

### Key Actions

* **Tau.CodingAgent**: session replacement 不再复用 `treeNavigationPrompt` 这条调用链。`/new`、`/resume`、`.jsonl` `/import` 现在单独走 `sessionSwitchPrompt` seam；tree interactive summary 继续只走 `treeNavigationPrompt`。
* **Tau.CodingAgent**: 新增 `CodingAgentSessionSwitchPromptState`，把 session replacement 的 prompt 输入边界从 tree navigation state 里拆出来；它继续共享同一套 `CodingAgentTreeNavigationDecision`，所以行为不变，但调用意图和后续扩展边界已经分开。
* **Tau.CodingAgent**: `CodingAgentHost` 新增 `PromptForSessionSwitchAsync(...)`，内部仍复用同一套 prompt 选择逻辑，但不再让 session replacement 通过 `PromptForTreeNavigationAsync(...)` 这条 tree 命名路径进入。
* **Tau.CodingAgent.Tests**: `/new`、`/resume`、`.jsonl` `/import` 的 summary 回归切到 `sessionSwitchPrompt` seam；同时补跑一条现有 tree interactive summary 测试，确认拆 seam 后 tree 本身没有被打坏。

### Design Intent (Why)

上一轮已经把 session switch prompt 的上下文补成 `reason/targetSessionPath`，但调用链名字仍然是 `treeNavigationPrompt`。这会让后续真正接近上游 `session_before_switch` 时，继续背着 tree 语义包袱。这个切片的目的就是把 session replacement 的 prompt seam 单独拎出来，让 tree navigation 和 session switching 共享 decision 结构，但不再共享同一个入口。

### Validation

* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --filter "TryHandleAsync_ImportCommand_WithJsonlPathCanSummarizeCurrentBranchBeforeSwitching|TryHandleAsync_NewCommand_CanSummarizeCurrentBranchBeforeResettingSession|TryHandleAsync_ResumeCommandWithoutArgs_CanSummarizeCurrentBranchBeforeSwitching|TryHandleAsync_TreeInteractiveCommand_CanSummarizeAbandonedBranchBeforeNavigation" --verbosity minimal` 通过（4 tests）。
* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。
* `git diff --check` 通过；仅出现既有 Git CRLF normalization warning。

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentHost.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`
* `docs/histories/2026-05/20260524-1603-composition-multiline-input.md`
* `next.md`

## [2026-05-24 21:24] | Task: Session switch prompt context baseline

### Execution Context

* **Agent ID**: `Codex 主控`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 继续不断移植，在移植进度100%之前不要询问我，你继续制定下一步计划，不断往复执行

### Changes Overview

**Scope:** `Tau.CodingAgent`

### Key Actions

* **Tau.CodingAgent**: `CodingAgentTreeNavigationPromptState` 不再只带 entry count / token estimate。现在对 tree navigation、`/new`、`/resume`、`.jsonl` `/import` 统一带上 `reason` 和可选 `targetSessionPath`，让切换前决策至少能知道“为什么切”和“准备切去哪里”。
* **Tau.CodingAgent**: `MaybeSummarizeCurrentBranchBeforeSessionSwitchAsync(...)` 现在按调用方显式传 `NewSession`、`ResumeSession`、`ImportSession`；tree navigation 仍走 `TreeNavigation`。这让同一条 summary hook 更接近上游 `session_before_switch(reason, targetSessionFile)` 的事实面。
* **Tau.CodingAgent**: `CodingAgentHost.PromptForTreeNavigationAsync(...)` 会根据 `reason/targetSessionPath` 输出更具体的 prompt 文案：`tree switch`、`new session`、`resume <file>`、`import <file>`，不再把所有切换都压成同一句抽象提示。
* **Tau.CodingAgent.Tests**: 不额外新开低收益测试文件，而是在已有 `/new`、`/resume`、`.jsonl` `/import` summary 回归里顺手固定 prompt state 的 `reason/targetSessionPath`，确认 richer context 确实沿着真实调用链传到了 prompt seam。

### Design Intent (Why)

前几轮已经把 summary/cancel hook 接到了多个 session replacement 路径，但这条 hook 仍然缺少上游 `session_before_switch` 最关键的两个事实字段：`reason` 和 `targetSessionFile`。这轮先把这两个上下文补进 Tau 的现有 seam，让后续如果继续接 extension/runtime switch hook，不需要再回头重塑 prompt state 和调用链。

### Validation

* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --filter "TryHandleAsync_ImportCommand_WithJsonlPathCanSummarizeCurrentBranchBeforeSwitching|TryHandleAsync_NewCommand_CanSummarizeCurrentBranchBeforeResettingSession|TryHandleAsync_ResumeCommandWithoutArgs_CanSummarizeCurrentBranchBeforeSwitching" --verbosity minimal` 通过（3 tests）。
* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。
* `git diff --check` 通过；仅出现既有 Git CRLF normalization warning。

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentHost.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`
* `docs/histories/2026-05/20260524-1603-composition-multiline-input.md`
* `next.md`

## [2026-05-24 21:08] | Task: JSONL import summary hook baseline

### Execution Context

* **Agent ID**: `Codex 主控`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 继续不断移植，在移植进度100%之前不要询问我，你继续制定下一步计划，不断往复执行

### Changes Overview

**Scope:** `Tau.CodingAgent`

### Key Actions

* **Tau.CodingAgent**: `.jsonl` 形式的 `/import <path>` 不再直接把当前 tree session 整体替掉。现在如果目标 JSONL 不是当前 active tree file，会先复用已有的 session switch summary/cancel 决策：
  - `no summary`：直接切到目标 JSONL session
  - `summarize` / `custom prompt`：先把当前 branch 收成 root-level `branch_summary`，再切到目标 JSONL session
  - `cancel`：保留当前 session，不执行 import
* **Tau.CodingAgent**: flat JSON snapshot import 继续保持原边界，不走这条 hook。它仍然是“恢复 flat snapshot 并把当前 tree 重写成该 snapshot”，不假装它和 JSONL session replacement 是同一语义。
* **Tau.CodingAgent.Tests**: 新增 `.jsonl` import summary 和 cancel focused 回归，并把既有 flat JSON import 回归一起带上，确认这轮只影响 JSONL replacement 路径。

### Design Intent (Why)

上一轮已经把 `/resume`、`/new` 和对应 RPC session replacement 接到同一条 branch summary hook，但 CLI `/import <path.jsonl>` 仍然是直接切走当前 session。上游 `importFromJsonl(...)` 本质上也是 session replacement，所以这轮把 JSONL import 拉进相同语义链；同时保留 flat JSON import 的旧边界，避免“导入 snapshot”和“切换 JSONL session”两类行为被混成一类。

### Validation

* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --filter "TryHandleAsync_ImportCommand_RestoresFlatSessionSnapshotWithoutInvokingRunner|TryHandleAsync_ImportCommand_WithJsonlPathCanSummarizeCurrentBranchBeforeSwitching|TryHandleAsync_ImportCommand_WithJsonlPathWhenSummaryPromptCancelled_KeepsCurrentSession" --verbosity minimal` 通过（3 tests）。
* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。
* `git diff --check` 通过；仅出现既有 Git CRLF normalization warning。

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`
* `docs/histories/2026-05/20260524-1603-composition-multiline-input.md`
* `next.md`

## [2026-05-24 20:46] | Task: New / new-session summary hook baseline

### Execution Context

* **Agent ID**: `Codex 主控`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 继续不断移植，在移植进度100%之前不要询问我，你继续制定下一步计划，不断往复执行

### Changes Overview

**Scope:** `Tau.CodingAgent`

### Key Actions

* **Tau.CodingAgent**: `/new` 不再无条件直接 reset。现在如果当前 tree branch 有可摘要内容，会先复用上一轮引入的 summary/cancel 决策：
  - `no summary`：直接开新 session
  - `summarize` / `custom prompt`：先把当前 branch 收成 root-level `branch_summary`，再 append `action=new` 开启新 branch
  - `cancel`：保留当前 session，不做 reset
* **Tau.CodingAgent**: `CodingAgentRpcHost new_session` 也补成和 `switch_session` 对称的 headless contract，支持 `summarizeCurrentBranch`、`customInstructions`、`replaceInstructions`，并继续保留既有 `parentSession` JSONL header metadata。
* **Tau.CodingAgent**: `CodingAgentHost` 的 summary prompt 文案从 `tree switch leaves ...` 收口成更通用的 `switch leaves ...`，避免同一 prompt 同时服务 tree / resume / new 时语义错位。
* **Tau.CodingAgent.Tests**: 新增 `/new` summary、`/new` cancel、RPC `new_session` summary focused 回归，同时顺手把 `/resume` / `switch_session` summary 路径并入同一轮 targeted filter，确认新旧 switch hook 没互相打坏。

### Design Intent (Why)

上一轮 session summary hook 只接到了 `/resume` 和 RPC `switch_session`。这意味着“切到别的 session”前能停车总结，但“直接开新 session”仍然是硬 reset，语义并不对称。这轮把 `/new` / `new_session` 也拉进同一条 switch hook，让 session lifecycle 至少在 Tau-native 主路径上形成统一的“切换前先处理当前 branch”的基线。

### Validation

* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --filter "TryHandleAsync_NewCommand_CanSummarizeCurrentBranchBeforeResettingSession|TryHandleAsync_NewCommand_WhenSwitchSummaryPromptCancelled_KeepsCurrentSession|RunAsync_NewSessionCanSummarizeCurrentBranchBeforeResetting|TryHandleAsync_ResumeCommandWithoutArgs_CanSummarizeCurrentBranchBeforeSwitching|RunAsync_SwitchSessionCanSummarizeCurrentBranchBeforeSwitching" --verbosity minimal` 通过（5 tests）。
* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。
* `git diff --check` 通过；仅出现既有 Git CRLF normalization warning。

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentHost.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentRpcHost.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentRpcHostTests.cs`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`
* `docs/histories/2026-05/20260524-1603-composition-multiline-input.md`
* `next.md`

## [2026-05-24 20:18] | Task: Resume / switch-session summary hook baseline

### Execution Context

* **Agent ID**: `Codex 主控`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 继续不断移植，在移植进度100%之前不要询问我，你继续制定下一步计划，不断往复执行

### Changes Overview

**Scope:** `Tau.CodingAgent`

### Key Actions

* **Tau.CodingAgent**: `CodingAgentTreeSessionController` 新增 `SummarizeCurrentBranchToRoot(...)`，把当前 branch 汇总成 root-level `branch_summary` 后，立即补一条 `session_info` 保存当前 session 的 provider/model/name，避免后续 resume 只剩 summary 文本却丢失会话身份。
* **Tau.CodingAgent**: `CodingAgentCommandRouter` 的 `/resume` 两条路径（直接 path/latest 与 selector 选择）现在统一在真正切 session 前走 `MaybeSummarizeCurrentBranchBeforeSessionSwitchAsync(...)`。如果当前 branch 有可摘要内容且 prompt 可用，会复用现有 tree summary decision：
  - `no summary`：直接切 session
  - `summarize` / `custom prompt`：先把当前 branch 收成 root summary，再切 session
  - `cancel`：取消这次 resume switch
* **Tau.CodingAgent**: `CodingAgentRpcHost switch_session` 现在也支持 headless summary hook：新增可选 `summarizeCurrentBranch`、`customInstructions`、`replaceInstructions` 字段；命中时先汇总当前 branch，再恢复目标 JSONL session，并把 summary entry/token 统计回写到 RPC response。
* **Tau.CodingAgent.Tests**: 新增 router / RPC focused 回归，固定 `/resume` switch summary、cancel、`switch_session` 显式 summarize contract，以及 summary 后当前 session 仍能恢复原 provider/model/name。
* **Docs**: 只同步 `next.md`、active parity plan 和同一条 history，不扩 README / QUALITY / ARCHITECTURE。

### Design Intent (Why)

前面已经把 branch summary 的显式入口补到 `/fork --summarize` 和 interactive tree navigation，但 session 切换仍然是“直接离开当前 branch”。这轮把 hook 接到真正的 session lifecycle 上，让 `/resume` 和 headless `switch_session` 在切走前也能复用同一条 summary contract，同时保持默认行为保守：只有 prompt/显式参数要求时才触发 LLM summary，不把自动切 session 变成隐藏的重调用。

### Validation

* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --filter "TryHandleAsync_ResumeCommandWithoutArgs_CanSummarizeCurrentBranchBeforeSwitching|TryHandleAsync_ResumeCommandWithoutArgs_WhenSwitchSummaryPromptCancelled_KeepsCurrentSession|RunAsync_SwitchSessionCanSummarizeCurrentBranchBeforeSwitching" --verbosity minimal` 通过（3 tests）。
* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。
* `git diff --check` 通过；仅出现既有 Git CRLF normalization warning。

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentRpcHost.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentTreeSessionStore.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentRpcHostTests.cs`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`
* `docs/histories/2026-05/20260524-1603-composition-multiline-input.md`
* `next.md`

## [2026-05-24 19:38] | Task: Interactive tree summary prompt baseline

### Execution Context

* **Agent ID**: `Codex 主控`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 继续不断移植，在移植进度100%之前不要询问我，你继续制定下一步计划，不断往复执行

### Changes Overview

**Scope:** `Tau.CodingAgent`, `Tau.Tui`

### Key Actions

* **Tau.CodingAgent**: `CollectBranchSummaryMessages(...)` 与 `BranchWithSummary(...)` 现在支持 nullable tree target/root 语义，给 interactive tree 的 user-entry rewind / root navigation 路径复用同一 summary 事实源。
* **Tau.CodingAgent**: `CodingAgentCommandRouter` 新增 `CodingAgentTreeNavigationPromptState` / `CodingAgentTreeNavigationDecision` 和 `treeNavigationPrompt` callback。interactive tree 选择后，如果会离开当前 branch 且确实有可摘要内容，就会先走这个 summary 决策，而不是无条件切过去。
* **Tau.CodingAgent**: interactive tree in-place navigation 现在支持三种行为：
  - `no summary`：直接导航
  - `summarize`：生成默认 tree branch summary 后再导航
  - `summarize with custom prompt`：把用户附加指令传给 `SummarizeBranchAsync(...)` 后再导航
  - `cancel`：取消这次 tree navigation
* **Tau.Tui**: `InteractiveConsoleSession.SetDraft(...)` 继续承担 tree user-entry navigation 的 draft preload；`CodingAgentHost` 则新增最小 `tree-summary>` / `summary-prompt>` prompt baseline，把 summary 选择接回当前 interactive/composition 会话。
* **Tau.CodingAgent.Tests**: 新增 router 回归 `TryHandleAsync_TreeInteractiveCommand_CanSummarizeAbandonedBranchBeforeNavigation()`，固定 interactive tree 确实会调用 summary decision、把 custom prompt 传给 `SummarizeBranchAsync(...)`，并把 summary message 带回当前 session。

### Design Intent (Why)

上一轮已经把 interactive tree 做成真正的 in-place navigation baseline，但和上游相比还有一个明显缺口：离开当前 branch 时没有 summary 选择，用户只能在 tree 和 `/fork --summarize` 之间来回切。本轮把 summary 决策接回同一条 tree navigation 路径，让 interactive tree 更接近上游真正可用的 branch-switch 行为。

### Validation

* `dotnet test tests/Tau.CodingAgent.Tests/Tau.CodingAgent.Tests.csproj --no-restore --filter "TryHandleAsync_TreeInteractiveCommand_InvokesNavigatorAndReturnsSelection|TryHandleAsync_TreeInteractiveCommand_UsesFullFilteredTreeInsteadOfDefaultWindow|TryHandleAsync_TreeInteractiveCommand_SelectingUserMessageRewindsAndLoadsDraft|TryHandleAsync_TreeInteractiveCommand_CanSummarizeAbandonedBranchBeforeNavigation" --verbosity minimal` 通过（4 tests）。
* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。
* `git diff --check` 通过；仅出现既有 Git CRLF normalization warning。

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentHost.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentTreeSessionStore.cs`
* `src/Tau.Tui/Runtime/InteractiveConsoleSession.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `docs/histories/2026-05/20260524-1603-composition-multiline-input.md`
* `next.md`

## [2026-05-24 19:09] | Task: Interactive tree filter hotkeys

### Execution Context

* **Agent ID**: `Codex 主控`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 继续不断移植，在移植进度100%之前不要询问我，你继续制定下一步计划，不断往复执行

### Changes Overview

**Scope:** `Tau.CodingAgent`

### Key Actions

* **Tau.CodingAgent**: raw / composition 两条 interactive tree navigator 现在都支持上游同名 filter hotkeys：`Ctrl+U` 在 `user-only` 与 `default` 之间切换，`Ctrl+O` 在 `all` 与 `default` 之间切换。
* **Tau.CodingAgent**: tree header hint 也同步更新，明确展示 `Ctrl+U user-only` 和 `Ctrl+O all`，继续和现有 `f` cycle 共存。
* **Tau.CodingAgent.Tests**: 新增 raw navigator 回归 `NavigateAsync_CtrlUTogglesUserOnly_AndCtrlOTogglesAll()`，固定两个 hotkey 都会改变 filter 状态与可见条目数。

### Design Intent (Why)

interactive tree 这条主屏已经逐步接回 metadata、richer search 和 label timestamp toggle，但上游 tree selector 里很高频的 `Ctrl+U` / `Ctrl+O` 还缺失，用户只能靠 `f` cycle 一直轮。这个差距不大，但很直接地影响 tree 浏览效率，所以继续在同一块 navigator 内补上。

### Validation

* `dotnet test tests/Tau.CodingAgent.Tests/Tau.CodingAgent.Tests.csproj --no-restore --filter "NavigateAsync_CtrlUTogglesUserOnly_AndCtrlOTogglesAll|NavigateAsync_ShiftTTogglesLabelTimestampsInRenderedLines|RunAsync_LocalSearchUsesSearchTextWhenDisplayLineDoesNotContainQuery" --verbosity minimal` 通过（3 tests）。
* `dotnet build Tau.slnx --no-restore --verbosity minimal` 通过，0 warning / 0 error。
* `git diff --check` 通过；仅出现既有 CRLF normalization warning。

### Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentTreeInteractiveNavigator.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentTreeCompositionNavigator.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentTreeInteractiveNavigatorTests.cs`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `docs/histories/2026-05/20260524-1603-composition-multiline-input.md`
* `next.md`
