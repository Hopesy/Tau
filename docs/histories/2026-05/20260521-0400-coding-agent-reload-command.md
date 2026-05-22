## [2026-05-21 04:00] | Task: CodingAgent reload command baseline

### Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Windows PowerShell`

### User Query

> 用户要求继续推进 `Tau.CodingAgent` 对 `pi-mono-main` 的 parity 移植。

### Changes Overview

**Scope:** `Tau.CodingAgent` slash command parity、`Tau.Tui` interactive editor keybindings、测试与仓库文档。

**Key Actions:**

* **新增 `/reload` baseline**: 在命令 catalog 和 router 中接入 `/reload`，支持重读 settings、JSON extension resources、prompt templates、skills 和交互式 editor keybindings。
* **同步当前进程状态**: reload 后立即更新 host retry policy、runner default thinking level、extension-contributed prompt/skill paths，并在 runner 使用生成 system prompt 时刷新 skill inventory。
* **补齐运行时 seam**: 新增 `CodingAgentExtensionResourceState` 保存 extension resource paths；prompt/skill stores 支持动态 additional paths provider；`InteractiveInputEditor` 支持替换当前 keybindings；runner interface 支持 `RefreshSkills(...)`。
* **补 targeted tests**: 覆盖 `/reload` 成功重载 settings/resources/skills/keybindings，以及附加参数返回 usage。
* **同步文档与计划**: 更新 README、ARCHITECTURE、QUALITY_SCORE、next 和两份 active execution plan，明确这是 baseline，不是完整上游 reload parity。

### Design Intent

上游 `/reload` 涵盖 keybindings、extensions、skills、prompts、themes 和 context files，同时还牵涉完整 extension runtime lifecycle。Tau 当前已经有 settings store、JSON extension command/resource loader、prompt/skill stores 和交互式 keybinding loader，但还没有完整 theme/context loader 或 TypeScript extension runtime。

本切片选择先把现有可变事实源做成可验证、可回归的当前进程 reload：用户修改 settings、extension resources、prompts、skills 或 keybinding JSON 后，不需要重启 CLI 即可让当前 host/runner/editor 吃到最新状态。命令输出保留 `themes/context files: not implemented`，避免把未移植的上游能力写成已完成。

### Files Modified

* `src/Tau.Tui/Runtime/InteractiveInputEditor.cs`
* `src/Tau.CodingAgent/Runtime/ICodingAgentRunner.cs`
* `src/Tau.CodingAgent/Runtime/RuntimeCodingAgentRunner.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentExtensionResourceState.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentPromptTemplateStore.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentSkillStore.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandCatalog.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentHost.cs`
* `src/Tau.CodingAgent/Program.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `tests/Tau.CodingAgent.Tests/FakeCodingAgentRunner.cs`
* `tests/Tau.Agent.Tests/RuntimeDelegationAgentRunnerTests.cs`
* `tests/Tau.WebUi.Tests/FakeWebUiRunner.cs`
* `README.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `next.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`

### Verification

* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal` - passed, 207/207 tests.
* `dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal` - passed, 0 warnings, 0 errors.

### Known Remaining Work

* Full upstream theme/context file reload is not implemented.
* Full TypeScript extension runtime lifecycle reload is not implemented.
* Complete app/session/tree/extension shortcut registry and footer hints remain future TUI/extension runtime parity work.
