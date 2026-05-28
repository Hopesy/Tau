## [2026-05-26 13:56] | Task: Pods model snapshot metadata surface

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10`

### User Query

> 用户要求继续不断移植，不在单轮结束后询问下一步；优先推进真实 parity 缺口，少做低收益文档和单测。

### Changes Overview

**Scope:** `Tau.Pods`

**Key Actions:**

* **[Model list visibility]**: `PodModelService.ListAsync(...)` 不再只返回 `models--*` 目录名；远端命令会逐个 cache 目录解析 `snapshots/`、`refs/main` 和单 snapshot fallback，并返回 `SnapshotCount`、`ResolvedModelPath`、`SnapshotFailureKind`。
* **[Model status visibility]**: `PodModelService.StatusAsync(...)` 在 cache 目录存在时输出同一套 snapshot metadata；cache 缺失仍保持 `Present=false`，不把缺模型误判为 SSH transport failure。
* **[CLI contract]**: `model list` 文本输出增加 `snapshots/resolved/snapshotFailure`；`model list --json` 和 `model status --json` 暴露 `snapshotCount`、`resolvedModelPath`、`snapshotFailureKind`，`status --json` 额外暴露 `modelCachePath`。
* **[Compatibility]**: `ParseCachedModels(...)` 保留旧单列 `models--*` 输出兼容；新 tab 分隔输出只在有额外列时增强 metadata。
* **[Focused coverage]**: 更新 `PodModelServiceTests`，新增/扩展 snapshot metadata、ambiguous snapshot 和 model status summary 回归；`PodsCliTests` 新增 `model list --json` / `model status --json` 公开合同回归。

### Design Intent (Why)

上一切片已经让 `vllm preflight/deploy` 在启动 vLLM 前解析远端 Hugging Face cache snapshot。但如果 `model list/status` 仍只判断 cache 顶层目录，运维用户要到 deploy 前才知道 `refs/main` 是否可用、snapshot 是否 ambiguous。该切片把同一类 snapshot/ref 语义提前暴露到 model lifecycle 命令面，让模型 cache 状态可脚本化检查，同时不做真实下载、revision 选择或远端 smoke。

### Files Modified

* `src/Tau.Pods/Models/PodModelResults.cs`
* `src/Tau.Pods/Services/PodModelService.cs`
* `src/Tau.Pods/Cli/PodsCli.cs`
* `tests/Tau.Pods.Tests/PodModelServiceTests.cs`
* `tests/Tau.Pods.Tests/PodsCliTests.cs`
* `next.md`

### Validation

* `dotnet build src\Tau.Pods\Tau.Pods.csproj --no-restore --verbosity minimal` -> passed, 0 warnings, 0 errors
* `dotnet test tests\Tau.Pods.Tests\Tau.Pods.Tests.csproj --no-restore --verbosity minimal` -> 102/102 passed
