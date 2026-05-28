## [2026-05-28 01:07] | Task: Tui/WebUi/Pods parity slice

### Execution Context

* **Agent ID**: `Codex main + Tui/WebUi/Pods workers`
* **Base Model**: `GPT-5 Codex`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 下一轮；继续多 Agent 并行快速移植，减少低收益文档和单元测试，把时间优先投到真实 pi-mono parity 缺口。

### Changes Overview

**Scope:** `Tau.Tui`, `Tau.WebUi`, `Tau.Pods`

**Key Actions:**

* **Tui input editor kill ring**: `InteractiveInputEditor` now keeps a session-local kill ring for Ctrl+K, Ctrl+U, Ctrl+Backspace and Ctrl+Delete; Ctrl+Y yanks the newest killed text and Alt+Y rotates the just-yanked text.
* **WebUi CodingAgent import strategy**: CodingAgent JSONL preview/import now returns and persists a stable conservative import strategy, including whether current-branch-only mode is active, whether only timeline messages are imported, whether the branch tree is persisted, and warning codes.
* **Pods logs CLI parity**: `logs` now supports `--config` / `--pod`, active pod fallback for the safe `<deployment-name>` form, and JSON `failureKind`; lifecycle logs events reuse the same failure classification.

### Design Intent

This round keeps the migration moving through small user-visible contracts rather than broad rewrites. Tui closes a high-frequency readline gap without implementing undo or a full editor box. WebUi makes conservative CodingAgent import behavior explicit before full branch/tree persistence exists. Pods aligns `logs` with the active-pod CLI pattern already used by model/deployment/vLLM commands without adding real SSH smoke requirements.

### Files Modified

* `src/Tau.Tui/Runtime/InteractiveInputEditor.cs`
* `tests/Tau.Tui.Tests/InteractiveInputEditorTests.cs`
* `src/Tau.WebUi/Contracts/WebChatContracts.cs`
* `src/Tau.WebUi/Services/CodingAgentJsonlSessionPreviewer.cs`
* `src/Tau.WebUi/Services/WebChatService.cs`
* `tests/Tau.WebUi.Tests/CodingAgentJsonlSessionPreviewerTests.cs`
* `tests/Tau.WebUi.Tests/WebUiEndpointTests.cs`
* `src/Tau.Pods/Cli/PodsCli.cs`
* `src/Tau.Pods/Models/PodLifecycleResults.cs`
* `src/Tau.Pods/Services/PodLifecycleService.cs`
* `tests/Tau.Pods.Tests/PodsCliTests.cs`
* `next.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`

### Verification

* `dotnet test tests\Tau.Tui.Tests\Tau.Tui.Tests.csproj --no-restore --filter FullyQualifiedName~InteractiveInputEditorTests --verbosity minimal` -> 54/54 passed.
* WebUi worker reported focused `WebUiEndpointTests` 18/18, `CodingAgentJsonlSessionPreviewerTests` 12/12 and `WebChatJsonlExporterTests` 10/10 passed.
* Pods worker reported focused `FullyQualifiedName~Logs` 21/21 passed and `dotnet build src\Tau.Pods\Tau.Pods.csproj --no-restore --verbosity minimal` passed with 0 warnings / 0 errors.

### Follow-up [2026-05-28 02:20]

**Scope:** `Tau.CodingAgent`, `Tau.Tui`, `Tau.WebUi`

**Key Actions:**

* **CodingAgent HTML read_file details**: `RuntimeCodingAgentRunner` now preserves `ToolResult.Details` by tool call id, and HTML export renders `read_file` details as a metadata block with path/kind/language/line/truncation/image fields.
* **Tui input editor undo**: `InteractiveInputEditor` now has a session-local minimal undo stack for Ctrl+Z / Ctrl+_, covering regular edits, multiline edits, history recall and kill/yank operations through text + cursor snapshots.
* **WebUi import audit UI**: CodingAgent JSONL import preview/import/reopen now surfaces `importStrategy` audit fields in `session-meta`, including timeline-only/current-branch/branch-tree-persisted state, leaf id and warning codes.
* **Pods worker result**: the Pods worker timed out and was closed; no new Pods code slice from that worker is accepted in this follow-up.

**Design Intent:**

This follow-up closes three already-started parity seams without broadening the round: read_file render details are consumed by the existing HTML exporter first, Tui undo stays local to one input session, and WebUi exposes the conservative import behavior without pretending branch-tree persistence exists.

**Files Modified:**

* `src/Tau.CodingAgent/Runtime/ICodingAgentToolResultDetailsProvider.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentHtmlSessionExporter.cs`
* `src/Tau.CodingAgent/Runtime/RuntimeCodingAgentRunner.cs`
* `tests/Tau.CodingAgent.Tests/FakeCodingAgentRunner.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `src/Tau.Tui/Runtime/InteractiveInputEditor.cs`
* `tests/Tau.Tui.Tests/InteractiveInputEditorTests.cs`
* `src/Tau.WebUi/Ui/WebUiPage.cs`
* `tests/Tau.WebUi.Tests/WebUiPageTests.cs`
* `next.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`
* `docs/histories/2026-05/20260528-0107-tui-webui-pods-parity.md`

**Verification:**

* `dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal` -> passed, 0 warnings / 0 errors.
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --filter "FullyQualifiedName~ExportHtmlCommand_RendersReadFileToolResultMetadata|FullyQualifiedName~ExportHtml" --verbosity minimal` -> 22/22 passed.
* `dotnet test tests\Tau.Tui.Tests\Tau.Tui.Tests.csproj --no-restore --filter FullyQualifiedName~InteractiveInputEditorTests --verbosity minimal` -> 57/57 passed.
* `dotnet test tests\Tau.WebUi.Tests\Tau.WebUi.Tests.csproj --no-restore --filter FullyQualifiedName~WebUiPageTests --verbosity minimal` -> 1/1 passed.

### Follow-up [2026-05-28 02:46]

**Scope:** `Tau.CodingAgent`, `Tau.Tui`, `Tau.WebUi`

**Key Actions:**

* **Tui autocomplete provider foundation**: added `TuiCombinedAutocompleteProvider` for slash command fuzzy suggestions, slash command argument completions, relative/root path completion, and `@file` attachment completion with quoting for filenames containing spaces. This is still provider logic only; Tab popup/editor wiring remains a later slice.
* **CodingAgent RPC bash streamed output**: `ICodingAgentShellRunner` now accepts progress events, the system shell runner streams stdout/stderr chunks, and RPC `bash` emits `bash_output` chunks plus lifecycle `bash_event` records while preserving the final `bash` response contract.
* **WebUi current-branch import control**: the Web UI import panel now exposes a `Current branch only` checkbox, and both CodingAgent JSONL preview/import calls reuse the same `currentBranchOnly=true|false` query helper so the UI default remains conservative full timeline unless the user opts in.
* **Pods worker result**: the Pods rollback worker did not finish within the integration window and was closed; no Pods rollback code from that worker is accepted in this follow-up.

**Design Intent:**

This follow-up advances three small parity baselines with direct user-facing value while keeping the batch narrow. Tui gets reusable autocomplete data before terminal popup wiring, RPC bash becomes observable for embedding clients without changing final response compatibility, and WebUi makes current-branch-only import an explicit user choice instead of a hidden query contract.

**Files Modified:**

* `src/Tau.Tui/Runtime/TuiAutocompleteProvider.cs`
* `tests/Tau.Tui.Tests/TuiAutocompleteProviderTests.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentShellRunner.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentRpcHost.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentRpcHostTests.cs`
* `src/Tau.WebUi/Ui/WebUiPage.cs`
* `tests/Tau.WebUi.Tests/WebUiPageTests.cs`
* `next.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`
* `docs/histories/2026-05/20260528-0107-tui-webui-pods-parity.md`

**Verification:**

* `dotnet test tests\Tau.Tui.Tests\Tau.Tui.Tests.csproj --no-restore --filter FullyQualifiedName~TuiAutocompleteProviderTests --verbosity minimal` -> 5/5 passed.
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --filter "FullyQualifiedName~CodingAgentRpcHostTests.RunAsync_Bash" --verbosity minimal` -> 4/4 passed.
* `dotnet test tests\Tau.WebUi.Tests\Tau.WebUi.Tests.csproj --no-restore --filter FullyQualifiedName~WebUiPageTests --verbosity minimal` -> 1/1 passed.
* `git diff --check -- next.md docs\exec-plans\active\2026-05-10-tau-complete-pi-mono-port.md docs\exec-plans\active\2026-05-20-coding-agent-parity-gap-analysis.md docs\histories\2026-05\20260528-0107-tui-webui-pods-parity.md src\Tau.Tui\Runtime\TuiAutocompleteProvider.cs tests\Tau.Tui.Tests\TuiAutocompleteProviderTests.cs src\Tau.CodingAgent\Runtime\CodingAgentRpcHost.cs src\Tau.CodingAgent\Runtime\CodingAgentShellRunner.cs tests\Tau.CodingAgent.Tests\CodingAgentRpcHostTests.cs src\Tau.WebUi\Ui\WebUiPage.cs tests\Tau.WebUi.Tests\WebUiPageTests.cs` -> passed, with existing CRLF normalization warnings on `next.md` and `src/Tau.WebUi/Ui/WebUiPage.cs`.

### Follow-up [2026-05-28 02:53]

**Scope:** `Tau.Tui`, `Tau.CodingAgent`

**Key Actions:**

* **Tui autocomplete Tab baseline**: the default keymap now maps Tab to `EditorAction.Complete`; `InteractiveInputEditor` can accept an `ITuiAutocompleteProvider`, applies the first suggestion directly, and records the edit in the existing undo stack so Ctrl+Z restores the previous text/cursor state.
* **CodingAgent extension EventBus foundation**: added a pure in-memory `CodingAgentExtensionEventBus` seam for future extension runtime work. It supports string event type and typed record publish/subscribe, ordered dispatch, `IDisposable` unsubscribe, handler exception aggregation, and cancellation passthrough.
* **Pods worker state**: the new Pods rollback worker has not returned yet in this follow-up, so no Pods rollback code is accepted or documented as complete here.

**Design Intent:**

This follow-up turns the previous autocomplete provider into a usable editor path without taking on popup rendering or multi-candidate UI. The EventBus slice fixes a core extension-runtime primitive locally before attempting TypeScript runtime loading, custom tools, or extension lifecycle integration.

**Files Modified:**

* `src/Tau.Tui/Abstractions/EditorAction.cs`
* `src/Tau.Tui/Runtime/KeyBindingMap.cs`
* `src/Tau.Tui/Runtime/InteractiveInputEditor.cs`
* `tests/Tau.Tui.Tests/KeyBindingMapTests.cs`
* `tests/Tau.Tui.Tests/InteractiveInputEditorTests.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentExtensionEventBus.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentExtensionEventBusTests.cs`
* `next.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`
* `docs/histories/2026-05/20260528-0107-tui-webui-pods-parity.md`

**Verification:**

* `dotnet test tests\Tau.Tui.Tests\Tau.Tui.Tests.csproj --no-restore --filter "FullyQualifiedName~KeyBindingMapTests|FullyQualifiedName~InteractiveInputEditorTests.ReadLineAsync_Tab|FullyQualifiedName~InteractiveInputEditorTests.ReadLineAsync_CtrlZUndoesAutocomplete|FullyQualifiedName~TuiAutocompleteProviderTests" --verbosity minimal` -> 19/19 passed.
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --filter FullyQualifiedName~EventBus --verbosity minimal` -> 5/5 passed.

### Follow-up [2026-05-28 03:56]

**Scope:** `Tau.CodingAgent`, `Tau.Pods`

**Key Actions:**

* **CodingAgent resume selector sort/threaded baseline**: `/resume` selector now carries `parentSession` metadata into `CodingAgentResumeSessionInfo`, supports `Ctrl+S` to cycle threaded/recent/relevance sort modes, and renders visible parent/child sessions as a tree in threaded mode when there is no search query.
* **CodingAgent resume selector verification fix**: the tree prefix renderer now emits first-level child labels as `└─ ...` instead of adding an extra root indentation level. The focused tests also create parent/child JSONL sessions with the same `cwd` that the selector current-scope filter uses.
* **Pods vLLM rollback closeout**: accepted the rollback slice that adds explicit `vllm rollback`, active pod fallback for the safe `<deployment-name>` form, stable JSON `failureKind`, and SSH-only rollback classification.

**Design Intent:**

This follow-up closes two already-started parity cuts without widening the round. Resume selector browsing now matches the upstream idea of switching sort mode and browsing session threads while keeping relevance scoring intentionally lightweight. Pods rollback stays in the existing vLLM orchestration surface as cleanup, not a multi-version rollout state machine.

**Files Modified:**

* `src/Tau.CodingAgent/Runtime/CodingAgentTreeSessionStore.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentResumeSelector.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentResumeSelectorTests.cs`
* `src/Tau.Pods/Cli/PodsCli.cs`
* `src/Tau.Pods/Models/PodVllmResults.cs`
* `src/Tau.Pods/Services/PodVllmOrchestrationService.cs`
* `tests/Tau.Pods.Tests/PodsCliTests.cs`
* `tests/Tau.Pods.Tests/PodVllmOrchestrationServiceTests.cs`
* `next.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`
* `docs/histories/2026-05/20260528-0107-tui-webui-pods-parity.md`

**Verification:**

* `dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal` -> passed, 0 warnings / 0 errors.
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --filter FullyQualifiedName~CodingAgentResumeSelectorTests --verbosity minimal` -> 12/12 passed.
* `dotnet test tests\Tau.Pods.Tests\Tau.Pods.Tests.csproj --no-restore --filter FullyQualifiedName~Rollback --verbosity minimal` -> 4/4 passed.

### Follow-up [2026-05-28 04:10]

**Scope:** `Tau.Tui`, `Tau.CodingAgent` docs alignment

**Key Actions:**

* **Tui autocomplete repeated Tab cycling**: accepted the parallel worker slice in `InteractiveInputEditor` so the first Tab starts an autocomplete session from the original text/cursor/prefix, repeated Tab cycles through the same candidate list with wraparound, and Ctrl+Z returns to the text/cursor before autocomplete started instead of stepping through every candidate.
* **Autocomplete session invalidation**: non-autocomplete edits clear the active autocomplete session, avoiding stale candidate reuse after cursor movement, typed input, deletion, history navigation, kill/yank, or undo.
* **CodingAgent ls plan correction**: corrected the active CodingAgent parity plan to reflect current code reality: `ls` already has 50KB output truncation and `ListDirectoryToolDetails`; the remaining gap is the upstream TUI custom renderer/render component, not byte truncation.

**Design Intent:**

This follow-up keeps the migration moving on a real interactive deficit while avoiding popup/overlay work. Repeated Tab cycling gives users a usable multi-candidate completion path now; visual candidate UI and completion hints remain separate TUI renderer work. The `ls` plan correction prevents future agents from reopening an already-completed byte truncation slice.

**Files Modified:**

* `src/Tau.Tui/Runtime/InteractiveInputEditor.cs`
* `tests/Tau.Tui.Tests/InteractiveInputEditorTests.cs`
* `next.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`
* `docs/histories/2026-05/20260528-0107-tui-webui-pods-parity.md`

**Verification:**

* `dotnet test tests\Tau.Tui.Tests\Tau.Tui.Tests.csproj --no-restore --filter "FullyQualifiedName~InteractiveInputEditorTests.ReadLineAsync_Tab|FullyQualifiedName~InteractiveInputEditorTests.ReadLineAsync_CtrlZUndoesAutocomplete|FullyQualifiedName~TuiAutocompleteProviderTests" --verbosity minimal` -> 7/7 passed.

### Follow-up [2026-05-28 04:15]

**Scope:** `Tau.CodingAgent`

**Key Actions:**

* **HTML `ls` tool result renderer**: accepted the parallel worker slice that renders `ls` results as a directory-aware HTML list for both current `name/` directory entries and old `[DIR]` lines, while keeping bracketed notices such as `[50.0KB limit reached]` as normal output items.
* **HTML `ls` metadata/details**: `CodingAgentHtmlSessionExporter` now consumes `ListDirectoryToolDetails` for `ls` tool results and emits a `directory metadata` block with entry limit and truncation summary. The `ls` tool call summary also displays `limit`.
* **Plan alignment**: updated `next.md` and the CodingAgent active plan so the remaining `ls` gap is the TUI custom renderer/render component, not HTML details or byte truncation.

**Design Intent:**

The HTML exporter already had custom render paths for known tools. This slice closes the `ls` half of the existing tool-result details work without changing tool execution, public RPC contracts, or the TUI renderer backlog.

**Files Modified:**

* `src/Tau.CodingAgent/Runtime/CodingAgentHtmlSessionExporter.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `next.md`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`
* `docs/histories/2026-05/20260528-0107-tui-webui-pods-parity.md`

**Verification:**

* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --filter FullyQualifiedName~TryHandleAsync_ExportHtmlCommand_RendersListDirectoryToolResultDetails --verbosity minimal` -> 1/1 passed.
* `dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal` -> passed, 0 warnings / 0 errors.
* `git diff --check -- src\Tau.CodingAgent\Runtime\CodingAgentHtmlSessionExporter.cs tests\Tau.CodingAgent.Tests\CodingAgentCommandRouterTests.cs` -> passed.

### Follow-up [2026-05-28 04:28]

**Scope:** `Tau.Tui`

**Key Actions:**

* **Autocomplete reverse cycling**: accepted the parallel worker slice that adds `EditorAction.CompletePrevious` and maps default Shift+Tab to it. `InteractiveInputEditor` now reuses the same autocomplete session for forward Tab and reverse Shift+Tab cycling.
* **Initial Shift+Tab behavior**: when no autocomplete session exists yet, Shift+Tab starts a session from the current text/cursor/prefix and selects the final candidate first, matching reverse-navigation expectations.
* **Autocomplete invalidation**: focused tests now cover that ordinary editing after autocomplete clears the current session, so a later Tab starts from the edited text instead of reusing stale candidates.

**Design Intent:**

This keeps autocomplete useful without taking on popup UI. The editor now has a keyboard-only bidirectional candidate path while preserving the earlier undo boundary: Ctrl+Z returns to the text/cursor before autocomplete started, not to each intermediate candidate.

**Files Modified:**

* `src/Tau.Tui/Abstractions/EditorAction.cs`
* `src/Tau.Tui/Runtime/KeyBindingMap.cs`
* `src/Tau.Tui/Runtime/InteractiveInputEditor.cs`
* `tests/Tau.Tui.Tests/KeyBindingMapTests.cs`
* `tests/Tau.Tui.Tests/InteractiveInputEditorTests.cs`
* `next.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`
* `docs/histories/2026-05/20260528-0107-tui-webui-pods-parity.md`

**Verification:**

* `dotnet test tests\Tau.Tui.Tests\Tau.Tui.Tests.csproj --no-restore --filter "FullyQualifiedName~InteractiveInputEditorTests.ReadLineAsync_ShiftTabCyclesAutocompleteBackwardAndWrapsFromLastItem|FullyQualifiedName~InteractiveInputEditorTests.ReadLineAsync_CtrlBackspaceClearsAutocompleteSession|FullyQualifiedName~KeyBindingMapTests" --verbosity minimal` -> 15/15 passed.
