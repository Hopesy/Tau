## [2026-06-06 20:12] | Task: Tui stdin-buffer foundation

### Execution Context

* Agent ID: `Codex`
* Base Model: `GPT-5`
* Runtime: `Windows / PowerShell / .NET 10`

### User Query

> 按照 `GOAL.md` 继续推进 Tau 100% pi-mono parity。

### Changes Overview

Scope: `Tau.Tui`

Key Actions:

* 对照上游 `packages/tui/src/stdin-buffer.ts` 新增 `TuiInputSequenceBuffer`。
* 固定普通字符、跨 chunk escape sequence、SGR mouse、OSC/DCS/APC、SS3/meta、bracketed paste、高字节 meta、flush/clear/timeout/destroy 行为。
* 同步 active parity matrix、active 100% plan、`next.md`、`docs/QUALITY_SCORE.md` 和 `docs/ARCHITECTURE.md`，明确该切片不关闭真实 `ProcessTerminal` / TTY / PTY smoke。

### Design Intent (Why)

真实 terminal host 需要先有稳定的 stdin sequence buffering 合同，否则 raw input、Kitty keyboard protocol response、bracketed paste 和后续 editor input 之间会混在同一层处理。该切片先把可纯单元测试固定的输入拆包层落到 `Tau.Tui`，为后续 `ProcessTerminal` lifecycle / drain / live terminal smoke 做底座。

### Files Modified

* `src/Tau.Tui/Runtime/TuiInputSequenceBuffer.cs`
* `tests/Tau.Tui.Tests/TuiInputSequenceBufferTests.cs`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `next.md`

### Validation

* `dotnet test tests\Tau.Tui.Tests\Tau.Tui.Tests.csproj --no-restore --verbosity minimal` passed: 225/225.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` passed: `Tau.Ai.Tests` 280, `Tau.Agent.Tests` 115, `Tau.Tui.Tests` 225, `Tau.CodingAgent.Tests` 435, `Tau.WebUi.Tests` 44, `Tau.Pods.Tests` 166; src/test build reported 0 warnings and 0 errors.
