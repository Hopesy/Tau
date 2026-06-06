## [2026-06-06 21:30] | Task: Tui text/truncated-text component parity

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI / PowerShell / .NET 10`

### User Query

> 继续执行 GOAL.md 的 Tau 100% pi-mono parity 计划，推进当前 Tui component 切片。

### Changes Overview

**Scope:** `Tau.Tui`

**Key Actions:**

* **Text component baseline**: 扩展 `TuiTextBlock`，对照上游 `packages/tui/src/components/text.ts` 固定空白文本不渲染、tab-normalized wrap、padding、full-line background formatter 和 cache invalidation 行为。
* **TruncatedText component**: 新增 `TuiTruncatedText`，对照上游 `components/truncated-text.ts` 固定首行截断、水平/垂直 padding、空文本 padded output 和 width-aware ellipsis。
* **Rendering helper**: 新增 `TuiText.ApplyBackgroundToLine(...)`，复用 visible-width padding 后再交给 background formatter。
* **Targeted coverage**: 在 `TuiComponentTests` 中补充 Text background/cache、TruncatedText first-line/padding/ellipsis 和 empty-text render 测试。
* **Docs sync**: 同步 parity matrix、active plan、`next.md`、`docs/ARCHITECTURE.md` 和 `docs/QUALITY_SCORE.md`，明确本轮关闭的是 `text.ts` / `truncated-text.ts` 库层 baseline，不关闭 Box background/cache 或真实 TUI host。

### Design Intent (Why)

上轮已关闭独立 spacer component parity，但 matrix 同一行仍把 `components/text.ts` 与 `truncated-text.ts` 留在 broader widget parity 缺口里。这里选择组件层小切片：保留现有 `TuiTextBlock` 调用形状，只补上上游 Text 的 background formatter/cache 行为，并用独立 `TuiTruncatedText` 避免继续用 `wrap: false` 混合承担截断组件语义。

### Validation

* `dotnet test tests\Tau.Tui.Tests\Tau.Tui.Tests.csproj --no-restore --verbosity minimal`
  * Passed: 243/243
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`
  * Passed: `Tau.Ai.Tests` 280, `Tau.Agent.Tests` 115, `Tau.Tui.Tests` 243, `Tau.CodingAgent.Tests` 435, `Tau.WebUi.Tests` 44, `Tau.Pods.Tests` 166

### Files Modified

* `src/Tau.Tui/Rendering/TuiText.cs`
* `src/Tau.Tui/Components/TuiTextBlock.cs`
* `src/Tau.Tui/Components/TuiTruncatedText.cs`
* `tests/Tau.Tui.Tests/TuiComponentTests.cs`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `next.md`
