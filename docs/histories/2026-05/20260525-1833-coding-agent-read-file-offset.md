## [2026-05-25 18:33] | Task: CodingAgent read file offset

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET`

### User Query

> 继续快速推进下一轮移植，优先真实上游行为缺口，少做大范围文档和低收益单元测试。

### Changes Overview

**Scope:** `Tau.CodingAgent`

**Key Actions:**

* Changed `read_file` schema and behavior from Tau's old 0-based offset to upstream-compatible 1-based offset.
* Kept `offset=0` as a forgiving alias for the first line to reduce breakage from existing local calls.
* Added offset out-of-bounds error: `Offset N is beyond end of file (X lines total)`.
* Added limit continuation hint: `[N more lines in file. Use offset=M to continue.]`.
* Added focused `ReadFileToolTests` coverage for 1-based offset, continuation hint, out-of-bounds error, and existing missing-file error.

### Design Intent

Upstream `read` tool documents `offset` as 1-indexed and gives the model an actionable next offset when output is limited. Tau now matches that core text-file paging behavior without broadening the slice into image handling, byte truncation, or richer renderer parity.

### Files Modified

* `src/Tau.CodingAgent/Tools/ReadFileTool.cs`
* `tests/Tau.CodingAgent.Tests/ReadFileToolTests.cs`
* `next.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`

### Validation

* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --filter ReadFileTool --verbosity minimal` passed: 4/4.
* `dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal` passed with 0 warnings and 0 errors.
