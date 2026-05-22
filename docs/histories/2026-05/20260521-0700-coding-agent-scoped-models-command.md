## [2026-05-21 07:00] | Task: CodingAgent scoped models command baseline

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell`

### 📥 User Query

> 继续按 `pi-mono-main` 移植计划推进 `Tau.CodingAgent` parity。

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent`、相关测试、README / architecture / quality / next / release notes / active plans

**Key Actions:**

* **Settings store**: 在 `CodingAgentSettingsSnapshot` / settings document 中新增 `EnabledModels`，映射 JSON 字段 `enabledModels`；读写时做 trim、去空、大小写不敏感去重，空数组归一为 `null`。
* **Command routing**: 在 command catalog / router 中接入 `/scoped-models [set|add|remove|clear|all] [provider/model ...]`；支持查看当前 scope、设置显式 scope、追加、移除、清空和恢复全模型。
* **Model reference parsing**: 命令使用当前 runner 暴露的 provider/model 列表解析 `provider/model` 或唯一 model id；未知模型、歧义 model id、空 scope 和参数错误返回明确错误。
* **Settings preservation**: `/scoped-models` 保存时保留同一 settings 文件里的默认 provider/model、`treeFilterMode`、retry policy 和 default thinking level；scope 等于全量模型时保存为 `null`，表示 all enabled / no filter。
* **Tests and docs**: 补 settings store 与 router targeted tests，更新 `/help` 预期，并同步 README、architecture、quality score、next、release notes 和两份 active execution plans。

### 🧠 Design Intent (Why)

上游 `/scoped-models` 的完整形态是交互式 selector，用于启用/禁用模型并决定 Ctrl+P cycling 的模型子集；上游 settings 以 `enabledModels` 表示这个 scope，缺失或隐式 all enabled 不应被物化成冗余全量列表。

Tau 当前还没有完整 TUI selector / Ctrl+P model cycling UI 层，因此本切片先做 Tau-native CLI/settings baseline：固定 `enabledModels` 的持久化语义、错误边界和 settings 保留行为。这样后续实现 scoped model selector 或 Ctrl+P cycling 时，可以直接复用同一个 settings contract，而不是重新定义模型 scope 存储。

### 📁 Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentSettingsStore.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandCatalog.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentSettingsStoreTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentHostTests.cs`
* `README.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`
* `docs/releases/feature-release-notes.md`
* `next.md`

### ✅ Verification

* `dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal`：通过，0 warning / 0 error。
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal`：通过，218/218。
* `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`：通过，src/tests 项目级 build/test 全部完成；测试计数为 `Tau.Ai.Tests` 194、`Tau.Agent.Tests` 54、`Tau.Tui.Tests` 56、`Tau.CodingAgent.Tests` 218、`Tau.Pods.Tests` 32。
* `git diff --check -- src\Tau.CodingAgent tests\Tau.CodingAgent.Tests README.md docs next.md`：退出码 0；仅提示若干已知 CRLF normalization warning，无空白错误。
