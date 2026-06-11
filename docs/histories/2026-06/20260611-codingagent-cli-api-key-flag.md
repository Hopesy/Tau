## [2026-06-11] | Task: CodingAgent CLI `--api-key` runtime wiring

### 🤖 Execution Context

* **Agent ID**: `Claude`
* **Base Model**: `Opus 4.8 (1M context)`
* **Runtime**: `Windows PowerShell / .NET 10`

### 📥 User Query

> 按照 `GOAL.md` 继续 Tau 100% pi-mono parity 迁移，领取下一个可审计 CLI 合同切片。

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent`

**Key Actions:**

* **CLI parse**: `CodingAgentCliArguments` 新增 `ApiKey` 字段，`--api-key <key>` / `--api-key=<key>` 从 `OptionsWithValue` 提到显式解析，不再被吞掉。
* **Runtime wiring**: `RuntimeCodingAgentRunner.Create(..., apiKey)` 把 CLI 提供的 key 放入 `StreamOptions.ApiKey`；`StreamFunctions` 经 `ProviderAuthResolver.ResolveApiKey(provider, options.ApiKey)` 让显式 key 优先于 env / auth.json / OAuth。
* **Program wiring**: `Program.cs` 把 `cli.ApiKey` 传给 `RuntimeCodingAgentRunner.Create(apiKey: ...)`。
* **Tests**: 新增 parser 回归（`--api-key`、`--api-key=` 两种形式）和 runner 回归（`OptionsCapturingProvider` 验证 CLI key 到达 provider `StreamOptions.ApiKey`）。

### 🧠 Design Intent (Why)

上游 `main.ts` 对 `parsed.apiKey` 调用 `authStorage.setRuntimeApiKey(model.provider, parsed.apiKey)`，让单次运行的显式 key 优先于持久化凭证。Tau 已有 `StreamOptions.ApiKey` 与 `ResolveApiKey` 的显式-key-优先语义，因此本切片只把 CLI 值接到既有解析链，而不是引入新的 runtime auth store。Tau 始终能解析出 provider/model（有默认值），因此不需要上游 "--api-key requires a model" 的 error 分支。

### ✅ Validation

* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --filter "CodingAgentInitialMessageBuilderTests|RuntimeCodingAgentRunnerTests" --no-restore`：34/34 passed
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`：passed（Ai 287、Agent 123、Tui 251、CodingAgent 572、WebUi 61、Pods 216）

### 📁 Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentInitialMessageBuilder.cs`
* `src/Tau.CodingAgent/Runtime/RuntimeCodingAgentRunner.cs`
* `src/Tau.CodingAgent/Program.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentInitialMessageBuilderTests.cs`
* `tests/Tau.CodingAgent.Tests/RuntimeCodingAgentRunnerTests.cs`
* `next.md`
* `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
