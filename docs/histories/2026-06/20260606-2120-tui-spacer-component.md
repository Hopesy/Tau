## [2026-06-06 21:20] | Task: Tui spacer component parity

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI / PowerShell / .NET 10`

### User Query

> 继续执行 GOAL.md 的 Tau 100% pi-mono parity 计划，推进当前 Tui component 切片。

### Changes Overview

**Scope:** `Tau.Tui`

**Key Actions:**

* **Spacer component**: 新增 `TuiSpacer`，对照上游 `packages/tui/src/components/spacer.ts` 固定默认 1 行空输出、`SetLines(...)` 动态更新和负数 line count 空输出行为。
* **Composition coverage**: 在 `TuiComponentTests` 中补充 spacer standalone 与 container composition 测试，固定其作为 vertical gap 插入组件树的行为。
* **Docs sync**: 同步 parity matrix、active plan、`next.md`、`docs/ARCHITECTURE.md` 和 `docs/QUALITY_SCORE.md`，把 `spacer.ts` 从“无独立 parity”更新为本地已覆盖。

### Design Intent (Why)

Tui component foundation 已覆盖 text、box、container、loader、markdown 和 image，但 matrix 仍明确指出 `components/spacer.ts` 没有独立 parity。该组件行为简单，适合作为小而完整的 Phase 2 Tui surface closure，不引入新的渲染抽象。

### Validation

* `dotnet test tests\Tau.Tui.Tests\Tau.Tui.Tests.csproj --no-restore --verbosity minimal`
  * Passed: 240/240
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`
  * Passed: `Tau.Ai.Tests` 280, `Tau.Agent.Tests` 115, `Tau.Tui.Tests` 240, `Tau.CodingAgent.Tests` 435, `Tau.WebUi.Tests` 44, `Tau.Pods.Tests` 166

### Files Modified

* `src/Tau.Tui/Components/TuiSpacer.cs`
* `tests/Tau.Tui.Tests/TuiComponentTests.cs`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `next.md`
