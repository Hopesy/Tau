## [2026-06-08 09:27] | Task: Pods model runner wrapper baseline

### Execution Context

* Agent ID: `Codex`
* Base Model: `GPT-5`
* Runtime: `Windows PowerShell, .NET 10`

### User Query

> 按照 `GOAL.md` 继续完成 Tau 对 `pi-mono-main` 的 100% 迁移。

### Changes Overview

Scope: `Tau.Pods`

Key actions:

* Added a local model runner / wrapper baseline for vLLM deploy planning and execution.
* Exposed runner/wrapper script paths and `usesPseudoTtyWrapper` through plan/deploy text, JSON and metadata.
* Updated stop/rollback cleanup to remove generated runner/wrapper scripts.
* Synced `GOAL.md`, active parity plan/matrix, `next.md` and `docs/QUALITY_SCORE.md` without claiming real remote e2e completion.

### Design Intent

Upstream `packages/pods/scripts/model_run.sh` and `packages/pods/src/commands/models.ts` do not launch `vllm serve` directly. They upload a runner script, wrap it in `script -q -f -c ... ~/.vllm_logs/<name>.log` to preserve pseudo-TTY/color output, save the wrapper PID, and watch the same log for ready/failure markers. Tau already had log path, startup marker, log-follow and startup-watch baselines; this slice closes the remaining local runner/wrapper contract so those features consume the same generated remote scripts. It still deliberately leaves real SSH/HF/GPU/vLLM smoke, upstream runner built-in HF download exact flow, save-before-watch/delete-config exact ordering and multi-version rollback open.

### Files Modified

* `GOAL.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `next.md`
* `src/Tau.Pods/Cli/PodsCli.cs`
* `src/Tau.Pods/Models/PodVllmServePlan.cs`
* `src/Tau.Pods/Services/PodVllmCommandPlanner.cs`
* `src/Tau.Pods/Services/PodVllmOrchestrationService.cs`
* `tests/Tau.Pods.Tests/PodVllmCommandPlannerTests.cs`
* `tests/Tau.Pods.Tests/PodVllmOrchestrationServiceTests.cs`
* `tests/Tau.Pods.Tests/PodsCliTests.cs`

### Validation

* `git diff --check` passed. Git only warned that several existing text files will normalize from CRLF to LF when touched.
* `dotnet build src\Tau.Pods\Tau.Pods.csproj --no-restore --verbosity minimal` passed with 0 warnings and 0 errors.
* `dotnet test tests\Tau.Pods.Tests\Tau.Pods.Tests.csproj --filter "PodVllmCommandPlannerTests|DeployAsync|VllmDeploy|VllmPlan|VllmRollback" --no-restore --verbosity minimal` passed 65/65.
* `dotnet test tests\Tau.Pods.Tests\Tau.Pods.Tests.csproj --no-restore --verbosity minimal` passed 214/214.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` passed with counts: `Tau.Ai.Tests` 280, `Tau.Agent.Tests` 119, `Tau.Tui.Tests` 251, `Tau.CodingAgent.Tests` 438, `Tau.WebUi.Tests` 44, `Tau.Pods.Tests` 214.

Note: an earlier parallel build/test attempt hit a Windows Roslyn file lock on `src\Tau.Pods\obj\Debug\net10.0\Tau.Pods.dll`; `dotnet build-server shutdown` was run and the same build/test chain passed serially.
