# CodingAgent RPC bash baseline

## 用户请求

继续按 Tau 的 pi-mono 移植计划推进，完成当前切片后指定下一步移植计划，不停在状态报告。

## 主要变更

- 新增 `ICodingAgentShellRunner`、`CodingAgentShellResult` 和 `SystemCodingAgentShellRunner`，给 RPC `bash` 命令提供独立的 shell execution seam。
- `CodingAgentRpcHost` 新增 `bash` / `abort_bash` 命令：`bash` 后台执行命令并在完成后写 response，`abort_bash` 只取消当前 shell 命令；并发 `bash` 会被拒绝。
- `SystemCodingAgentShellRunner` 使用当前工作目录执行命令，Windows 走 `cmd.exe /d /s /c`，非 Windows 走 `/bin/bash -lc`，返回 output、exitCode、cancelled、truncated 和可选 fullOutputPath。
- `CodingAgentRpcHostTests` 新增 4 个 targeted tests，覆盖成功响应、并发拒绝、取消和缺失 command 错误。
- 同步 README、ARCHITECTURE、QUALITY_SCORE、next、两份 active execution plan 和 release notes，明确这是 Tau-native RPC bash baseline，不是完整 streamed terminal subsystem。

## 设计动机

上游 RPC 协议把 `bash` / `abort_bash` 定义为 headless 控制面命令，返回 `BashResult`。Tau 之前已有 `ShellTool`，但它属于模型内部工具调用，不适合作为 RPC 控制面实现：RPC 需要独立取消、并发拒绝和测试替身。因此本切片新增 `ICodingAgentShellRunner`，让 RPC host 只负责协议、后台任务生命周期和响应写出，生产 shell 行为集中到 runner。

`bash` 不能在 stdin handler 内同步等待，否则 `abort_bash` 会因为读循环阻塞而无法进入。当前实现先记录 active bash task，再后台执行，完成后异步写对应 response；stdin 读循环继续处理 `abort_bash`。

本切片刻意不实现 streamed stdout/stderr event、terminal UI 或 extension UI 子协议。这些需要独立协议和渲染边界，后续按 full RPC parity 继续推进。

## 关键文件

- `src/Tau.CodingAgent/Runtime/CodingAgentShellRunner.cs`
- `src/Tau.CodingAgent/Runtime/CodingAgentRpcHost.cs`
- `tests/Tau.CodingAgent.Tests/CodingAgentRpcHostTests.cs`
- `README.md`
- `docs/ARCHITECTURE.md`
- `docs/QUALITY_SCORE.md`
- `next.md`
- `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
- `docs/exec-plans/active/2026-05-20-coding-agent-parity-gap-analysis.md`
- `docs/releases/feature-release-notes.md`

## 验证

- `dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal`
- `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal`

## 下一步移植计划

继续 `Tau.CodingAgent` RPC parity 的下一个窄切片：full settings RPC baseline。先对照上游 settings RPC / session settings surface，复用现有 `CodingAgentSettingsStore`，只补可持久化且可本地验证的 headless settings read/write 命令；不在同一切片实现完整 TUI selector 或 extension UI。
