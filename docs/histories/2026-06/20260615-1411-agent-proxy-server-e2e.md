## [2026-06-15 14:11] | Task: Agent proxy server e2e

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10`

### User Query

> 按照 `GOAL.md` 继续 Tau 100% pi-mono parity 迁移，从 Phase 2 candidate queue 领取下一条可验证切片。

### Changes Overview

**Scope:** `Tau.Agent` proxy transport validation, release/smoke scripts, parity docs, and one test fixture correction needed by the final gate.

**Key Actions:**

* Added a loopback HTTP/SSE server-path e2e case to `ProxyStreamProviderTests`, proving `ProxyStreamProvider` can POST to a real `/api/stream` endpoint, send bearer auth and request envelope, and rebuild stripped SSE proxy events into a Tau assistant message.
* Added proxy error hardening coverage for missing terminal events and malformed SSE JSON so the stream terminates with `ErrorEvent` instead of hanging.
* Added `scripts/verify-agent-proxy-server-e2e.ps1` and wired it into `verify-dotnet.ps1 -RunSmoke`, `plan-release.ps1`, and `verify-release-contracts.ps1`.
* Updated `GOAL.md`, `next.md`, README, `docs/QUALITY_SCORE.md`, the active parity plan, and the matrix to mark Agent stream proxy local server-path contract as verified while keeping provider/OAuth, registry/signing/provenance, package/global alias, and export-shape gaps open.
* Fixed `CodingAgentResumeSelectorTests.SelectAsync_CtrlRRenamesCurrentSessionAndReturnsUpdatedNameWhenCancelled` so the temporary JSONL session is created and selected under the same cwd; otherwise the selector's default current-scope filter can hide the fixture session and turn the scripted rename into a plain cancel.

### Design Intent (Why)

The existing proxy tests only used a fake `HttpMessageHandler`, so they proved request serialization and event parsing but not a real server path. The new test keeps the implementation simple and deterministic by using a local loopback TCP HTTP/SSE server instead of external credentials or a long-running service. That closes the Tau.Agent proxy transport contract without pretending that unrelated real provider/OAuth e2e or package registry gates are complete.

The final project gate also exposed a deterministic test-fixture mismatch in an existing CodingAgent resume selector test: the store header used the process cwd, while the test's session file lived in a temp directory. Passing the same temp directory through the store and selector state keeps the test aligned with the selector's intended current-scope behavior without changing product code.

### Validation

* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --filter FullyQualifiedName~CodingAgentResumeSelectorTests.SelectAsync_CtrlRRenamesCurrentSessionAndReturnsUpdatedNameWhenCancelled --no-restore --verbosity minimal -m:1` passed 1/1.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-agent-proxy-server-e2e.ps1 -SkipRestore` passed `ProxyStreamProviderTests` 5/5.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-agent-proxy-server-e2e.ps1 -SkipRestore -Json` returned `succeeded=true`, `exitCode=0`, and the `ProxyStreamProviderTests` filter.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-release-contracts.ps1 -Json` passed and included `agentProxyServerE2e.succeeded=true`, `exitCode=0`.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore -RunSmoke` passed: Ai 344, Agent 126, Tui 251, CodingAgent 631, WebUi 72, Pods 216, plus `tau-ai`, Agent examples, Agent package consumer, Agent proxy loopback server-path, WebUi, and Mom smoke.

### Files Modified

* `tests/Tau.Agent.Tests/ProxyStreamProviderTests.cs`
* `scripts/verify-agent-proxy-server-e2e.ps1`
* `scripts/verify-dotnet.ps1`
* `scripts/plan-release.ps1`
* `scripts/verify-release-contracts.ps1`
* `tests/Tau.CodingAgent.Tests/CodingAgentResumeSelectorTests.cs`
* `GOAL.md`
* `next.md`
* `README.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
