# Tau pi-mono 移植基线与 P0 收口计划

## 目标

把 Tau 从“能编译的移植骨架”推进到“有明确第一条用户路径、可持续迭代的 .NET 移植项目”。第一阶段不追求一次性补齐全部上游能力，而是先收口出一个可验证的 P0：本地可运行的编码 Agent CLI 基线，以及与之匹配的测试、CI 和文档边界。

## 当前阶段结论

截至 2026-04-23，Tau 的真实状态可以概括为：

- `Tau.Ai` 和 `Tau.Agent` 已有可继续扩展的核心骨架
- `Tau.CodingAgent` 已恢复最小可运行 CLI、独立 `csproj` build 与基本 smoke 测试
- `Tau.Tui` 已有最小 transcript / input buffer / session 层
- `Tau.WebUi / Tau.Mom / Tau.Pods` 仍处于占位或模板态

因此这份计划不是“全面移植路线图”，而是 **当前主线收口计划**：先把 Tau 变成一个真实可迭代的 CLI-first 项目。

## 范围

- 包含：
  - 盘点 Tau 与上游 `pi-mono` 的能力差距，并把结论固化为仓库内计划。
  - 锁定第一条用户路径：`Tau.CodingAgent` 本地交互式编码流程。
  - 围绕 P0 路径分阶段补齐 `Tau.Tui`、`Tau.CodingAgent`、`Tau.Agent`、`Tau.Ai` 所需的最小能力。
  - 为 P0 路径补最小 smoke 测试、`dotnet build/test` CI 以及必要文档。
- 不包含：
  - 一次性完整移植 `Tau.WebUi`、`Tau.Mom`、`Tau.Pods` 全部产品面。
  - 在 P0 阶段追求与上游 TypeScript API 的 1:1 完整兼容。
  - 提前铺开所有 provider、OAuth、扩展生态、主题系统和包管理能力。

## 背景

- 相关文档：
  - `docs/ARCHITECTURE.md`
  - `docs/product-specs/tau-port-overview.md`
  - `docs/QUALITY_SCORE.md`
  - `docs/REPO_COLLAB_GUIDE.md`
- 相关代码路径：
  - `src/Tau.Ai/`
  - `src/Tau.Agent/`
  - `src/Tau.CodingAgent/`
  - `src/Tau.Tui/`
  - `src/Tau.WebUi/`
  - `src/Tau.Mom/`
  - `src/Tau.Pods/`
  - `tests/`
- 已知约束：
  - 当前仓库已经有逐项目 build/test 的稳定验证链，但 `Tau.slnx` 仍会在这台机器上触发 solution metaproj / workload resolver 异常。
  - `Tau.CodingAgent` 独立 build / run 已恢复，但暂时通过显式 `Reference + HintPath` 指向 sibling 输出 DLL 来绕过 project reference 链问题。
  - `Tau.Tui` 已脱离空壳，但还只是最小交互层，不是完整 TUI。
  - `Tau.WebUi`、`Tau.Mom`、`Tau.Pods` 仍处于模板/占位状态。
  - 仓库 CI 已覆盖真实项目级 .NET 构建与测试，但 solution 级门禁还未恢复。

## 风险

- 风险：一开始就横向铺开所有应用面，导致每个模块都停在半成品。
  - 缓解方式：先锁定 CLI 这条第一用户路径，按依赖顺序推进 `Tui -> CodingAgent -> 测试/CI`。
- 风险：只按文件名机械对照上游，移植出大量表面兼容但没有真实验收路径的代码。
  - 缓解方式：每个里程碑都以“用户可走通的场景”和最小验证命令为验收标准。
- 风险：`Tau.Ai`/`Tau.Agent`/`Tau.CodingAgent` 边界在实现过程中继续漂移。
  - 缓解方式：每完成一阶段，同步更新计划和架构文档，保持目标边界可追踪。
- 风险：过早接入 Web/Slack/Pods 造成实现顺序反转。
  - 缓解方式：在 CLI P0 稳定前，这三个应用面只做计划，不进入实作。

## 里程碑

1. 基线收敛与第一用户路径锁定。
2. P0 CLI 路径实现：补齐最小 `Tau.Tui`，让 `Tau.CodingAgent` 脱离 demo 状态。
3. P0 验证与工程化：补 smoke 测试、`dotnet test`、CI、README/计划同步。
4. 二阶段扩展评估：基于 CLI P0 的稳定边界，再决定 `WebUi / Mom / Pods / 更多 Provider` 的进入顺序。

## 实施切片

### 切片 A：文档与基线收口

- 把模板态文档替换成 Tau 的真实阶段描述
- 明确第一条用户路径、非目标和实施顺序
- 让 `QUALITY_SCORE`、plan、README 三者一致

### 切片 B：`Tau.Tui` 最小可用层

- 建立终端抽象
- 支撑最小输入/输出循环
- 能承载 `Tau.CodingAgent` 的消息输出与用户输入

### 切片 C：`Tau.CodingAgent` 从 demo 到 P0

- 去掉对裸 `Console.ReadLine()` 的依赖
- 用 `Tau.Tui` 承载输入和输出
- 保留当前工具装配，但把运行阶段展示清楚

### 切片 D：验证与门禁

- 为 `Tau.Ai` / `Tau.Agent` / `Tau.CodingAgent` 补最小 smoke 测试
- 把 `dotnet build` / `dotnet test` 接入主 CI
- 保证仓库文档与实现状态一致

### 切片 E：二阶段规划入口

- 基于 CLI P0 的稳定边界，重新判断：
  - 是先扩 `Tau.Ai` 的 provider / auth
  - 还是先进入 `Tau.WebUi`
  - 还是先补 `Tau.Mom / Tau.Pods`

### 切片 F：`Tau.Ai` provider / auth / registry 收口

- 补齐 `ModelCatalog` 与仓库内置模型表
- 把 `ProviderAuthResolver`、`EnvironmentApiKeyResolver`、`OAuthCredentialStore` 接到统一 provider 认证入口
- 扩出第一轮 provider 面：
  - Azure OpenAI Responses
  - OpenAI Codex Responses
  - Mistral
  - Google Vertex
  - Google Gemini CLI / Antigravity
  - Amazon Bedrock
- 为上面这些能力补最小可执行测试，而不是停留在空壳测试项目

## 验证方式

- 命令：
  - `bash scripts/verify-dotnet.sh`
  - `bash scripts/verify-dotnet.sh --skip-restore`
  - `dotnet run --project src/Tau.CodingAgent/Tau.CodingAgent.csproj --no-build`
- 手工检查：
  - 本地可以启动 `Tau.CodingAgent` 并完成一轮真实输入、流式输出、工具调用显示。
  - 交互式输入不再依赖裸 `Console.ReadLine()`，而是经过 `Tau.Tui` 最小能力承载。
  - 计划、质量评分、架构文档与当前实现状态保持一致。
- 观测检查：
  - 先以控制台输出为主，必要时补最小日志。
  - 对关键路径（模型调用、工具执行、会话流转）保留可定位失败阶段的输出。

## 阶段退出标准

只有满足下面条件，才能认为这份计划的 P0 阶段基本完成，并进入下一阶段：

- `Tau.CodingAgent` 的第一条交互路径可重复验证
- `Tau.Tui` 已承载最小交互，不再依赖裸控制台输入
- 至少有一组 smoke 测试覆盖关键路径
- 主 CI 覆盖真实 .NET 构建与测试
- `WebUi / Mom / Pods` 的进入顺序已基于新基线重新评估

## 进度记录

- [x] 里程碑 1：完成 Tau 与上游 `pi-mono` 的结构盘点，并收敛出第一用户路径。
- [x] 切片 A：完成文档与基线收口，替换模板态项目文档。
- [x] 切片 B：实现 `Tau.Tui` 最小终端抽象与会话层。
- [x] 切片 C：将 `Tau.CodingAgent` 主循环抽为可测试宿主，并接入 `Tau.Tui`。
- [x] 切片 D：补齐最小 smoke 测试，并将 `dotnet build/test` 接入主 CI。
- [x] 切片 B.1：为 `Tau.Tui` 增加 transcript 与输入缓冲，形成最小消息历史基线。
- [x] 里程碑 2：实现 `Tau.Tui` 最小能力并接入 `Tau.CodingAgent`。
- [x] 里程碑 3：补齐测试、CI 和文档交付面。
- [x] 切片 F：完成 `Tau.Ai` 的第一轮 provider / auth / registry 收口，并补齐最小行为测试。
- [x] 切片 D.1：恢复 `Tau.CodingAgent.csproj` / `Tau.CodingAgent.Tests.csproj` 的独立 build，并把主 CI 改成显式项目级验证链。
- [ ] 里程碑 4：评估并排定后续 `WebUi / Mom / Pods / Provider` 扩展顺序。
- [ ] 里程碑 4.1：解决 `Tau.slnx` / metaproj / workload resolver 异常，并把当前 DLL `HintPath` workaround 收口。

## 决策记录

- 2026-04-23：决定先把 Tau 收口为“CLI-first”的 .NET 移植项目，而不是同时平推所有应用面。原因是当前唯一接近可用的路径是 `Tau.Ai + Tau.Agent + Tau.CodingAgent`，继续分散推进会放大半成品面积。
- 2026-04-23：决定把 `Tau.Tui` 作为下一阶段的第一实现切片。原因是它是 `Tau.CodingAgent` 从 demo 变成真实交互产品的直接阻塞点。
- 2026-04-23：决定在 CLI P0 稳定前，仅维护 `Tau.WebUi`、`Tau.Mom`、`Tau.Pods` 的规划和边界，不进入大规模移植。原因是这些模块都依赖更稳定的 Agent/TUI/配置基础。
- 2026-04-23：决定先把模板态文档整体收口，再推进代码实现。原因是当前仓库已经从模板进入真实项目，如果文档继续停留在模板语气，会导致计划、优先级和质量判断持续漂移。
- 2026-04-23：决定把 `Tau.CodingAgent` 主循环抽成 `CodingAgentHost + ICodingAgentRunner`。原因是当前阶段最重要的是建立宿主边界和可测试性，而不是继续把交互、配置和 runtime 编排揉在 `Program.cs`。
- 2026-04-23：决定在 CI 与本地验证中按测试项目逐个执行 `dotnet test --no-build --no-restore`。原因是当前 `Tau.slnx` 的方案级 `VSTest` 入口不稳定，且沙箱环境下 restore 会被网络限制干扰。
- 2026-04-23：决定先在 `Tau.Tui` 内引入最小 transcript 与 `InputBuffer`，而不是直接做复杂组件树。原因是当前最缺的是“消息历史语义”和“输入提交边界”，先把这两层固化，后面再长出真正的消息区和编辑器会更稳。
- 2026-04-23：决定在 CLI P0 之后，优先先补 `Tau.Ai` 的 provider / auth / model registry，而不是直接进 `WebUi / Mom / Pods`。原因是用户已明确把 provider 覆盖、model registry、OAuth 和 env auth 设为当前最重要缺口，而且这部分会直接决定后续所有应用面的配置与调用边界。
- 2026-04-23：决定先把 `Tau.Ai` 的 OAuth/login 流程收口为“registry + auth.json + refresh 骨架 + model mutation”，而不是一口气完整移植所有 browser/device-code 登录流程。原因是当前更需要先把 provider 发现、凭证解析和调用入口统一起来，再逐个把 provider-specific login flow 补完整。

- 2026-04-23：决定把主 CI 与本地主验证入口统一为 `scripts/verify-dotnet.sh`。原因是 solution 级 `Tau.slnx` 构建在当前机器上仍不可信，而显式项目顺序更能反映当前工程真实依赖和门禁。
- 2026-04-23：决定暂时接受 `Tau.CodingAgent` / `Tau.CodingAgent.Tests` 使用 `HintPath` 指向 sibling 输出 DLL 的 workaround，以换回独立 build / run / test 的可重复性；后续再单独收口回正常 project reference。
