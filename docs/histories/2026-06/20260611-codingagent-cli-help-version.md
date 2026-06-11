## [2026-06-11] | Task: CodingAgent CLI --help / --version parity

### 🤖 Execution Context

* **Agent ID**: `Claude`
* **Base Model**: `Opus 4.8`
* **Runtime**: `Windows PowerShell / .NET 10`

### 📥 User Query

> 按 `GOAL.md` 继续 Tau 100% pi-mono parity 迁移，从 Phase 2 Candidate Queue 领取一个可审计 contract 切片。

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent` CLI argument parsing / startup, targeted tests

**Key Actions:**

* **Bug fix + contract closure**: 之前 `CodingAgentCliArguments.Parse` 把 `--help`/`-h`/`--version`/`-v` 放进 `BooleanOptions` 后**直接丢弃**，导致 `pi --help` 与 `pi --version` 实际会尝试启动 agent，而不是打印帮助/版本并退出。本轮把它们从 `BooleanOptions` 移除，新增 `Help` / `Version` 字段并显式解析（含 `-h`/`-v` 短选项）。
* **Version output**: `Program.cs` 在解析后立即处理 `--version`，对照上游 `main.ts` 的 `console.log(VERSION); process.exit(0)`，输出当前 assembly informational/file version（规整为 `major.minor.patch`）并返回 0。
* **Help output**: 新增 `CodingAgentCliHelp`，对照上游 `cli/args.ts` 的 `printHelp(extensionFlags)`，渲染 usage、package commands（install/remove/uninstall/update/list/config）、全部 options 和 extension-registered CLI flags；`Program.cs` 在 extension store 加载后、extension flag 校验前处理 `--help`，使带未知 flag 的 `--help` 不会先报错，并能列出 extension flags（对齐上游 help-after-resources 时序）。
* **Command name**: help/usage 默认命令名为上游 `APP_NAME`=`pi`，可由 `TAU_CODING_AGENT_COMMAND_NAME` 覆盖，复刻 `Tau.Ai.Cli` 的 `TAU_AI_CLI_COMMAND_NAME` 别名模式。
* **Tests**: `CodingAgentInitialMessageBuilderTests` 新增 `Parse` 对 `--help`/`-h`/`--version`/`-v` 的回归，并固定 `CodingAgentCliHelp.BuildHelpText` 的 usage、command 名注入和 extension-flag 列出行为。

### 🧠 Design Intent (Why)

`--help` / `--version` 是用户最早会碰到的 CLI 合同，上游有明确退出语义。Tau 之前解析后丢弃这两个 flag 属于真实 parity 缺口（且是可见 bug）。本轮按上游 `main.ts` 的 early-version / help-after-resources 时序补齐，help 复用真实 extension flag 列表，避免与文档帮助文案分叉。命令名通过 env 注入，使 release wrapper 下的 `pi` 别名能在 usage 文案里被感知，而不是硬编码字符串。

本切片只关闭 `--help` / `--version` 输出合同。其它 CLI flag（如 `--api-key`、`--session`、`--fork`、`--session-dir`、`--models`、`--tools`、`--continue`、`--resume`、`--list-models`、`--append-system-prompt`、`--verbose`、`--offline`）的真实 runtime wiring 仍在 CodingAgent CLI parity backlog 中保持 open。

### ✅ Validation

* `dotnet test tests/Tau.CodingAgent.Tests/Tau.CodingAgent.Tests.csproj --filter "CodingAgentInitialMessageBuilderTests" --no-restore --verbosity minimal`：17/17 passed
* `dotnet run --project src/Tau.CodingAgent --no-build -- --version`：输出 `0.1.0`
* `dotnet run --project src/Tau.CodingAgent --no-build -- --help`：输出 usage / commands / options（命令名 `pi`）
* `dotnet test tests/Tau.CodingAgent.Tests/Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal`：542/542 passed
* `powershell -NoProfile -ExecutionPolicy Bypass -File ./scripts/verify-dotnet.ps1 -SkipRestore`：passed（Ai 287、Agent 123、Tui 251、CodingAgent 542、WebUi 61、Pods 216）

### 📁 Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentInitialMessageBuilder.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCliHelp.cs` (new)
* `src/Tau.CodingAgent/Program.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentInitialMessageBuilderTests.cs`
