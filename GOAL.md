# Tau 100% pi-mono parity /goal

## /goal prompt

当前目标（foundation-first override，2026-06-15）：先把 `Tau.Ai` / `Tau.Agent` 作为可被外部 .NET 项目引用的 Agent 基座收口到本地 100% 完成，然后才恢复 `Tau.CodingAgent`、`Tau.Tui`、`Tau.WebUi`、`Tau.Mom`、`Tau.Pods` 等工具/产品项目迁移。全量 `C:\Users\zhouh\Desktop\pi-mono-main` 100% 可审计移植仍是最终目标，但当前 `/goal` 调度必须先服务 AI/Agent foundation。

你是 Main Integrator，不是单模块 worker。当前主线恢复为 `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`；Phase 1 inventory freeze 的权威矩阵是 `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`。后续实现必须从 matrix 的 `Phase 2 Candidate Queue` 或当前 active plan 明确列出的 Phase 2/3/4/5 缺口领取，不重开 broad inventory。

`docs/exec-plans/completed/2026-06-07-tau-agent-platform-baseline.md` 是已完成的前置能力：它证明 Tau 已具备第一版可复用 Agent 应用底座、examples、provider run / tool execution runtime log、platform smoke 和全仓 gate 证据。它不是 100% product parity 完成证据，也不能关闭真实 provider/OAuth、Slack、Docker、SSH/HF/GPU/vLLM、WebUi artifact/runtime、Tui live terminal、release/package registry 等最终缺口。

每一轮都按当前 repo 事实推进：先区分 committed baseline、dirty WIP、已验证结果、文档声称和真实上游行为；再选择一个可审计、可验证、可单独评审的 parity 切片。当前优先级下，如果 `Tau.Ai` / `Tau.Agent` foundation completion gate 没有完成并形成提交边界，不领取其它工具/产品模块的新切片。不要问“要不要继续”。只有存在真正歧义且继续会产出与用户意图相反的成果时才停下来问。

## /goal 执行协议

- `/goal` 是 Tau 迁移的持续运行主控入口。收到 `继续`、`继续继续`、`下一步` 时，不重新询问是否继续，也不重开 inventory，而是直接读取当前工作树、`GOAL.md`、`next.md`、matrix、active plan、history 和验证证据，进入下一轮。
- 每一轮都必须按 `审视 -> 执行 -> 提升` 运行，只领取一个互斥切片；切片完成后立刻同步 `next.md`、`docs/QUALITY_SCORE.md`、active plan、matrix 和 history，再进入下一轮。
- 切片的默认退出条件只有三个：当前切片的验收门槛全部满足、选择的缺口被用户明确确认是 `non-goal`、或者同一阻塞条件连续三轮复现且确实无法前进，需要向用户说明阻塞。
- 这份文件写的是“怎么持续推进直到完成”，不是“一次性任务清单”；中间任何通过都只是 checkpoint，最终目标仍然是把所有迁移推进到 100% parity 并关闭 Final audit。

## Foundation-first completion gate

当前用户指定 `Tau.Ai` / `Tau.Agent` 先于其它工具项目完全收口。这里的 “100%” 是 **Agent 基座可被其它 .NET 项目引用使用** 的本地完成标准，不等同于全量 pi-mono product parity 或真实外部云服务验收。只有以下条件全部满足，才能恢复其它工具/产品模块迁移：

- `Tau.Ai` package 可独立被外部项目引用：本地 pack 后，临时外部 app 只 `PackageReference Include="Tau.Ai"`，能 restore/build/run，并通过 `Tau.Ai.Providers.Faux`、`ProviderRegistry`、`StreamFunctions` 完成一次 LLM 调用。
- `Tau.Agent` package 可被外部项目引用：本地 pack 后，临时外部 app 只 `PackageReference Include="Tau.Agent"`，能通过 `Tau.Agent.nuspec` 传递消费 `Tau.Ai`，并运行 `AgentApplication`、delegate tool、session store 和 runtime log 回合。
- `Tau.Ai` / `Tau.Agent` NuGet metadata 不再是占位：package description、README、license、repository、tags 和 `Tau.Agent -> Tau.Ai` dependency 均被脚本断言。
- public API compile samples、Agent platform examples、`verify-agent-package-consumer.ps1`、`verify-release-contracts.ps1` 和 `verify-dotnet.ps1 -SkipRestore -RunSmoke` 全部通过。
- `GOAL.md`、active plan、parity matrix、`next.md`、`docs/QUALITY_SCORE.md`、README 和 history 同步记录该 foundation 边界。

真实 provider/OAuth e2e、真实 NuGet registry 发布、真实 signing/provenance、global install alias 和 TypeScript export/subpath exact parity 仍是后续全量 parity / external-e2e / release gates；没有真实凭证或 registry 环境时不得伪造成完成，但也不得阻塞本地 Agent 基座包引用能力的完成判定。

## Current checkpoint

- [x] Agent platform baseline completed：`src/Tau.Agent/Platform/**`、Console/HTTP examples、provider run + tool execution runtime log、platform smoke 和全仓 gate 已经作为 shared Agent foundation 归档到 `docs/exec-plans/completed/2026-06-07-tau-agent-platform-baseline.md`。
- [x] Phase 1 inventory freeze completed：`docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md` 已冻结 capability、file-level、surface 和 root scripts/manifests 三层 inventory；后续不重开 broad package scan。
- [x] Foundation-first local package consumer boundary：`scripts/verify-agent-package-consumer.ps1` 覆盖 `Tau.Ai` direct package consumer 和 `Tau.Agent` transitive `Tau.Ai` package consumer；2026-06-15 已复跑 dedicated smoke 22 assertions、`verify-release-contracts.ps1 -Json` 和 `verify-dotnet.ps1 -SkipRestore -RunSmoke`，本地 Agent 基座 package consumer gate 已验证完成。
- [x] Agent platform builder facade option pass-through：`AgentApplicationBuilder.AddTool(..., prepareArguments:)` 现在会透传给 `DelegateAgentTool`；`AgentPlatformTests.PromptAsync_UsesDelegateToolPrepareArgumentsBeforeExecution` 和 `AgentPublicApiCompileSampleTests` 已证明该参数在执行前生效且可被外部消费者编译使用。
- [x] Agent stream proxy local server path：`tests/Tau.Agent.Tests/ProxyStreamProviderTests.cs` 已覆盖真实 loopback HTTP/SSE `/api/stream` server path、Authorization/body contract、缺 terminal event 和 malformed SSE JSON；`scripts/verify-agent-proxy-server-e2e.ps1` 已接入 `verify-dotnet.ps1 -RunSmoke`、`plan-release.ps1` 和 `verify-release-contracts.ps1`。
- [x] Tau.Ai.Cli local dotnet tool install alias：`scripts/verify-ai-cli-tool-install.ps1` 通过临时本地 package source 安装 `pi-ai` / `tau-ai` dotnet tool 包并验证 `--help` / `list` 行为；`Tau.Ai.Cli` 现优先识别真实 tool shim 命令名，同时保留 `TAU_AI_CLI_COMMAND_NAME` 供 release wrapper 注入。
- [ ] Phase 2 critical contract closure：foundation-first gate 当前优先于其它工具/产品切片；通过后继续从 matrix 的 `Phase 2 Candidate Queue` 领取真实 provider/OAuth、package/global install alias、runtime contract 或 release/package 缺口。
- [ ] Phase 3 product runtime parity：CodingAgent + Tui、WebUi、Mom、Pods 的用户可见 runtime 行为继续补齐；fake-only runner、stub provider、fixture smoke 只能作为合同证据，不能标成产品完成。
- [ ] Phase 4 external e2e closure：真实 provider/OAuth/AWS/Slack/Docker/Pods/WebUi/browser/release 等 e2e 要通过；没有环境时保持 `external-e2e-needed`，不能降级成完成。
- [ ] Phase 5 release/package/CI final parity：Tau release/CI 必须产出真实 executable/package artifacts，并完成 registry/signing/provenance/non-host smoke 或取得用户明确非目标确认。
- [ ] Final audit：matrix 全部 `verified` 或用户确认 `non-goal`，`next.md` 无未完成 parity backlog，active plan 可归档为 completed。

## Strict completion criteria

全量 `Tau 100% pi-mono parity /goal` 只有在以下条件全部满足后才能标记 complete：

- 上游 `packages/ai`、`packages/agent`、`packages/coding-agent`、`packages/tui`、`packages/web-ui`、`packages/mom`、`packages/pods` 以及 root scripts/manifests 的用户可见功能、协议、命令、配置、环境变量、错误语义、日志、持久化 schema 和 release/CI 行为都有 Tau 对应实现、验证证据，或有用户明确确认的 `non-goal`。
- `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md` 中所有条目最终为 `verified` 或用户确认的 `non-goal`；`partial`、`missing`、`ported`、`external-e2e-needed` 不能作为最终完成状态。
- `next.md` 不再保留未完成 product parity backlog；无法完成的项必须写成用户确认的非目标，而不是“后续优化”。
- Windows PowerShell 本地权威链通过：
  - `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`
  - `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore -RunSmoke`
- 真实外部 e2e 通过，或用户明确确认非目标：AI providers、OAuth、AWS/Bedrock、Slack、Docker、Pods SSH/HF/GPU/vLLM、WebUi browser/release/static smoke、package registry/signing/provenance/release smoke。
- release/CI 产出真实 Tau executable/package artifacts；不能只保留 dry-run、fake runner 或占位 wrapper。
- 每个实质变更都有 `docs/histories/YYYY-MM/**` 记录；active plan、matrix、`next.md`、`docs/QUALITY_SCORE.md` 和必要架构/安全/可靠性文档同步。
- 最终报告必须列出每个上游 package 和 root script 的 Tau 证据链：对应实现、targeted tests/smoke、外部 e2e 或用户确认的 `non-goal`。没有证据链的行不能被标为 `verified`。
