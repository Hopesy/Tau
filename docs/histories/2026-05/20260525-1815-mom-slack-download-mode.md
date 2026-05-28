## [2026-05-25 18:15] | Task: Mom Slack download mode

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell / .NET`

### User Query

> 持续快速推进 pi-mono 到 Tau 的功能移植，降低大段文档和低收益单元测试占比；本轮继续下一块真实 parity。

### Changes Overview

**Scope:** `Tau.Mom`

**Key Actions:**

* Added `mom --download <channel-id>` / `--download=<channel-id>` command-line mode.
* Added `SlackChannelHistoryDownloadService` for Slack channel transcript export through `conversations.info`, paged `conversations.history`, and thread `conversations.replies`.
* Download mode writes transcript to stdout and progress to stderr, and does not start hosted Mom workers.
* Added targeted coverage for CLI parsing, chronological transcript output, channel-id fallback, and missing bot-token failure.

### Design Intent

Upstream Mom has a user-requested Slack history export path separate from runtime backfill. Tau keeps the same separation: startup backfill mutates existing channel logs incrementally, while `--download` is an explicit full export command with stdout/stderr behavior and no worker side effects.

### Files Modified

* `src/Tau.Mom/MomCommandLine.cs`
* `src/Tau.Mom/Program.cs`
* `src/Tau.Mom/SlackChannelHistoryDownloadService.cs`
* `tests/Tau.Agent.Tests/MomSandboxAndToolsTests.cs`
* `tests/Tau.Agent.Tests/SlackChannelHistoryDownloadServiceTests.cs`
* `next.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`

### Validation

* `dotnet build src\Tau.Mom\Tau.Mom.csproj --no-restore --verbosity minimal` passed.
* `dotnet test tests\Tau.Agent.Tests\Tau.Agent.Tests.csproj --no-restore --verbosity minimal --filter "FullyQualifiedName~SlackChannelHistoryDownloadServiceTests|FullyQualifiedName~CommandLine_ParseStripsDownloadModeAndChannelFromHostArgs"` passed: 4/4.
* `dotnet run --project src\Tau.Mom\Tau.Mom.csproj --no-build -- --download C123456` exited 1 as expected on this machine without `MOM_SLACK_BOT_TOKEN`, with the missing bot-token diagnostic.
