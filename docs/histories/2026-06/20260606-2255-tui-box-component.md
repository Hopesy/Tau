## [2026-06-06 22:55] | Task: Tui box component parity

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI / PowerShell / .NET 10`

### User Query

> 继续执行 GOAL.md 的 Tau 100% pi-mono parity 计划，推进当前 Tui component 切片。

### Changes Overview

**Scope:** `Tau.Tui`

**Key Actions:**

* **Box component baseline**: 扩展 `TuiBox`，对照上游 `packages/tui/src/components/box.ts` 固定 child rendering、水平/垂直 padding、optional background formatter 和 `SetBackgroundFormatter(...)` 运行期切换。
* **Cache parity**: 为 `TuiBox` 增加上游同款 bg-sample/child-line cache invalidation，formatter 输出变化时无需显式 invalidate 也会重新渲染。
* **Targeted coverage**: 在 `TuiComponentTests` 中补充 Box background formatter/cache 测试，覆盖 cache reuse、formatter 切换、全行 background 和 visible width。
* **Docs sync**: 同步 parity matrix、active plan、`next.md`、`docs/ARCHITECTURE.md` 和 `docs/QUALITY_SCORE.md`，把 `components/text.ts` / `truncated-text.ts` / `box.ts` / `spacer.ts` 组件组更新为本地库层 `ported`，并明确完整 TUI host/theme/TTY 缺口仍在其它行跟踪。

### Design Intent (Why)

上一轮关闭了 Text / TruncatedText baseline 后，matrix 同一组件行剩余明确缺口是上游 `Box` 的 background formatter 与 cache 语义。这里继续选择同模块小切片：补 `TuiBox` 的上游可测试行为，不扩大到真实 terminal host、theme rendering 或 PTY smoke。

### Validation

* `dotnet test tests\Tau.Tui.Tests\Tau.Tui.Tests.csproj --no-restore --verbosity minimal`
  * Passed: 244/244
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`
  * Passed: `Tau.Ai.Tests` 280, `Tau.Agent.Tests` 115, `Tau.Tui.Tests` 244, `Tau.CodingAgent.Tests` 435, `Tau.WebUi.Tests` 44, `Tau.Pods.Tests` 166

### Files Modified

* `src/Tau.Tui/Components/TuiBox.cs`
* `tests/Tau.Tui.Tests/TuiComponentTests.cs`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `next.md`
