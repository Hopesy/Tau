## [2026-06-08 04:01] | Task: Pods duplicate deployment guard baseline

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI on Windows / PowerShell`

### User Query

> 继续按照 `GOAL.md` 推进 Tau 100% pi-mono parity；本次继续收口 `Tau.Pods` start/deploy 本地配置防护和 operation failure schema 合同。

### Changes Overview

**Scope:** `Tau.Pods`

**Key Actions:**

* 对照上游 `packages/pods/src/commands/models.ts` 的 `pod.models[name]` duplicate guard，`vllm deploy` 和 top-level `start` 现在会在远端 preflight/deploy 前拒绝同名 configured deployment。
* 重复 deployment 返回 `failureKind=model-already-exists`，不执行 SSH、不覆盖原 configured model state。
* `PodVllmOperationResult` 增加 top-level `FailureKind`，CLI JSON/text operation 输出同步暴露该字段。
* 现有 deploy/stop failure 也会写出稳定 failure kind，减少只靠 summary 判定失败类别的歧义。
* 扩展 `PodsCliTests`，固定 duplicate deploy/start JSON 合同、无远端命令执行、原 state 保持不变，以及新增 text `failure=...` 输出。

### Design Intent

上游 `startModel` 在本地配置已存在同名 model 时直接拒绝启动，避免覆盖已有 `pod.models[name]` 状态。Tau 此前成功部署会写回 state，但没有在启动前阻断同名 deployment。本切片关闭这层本地 guard，并顺手把 operation failure kind 显式化；它不实现上游 `model_run.sh` wrapper、实时 `tail -f` startup watcher、watcher 失败后的完整配置删除流程，也不声明真实 SSH/HF/GPU/vLLM smoke 已完成。

### Verification

* `dotnet build src\Tau.Pods\Tau.Pods.csproj --no-restore --verbosity minimal` -> passed.
* `dotnet test tests\Tau.Pods.Tests\Tau.Pods.Tests.csproj --filter "VllmDeploy_WithExistingDeploymentName_RejectsBeforeRemoteExecution|Start_WithExistingDeploymentName_RejectsBeforeRemoteExecution|VllmDeploy_WithTextOutput_ExecutesRemoteCommandAndPrintsContract|VllmStop_WhenRunnerFails_ReturnsNonZeroAndPrintsError|VllmDeploy_WhenHealthFails_PrintsRollbackInJson" --no-restore --verbosity minimal` -> passed 5/5.
* `dotnet test tests\Tau.Pods.Tests\Tau.Pods.Tests.csproj --no-restore --verbosity minimal` -> passed 207/207.
* `git diff --check` -> passed.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` -> passed; counts: `Tau.Ai.Tests` 280, `Tau.Agent.Tests` 119, `Tau.Tui.Tests` 251, `Tau.CodingAgent.Tests` 438, `Tau.WebUi.Tests` 44, `Tau.Pods.Tests` 207.

### Files Modified

* `src/Tau.Pods/Models/PodVllmResults.cs`
* `src/Tau.Pods/Services/PodVllmOrchestrationService.cs`
* `src/Tau.Pods/Cli/PodsCli.cs`
* `tests/Tau.Pods.Tests/PodsCliTests.cs`
* `GOAL.md`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
