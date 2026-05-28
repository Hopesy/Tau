## [2026-05-26 20:42] | Task: Pods lifecycle runtime events

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10`

### User Query

> 继续下一轮移植，优先快速推进核心 baseline；减少低收益单元测试和大段文档同步。

### Changes Overview

**Scope:** `Tau.Pods` non-vLLM lifecycle observability

**Key Actions:**

* **Log sink injection**: `PodLifecycleService` now accepts an optional `ITauLogSink` while preserving existing constructor defaults.
* **CLI wiring**: `PodsCli` now passes the process runtime log sink into the default lifecycle service, so production commands write to `TAU_LOG_FILE` / `.tau/log.jsonl` through the existing sink path.
* **Lifecycle events**: Added `lifecycle.health/deploy/stop/restart/logs/deployments.start|end` runtime events with `category=pod`.
* **Safe payloads**: Events record summary fields such as pod id, operation, deployment/model ids, transport, success, exit code, failure kind, tail count, line count, deployment count, and latency. They intentionally do not write full remote command text, stdout, or stderr.
* **Targeted coverage**: Added focused tests for SSH deploy event payloads and unsupported transport classification.
* **Roadmap sync**: Updated `next.md` so non-vLLM pod lifecycle events are no longer listed as a remaining observability gap.

### Design Intent (Why)

vLLM orchestration and probe already emitted runtime events, but ordinary pod lifecycle commands still left `.tau/log.jsonl` blind for deploy, stop, restart, logs, deployments, and health. This slice keeps CLI behavior and command execution semantics unchanged, and only adds a low-cardinality audit stream that can be filtered without leaking full remote command output.

### Files Modified

* `src/Tau.Pods/Services/PodLifecycleService.cs`
* `src/Tau.Pods/Cli/PodsCli.cs`
* `tests/Tau.Pods.Tests/PodLifecycleServiceTests.cs`
* `next.md`

### Validation

* `dotnet test tests\Tau.Pods.Tests\Tau.Pods.Tests.csproj --no-restore --filter "FullyQualifiedName~PodLifecycleServiceTests" --verbosity minimal` passed: 26/26.
* `dotnet build src\Tau.Pods\Tau.Pods.csproj --no-restore --verbosity minimal` passed: 0 warnings, 0 errors.
