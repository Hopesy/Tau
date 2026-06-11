## [2026-06-11] | Task: CodingAgent CLI --append-system-prompt 与 prompt-input file/literal 解析

### 🤖 Execution Context

* **Agent ID**: `Claude`
* **Base Model**: `Opus 4.8 (1M context)`
* **Runtime**: `Windows PowerShell / .NET 10`

### 📥 User Query

> 按 `GOAL.md` 继续 Tau 100% pi-mono parity 迁移；本轮领取 CodingAgent top-level CLI parity 的 `contract` 切片。

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent`

**Key Actions:**

* **CLI 解析**: `CodingAgentCliArguments` 新增 repeatable `--append-system-prompt <text>`，解析为有序 `AppendSystemPrompt` 列表（保留输入顺序、空白值不入列），并从 `OptionsWithValue` 移到显式处理。
* **prompt-input 解析**: `Program.cs` 新增 `ResolvePromptInput`（对照上游 `resolve-loader.ts` 的 `resolvePromptInput`：存在的文件路径读取内容，否则按字面量），同时应用到 `--system-prompt` 和每个 `--append-system-prompt` 值，append 值用 `\n\n` 合并为单段 `CombineAppendSystemPrompt`。
* **runner 注入**: `RuntimeCodingAgentRunner.Create` / 构造函数新增 `appendSystemPrompt`，`BuildSystemPrompt` 在基础 prompt 之后、context files / skills 之前插入 append 段（对照上游 `buildSystemPrompt` 的 `appendSection`）；custom system prompt 也通过同一 `AppendToSystemPrompt` 追加。`RefreshSystemPromptResources`（`/reload`）保留 `_appendSystemPrompt`，避免重建时丢失。
* **Regression coverage**: 新增 RuntimeCodingAgentRunner 回归（generated prompt append、`/reload` 后保留 append、custom prompt append），并补 CLI 解析回归（repeatable append + 顺序）。

### 🧠 Design Intent (Why)

上游 `--append-system-prompt` 是可重复 flag，每个值通过 `resolvePromptInput` 解析为文件内容或字面量，最终 `join("\n\n")` 追加到 system prompt（custom 或 generated 都生效），位置在基础 prompt 之后、project context 之前。Tau 之前把该 flag 解析后直接丢弃，且 `--system-prompt` 只当字面量，未对齐 file-path 解析。本轮把 file/literal 解析共享给两个 flag 并把 append 段贯通到 runner 构造与 `/reload` 重建，关闭该 CLI contract 缺口；`--continue/--resume/--session/--tools/--models/--thinking` 等仍需各自 runtime wiring，保持 open。

### ✅ Validation

* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --filter "AppendSystemPrompt|CodingAgentInitialMessageBuilder|RefreshSystemPromptResources" --no-restore --verbosity minimal`：40/40 passed。
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`：passed（Ai 287、Agent 123、Tui 251、CodingAgent 546、WebUi 61、Pods 216）。

### 📁 Files Modified

* `src/Tau.CodingAgent/Program.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentInitialMessageBuilder.cs`
* `src/Tau.CodingAgent/Runtime/RuntimeCodingAgentRunner.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentInitialMessageBuilderTests.cs`
* `tests/Tau.CodingAgent.Tests/RuntimeCodingAgentRunnerTests.cs`
* `next.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
