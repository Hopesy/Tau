## [2026-05-23 21:28] | Task: JSONL redaction and vLLM JSON plan closure

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell, .NET 10`

### User Query

> 继续推进 Tau 的 pi-mono 移植链路，并完成当前已落地切片的验证、文档与历史记录收口。

### Changes Overview

**Scope:** `Tau.Ai`, `Tau.CodingAgent`, `Tau.Pods`, repo docs

**Key Actions:**

* **JSONL runtime log redaction**: `JsonlTauLogSink` 不再逐字段手工 redaction，而是先生成完整 JSONL 行，再复用 `JsonlSecretRedactor.RedactLine` 递归脱敏 JSON string value，保留 object key、number、bool 和 null。
* **CodingAgent tree session redaction**: `CodingAgentTreeSessionStore` 在 session header、append entry、rewrite 和 branch export 写出时统一走 `JsonlSecretRedactor`；默认由 `TAU_CODING_AGENT_REDACT_SECRETS` 控制，可用 `0/false` 关闭。
* **Pods vLLM JSON plan**: `vllm plan` 新增 `--json` 输出，保留默认文本输出；JSON 包含 pod、deployment、model path、port、served model、systemd unit、metadata 和 remote command，仍然只生成 plan，不执行 SSH 或 systemd。
* **Docs sync**: README、架构、质量评分、总 plan、next 和 release notes 同步三条已验证切片的完成边界，并保留 WebUi JSONL、field key、非标准 secret pattern、真实 vLLM orchestration 等剩余缺口。

### Design Intent (Why)

本轮把已经存在的通用 JSONL 脱敏 helper 提升为多个 writer 的共享事实源，避免 runtime log、CodingAgent tree session 和 Mom channel log 各自维护不同规则。对 JSONL 文件只处理 string value，是为了避免破坏 field key、数值、布尔和 null 的可读性与机器可解析性。

`vllm plan --json` 只补 machine-readable plan 输出，不提前接远端 orchestration。这样外部脚本可以稳定消费计划结果，同时仍明确不执行 SSH、不启动 systemd、不写远端状态。

### Validation

* `dotnet test tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --no-restore --verbosity minimal` passed.
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal` passed.
* `dotnet test tests\Tau.Pods.Tests\Tau.Pods.Tests.csproj --no-restore --verbosity minimal` passed.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` initially failed before source compilation because local NuGet cache missed `microsoft.net.illink.tasks/10.0.8` analyzer DLLs.
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1` restored dependencies and passed full project-level validation: `Tau.Ai.Tests` 205, `Tau.Agent.Tests` 76, `Tau.Tui.Tests` 110, `Tau.CodingAgent.Tests` 346, `Tau.Pods.Tests` 66.

### Files Modified

* `src/Tau.Ai/Observability/JsonlTauLogSink.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentTreeSessionStore.cs`
* `src/Tau.Pods/Cli/PodsCli.cs`
* `tests/Tau.Ai.Tests/JsonlTauLogSinkTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentTreeSessionRedactionTests.cs`
* `tests/Tau.Pods.Tests/PodsCliTests.cs`
* `README.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `docs/releases/feature-release-notes.md`
* `next.md`
