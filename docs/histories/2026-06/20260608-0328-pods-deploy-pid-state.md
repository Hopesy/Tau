## [2026-06-08 03:28] | Task: Pods deploy PID state writeback baseline

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI on Windows / PowerShell`

### User Query

> 继续按照 `GOAL.md` 推进 Tau 100% pi-mono parity；本次继续收口 `Tau.Pods` remote process state 的本地 PID 写回合同。

### Changes Overview

**Scope:** `Tau.Pods`

**Key Actions:**

* 对照上游 `packages/pods/src/commands/models.ts` 成功 `startModel` 后把 `pid` 写入 `config.pods[podName].models[name]` 的合同，扩展 Tau vLLM deploy result。
* `PodVllmOrchestrationService` 的 systemd 路径在启动后 best-effort 查询 `systemctl --user show <unit> --property=MainPID --value`，fallback `nohup` 路径继续写 `.pid` 文件并回显 `pid=<n>`。
* `PodVllmOperationResult` 新增 `ProcessId`，从 deploy stdout 中解析正整数 `pid=<n>`。
* `PodsCli` JSON operation output 新增 `processId`；`PodsConfigStore.ApplyVllmDeploymentResult` 在 `ProcessId` 可用时写入 configured model `Pid`，否则继续写 `0`。
* 扩展 vLLM orchestration service 与 CLI deploy state tests，覆盖 remote command 生成、PID 解析、JSON 输出和 config writeback。

### Design Intent

本切片只关闭本地 deploy PID state writeback baseline，不把 Tau 的 systemd/nohup orchestration 宣称为上游 `model_run.sh` wrapper 的完全等价实现。真实远端 `MainPID` / fallback `.pid` 是否准确仍需要真实 SSH/HF/GPU/vLLM smoke；`~/.vllm_logs` startup streaming、失败日志实时判定、多版本 rollback 和可用 pod-agent runtime 仍保留为后续缺口。

### Verification

* `dotnet test tests\Tau.Pods.Tests\Tau.Pods.Tests.csproj --filter "DeployAsync_ExecutesPlannerRemoteCommandThroughSshRunner|VllmDeploy_Success_UpdatesConfiguredModelStateForGpuAllocation" --no-restore --verbosity minimal` -> passed 2/2.
* `dotnet test tests\Tau.Pods.Tests\Tau.Pods.Tests.csproj --no-restore --verbosity minimal` -> passed 204/204.
* `git diff --check` -> passed.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` -> passed; counts: `Tau.Ai.Tests` 280, `Tau.Agent.Tests` 119, `Tau.Tui.Tests` 251, `Tau.CodingAgent.Tests` 438, `Tau.WebUi.Tests` 44, `Tau.Pods.Tests` 204.

### Files Modified

* `src/Tau.Pods/Services/PodVllmOrchestrationService.cs`
* `src/Tau.Pods/Models/PodVllmResults.cs`
* `src/Tau.Pods/Services/PodsConfigStore.cs`
* `src/Tau.Pods/Cli/PodsCli.cs`
* `tests/Tau.Pods.Tests/PodVllmOrchestrationServiceTests.cs`
* `tests/Tau.Pods.Tests/PodsCliTests.cs`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `GOAL.md`
