### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET 10`

### User Query

> 继续继续

### Changes Overview

**Scope:** `Tau.Ai.Cli`, release contract scripts, and Tau parity docs.

**Key Actions:**

* Added a local dotnet tool install rehearsal for `Tau.Ai.Cli` that packs temporary `pi-ai` and `tau-ai` tool packages into a temp source, installs them to tool-paths, and verifies `--help` / `list` behavior under both aliases.
* Updated `Tau.Ai.Cli` to prefer the real tool shim command name when running as a dotnet tool, while keeping the existing `TAU_AI_CLI_COMMAND_NAME` override for release wrappers and tests.
* Wired the new tool-install smoke into `verify-dotnet.ps1 -RunSmoke`, `plan-release.ps1`, and `verify-release-contracts.ps1`.
* Synchronized `GOAL.md`, `next.md`, `docs/QUALITY_SCORE.md`, and the active parity matrix so the repo records this as a local package/global install alias rehearsal rather than a real registry publish or full global alias parity closure.

### Design Intent (Why)

The previous AI/Agent work had already proved local package consumption and release wrapper alias behavior, but the remaining gap was a practical install rehearsal for the CLI package itself. A temp tool source is enough to prove that `Tau.Ai.Cli` can be packed, installed, and run under both `pi-ai` and `tau-ai` command names without pretending that real NuGet registry publish, signing/provenance, or global marketplace install are complete.

### Validation

* `dotnet pack src/Tau.Ai.Cli/Tau.Ai.Cli.csproj -c Release -o <temp> -p:PackAsTool=true -p:ToolCommandName=pi-ai -p:PackageId=Tau.Ai.Cli.Tool --no-restore`
* `dotnet tool install --tool-path <temp> --add-source <temp> Tau.Ai.Cli.Tool --version 0.1.0 --ignore-failed-sources`
* `pi-ai --help` from the installed tool path showed `Usage: pi-ai <command> [provider] [options]`

### Files Modified

* `src/Tau.Ai.Cli/AiCliRunner.cs`
* `tests/Tau.Ai.Tests/AiCliRunnerTests.cs`
* `scripts/verify-ai-cli-tool-install.ps1`
* `scripts/verify-dotnet.ps1`
* `scripts/plan-release.ps1`
* `scripts/verify-release-contracts.ps1`
* `GOAL.md`
* `next.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
* `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
