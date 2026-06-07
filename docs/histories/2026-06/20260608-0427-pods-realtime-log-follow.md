## [2026-06-08 04:27] | Task: Pods realtime log follow baseline

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI on Windows / PowerShell`

### User Query

> 继续按照 `GOAL.md` 推进 Tau 100% pi-mono parity；本次继续收口 `Tau.Pods` top-level realtime log viewing 合同。

### Changes Overview

**Scope:** `Tau.Pods`

**Key Actions:**

* 对照上游 `packages/pods/src/commands/models.ts` 的 `viewLogs()`，新增 `PodExecService.OpenCommandAsync`，用于继承 stdout/stderr 执行 SSH remote command。
* `logs --follow` / `logs -f` 现在会在 active/指定 pod 上检查 configured model state，然后执行 `tail -f ~/.vllm_logs/<name>.log`。
* `logs --follow` 对 missing configured model 返回 `failureKind=model-not-found`，不执行 SSH。
* `--json --follow` 明确拒绝；普通 `logs --json` 快照合同保持不变，并继续输出 `follow=false`。
* 扩展 `PodExecServiceTests`、`PodLifecycleServiceTests` 和 `PodsCliTests`，固定 inherited stdio、远端 command、configured model guard 和 CLI 冲突参数行为。

### Design Intent

上游 `pi logs <name>` 不是一次性日志快照，而是通过 SSH 继承 stdio 执行 `tail -f ~/.vllm_logs/<name>.log`，并且只允许查看已存在于 `pod.models[name]` 的 configured model。Tau 此前 `logs` 只通过捕获式 SSH 执行 `tail -n` 快照，无法表达长时间实时流。本切片把实时查看作为显式 `--follow` 路径落地，同时保留既有 Tau JSON 快照合同，避免把永不结束的流包装成 machine-readable JSON。

该切片只关闭 top-level realtime log viewing baseline；它不实现 deploy-time startup watcher 自动停止、watcher 失败后的 configured model 删除、exact `model_run.sh` wrapper、pseudo-TTY `script` 行为、真实 SSH/HF/GPU/vLLM smoke 或多版本 rollback。

### Verification

* `dotnet build-server shutdown` -> passed; used to clear a prior Roslyn file lock caused by parallel build/test execution.
* `dotnet build src\Tau.Pods\Tau.Pods.csproj --no-restore --verbosity minimal` -> passed.
* `dotnet test tests\Tau.Pods.Tests\Tau.Pods.Tests.csproj --filter "OpenCommandAsync|FollowLogsAsync|Logs_Follow|Logs_WithoutPodId_UsesActivePodAndEmitsJsonFailureKind|Logs_WithConfigPodOptions_UsesExplicitPodAndTail" --no-restore --verbosity minimal` -> passed 7/7.
* `dotnet test tests\Tau.Pods.Tests\Tau.Pods.Tests.csproj --no-restore --verbosity minimal` -> passed 212/212.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` -> passed; counts: `Tau.Ai.Tests` 280, `Tau.Agent.Tests` 119, `Tau.Tui.Tests` 251, `Tau.CodingAgent.Tests` 438, `Tau.WebUi.Tests` 44, `Tau.Pods.Tests` 212.

### Files Modified

* `src/Tau.Pods/Models/PodExecResult.cs`
* `src/Tau.Pods/Models/PodLifecycleResults.cs`
* `src/Tau.Pods/Services/PodExecService.cs`
* `src/Tau.Pods/Services/PodLifecycleService.cs`
* `src/Tau.Pods/Cli/PodsCli.cs`
* `tests/Tau.Pods.Tests/PodExecServiceTests.cs`
* `tests/Tau.Pods.Tests/PodLifecycleServiceTests.cs`
* `tests/Tau.Pods.Tests/PodsCliTests.cs`
* `GOAL.md`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
