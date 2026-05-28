## [2026-05-26 14:52] | Task: CodingAgent tool truncation parity

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10`

### User Query

> 继续不断移植，在移植进度 100% 前不要询问，快速推进下一轮 pi-mono parity，少做低收益文档和单元测试。

### Changes Overview

**Scope:** `Tau.CodingAgent`

**Key Actions:**

* Added shared CodingAgent tool output `truncateHead` parity with upstream defaults: 2000 lines and 50KB, byte-counted with UTF-8 and without partial head lines.
* Updated `read_file` text reads to apply upstream line/byte truncation, emit `Showing lines ... Use offset=...` continuation notices, expose truncation details via `ToolResult.Details`, and return the upstream-style first-line-too-large bash fallback notice.
* Updated `ls` to apply the same 50KB output truncation after entry formatting and to expose both entry-limit and byte truncation details via `ToolResult.Details`.
* Kept the slice limited to tool output parity. Image reads, syntax-highlight rendering, and TUI render detail presentation remain separate backlog items.

### Design Intent

Upstream `read` and `ls` both protect the model context by truncating tool output before it is returned, and they preserve enough metadata for renderers to show truncation state. Tau already had paging and entry-format parity, but still returned arbitrarily large text/directory output. This slice closes that context-safety gap without touching RPC/settings or the broader renderer stack.

### Files Modified

* `src/Tau.CodingAgent/Tools/ToolOutputTruncator.cs`
* `src/Tau.CodingAgent/Tools/ReadFileTool.cs`
* `src/Tau.CodingAgent/Tools/ListDirectoryTool.cs`
* `tests/Tau.CodingAgent.Tests/ReadFileToolTests.cs`
* `tests/Tau.CodingAgent.Tests/ListDirectoryToolTests.cs`
* `next.md`

### Validation

* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --filter "FullyQualifiedName~ReadFileToolTests|FullyQualifiedName~ListDirectoryToolTests" --verbosity minimal` passed: 13/13.
* `dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal` passed with 0 warnings and 0 errors.
* `git diff --check -- <slice paths>` completed without whitespace errors; only the existing `next.md` CRLF normalization warning was printed.
