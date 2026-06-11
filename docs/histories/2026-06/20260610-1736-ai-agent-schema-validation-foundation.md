## [2026-06-10 17:36] | Task: 完善 Tau.Ai / Tau.Agent 底座

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI / PowerShell`

### 📥 User Query

> 继续完善 Tau.Ai 和 Tau.Agent 底座，并同步验证和计划文档

### 🛠 Changes Overview

**Scope:** `Tau.Ai`, `Tau.Agent`, parity docs/history

**Key Actions:**

* **Tool schema validation baseline**: 扩展 `Tau.Ai.ToolArgumentValidator`，在现有 object/array/required/properties/items/type coercion/enum/anyOf 基础上补齐常见 AJV/TypeBox schema keywords，包括 `const`、`oneOf`、`allOf`、`additionalProperties`、`patternProperties`、字符串长度/正则/format、数字边界/排他边界/`multipleOf`、数组长度/唯一性和 property count。
* **Agent reuse**: `Tau.Agent` 继续复用同一个 validator，因此 tool execution 前的 schema 校验与 error-result 行为一起增强，没有引入独立的 Agent-side schema 逻辑。
* **Test coverage**: 补充 `Tau.Ai.Tests` 的 `ToolArgumentValidatorTests`，覆盖 additional properties / pattern properties、字符串与数组约束、format / exclusive bounds / property counts、allOf / anyOf / oneOf / const 等场景。
* **Docs sync**: 同步 `next.md`、`docs/QUALITY_SCORE.md`、`docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md` 和 `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`，把 AI/Agent 的 schema helper baseline、测试计数和剩余差距写回当前进度文档。

### 🧠 Design Intent (Why)

先把 `Tau.Ai` 的 schema 校验能力提升到能覆盖 Agent 现在实际消费的常见 tool schema 关键字，再让 `Tau.Agent` 复用同一条校验链，避免在两个包里分叉维护重复规则。实现保持 .NET-native best-effort 路径，不引入 AJV/TypeBox 运行时依赖，也不把完整 runtime codegen / CSP fallback parity 伪装成已完成。

### ✅ Validation

* `dotnet test tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --filter ToolArgumentValidatorTests --no-restore --verbosity minimal`：8/8 passed
* `dotnet test tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --no-restore --verbosity minimal`：287/287 passed（收口阶段完整项目复验计数）
* `dotnet test tests\Tau.Agent.Tests\Tau.Agent.Tests.csproj --no-restore --verbosity minimal`：123/123 passed
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`：passed（Ai 287、Agent 123、Tui 251、CodingAgent 531、WebUi 61、Pods 216）

### 📁 Files Modified

* `src/Tau.Ai/Validation/ToolArgumentValidator.cs`
* `tests/Tau.Ai.Tests/ToolArgumentValidatorTests.cs`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
