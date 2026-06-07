## [2026-06-08 03:14] | Task: Pods GPU allocation state baseline

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI on Windows / PowerShell`

### User Query

> 按照 `GOAL.md` 继续 Tau 100% pi-mono parity；本次在提交前补齐 `Tau.Pods` usage-aware GPU allocation state 本地合同切片。

### Changes Overview

**Scope:** `Tau.Pods`

**Key Actions:**

* 对照上游 `packages/pods/src/commands/models.ts` 的 `selectGPUs`，让 `PodVllmCommandPlanner` 从 configured `pod.Models[*].Gpu` 统计 usage，按最少使用 GPU 选择 `selectedGpus`；请求全部 GPU 时保持 pod inventory 顺序。
* 对照上游 `startModel` 保存 `config.pods[podName].models[name]` 的行为，成功 `vllm deploy` / top-level `start` 后写回 configured model state：model id、port、selected GPU ids 和临时 `Pid=0`。
* 对照上游 `stopModel` / stop-all 删除 configured model state 的行为，成功 `vllm stop` / top-level `stop` / no-arg stop-all 后删除对应 deployment state；stop-all 只删除成功停止的 deployment。
* 新增 planner 与 CLI 回归，覆盖 least-used GPU selection、known-model 多 GPU selection、deploy 成功写回 state、stop 成功删除 state、stop-all 只清理成功项。
* 同步 active parity matrix、100% parity plan、`next.md`、`docs/QUALITY_SCORE.md` 和 `GOAL.md`，把 usage-aware GPU allocation 从 open 本地合同改为已覆盖 baseline，同时保留真实远端/e2e/PID/log-tail 边界。

### Design Intent

本切片关闭的是上游 `pod.models[*].gpu` usage-aware allocation 的本地状态合同，而不是 Pods final verified。Tau 当前 vLLM orchestration 走 systemd/nohup + health window，尚未暴露一个等价于上游 `model_run.sh` wrapper 的可审计 runner PID，因此 persisted `Pid=0` 是明确边界；真实 PID 捕获、`~/.vllm_logs` startup streaming、可用 pod-agent runtime、真实 SSH/HF/GPU/vLLM smoke、多版本 rollback 仍保留为后续缺口。

### Verification

* `dotnet test tests\Tau.Pods.Tests\Tau.Pods.Tests.csproj --filter "PodVllmCommandPlannerTests|VllmDeploy|Stop_WithDeploymentName|Stop_WithoutDeploymentName" --no-restore --verbosity minimal` -> passed 29/29.
* `dotnet test tests\Tau.Pods.Tests\Tau.Pods.Tests.csproj --no-restore --verbosity minimal` -> passed 204/204.
* `git diff --check` -> passed, with CRLF/LF normalization warnings only.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` -> passed; counts: `Tau.Ai.Tests` 280, `Tau.Agent.Tests` 119, `Tau.Tui.Tests` 251, `Tau.CodingAgent.Tests` 438, `Tau.WebUi.Tests` 44, `Tau.Pods.Tests` 204.

### Files Modified

* `src/Tau.Pods/Services/PodVllmCommandPlanner.cs`
* `src/Tau.Pods/Services/PodsConfigStore.cs`
* `src/Tau.Pods/Cli/PodsCli.cs`
* `tests/Tau.Pods.Tests/PodVllmCommandPlannerTests.cs`
* `tests/Tau.Pods.Tests/PodsCliTests.cs`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `GOAL.md`
