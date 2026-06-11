## [2026-06-10 21:01] | Task: 收口 Tau.Ai `pi-ai` release wrapper 命名合同

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI / PowerShell`

### 📥 User Query

> 继续继续

### 🛠 Changes Overview

**Scope:** `Tau.Ai.Cli`, release artifact scripts, Tau.Ai tests

**Key Actions:**

* **Command-name alias**: `Tau.Ai.Cli` 现在读取 `TAU_AI_CLI_COMMAND_NAME`，让同一个 CLI 可执行文件在 `tau-ai` 和 `pi-ai` wrapper 下分别输出对应的 help / error usage 名称。
* **Release wrapper**: `scripts/build-release-artifacts.ps1` 生成 Windows/Unix wrapper 时，会按当前 alias 注入 `TAU_AI_CLI_COMMAND_NAME`，确保 `pi-ai` 不是单纯的文件名副本，而是可感知的公开入口名；Unix wrapper 使用 POSIX 参数展开取得脚本名，避免 GNU-only `basename --` 依赖。
* **Smoke closure**: `scripts/smoke-release-artifacts.ps1` 新增 `pi-ai --help` 验证；同时修正 `examples` 产物预期，匹配当前 release artifact manifest 的真实内容。
* **Tests**: `tests/Tau.Ai.Tests/AiCliRunnerTests.cs` 补了命令名环境变量、显式 `pi-ai` 命名和 unknown provider 错误文案的回归。
* **Docs sync**: 同步更新 `GOAL.md`、`next.md`、`docs/QUALITY_SCORE.md`、`docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md` 和 `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`，把 `pi-ai` wrapper 的命名合同标成已闭合，同时保留 package/global install alias、exact TypeScript export/subpath 和真实 OAuth/provider e2e 作为后续缺口。

### 🧠 Design Intent (Why)

上游 `pi-ai` 的入口名不仅是 wrapper 路径，还会体现在用户看到的 usage / error 文案里。Tau 之前已经能产出 `tau-ai` 和 `pi-ai` wrapper，但运行时命令名仍偏向固定字符串，导致 alias 只是在文件层面存在。把命令名显式传到 CLI，可以把 release artifact 的公开语义收口到“可运行且可感知”，并让 smoke 直接验证这层合同。

### ✅ Validation

* `dotnet test tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --filter AiCliRunnerTests --no-restore --verbosity minimal`：10/10 passed
* `dotnet test tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --no-restore --verbosity minimal`：287/287 passed
* `dotnet restore Tau.slnx -r win-x64 --verbosity minimal`：passed
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-release-artifacts.ps1 -Runtime win-x64 -SkipRestore`：passed，内置 artifact smoke 覆盖 `tau-ai list`、`pi-ai list`、`pi-ai --help`、`pi` RPC、`pi-pods --help`、WebUi 和 Mom smoke

### 📁 Files Modified

* `src/Tau.Ai.Cli/AiCliRunner.cs`
* `tests/Tau.Ai.Tests/AiCliRunnerTests.cs`
* `scripts/build-release-artifacts.ps1`
* `scripts/smoke-release-artifacts.ps1`
