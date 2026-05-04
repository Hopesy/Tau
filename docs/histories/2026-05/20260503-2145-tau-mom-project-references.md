## [2026-05-03 21:45] | Task: tau mom project references

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5.4`
* **Runtime**: `Windows PowerShell / .NET 10 preview`

### 📥 User Query

> 继续继续

### 🛠 Changes Overview

**Scope:** `Tau.Mom`

**Key Actions:**

* **[Reference cleanup]**: 将 `Tau.Mom` 对 `Tau.Ai`、`Tau.CodingAgent`、`Tau.Tui` 的 DLL `HintPath` 改为 `ProjectReference`。
* **[Validation]**: 通过干净构建确认 `Tau.Mom` 可在无预置 DLL 的情况下正常编译。
* **[Docs sync]**: 将架构、质量评分、execution plan 和 next 清单中的剩余 workaround 状态同步改为已完成。

### 🧠 Design Intent (Why)

`Tau.CodingAgent` / `Tau.WebUi` / `Tau.CodingAgent.Tests` 已经收口到项目引用，这一步把剩余唯一的 `Tau.Mom` workaround 一并清掉，避免仓库同时维护两套引用语义。这样干净构建路径完全由 MSBuild 依赖图决定，不再依赖顺序生成的中间 DLL。

### 📁 Files Modified

* `src/Tau.Mom/Tau.Mom.csproj`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-04-23-tau-port-baseline.md`
* `next.md`
