## [2026-06-08 03:41] | Task: Pods vLLM log path compatibility baseline

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI on Windows / PowerShell`

### User Query

> 继续按照 `GOAL.md` 推进 Tau 100% pi-mono parity；本次继续收口 `Tau.Pods` remote process/log schema 的本地日志路径合同。

### Changes Overview

**Scope:** `Tau.Pods`

**Key Actions:**

* 对照上游 `packages/pods/src/commands/models.ts` 与 `packages/pods/scripts/model_run.sh` 的 `~/.vllm_logs/<name>.log` 日志路径合同，给 Tau vLLM plan 增加 `LogPath`。
* `PodVllmCommandPlanner` 现在在 plan JSON 与 metadata JSON 暴露 `logPath`，并让 systemd unit 创建 `%h/.vllm_logs`。
* vLLM systemd unit 现在用 `StandardOutput=append:%h/.vllm_logs/<name>.log` 与 `StandardError=append:%h/.vllm_logs/<name>.log` 保存输出。
* fallback `nohup` deploy 路径改为写 `~/.vllm_logs/<name>.log`，继续保留 `.tau_pods/<name>.pid` 用于 PID state。
* top-level `logs` 远端命令现在先 tail `~/.vllm_logs/<name>.log`，再回退到 `journalctl` 与旧 `.tau_pods` 日志路径。
* 扩展 planner、orchestration 和 CLI logs 测试，固定 log path、systemd output directives 和 logs path preference。

### Design Intent

本切片只关闭上游 vLLM log path 的本地合同，让后续 startup watcher 或真实 e2e smoke 有稳定日志输入。Tau 仍采用当前 service/nohup orchestration，不在本轮声称完全等价上游 `model_run.sh` wrapper、pseudo-TTY `script`、实时 `tail -f`、startup complete/failure line parsing 或真实远端 vLLM 健康。

### Verification

* `dotnet build src\Tau.Pods\Tau.Pods.csproj --no-restore --verbosity minimal` -> passed.
* `dotnet test tests\Tau.Pods.Tests\Tau.Pods.Tests.csproj --filter "PodVllmCommandPlannerTests|DeployAsync_ExecutesPlannerRemoteCommandThroughSshRunner|Logs_WithoutPodId_UsesActivePodAndEmitsJsonFailureKind|Logs_WithConfigPodOptions_UsesExplicitPodAndTail|VllmPlan_WithJsonOption_PrintsMachineReadablePlan|VllmDeploy_Success_UpdatesConfiguredModelStateForGpuAllocation" --no-restore --verbosity minimal` -> passed 19/19.
* `dotnet test tests\Tau.Pods.Tests\Tau.Pods.Tests.csproj --no-restore --verbosity minimal` -> passed 204/204.
* `git diff --check` -> passed.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` -> passed; counts: `Tau.Ai.Tests` 280, `Tau.Agent.Tests` 119, `Tau.Tui.Tests` 251, `Tau.CodingAgent.Tests` 438, `Tau.WebUi.Tests` 44, `Tau.Pods.Tests` 204.

### Files Modified

* `src/Tau.Pods/Models/PodVllmServePlan.cs`
* `src/Tau.Pods/Services/PodVllmCommandPlanner.cs`
* `src/Tau.Pods/Services/PodVllmOrchestrationService.cs`
* `src/Tau.Pods/Services/PodLifecycleService.cs`
* `src/Tau.Pods/Cli/PodsCli.cs`
* `tests/Tau.Pods.Tests/PodVllmCommandPlannerTests.cs`
* `tests/Tau.Pods.Tests/PodVllmOrchestrationServiceTests.cs`
* `tests/Tau.Pods.Tests/PodsCliTests.cs`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `GOAL.md`
