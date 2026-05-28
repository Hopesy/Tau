# [2026-05-25 17:56] | Task: RPC steer/follow_up images baseline

## Execution Context

* **Agent ID**: `Codex`
* **Runtime**: `Windows PowerShell, .NET 10`

## User Query

> 继续下一轮 Tau <- pi-mono-main 快速移植，多 Agent 并行，少做低收益单元测试和大段文档同步，尽快推进核心 baseline。

## Changes Overview

**Scope:** `Tau.CodingAgent` RPC content input path

**Key Actions:**

* RPC `steer` and `follow_up` now accept upstream-style `images` arrays and forward text plus image content blocks to the runner.
* Active `prompt` with `streamingBehavior=steer|followUp` now uses the same content path, so image payloads are not dropped when a prompt is routed into an already-running turn.
* `ICodingAgentRunner` now exposes content-block overloads for `Steer` and `FollowUp`; production runner forwards them as `UserMessage` content, while test fakes record the content for contract checks.
* Plan tracking was updated only for the facts that affect future migration decisions.

## Design Intent

Upstream RPC defines `images?: ImageContent[]` on `prompt`, `steer`, and `follow_up`. Tau already parsed images for starting a new prompt, but direct steering/follow-up and active-prompt routing still used string-only calls. This slice keeps the existing parser and runner seam, avoids introducing a new RPC content abstraction, and narrows the change to the missing upstream contract paths.

## Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentRpcHost.cs`
* `src/Tau.CodingAgent/Runtime/ICodingAgentRunner.cs`
* `src/Tau.CodingAgent/Runtime/RuntimeCodingAgentRunner.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentRpcHostTests.cs`
* `tests/Tau.CodingAgent.Tests/FakeCodingAgentRunner.cs`
* `tests/Tau.Agent.Tests/RuntimeDelegationAgentRunnerTests.cs`
* `tests/Tau.WebUi.Tests/FakeWebUiRunner.cs`
* `next.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`

## Validation

* `dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal` passed with 0 warnings / 0 errors.
* `dotnet build src\Tau.Tui\Tau.Tui.csproj --no-restore --verbosity minimal` passed with 0 warnings / 0 errors.
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal --filter CodingAgentRpcHostTests` passed: 48 / 48.
* `dotnet test tests\Tau.Agent.Tests\Tau.Agent.Tests.csproj --no-restore --verbosity minimal --filter RuntimeDelegationAgentRunnerTests` passed: 10 / 10.
* `dotnet build tests\Tau.WebUi.Tests\Tau.WebUi.Tests.csproj --no-restore --verbosity minimal` passed with 0 warnings / 0 errors.
