## [2026-06-06 21:05] | Task: Tui ProcessTerminal lifecycle parity

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI / PowerShell / .NET 10`

### User Query

> 继续执行 GOAL.md 的 Tau 100% pi-mono parity 计划，推进当前 Tui terminal host 切片。

### Changes Overview

**Scope:** `Tau.Tui`

**Key Actions:**

* **ProcessTerminal lifecycle seam**: 新增 `TuiProcessTerminal`、`ITuiProcessTerminalTransport` 和 `ITuiTerminalTimer`，对照上游 `packages/tui/src/terminal.ts` 固定 raw mode、UTF-8 input、stdin resume/pause、bracketed paste、resize、Unix dimension refresh seam、Windows VT input seam、Kitty protocol、modifyOtherKeys fallback、input drain、ANSI cursor/clear/title 等本地可测试合同。
* **Diagnostic write logging**: 新增 `TuiTerminalWriteLog`，支持 `TAU_TUI_WRITE_LOG` 并兼容上游 `PI_TUI_WRITE_LOG`；配置为目录时生成 timestamped log file，写日志失败不影响 terminal output。
* **Targeted coverage**: 新增 `TuiProcessTerminalTests`，覆盖 start/stop、Windows/Unix 分支、stdin-buffer 拆包、paste re-wrap、Kitty response 消费、modifyOtherKeys fallback、drain、ANSI operation、double-start guard 和 write-log 行为。
* **Docs sync**: 同步 `next.md`、parity matrix、`docs/ARCHITECTURE.md` 和 `docs/QUALITY_SCORE.md`，明确本轮关闭的是本地 lifecycle seam，不关闭真实 TTY/PTY、硬件 cursor 或 live terminal smoke。

### Design Intent (Why)

上轮已完成 stdin-buffer、loader、Markdown 和 terminal image foundation，但 `terminal.ts` 仍缺承载这些能力的 terminal lifecycle seam。这里用 transport/timer 抽象把平台 I/O 和时间行为隔离出来，让 raw mode、keyboard protocol、drain 和日志路径可以在单元测试中确定性验证，同时保留后续接真实 PTY/TTY smoke 的边界。

### Validation

* `dotnet test tests\Tau.Tui.Tests\Tau.Tui.Tests.csproj --no-restore --verbosity minimal`
  * Passed: 237/237
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`
  * Passed: `Tau.Ai.Tests` 280, `Tau.Agent.Tests` 115, `Tau.Tui.Tests` 237, `Tau.CodingAgent.Tests` 435, `Tau.WebUi.Tests` 44, `Tau.Pods.Tests` 166

### Files Modified

* `src/Tau.Tui/Runtime/TuiProcessTerminal.cs`
* `tests/Tau.Tui.Tests/TuiProcessTerminalTests.cs`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `next.md`
