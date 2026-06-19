## [2026-06-18 00:44] | Task: close AI/Agent export-shape decision

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell`

### User Query

> 继续完成 ai 和 agent 的迁移 100%。

### Changes Overview

**Scope:** `Tau.Ai`, `Tau.Agent`, release contract scripts, migration docs

**Key Actions:**

* Added `docs/AI_AGENT_EXPORT_SHAPE.md` as the repo-owned decision record for mapping upstream `packages/ai` package exports, AI index exports and `packages/agent` index exports to Tau-native .NET assemblies, namespaces and NuGet package surfaces.
* Added `scripts/verify-ai-agent-export-shape.ps1` to assert 12 upstream AI package exports, 13 AI index export groups, 4 Agent index export groups and the required implementation/test/smoke evidence files.
* Wired the export-shape smoke into `scripts/plan-release.ps1` and `scripts/verify-release-contracts.ps1`, so release planning and release contract validation fail if the AI/Agent public export-shape record drifts.
* Synced `README.md`, `GOAL.md`, `next.md`, `docs/QUALITY_SCORE.md` and the active parity plan/matrix to record that Tau does not create a TypeScript/npm compatibility shim for the current .NET foundation gate.

### Design Intent (Why)

The previous AI/Agent foundation work proved local package consumption, Agent platform API usage, AI CLI tool install rehearsal and proxy-server e2e. The remaining public-surface ambiguity was whether Tau still needed to reproduce upstream TypeScript/npm barrel and subpath exports. This slice makes the decision explicit: the current foundation gate is .NET-native, with `Tau.Ai` and `Tau.Agent` exposed through assemblies, namespaces and NuGet metadata. A future TypeScript/npm compatibility package would be a separate product surface, not a blocker for local .NET Agent foundation completion.

### Validation

* `git diff --check` passed, with only the existing CRLF normalization warning for `docs/QUALITY_SCORE.md`.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-ai-agent-export-shape.ps1 -Json` passed: 75 assertions, 12 AI package exports, 13 AI index export groups, 4 Agent index export groups and 37 evidence files.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-release-package-publish.ps1 -Json` passed: 30 assertions, 5 default packages and 2 tool packages.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-release-contracts.ps1 -Json` passed: 75 assertions, package count 5, tool package count 2 and export-shape smoke included.
* `dotnet test tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --filter AiPublicApiCompileSampleTests --no-restore --verbosity minimal` passed 1/1.
* `dotnet test tests\Tau.Agent.Tests\Tau.Agent.Tests.csproj --filter AgentPublicApiCompileSampleTests --no-restore --verbosity minimal` passed 1/1.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore -RunSmoke` passed full src/test build, `Tau.Ai.Tests` 346/346, `Tau.Agent.Tests` 127/127, `Tau.Tui.Tests` 251/251, `Tau.CodingAgent.Tests` 631/631, `Tau.WebUi.Tests` 72/72, `Tau.Pods.Tests` 216/216, AI CLI tool install smoke, Agent package consumer smoke, Agent proxy-server e2e smoke, WebUi smoke and Mom smoke.

### Remaining Boundaries

This closes the local Tau-native AI/Agent export-shape decision and foundation release contract. It does not close real provider/OAuth e2e, real NuGet/package registry promotion, real global install from the intended feed, package signing, provenance attestation or future TypeScript/npm compatibility package work.

### Files Modified

* `docs/AI_AGENT_EXPORT_SHAPE.md`
* `scripts/verify-ai-agent-export-shape.ps1`
* `scripts/plan-release.ps1`
* `scripts/verify-release-contracts.ps1`
* `README.md`
* `GOAL.md`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
