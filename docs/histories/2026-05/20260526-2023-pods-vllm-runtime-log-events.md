## [2026-05-26 20:23] | Task: Pods vLLM runtime log events

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10`

### User Query

> 继续下一轮移植，优先多 Agent 并行，少做低收益单元测试，尽快把核心 parity baseline 写进仓库。

### Changes Overview

**Scope:** `Tau.Pods` vLLM orchestration observability

**Key Actions:**

* **Runtime events**: `PodVllmOrchestrationService` now accepts an optional `ITauLogSink` and emits `vllm.preflight/deploy/status/health/stop/rollback.start|end` summary events.
* **CLI wiring**: `PodsCli` now passes the environment-created runtime log sink into the default vLLM orchestration service, so `TAU_LOG_FILE` covers vLLM operations as well as pod probe events.
* **Targeted coverage**: Added focused tests for successful deploy/preflight/health event emission and rollback event emission after unhealthy health checks.
* **Roadmap sync**: Updated `next.md` to record the Tau-native vLLM operation log baseline while keeping full trace/correlation, probe field enrichment, real vLLM smoke, and non-vLLM pod operation events open.

### Design Intent (Why)

Tau already had a shared `ITauLogSink` and `JsonlTauLogSink` baseline, but vLLM `preflight/deploy/status/health/stop/rollback` results only appeared in CLI output. This slice makes those operation lifecycles visible in runtime JSONL logs without inventing a broad trace system. The events intentionally keep to summary fields such as pod, operation, deployment, model, success, exit code, state, failure kind, attempts, and rollback status; full remote commands, stdout, and stderr remain in CLI result objects rather than being duplicated into runtime logs.

### Files Modified

* `src/Tau.Pods/Services/PodVllmOrchestrationService.cs`
* `src/Tau.Pods/Cli/PodsCli.cs`
* `tests/Tau.Pods.Tests/PodVllmOrchestrationServiceTests.cs`
* `next.md`

### Validation

* `dotnet test tests\Tau.Pods.Tests\Tau.Pods.Tests.csproj --no-restore --filter "FullyQualifiedName~PodVllmOrchestrationServiceTests" --verbosity minimal` passed: 21/21.
* `dotnet build src\Tau.Pods\Tau.Pods.csproj --no-restore --verbosity minimal` passed: 0 warnings, 0 errors.
* `git diff --check -- src\Tau.Pods\Services\PodVllmOrchestrationService.cs src\Tau.Pods\Cli\PodsCli.cs tests\Tau.Pods.Tests\PodVllmOrchestrationServiceTests.cs next.md` passed with only the existing `next.md` CRLF normalization warning.
