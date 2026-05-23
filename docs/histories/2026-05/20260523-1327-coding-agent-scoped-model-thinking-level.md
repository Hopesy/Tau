## [2026-05-23 13:27] | Task: scoped model thinking-level per-entry baseline

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10`

### 📥 User Query

> 继续当前 Tau / pi-mono parity 迁移计划，推进 scoped model thinking-level per-entry 切片。

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent` runtime、settings/model scope、RPC cycle、selector metadata preservation、docs/history。

**Key Actions:**

* **[Pattern parsing]**: 新增 `CodingAgentScopedModelPatterns`，按上游语义先尝试完整 model reference exact match，再按最后一个冒号解析 `off/minimal/low/medium/high/xhigh` thinking suffix，避免误伤模型 id 中的冒号。
* **[Scoped settings]**: 保持 `enabledModels` 字符串数组合同不变，允许条目写成 `provider/model:thinking`；`/scoped-models set/add/remove/current` 会解析、展示并保存 suffix。
* **[Cycle behavior]**: Ctrl+P / Ctrl+Shift+P model cycle 和 RPC `cycle_model` 切到带 suffix 的 scoped model 时，会更新 runner `ThinkingLevel` 并写回 settings `defaultThinkingLevel`；未带 suffix 的条目继续继承当前/default thinking。
* **[Selector preservation]**: `CodingAgentScopedModelsSelector` 识别已有 suffix 并选中对应模型；selector 保存时保留旧 suffix metadata，但本切片不新增逐项 thinking 编辑 UI。
* **[Tests/docs]**: 新增 parser、settings round-trip、命令展示/保存、selector suffix 保留、CLI/RPC cycle override 回归；同步 README、ARCHITECTURE、QUALITY_SCORE、next、active plans 和 release notes。

### 🧠 Design Intent (Why)

上游 scoped model 仍使用 `enabledModels`/`--models` 风格字符串 pattern 表达 per-entry thinking，因此 Tau 不引入新 JSON schema。这样能保留 settings/RPC 兼容性，并把本轮风险限定在解析、展示和 cycle 应用上。显式 `:off` 需要和“没有 override”区分，所以运行态保留 raw thinking suffix：`null` 表示继承，`off` 表示主动关闭。

完整上游 parity 仍未完成：multi-select UI 现在只保留 suffix，不提供逐项编辑；模型能力 clamp 也尚未接入。

### 📁 Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentScopedModelPatterns.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentRpcHost.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentScopedModelsSelector.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentScopedModelPatternsTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentSettingsStoreTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentRpcHostTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentScopedModelsSelectorTests.cs`
* `README.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`
* `docs/releases/feature-release-notes.md`
* `next.md`
