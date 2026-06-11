## [2026-06-11] | Task: CodingAgent CLI `--thinking` 初始 reasoning level

### 🤖 Execution Context

* **Agent ID**: `Claude`
* **Base Model**: `Opus 4.8 (1M context)`
* **Runtime**: `Windows PowerShell / .NET 10`

### 📥 User Query

> 按 `GOAL.md` 继续 Tau 100% pi-mono parity 迁移，关闭 CodingAgent top-level CLI 合同缺口。

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent` CLI 参数解析与 runner thinking-level 接线

**Key Actions:**

* **`--thinking` 解析**：`CodingAgentCliArguments` 新增 `Thinking` 字段，按上游 `cli/args.ts` `VALID_THINKING_LEVELS`（`off/minimal/low/medium/high/xhigh`）校验 `--thinking <level>` 与 `--thinking=<level>`；非法值产出 warning diagnostic（`Invalid thinking level "x". Valid values: ...`），不再被静默吞掉；缺值抛 usage error。
* **precedence 接线**：`Program.cs` 现在让显式 `--thinking` 优先于 settings `DefaultThinkingLevel`，解析后按 `CodingAgentThinkingLevels.ParseOrNull` + `ClampForModel` 应用到 runner，对应上游 `parsed.thinking` 覆盖 saved/scoped thinking 的行为。
* **Regression coverage**：新增 parser 回归覆盖有效 level、inline `=value`、大小写归一、非法值 warning 与缺值 usage error。

### 🧠 Design Intent (Why)

之前 `--thinking` 落在 `OptionsWithValue`，被 consume-and-discard，CLI 用户无法在启动时设定 reasoning level，只能靠 settings。上游把 `--thinking` 作为最高优先级 override。本切片复用 Tau 既有的 `CodingAgentThinkingLevels` parse/clamp 能力，把 CLI 值接到 runner，并复用上一刀新建的 `CodingAgentCliDiagnostic` 基础设施做非法值告警，保持与 `--tools` 一致的 warning 语义。

### ✅ Validation

* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --filter "CodingAgentInitialMessageBuilderTests" --no-restore --verbosity minimal`：28/28 passed
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`：passed（Ai 287、Agent 123、Tui 251、CodingAgent 560、WebUi 61、Pods 216）
* 手动 smoke：`dotnet run -- --thinking bogus -p "hi"` 输出 `Warning: Invalid thinking level "bogus". Valid values: off, minimal, low, medium, high, xhigh` 后继续运行。

### 📁 Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentInitialMessageBuilder.cs`
* `src/Tau.CodingAgent/Program.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentInitialMessageBuilderTests.cs`
* `next.md`
* `docs/QUALITY_SCORE.md`（无需改动则略）
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
