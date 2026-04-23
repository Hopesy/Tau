# 质量评分

用这份文档按产品区域和架构层次记录当前质量水位，方便持续知道最薄弱的地方在哪。

## 评分标准

- `A`：覆盖完整、行为稳定、文档清楚、运行风险低。
- `B`：整体可接受，但还有明确短板。
- `C`：能用，但需要针对性补强。
- `D`：脆弱、缺少规范，或很多行为尚未定义。

## 当前基线（2026-04-23）

| 区域 | 评分 | 原因 | 下一步 |
| --- | --- | --- | --- |
| 产品定义 | C | 已有 `docs/ARCHITECTURE.md` 和 `docs/product-specs/tau-port-overview.md`，但第一条真实用户路径还没有完全收口成产品级体验。 | 继续围绕 `Tau.CodingAgent` 的第一条用户路径推进，并把 session / settings / auth / richer rendering 补齐。 |
| Tau.Ai | C | 已补上 `ModelCatalog`、内建模型表、provider auth resolver、auth.json 读取、OAuth provider registry，以及 Azure OpenAI Responses / OpenAI Codex / Mistral / Vertex / Gemini CLI / Bedrock 的第一轮接线；但 Bedrock SigV4、完整 OAuth login flow、generated model pipeline 和更高保真 provider 行为还没完成。 | 继续把 provider 骨架从“可发现/可配置”推进到“可完整调用”，优先补 Bedrock、Responses/Codex fidelity 和 OAuth login/refresh 真正移植。 |
| Tau.Agent | C | 双层循环、工具执行器、状态与事件骨架已具备；但高层 Agent API、行为回归和对外接入方式仍不完整。 | 在支撑 `Tau.CodingAgent` 的过程中补齐最小 façade 和测试。 |
| Tau.CodingAgent | C | 已经具备最小 CLI 宿主、可测试主循环和基础工具链，不再是裸 `Console.ReadLine()` demo；运行时默认 model/provider 也已接到 `ModelCatalog`，独立 `csproj` 的 build / run 已恢复。短板主要变成 session、settings、auth UI、多模式与 richer rendering 仍未实现，以及当前 solution build 仍依赖工程化 workaround。 | 一边继续补会话/配置层，一边把 `Tau.slnx` / metaproj 异常和当前 `HintPath` workaround 收口回更正常的项目引用结构。 |
| Tau.Tui | C | 已经有最小终端抽象、transcript、输入缓冲和会话层，并承载 `Tau.CodingAgent` 主路径；但还没有真正的组件系统、编辑器、渲染层和键盘体系。 | 从最小输入编辑与消息渲染开始继续向真实 TUI 推进。 |
| Tau.WebUi | D | 目前只是 ASP.NET Core `Hello World` 占位，还没有聊天 UI、流式绑定、会话和附件体系。 | 在 CLI P0 稳定前保持规划态，不提前展开。 |
| Tau.Mom | D | 目前还是 worker 模板，只能周期打日志，没有 Slack、sandbox、workspace、tool/message 流程。 | 在 Agent/Session 边界稳定后再进入实现。 |
| Tau.Pods | D | 目前只有控制台占位，还没有 pod 配置、SSH、模型生命周期和 CLI 命令体系。 | 延后到核心 Agent 体验稳定后再移植。 |
| 测试 | C | `Tau.Tui`、`Tau.CodingAgent`、`Tau.Agent` 的最小 smoke 测试已在跑，这一轮又把 `Tau.Ai` 补到了 16 个真实行为测试；但 provider 端到端、Agent 行为回归和更高层 CLI 交互验证仍然偏薄。 | 继续补 provider payload / auth / stream 级测试，并增加能覆盖 CLI 默认接线的验证。 |
| CI/CD | C | 主 CI 已改为 `scripts/verify-dotnet.sh`，按显式项目顺序 restore / build / test，避开了当前 `Tau.slnx` 的 metaproj 异常，形成了真实可重复的项目级门禁；但 release 产物仍不是 Tau 的真实可执行交付件，solution build debt 也还没消除。 | 让 release 链开始对应真实 Tau 构建产物，并补一层对 `Tau.slnx` / workload resolver 异常与项目引用 workaround 的环境诊断。 |
| 可观测性 | D | 当前主要依赖控制台输出，没有约定关键路径日志或定位失败阶段的统一方式。 | 先为模型调用、工具执行和交互状态补最小可定位输出。 |
| 安全与配置 | C | 当前已经补了环境变量矩阵、auth.json 和 OAuth 凭证读取骨架，配置边界比之前清楚；但 secrets 存储策略、OAuth login UX、token migration 和多运行态配置仍未完整定义。 | 在继续推进 `Tau.CodingAgent` 会话/设置页时，把 auth/config 的持久化与迁移规则一起落仓库。 |
