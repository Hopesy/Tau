## [2026-05-28 00:36] | Task: read_file metadata and Mom attach details

### Execution Context

* **Agent ID**: `Codex main + Mom worker`
* **Base Model**: `GPT-5 Codex`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 继续下一轮快速移植；使用多 Agent 并行，降低文档同步速度，少做单元测试，把时间优先投到 pi-mono parity 的真实缺口。

### Changes Overview

**Scope:** `Tau.CodingAgent`, `Tau.Mom`

**Key Actions:**

* **CodingAgent read_file render metadata**: `read_file` now accepts the upstream-compatible `file_path` alias and returns `ReadFileToolDetails` for text/image reads, including path, kind, language, line range, total lines, continuation status, image mime, size and omit status.
* **Mom attach fidelity**: Mom `attach` now reports the final attachment title when present and exposes structured `MomAttachToolDetails` metadata for path, sandbox/workspace path, file name, title and byte size.
* **Minimal plan sync**: Updated the active CodingAgent parity plan, the full migration plan decisions, and `next.md` without expanding README/ARCHITECTURE/QUALITY.

### Design Intent

`read_file` already had text/image content parity, but renderer-facing metadata was still missing. This slice keeps the implementation dependency-free and gives later TUI/HTML/custom renderers stable facts without claiming full syntax-highlight or auto-resize parity.

Mom `attach` already staged files, but visible output and metadata did not fully match the upstream title-first behavior. The change keeps the outer attachment list contract stable while adding structured details where Tau already carries tool metadata.

### Files Modified

* `src/Tau.CodingAgent/Tools/ReadFileTool.cs`
* `tests/Tau.CodingAgent.Tests/ReadFileToolTests.cs`
* `src/Tau.Mom/MomTools.cs`
* `tests/Tau.Agent.Tests/MomSandboxAndToolsTests.cs`
* `next.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`
