## [2026-05-26 20:54] | Task: Pods model runtime events

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10`

### User Query

> 继续下一轮移植；多 Agent 并行加速，少做低收益单元测试，把核心 baseline 写进仓库。

### Changes Overview

**Scope:** `Tau.Pods` model lifecycle observability

**Key Actions:**

* **Parallel worker slice**: A bounded worker implemented the `PodModelService` runtime-event patch only in the service and its tests; main control integrated CLI wiring and docs/history.
* **Log sink injection**: `PodModelService` now accepts an optional `ITauLogSink`, defaulting to `NullTauLogSink.Instance`.
* **CLI wiring**: `PodsCli` now passes the process runtime log sink into the default model service, so `model list/pull/remove/status` commands write to the same `TAU_LOG_FILE` / `.tau/log.jsonl` path as probe, lifecycle, and vLLM commands.
* **Model events**: Added `model.list/pull/remove/status.start|end` runtime events with `category=pod`.
* **Safe payloads**: Events record summary fields such as pod id, operation, model id, transport, success, exit code, failure kind, model count, and available status. They intentionally do not write full remote command text, stdout, or stderr.
* **Targeted coverage**: Added focused tests for list success events, unsupported transport classification, status available field, and snapshot failure kind propagation.
* **Roadmap sync**: Updated `next.md` so model lifecycle operation events are no longer listed as a remaining observability gap.

### Design Intent (Why)

The pod CLI now has runtime events for probe, vLLM orchestration, and ordinary lifecycle commands. Model cache operations were the remaining Tau.Pods command family that could fail without a compact runtime audit trail. This slice keeps model behavior unchanged and adds only low-cardinality event summaries that are useful in `.tau/log.jsonl` without leaking remote command output.

### Files Modified

* `src/Tau.Pods/Services/PodModelService.cs`
* `src/Tau.Pods/Cli/PodsCli.cs`
* `tests/Tau.Pods.Tests/PodModelServiceTests.cs`
* `next.md`

### Validation

* `dotnet test tests\Tau.Pods.Tests\Tau.Pods.Tests.csproj --filter FullyQualifiedName~PodModelServiceTests --no-restore --verbosity minimal` passed: 11/11.
* `git diff --check -- src\Tau.Pods\Services\PodModelService.cs tests\Tau.Pods.Tests\PodModelServiceTests.cs` passed with no output in the worker slice.
