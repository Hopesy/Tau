## [2026-06-08 01:25] | Task: Pods record-shaped config parity

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI on Windows PowerShell`

### User Query

> 提交所有git并push

### Changes Overview

**Scope:** `Tau.Pods` config/schema compatibility, parity docs, history

**Key Actions:**

* **[Config schema]**: `PodsConfigStore` now reads upstream record-shaped `pods` config and writes the same upstream shape while continuing to load legacy Tau list-shaped configs.
* **[Pod metadata]**: `PodDefinition` now carries the original `ssh` command and per-pod configured model state so upstream `models` records can round-trip through Tau.
* **[CLI registration]**: local `setup` registration preserves the parsed SSH command in persisted pod config.
* **[Tests/docs]**: added config store regression coverage for upstream record-shaped load/save and legacy list-shaped load; synced parity matrix, `next.md`, architecture and quality notes.

### Design Intent (Why)

Upstream `packages/pods/src/config.ts` stores pods as a record keyed by pod id, with each pod carrying its configured model records. Tau previously used a list-shaped internal config, which kept path/env parity incomplete even after default path and missing-file semantics were fixed. This slice keeps Tau's internal typed model simple, but moves the persisted file contract to the upstream shape and keeps legacy list-shaped config readable for migration.

### Files Modified

* `src/Tau.Pods/Cli/PodsCli.cs`
* `src/Tau.Pods/Models/PodDefinition.cs`
* `src/Tau.Pods/Models/PodConfiguredModel.cs`
* `src/Tau.Pods/Models/UpstreamPodsConfig.cs`
* `src/Tau.Pods/Serialization/PodsJsonContext.cs`
* `src/Tau.Pods/Services/PodsConfigStore.cs`
* `src/Tau.Pods/Services/PodsConfigValidator.cs`
* `tests/Tau.Pods.Tests/PodsConfigValidatorTests.cs`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
* `next.md`

### Validation

* `dotnet test tests\Tau.Pods.Tests\Tau.Pods.Tests.csproj --no-restore --verbosity minimal` passed 192/192.
