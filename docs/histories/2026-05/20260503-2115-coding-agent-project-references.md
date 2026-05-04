## [2026-05-03 21:15] | Task: coding-agent project references

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5.4`
* **Runtime**: `Windows PowerShell / .NET 10 preview`

### 📥 User Query

> 继续 Tau 移植，处理上一轮验证暴露的工程化问题。

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent`, `Tau.WebUi`, `Tau.CodingAgent.Tests`

**Key Actions:**

* **[Reference cleanup]**: 将 `Tau.CodingAgent` 对 `Tau.Ai`、`Tau.Agent`、`Tau.Tui` 的 DLL `HintPath` 改为 `ProjectReference`。
* **[Web host cleanup]**: 将 `Tau.WebUi` 对 `Tau.Ai`、`Tau.CodingAgent`、`Tau.Tui` 的 DLL `HintPath` 改为 `ProjectReference`。
* **[Test cleanup]**: 将 `Tau.CodingAgent.Tests` 对 Tau 项目的 DLL `HintPath` 改为 `ProjectReference`，避免 stale assembly 和并行构建顺序问题。
* **[Docs sync]**: 更新架构、质量评分、execution plan 和 next 清单，明确 `Tau.Mom` 仍是剩余引用 workaround。

### 🧠 Design Intent (Why)

上一轮 `/import` 验证过程中，清理 `bin/obj` 后暴露出测试项目依赖预先生成 DLL 的问题；并行构建还可能导致旧程序集被加载。把已能稳定验证的 `Tau.CodingAgent` / `Tau.WebUi` / `Tau.CodingAgent.Tests` 链路收回 `ProjectReference`，能让干净构建由 MSBuild 管理依赖顺序。`Tau.Mom` 和 `Tau.slnx` 仍单独保留为后续工程化任务，避免把引用结构、solution metaproj 和产品能力混在一轮里。

### 📁 Files Modified

* `src/Tau.CodingAgent/Tau.CodingAgent.csproj`
* `src/Tau.WebUi/Tau.WebUi.csproj`
* `tests/Tau.CodingAgent.Tests/Tau.CodingAgent.Tests.csproj`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-04-23-tau-port-baseline.md`
* `next.md`
