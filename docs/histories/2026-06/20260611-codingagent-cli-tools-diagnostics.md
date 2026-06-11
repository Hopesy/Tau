## [2026-06-11] | Task: CodingAgent CLI `--tools` / `--no-tools` selection + arg diagnostics

### 🤖 Execution Context

* **Agent ID**: `Claude`
* **Base Model**: `Opus 4.8 (1M context)`
* **Runtime**: `Windows PowerShell / .NET 10`

### 📥 User Query

> 按 `GOAL.md` 继续 Tau 100% pi-mono parity 迁移，领取下一条可审计 contract 切片。

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent` CLI argument parsing + runner tool selection, targeted tests, docs/history sync

**Key Actions:**

* **`--tools` / `--no-tools` parsing**: `CodingAgentCliArguments` now parses `--no-tools` (boolean) and repeatable/inline `--tools <a,b>` / `--tools=a,b`. Upstream CLI tool names (`read, bash, edit, write, grep, find, ls`) map to Tau built-in tool `IAgentTool.Name` values (`read_file, shell, edit_file, write_file, grep, glob, ls`) via `CliToolNameToTauToolName`. Unknown tool names produce a `warning` diagnostic listing valid names; the parse drops the unknown name and continues.
* **Argument diagnostics**: added `CodingAgentCliDiagnostic(Type, Message)`. Unknown single-dash options (e.g. `-x`) now produce an `error` diagnostic instead of being silently swallowed. `Program.cs` prints collected diagnostics after parse (`Error:` / `Warning:` prefixes) and exits 1 when any error is present, mirroring upstream `main.ts`.
* **Runner tool selection**: `RuntimeCodingAgentRunner.CreateDefaultTools` gained an optional `selectedBuiltInToolNames` parameter. `null` keeps Tau's full default tool set (unchanged behavior); an explicit (possibly empty) list enables only the named built-ins. Extension tools always load regardless, matching upstream `createAgentSession`. `Program.cs` resolves the selection: `--no-tools` alone → empty built-ins; `--no-tools` + `--tools X` or `--tools X` alone → only X; neither → null.
* **Tests**: added parser tests for `--no-tools`, `--tools` (comma/inline/dedup), unknown-tool warning, unknown-option error, and runner tests for built-in filtering with/without extension tools.
* **Docs sync**: updated parity matrix CLI rows and `next.md`.

### 🧠 Design Intent (Why)

Upstream `cli/args.ts` `parseArgs` produces a `diagnostics` array and resolves `--tools` against `allTools`; `main.ts` prints diagnostics (exit 1 on error) and maps `--no-tools`/`--tools` onto the session's tool set while always loading extension tools. Tau previously parsed `--tools` into a value-consuming sink and `--no-tools` into a boolean sink, then discarded both, and silently swallowed unknown single-dash options. This slice closes the parse + selection + diagnostics contract while keeping Tau's default tool set unchanged when neither flag is supplied (the upstream default-set difference — grep/find/ls off by default — is intentionally left as a separate documented gap to avoid changing default behavior in a parsing slice).

### ✅ Validation

* `dotnet test tests/Tau.CodingAgent.Tests/Tau.CodingAgent.Tests.csproj --filter "CodingAgentInitialMessageBuilderTests|RuntimeCodingAgentRunnerTests" --no-restore`：50/50 passed.
* `pi --tools bogus` smoke：`Warning: Unknown tool "bogus". Valid tools: read, bash, edit, write, grep, find, ls` then continues.
* `pi -x` smoke：`Error: Unknown option: -x` and exit code 1.
* `powershell -NoProfile -ExecutionPolicy Bypass -File ./scripts/verify-dotnet.ps1 -SkipRestore`：passed (Ai 287, Agent 123, Tui 251, CodingAgent 556, WebUi 61, Pods 216).

### 📁 Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentInitialMessageBuilder.cs`
* `src/Tau.CodingAgent/Runtime/RuntimeCodingAgentRunner.cs`
* `src/Tau.CodingAgent/Program.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentInitialMessageBuilderTests.cs`
* `tests/Tau.CodingAgent.Tests/RuntimeCodingAgentRunnerTests.cs`
* `next.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
