## [2026-05-26 20:30] | Task: Pods probe runtime log fields

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10`

### User Query

> 继续下一轮移植，优先把核心 baseline 写进仓库，减少低收益单元测试和大段文档同步。

### Changes Overview

**Scope:** `Tau.Pods` probe observability

**Key Actions:**

* **Probe fields**: `PodProbeService` now enriches `probe.end` runtime log events with `summary`, `statusCode`, `endpoint`, `host`, `port`, and `failureKind`.
* **Failure classification**: Probe runtime events now report `none`, `http-status`, `http-error`, `tcp-error`, or `unknown` as lightweight machine-filterable failure kinds.
* **Targeted coverage**: Added focused tests for HTTP success and HTTP exception log fields without relying on unstable real network failures.
* **Roadmap sync**: Updated `next.md` to remove probe field enrichment from the remaining observability gap.

### Design Intent (Why)

`probe.start/end` already existed, but the end event only carried pod id, transport, success, and latency. That was not enough to diagnose which endpoint or target failed from `.tau/log.jsonl` alone. This slice keeps probe behavior unchanged and only enriches the runtime event payload with existing `PodProbeResult` summary fields.

### Files Modified

* `src/Tau.Pods/Services/PodProbeService.cs`
* `tests/Tau.Pods.Tests/PodProbeServiceTests.cs`
* `next.md`

### Validation

* `dotnet test tests\Tau.Pods.Tests\Tau.Pods.Tests.csproj --no-restore --filter "FullyQualifiedName~PodProbeServiceTests" --verbosity minimal` passed: 4/4.
* `dotnet build src\Tau.Pods\Tau.Pods.csproj --no-restore --verbosity minimal` passed: 0 warnings, 0 errors.
