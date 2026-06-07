## [2026-06-08 04:45] | Task: Pods deploy-time startup watcher baseline

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI on Windows / PowerShell`

### User Query

> 继续按照 `GOAL.md` 推进 Tau 100% pi-mono parity；本次继续收口 `Tau.Pods` deploy-time startup watcher 合同。

### Changes Overview

**Scope:** `Tau.Pods`

**Key Actions:**

* 对照上游 `packages/pods/src/commands/models.ts` 的 `startModel()` log watcher，新增 `PodVllmStartupWatchResult` 和 `PodVllmOrchestrationService.WatchStartupAsync`。
* `vllm deploy` / top-level `start` 在 remote deploy command 成功后先扫描 `~/.vllm_logs/<name>.log`，看到 `Application startup complete` 后才继续现有 `/health` readiness。
* watcher 识别 `Model runner exiting with code`、`Script exited with code`、OOM 和 engine initialization failure 时返回 `failureKind=startup-failed`，触发 rollback。
* CLI text 输出新增 `[startup-watch]` 区块，JSON 输出新增 `startupWatch` 子对象。
* watcher 失败时 deploy 返回非零，且 `PodsCli` 不写入 failed deployment configured model state。
* 同步 `GOAL.md`、`next.md`、`docs/QUALITY_SCORE.md` 和 active parity plan/matrix，明确 exact `model_run.sh` wrapper、pseudo-TTY `script`、真实 SSH/HF/GPU/vLLM smoke、多版本 rollback 仍 open。

### Design Intent

上游 `startModel()` 在启动远端 model runner 后，会跟随 `~/.vllm_logs/<name>.log` 并等待 startup complete / failure marker；失败时删除 configured model state。Tau 之前只在 `health/status` 路径读取这些 marker，deploy path 仍是启动后直接进入 `/health` readiness。

本切片把 log marker watcher 纳入 deploy path，但保留 Tau 既有 JSON/text deploy 合同：watcher 是结构化子结果，不把无限 `tail -f` 直接接到 JSON deploy stdout。这样既能审计启动等待和失败 rollback，也不破坏 machine-readable 输出。

该切片只关闭 deploy-time startup marker wait / failure rollback / no-writeback baseline；它不实现 exact `model_run.sh` wrapper、pseudo-TTY `script`、真实 SSH/HF/GPU/vLLM smoke、多版本 rollback 或 Pods final `verified` 状态。

### Verification

* `dotnet build src\Tau.Pods\Tau.Pods.csproj --no-restore --verbosity minimal` -> passed.
* `dotnet test tests\Tau.Pods.Tests\Tau.Pods.Tests.csproj --filter "DeployAsync|VllmDeploy|Start_WithName|Start_WithExisting" --no-restore --verbosity minimal` -> passed 34/34.
* `dotnet test tests\Tau.Pods.Tests\Tau.Pods.Tests.csproj --no-restore --verbosity minimal` -> passed 214/214.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` -> passed; counts: `Tau.Ai.Tests` 280, `Tau.Agent.Tests` 119, `Tau.Tui.Tests` 251, `Tau.CodingAgent.Tests` 438, `Tau.WebUi.Tests` 44, `Tau.Pods.Tests` 214.

### Files Modified

* `src/Tau.Pods/Models/PodVllmResults.cs`
* `src/Tau.Pods/Services/PodVllmOrchestrationService.cs`
* `src/Tau.Pods/Cli/PodsCli.cs`
* `tests/Tau.Pods.Tests/PodVllmOrchestrationServiceTests.cs`
* `tests/Tau.Pods.Tests/PodsCliTests.cs`
* `GOAL.md`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
