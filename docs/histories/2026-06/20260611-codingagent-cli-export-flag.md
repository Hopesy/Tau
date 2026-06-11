## [2026-06-11] | Task: CodingAgent CLI `--export <file.jsonl> [output.html]` 收口

### 🤖 Execution Context

* **Agent ID**: `Claude`
* **Base Model**: `Opus 4.8 (1M context)`
* **Runtime**: `Windows PowerShell / .NET 10`

### 📥 User Query

> 按 `GOAL.md` 继续 Tau 100% pi-mono parity 迁移，从 Phase 2 Candidate Queue 领取下一个 CodingAgent top-level CLI 合同切片。

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent`（CLI 参数 + 独立导出入口 + 测试），docs/history 同步

**Key Actions:**

* **`--export` 解析**：`CodingAgentCliArguments` 新增 `Export` 字段，`--export <path>` / `--export=<path>` 显式解析，从 `OptionsWithValue` 移除原先 consume-and-discard 行为。
* **独立导出入口**：新增 `CodingAgentSessionFileExporter`，对照上游 `core/export-html/index.ts` 的 `exportFromFile`：校验输入为存在的 `.jsonl` session 文件，通过 `CodingAgentTreeSessionController.OpenOrCreate(path)` 加载，复用现有 `CodingAgentHtmlSessionExporter.Export(...)` 渲染 standalone HTML；输出路径缺省时回落 `pi-session-<input>.html`。返回结构化 `Result(Success, OutputPath, ErrorMessage)`，把 `IOException`/`UnauthorizedAccessException`/`InvalidOperationException`/`JsonException` 归一为友好错误而不是崩溃。
* **Program 接线**：`Program.cs` 在 version 检查后、进入 runner/UI 构造前处理 `--export`，存在时调用导出入口并 `Exported to: <path>` / `Error: <message>` 后退出（成功 0 / 失败 1），不进入交互、print 或 RPC 路径。
* **测试**：新增 `CodingAgentSessionFileExporterTests`（默认输出名、显式输出路径、文件缺失、非 `.jsonl`、无效 session header 五个场景，用真实 `CodingAgentTreeSessionStore` 写入 session 再导出），并补 `CodingAgentInitialMessageBuilderTests` 的 `--export` / `--export=` 解析回归。

### 🧠 Design Intent (Why)

`--export` 是上游一个完整的“读取 session 文件 -> 渲染 HTML -> 退出”流程，独立于交互 runner。Tau 已经具备 JSONL tree session 加载（`CodingAgentTreeSessionController`）和 HTML transcript 渲染（`CodingAgentHtmlSessionExporter`）两块能力，本切片只需要把它们按上游 `exportFromFile` 的语义拼成一个早退出 CLI 入口。把核心逻辑放进可测试的 `CodingAgentSessionFileExporter` 而不是留在 `Program.cs` 顶层语句里，是为了让该合同有 targeted 回归，而不是只靠手动 smoke。

### ✅ Validation

* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --filter "CodingAgentSessionFileExporterTests" --no-restore --verbosity minimal`：5/5 passed。
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`：passed（Ai 287、Agent 123、Tui 251、CodingAgent 569、WebUi 61、Pods 216）。

### 📁 Files Modified

* `src/Tau.CodingAgent/Program.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentInitialMessageBuilder.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentSessionFileExporter.cs`（新增）
* `tests/Tau.CodingAgent.Tests/CodingAgentSessionFileExporterTests.cs`（新增）
* `tests/Tau.CodingAgent.Tests/CodingAgentInitialMessageBuilderTests.cs`
* `next.md`
* `docs/QUALITY_SCORE.md` 不变（无新增风险面）
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
