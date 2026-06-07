## [2026-06-08 00:47] | Task: 对齐 Pods 默认配置路径

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI / PowerShell`

### 📥 User Query

> 按照 `GOAL.md` 继续推进 100% pi-mono parity。

### 🛠 Changes Overview

**Scope:** `Tau.Pods` config path/env compatibility

**Key Actions:**

* **[Config default]**: 将无显式 config path 时的默认路径从 Tau-local `tau.pods.json` 改为上游兼容的 `PI_CONFIG_DIR/pods.json` 或 no-env `~/.pi/pods.json`。
* **[Missing config]**: `PodsConfigStore.Load` 在配置文件不存在时返回空 `PodsConfig`，让 `list --json` / `validate --json` 对 missing default config 不再报 `Config not found`。
* **[Tests/docs]**: 用 isolated `PI_CONFIG_DIR` 更新无显式路径 CLI 回归，避免测试写真实用户 home；同步 `GOAL.md`、matrix、active plan、`next.md`、quality、README、architecture 和 history。

### 🧠 Design Intent (Why)

上游 `packages/pods/src/config.ts` 的默认合同是 `PI_CONFIG_DIR` 或 `homedir()/.pi` 下的 `pods.json`，并且 missing config 返回 `{ pods: {} }`。本次把 Tau 的默认路径和 missing-file 行为推进到同一合同，同时保留 record-shaped config/schema migration 和真实 SSH/HF/GPU/vLLM e2e 为后续独立缺口。

### ✅ Validation

* `dotnet build src\Tau.Pods\Tau.Pods.csproj --no-restore --verbosity minimal`
* `dotnet test tests\Tau.Pods.Tests\Tau.Pods.Tests.csproj --filter "PodsCli|Config" --no-restore --verbosity minimal` -> 92/92 passed
* `dotnet test tests\Tau.Pods.Tests\Tau.Pods.Tests.csproj --no-restore --verbosity minimal` -> 189/189 passed
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` -> Ai 280, Agent 119, Tui 251, CodingAgent 438, WebUi 44, Pods 189 passed

### 📁 Files Modified

* `src/Tau.Pods/Cli/PodsCli.cs`
* `src/Tau.Pods/Services/PodsConfigStore.cs`
* `tests/Tau.Pods.Tests/PodsCliTests.cs`
* `tests/Tau.Pods.Tests/PodsConfigValidatorTests.cs`
* `GOAL.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `README.md`
* `docs/ARCHITECTURE.md`
