## [2026-06-08 03:49] | Task: Pods vLLM startup log marker baseline

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI on Windows / PowerShell`

### User Query

> 继续按照 `GOAL.md` 推进 Tau 100% pi-mono parity；本次继续收口 `Tau.Pods` remote process/log schema 的 startup marker 判定合同。

### Changes Overview

**Scope:** `Tau.Pods`

**Key Actions:**

* 对照上游 `packages/pods/src/commands/models.ts` 中 `startModel` 的 log watcher marker，扩展 `PodVllmOrchestrationService` 的 health/status shell command。
* `vllm health/status` 现在在 curl `/health` 不 ready 后优先扫描 `~/.vllm_logs/<deployment>.log` 最近日志。
* `Application startup complete` 会映射为 ready。
* `Model runner exiting with code`、`Script exited with code`、`torch.OutOfMemoryError`、`CUDA out of memory` 和 engine initialization failure 会映射为 unhealthy/startup-failed。
* 保留旧 `.tau_pods/<deployment>.log` 失败扫描 fallback，兼容旧 Tau 日志路径。
* `ParseState` 现在也能从 raw startup complete/failure marker 直接映射 ready/unhealthy，避免只依赖 shell 回显 `ready` / `unhealthy` token。
* 扩展 `PodVllmOrchestrationServiceTests`，固定 health/status command 的 `~/.vllm_logs` 扫描和 raw marker 状态解析。

### Design Intent

本切片只关闭本地 startup marker 判定 baseline，让 Tau 的 health/status 能消费上一切片对齐的 upstream log path。它不实现上游实时 `tail -f` 监控，不在失败后自动删除 configured model，也不声明 `model_run.sh` wrapper、pseudo-TTY `script` 或真实 SSH/HF/GPU/vLLM smoke 已等价。

### Verification

* `dotnet build src\Tau.Pods\Tau.Pods.csproj --no-restore --verbosity minimal` -> passed.
* `dotnet test tests\Tau.Pods.Tests\Tau.Pods.Tests.csproj --filter "StatusAsync_BuildsStatusCommandAndParsesState|StatusAsync_ParsesReadyAndUnhealthyOutput|HealthAsync_BuildsHealthProbeCommandAndRequiresReady|HealthAsync_ParsesStartupLogMarkers|VllmHealth_WithJsonOutput_PrintsReadyContract|VllmHealth_WithTextOutput_ReturnsNonZeroForUnhealthy|VllmDeploy_WhenHealthFails_PrintsRollbackInJson|VllmDeploy_WithHealthRetryOptions_RetriesUntilReadyAndPrintsJsonAttempts" --no-restore --verbosity minimal` -> passed 7/7.
* `dotnet test tests\Tau.Pods.Tests\Tau.Pods.Tests.csproj --no-restore --verbosity minimal` -> passed 205/205.
* `git diff --check` -> passed.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` -> passed; counts: `Tau.Ai.Tests` 280, `Tau.Agent.Tests` 119, `Tau.Tui.Tests` 251, `Tau.CodingAgent.Tests` 438, `Tau.WebUi.Tests` 44, `Tau.Pods.Tests` 205.

### Files Modified

* `src/Tau.Pods/Services/PodVllmOrchestrationService.cs`
* `tests/Tau.Pods.Tests/PodVllmOrchestrationServiceTests.cs`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `GOAL.md`
