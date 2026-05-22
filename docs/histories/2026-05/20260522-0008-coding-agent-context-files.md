# CodingAgent context files baseline

## 用户请求

继续按 Tau 的 pi-mono 移植计划推进，完成当前切片后指定下一步移植计划，不停在状态报告。

## 主要变更

- 新增 `CodingAgentContextFileStore`，默认加载用户目录 `~/.tau/AGENTS.md` 或 `~/.tau/CLAUDE.md`，再按 parent-to-cwd 顺序加载项目链上的 `AGENTS.md` 或 `CLAUDE.md`；同一目录优先 `AGENTS.md`，不可读文件跳过。
- `RuntimeCodingAgentRunner` 的生成 system prompt 现在会追加 `# Project Context` context files section；`RefreshSystemPromptResources(...)` 可在生成 prompt 模式下同时刷新 skills 和 context files，自定义 system prompt 不被覆盖。
- `Program.cs` 新增 `--no-context-files` / `-nc`，用于禁用默认 context file 加载。
- `/reload` 会重读 context files，并把输出从旧的 `themes/context files: not implemented` 拆成 `context files: N, runner prompt refreshed|unchanged` 和 `themes: not implemented`。
- 测试补齐 context file store 顺序/优先级/禁用、生成 prompt 注入、prompt refresh、自定义 prompt 不覆盖和 `/reload` context refresh。
- 同步 `docs/ARCHITECTURE.md`、`docs/QUALITY_SCORE.md`、`next.md`、active execution plan 和 release notes，明确 context files baseline 已完成，theme loader、完整 TypeScript extension runtime 和 full resource selector 仍未完成。

## 设计动机

上游 coding-agent 会把 `AGENTS.md` / `CLAUDE.md` 作为项目上下文注入 system prompt；Tau 之前已有 prompt、skill 和 JSON extension resource baseline，但 context files 仍被 `/reload` 明确标成未实现，导致仓库协作规则不会自动进入 runner。

本切片选择 Tau-native 用户目录 `~/.tau`，而不是照搬上游 `~/.pi/agent`，因为 Tau 现有 prompts、skills、extensions、settings 和本地状态都已经围绕 `.tau` 组织。项目目录顺序则对齐上游核心语义：先用户级，再从父级到当前目录逐层加载；同一目录 `AGENTS.md` 优先于 `CLAUDE.md`。

本切片刻意不实现完整 theme loader、TypeScript extension runtime lifecycle 或 full resource selector。这些属于更大 runtime/resource 系统，后续继续按 active plan 单独收口。

## 关键文件

- `src/Tau.CodingAgent/Runtime/CodingAgentContextFileStore.cs`
- `src/Tau.CodingAgent/Runtime/RuntimeCodingAgentRunner.cs`
- `src/Tau.CodingAgent/Runtime/ICodingAgentRunner.cs`
- `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
- `src/Tau.CodingAgent/Runtime/CodingAgentHost.cs`
- `src/Tau.CodingAgent/Program.cs`
- `tests/Tau.CodingAgent.Tests/CodingAgentContextFileStoreTests.cs`
- `tests/Tau.CodingAgent.Tests/RuntimeCodingAgentRunnerTests.cs`
- `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
- `docs/ARCHITECTURE.md`
- `docs/QUALITY_SCORE.md`
- `next.md`
- `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
- `docs/releases/feature-release-notes.md`

## 验证

- `dotnet build .\src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal`
- `dotnet test .\tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal`

## 下一步移植计划

继续 `Tau.CodingAgent` resource/runtime parity 的下一个窄切片：完整 resource selector 或 theme loader 二选一按 upstream 真实入口切入。优先先对照上游 `resource-loader.ts` 和相关 TUI command surface，固定 Tau 需要的最小数据模型、命令输出和可测试 seam；不要把 TypeScript extension runtime、theme loader 和 resource selector 混在同一个不可评审的大切片里。
