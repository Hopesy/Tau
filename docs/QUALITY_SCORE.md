# 质量评分

用这份文档按产品区域和架构层次记录当前质量水位，方便持续知道最薄弱的地方在哪。

## 评分标准

- `A`：覆盖完整、行为稳定、文档清楚、运行风险低。
- `B`：整体可接受，但还有明确短板。
- `C`：能用，但需要针对性补强。
- `D`：脆弱、缺少规范，或很多行为尚未定义。

## 当前基线（2026-04-24）

| 区域 | 评分 | 原因 | 下一步 |
| --- | --- | --- | --- |
| 产品定义 | C | 已有 `docs/ARCHITECTURE.md` 和 `docs/product-specs/tau-port-overview.md`，并且不再只围绕 CLI 说话，`WebUi / Mom / Pods` 也已经进入真实产品切片。当前问题不再是“有没有模块”，而是每个模块的第二层能力还不完整。 | 继续把每个应用面的第二层能力写实：WebUi 流式、Mom Slack/workspace、Pods lifecycle。 |
| Tau.Ai | C | 已补上 `ModelCatalog`、内建模型表、provider auth resolver、auth.json 读取、OAuth provider registry，以及 Azure OpenAI Responses / OpenAI Codex / Mistral / Vertex / Gemini CLI / Bedrock 的第一轮接线；这轮又收口了 OpenAI / request-body source-gen serializer 缺口，避免 runtime 因 `List<object>`/primitive metadata 直接崩。 | 继续把 provider 骨架从“可发现/可配置/可序列化”推进到“可完整调用”，优先补 Bedrock、Responses/Codex fidelity 和 OAuth login/refresh 真正移植。 |
| Tau.Agent | C | 双层循环、工具执行器、状态与事件骨架已具备，继续稳定承接各应用面；本轮还承担了 Mom 结构化委派测试的宿主复用角色。但高层 Agent API、行为回归和对外 façade 仍不完整。 | 在支撑 WebUi / Mom 的过程中继续补 façade 与行为测试。 |
| Tau.CodingAgent | C | 已具备最小 CLI 宿主、可测试主循环和基础工具链，不再是裸 `Console.ReadLine()` demo；运行时默认 model/provider 也已接到 `ModelCatalog`，独立 `csproj` 的 build / run 已恢复。当前又新增了显式 `Create(provider, model, history)` 宿主工厂，为 WebUi/Mom 共享 runtime 打开了正确边界。 | 一边继续补会话/配置层，一边把 `Tau.slnx` / metaproj 异常和当前 `HintPath` workaround 收口回更正常的项目引用结构。 |
| Tau.Tui | C | 已经有最小终端抽象、transcript、输入缓冲和会话层，并承载 `Tau.CodingAgent` 主路径；但还没有真正的组件系统、编辑器、渲染层和键盘体系。 | 从最小输入编辑与消息渲染开始继续向真实 TUI 推进。 |
| Tau.WebUi | C | 已经从 ASP.NET Core `Hello World` 提升为第二层 Web 宿主：首页聊天页、status/catalog/session/messages API、provider/model 选择和 `output/webui-sessions.json` 持久化都已可跑；同时新增了最小 runner/store 测试。仍然缺流式前端、附件、auth/settings UX 和 richer rendering。 | 继续补流式消息绑定、会话恢复体验、附件和更完整的 auth/settings 入口。 |
| Tau.Mom | C | 已经从 worker 模板提升到结构化本地委派宿主：`--once`、inbox/outbox/archive、`.txt/.md/.json` 输入、`provider/model/workingDirectory/metadata` 字段和结果落盘都已现场跑通；同时新增了 2 个最小测试。仍未接 Slack/workspace/sandbox。 | 继续补 Slack 接线、workspace/sandbox/tool delegation 语义，以及更高层 delegation flow。 |
| Tau.Pods | C | 已从控制台占位提升为具备主动运维入口的 CLI：config init/list/validate/status、`probe`、`exec`、sample config、probe/exec service 和 source-gen JSON 已具备，并有 7 个测试通过。仍缺 deploy、restart、lifecycle 和更高层 pod orchestration。 | 继续进入 deploy / stop / restart / health 与更高层 lifecycle。 |
| 测试 | C | `Tau.Ai.Tests`、`Tau.CodingAgent.Tests`、`Tau.Agent.Tests`、`Tau.Pods.Tests` 当前都能真实通过；这轮除了 `Tau.Ai` 的 provider serialization 回归测试和 `WebUi` 的 runner/store 测试，又补上了 `Mom` 的结构化委派测试以及 `Pods` 的 probe/exec 测试。 | 继续把高价值 runtime smoke 自动化，尤其是 WebUi、Mom、Pods 的端到端行为。 |
| CI/CD | C | 主 CI 已改为 `scripts/verify-dotnet.sh`，按显式项目顺序 restore / build / test，形成了真实可重复的项目级门禁；现在也已经覆盖到 `WebUi / Mom / Pods` 三个应用面。release 产物仍不是 Tau 的真实可执行交付件。 | 让 release 链开始对应真实 Tau 构建产物，并补一层对 `Tau.slnx` / workload resolver 异常与项目引用 workaround 的环境诊断。 |
| 可观测性 | D | 当前主要依赖控制台输出、Web API 返回、Mom outbox JSON 和 Pods probe/exec 输出，没有统一日志/trace 约定。虽然已经能看见 session store、delegation output、probe result、exec summary，但还没有系统化可观测协议。 | 先为 provider 调用、tool execution、session / delegation / pod operation 阶段补最小日志。 |
| 安全与配置 | C | 当前已经补了环境变量矩阵、auth.json 和 OAuth 凭证读取骨架，配置边界比之前清楚；WebUi 已具备会话级 provider/model 配置，Mom 已具备结构化 provider/model/workdir 请求，Pods 已具备 probe/exec 配置。但 secrets 存储策略、OAuth login UX、token migration 和多运行态配置仍未完整定义。 | 在继续推进 `Tau.CodingAgent` / `WebUi` / `Mom` / `Pods` 的设置层时，把 auth/config 的持久化与迁移规则一起落仓库。 |
