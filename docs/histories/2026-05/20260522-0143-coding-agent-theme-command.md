# CodingAgent theme command baseline

## 用户请求

继续按 Tau 的 `pi-mono-main` 移植计划推进当前 `Tau.CodingAgent` parity 切片，并按 harness-init 约定同步 plan/history/quality/release 文档。

## 主要变更

- 新增 `/theme [current|list|set|clear] [name]` command catalog 入口，并让 `/help` 继续从 `CodingAgentCommandCatalog.HelpLine` 读取同一事实源。
- `CodingAgentCommandRouter` 新增 theme handler：
  - `/theme` / `/theme current` 显示当前 settings `theme`，未设置时回到默认 `dark`。
  - `/theme list` 复用 `CodingAgentThemeStore.LoadStatus()` 列出 built-in、用户、项目、env/CLI 和 extension-contributed themes，并展示 diagnostics。
  - `/theme set <name>` 大小写不敏感校验主题存在后写回 settings `theme`。
  - `/theme clear` 清空 settings `theme`，恢复默认 `dark`。
- `/settings` summary 纳入当前 theme，settings 保存时保留同一 JSON 文件里的其他字段。
- 测试补齐 `/theme` list/set/clear/current、missing theme、usage、无 settings store、无 theme store，以及 host help/catalog 同步。
- 同步 `docs/ARCHITECTURE.md`、`docs/QUALITY_SCORE.md`、`next.md`、active execution plan 和 release notes，明确这次是 CLI/settings-backed theme selection baseline，不是完整上游 theme selector、TUI rendering 或 file watcher parity。

## 设计动机

上游 coding-agent 具备 settings-backed theme selection 和 theme discovery。Tau 已经有 `CodingAgentThemeStore` 与 settings `Theme` 字段，但缺少用户可见的 CLI 命令面，导致用户只能手改 settings JSON。

本切片选择先固定 `current/list/set/clear` 的小命令面，因为它复用现有 theme loader/status，行为可审计、可测试，也能给后续 TUI selector、runtime theme rendering 和 theme file watcher 复用同一 settings 事实源。

本切片刻意不把完整上游 theme selector、TUI theme rendering、theme file watcher 或 TypeScript extension runtime 写成已完成。

## 关键文件

- `src/Tau.CodingAgent/Runtime/CodingAgentCommandCatalog.cs`
- `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
- `src/Tau.CodingAgent/Runtime/CodingAgentSettingsStore.cs`
- `src/Tau.CodingAgent/Runtime/CodingAgentThemeStore.cs`
- `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
- `tests/Tau.CodingAgent.Tests/CodingAgentHostTests.cs`
- `docs/ARCHITECTURE.md`
- `docs/QUALITY_SCORE.md`
- `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
- `docs/releases/feature-release-notes.md`
- `next.md`

## 验证

- `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal`：通过，257/257。
- `dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal`：通过，0 warning / 0 error。
- `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`：通过，src/tests 项目级 build 全部 0 warning / 0 error，测试计数为 `Tau.Ai.Tests` 194、`Tau.Agent.Tests` 58、`Tau.Tui.Tests` 56、`Tau.CodingAgent.Tests` 257、`Tau.Pods.Tests` 32。

## 下一步移植计划

继续 `Tau.CodingAgent` theme parity 的下一段：在不扩散到完整 TypeScript extension runtime 的前提下，优先对照上游 theme selector 和 TUI rendering 入口，固定 Tau 需要的 theme application seam、可验证渲染边界和 watcher/refresh 行为，再决定是否拆成 selector、rendering、watcher 三个独立切片。
