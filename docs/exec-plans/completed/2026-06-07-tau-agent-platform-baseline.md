# Tau Agent Platform Baseline

## 目标

先把 Tau 建成可复用、可嵌入、可发布的 .NET Agent 应用底座，而不是继续把 TUI/UI polish 或完整 pi-mono 产品体验 parity 作为当前主线。

最终状态是：外部 .NET 项目可以用清晰的 public API 创建 Agent、注册工具、配置 provider/model/auth、订阅 streaming events、保存/恢复 session、接入 runtime log，并通过 Console / ASP.NET Core / Worker 示例快速搭出 Agent 应用。

## 范围

- 包含：
  - `Tau.Agent` 应用开发入口和 public contract。
  - `Tau.Ai` provider/model/auth/config/redaction 在 Agent 应用中的消费方式。
  - Tool registration、schema validation、execution result、error/cancellation 和 trace contract。
  - 可复用 session/state/log/audit 边界，必要时从 `Tau.CodingAgent` 封装或下沉能力。
  - runtime observability：correlation id、session id、message id、provider run、tool execution、usage/cost、redaction。
  - Console Agent example 和 ASP.NET Core 或 Worker Agent example。
  - NuGet/library boundary、release/package 策略和本地验证链。
- 不包含：
  - TUI focus stack、theme rendering、terminal image、settings submenu、TreeSelector exact parity。
  - WebUi artifact/sandbox runtime、Mom Slack/Docker real smoke、Pods SSH/HF/GPU/vLLM real smoke，除非它们是验证平台底座必要示例。
  - 放弃 100% pi-mono parity。旧 parity matrix 保留为后续审计路线，只是不再是当前优先执行线。

## 背景

- 相关文档：
  - `GOAL.md`
  - `docs/REPO_COLLAB_GUIDE.md`
  - `docs/ARCHITECTURE.md`
  - `docs/QUALITY_SCORE.md`
  - `next.md`
  - `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
  - `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
- 相关代码路径：
  - `src/Tau.Ai/**`
  - `src/Tau.Agent/**`
  - `src/Tau.CodingAgent/**`
  - `src/Tau.WebUi/**`
  - `src/Tau.Mom/**`
  - `tests/Tau.Ai.Tests/**`
  - `tests/Tau.Agent.Tests/**`
  - `tests/Tau.CodingAgent.Tests/**`
  - `tests/Tau.WebUi.Tests/**`
- 已知约束：
  - 当前工作树已有 `/settings select` UI parity WIP，已验证但未提交。Agent platform 工作不要继续扩大该 UI 切片，也不要把它混入 SDK/API 提交边界。
  - Windows / PowerShell 是当前本地权威验证链。
  - fake provider 可以用于平台合同测试，但真实 provider/OAuth/e2e 缺口必须继续标注，不得伪装成完成。
  - 涉及 secret/auth/runtime log 的改动必须默认脱敏，不记录真实密钥。

## 当前实现基线

截至本计划接管时，Tau 不是从零开始搭 Agent 内核：

- `Tau.Agent` 已有 `AgentOptions` / `Agent` facade、`AgentRuntime` 双层循环、`RunStream(...)`、`AgentEvent`、`IAgentTool`、tool lifecycle events、usage/stop reason、cancellation 和 `ITauLogSink` 接线。
- `Tau.Ai` 已有 `ProviderRegistry`、model/auth/config、`Tau.Ai.Providers.Faux`、`ToolArgumentValidator`、`TauRuntimeLogContext`、`JsonlTauLogSink` 和默认 JSONL string-value redaction。
- `Tau.CodingAgent` 已有大量产品层 session/settings/auth/model/log/export 能力，但这些能力带有 CLI/TUI 产品语义，不能直接作为平台 SDK public contract 暴露。
- 现有 public API sample 已证明 facade/runtime/proxy/event 能编译运行，但对普通应用开发者仍偏底层：需要手动构造 registry/model/tool class，缺少应用级 builder、delegate tool、run result、session adapter、example smoke 和 package 消费说明。

因此本计划的工程策略是：**复用现有 Agent/Ai 内核，增加薄平台层和示例验证，不重写 runtime，不继续 UI parity。**

## 风险

- 风险：直接复用 `Tau.CodingAgent` 内部类型会把 CLI 产品实现泄漏成平台 API。
  - 缓解方式：先定义平台边界，只暴露应用开发者需要的最小合同；内部实现可复用，但 public API 不绑定 CLI UI 结构。
- 风险：继续沿旧 parity plan 自动推进，会把时间投入 UI polish。
  - 缓解方式：`GOAL.md`、`next.md` 和旧 parity plan 顶部都明确当前优先线切换。
- 风险：底座 API 过早抽象成大框架。
  - 缓解方式：以两个可运行示例驱动 API，先覆盖创建 Agent、tool call、session/log、streaming，再考虑扩展。
- 风险：fake-only validation 被误读为可生产。
  - 缓解方式：fake provider 只证明平台合同；真实 provider/OAuth/e2e 继续留在后续 external validation。

## 里程碑

1. 目标切换与计划接管。
2. 平台 API 与边界审视。
3. Agent SDK / host baseline。
4. 应用模板 / 示例。
5. 平台可靠性、安全边界与交付策略。

## 当前 checkpoint

- Phase 0 已完成：`GOAL.md`、本 completed plan、`next.md`、`docs/QUALITY_SCORE.md` 和 history 已把当前主线切到 Agent Platform Baseline，并在验收后归档。
- WP1-WP4 已有首版实现：`src/Tau.Agent/Platform/**` 提供薄平台 API；`tests/Tau.Agent.Tests/AgentPlatformTests.cs` 和 public API compile sample 固定 fake provider、delegate tool、session/log/cancellation 合同；`examples/Tau.Agent.ConsoleExample` 与 `examples/Tau.Agent.HttpExample` 提供两个最小应用示例；`scripts/verify-agent-platform-examples.ps1` 已能 build/smoke 两个示例。
- 当前不是“实现是否可行”的阶段；Phase 4/5 本地验收已经收口：全仓验证、provider run + tool execution runtime log、package/release 边界、provider/auth 缺口说明、安全/质量/架构/history 同步和 completion audit 均已有当前证据。
- 第一版 API 只包裹现有 `Agent` / `AgentRuntime` / `ProviderRegistry` / `IAgentTool` / `TauRuntimeLogContext`，没有重写 runtime，也没有把 `Tau.CodingAgent` command router、TUI selector/session 或 TreeSelector UI 类型暴露为 SDK public surface。
- 当前验收已通过 `dotnet test tests\Tau.Agent.Tests\Tau.Agent.Tests.csproj --filter "AgentPublicApiCompileSampleTests|AgentPlatform" --no-restore --verbosity minimal` 5/5、`powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-agent-platform-examples.ps1 -SkipRestore`、`dotnet test tests\Tau.Agent.Tests\Tau.Agent.Tests.csproj --no-restore --verbosity minimal` 119/119、`dotnet test tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --no-restore --verbosity minimal` 280/280、`powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` 和 `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore -RunSmoke`。

## Platform surface checklist

第一版 public surface 只冻结应用开发者必须依赖的最小合同。状态含义：`existing` 表示已有底层能力但可能缺少平台包装；`new` 表示本计划需要新增；`defer` 表示明确不进 Agent platform baseline。

| Surface | 当前事实 | 本计划动作 | 验收证据 |
| --- | --- | --- | --- |
| Agent 创建与配置 | `AgentOptions` / `Agent` 已有，现已补 `AgentApplication` / `AgentApplicationBuilder` | 首版完成；后续只补命名/诊断 polish | public API compile sample + targeted tests |
| Provider/model | `ProviderRegistry`、`ModelCatalog`、Faux provider 已有，builder 支持显式 registry/model | fake provider 合同完成；真实 provider/OAuth e2e 继续标注为缺口 | fake provider test；真实 provider e2e 标注为缺口 |
| System prompt/messages | `AgentOptions.SystemPrompt`、`Messages` 已有，builder 支持 system prompt、initial/restored messages、普通 prompt run | 首版完成 | compile sample |
| Tool registration | `IAgentTool` 和 schema validation 已有，已补 `DelegateAgentTool` / delegate overload | 首版完成；不创建独立 tool runtime | tool call test |
| Tool error/cancel | runtime 已有 cancellation/error-as-result 基线，platform 取消失败不保存 session 且回滚内存消息 | 首版完成；全仓验证继续覆盖底层 tool error 分类 | targeted tests + AgentRuntime tests |
| Streaming events | `Agent.Subscribe` / `AgentRuntime.RunStream` 已有，platform `AgentRunResult.Events` 收集可审计 events | 首版完成；后续可加更细 adapter | run result/event tests |
| Usage/stop reason | `AssistantMessage.Usage` / `StopReason` 已有，`AgentRunResult` 直接暴露 usage/stop reason/error/cancel 状态 | 首版完成 | run result tests |
| Session/state | CodingAgent 有 JSONL tree；platform 已补 UI-free snapshot/store | 首版完成；不做 branch tree UI | save/restore tests |
| Observability | `ITauLogSink`、`TauRuntimeLogContext`、provider run trace 和 tool execution trace 已有，platform 自动补 correlation/session/message id | 首版完成 | log assertion |
| Redaction | JSONL sink 默认 string-value redaction 已有，provider run 和 tool trace 只写 provider/model/usage/cost/bytes/failureKind 等摘要 | 首版完成；非标准 secret pattern 仍是长期安全 backlog | log assertion + redaction docs |
| Console example | `examples/Tau.Agent.ConsoleExample` 已存在 | 首版完成 | smoke command |
| ASP.NET Core / Worker example | `examples/Tau.Agent.HttpExample` 已存在 | 首版完成 | smoke command |
| Package boundary | release scripts 已有库包 publish baseline；examples 已进 solution；`README.md`、`next.md` 和质量文档明确 `Tau.Ai` / `Tau.Agent` 默认 library boundary、examples 源码模板边界和 CI/smoke 入口 | baseline 完成；真实 registry 发布演练继续留作后续外部验证 | docs + full gate + RunSmoke |
| TUI/UI parity | 已有独立 WIP | baseline 阶段 defer，不纳入 SDK/API 提交 | plan/next 保持后置 |

## 第一版 API 骨架候选

这不是最终命名承诺；实现前仍要读当前源码/tests。但后续 Agent 应该优先沿这个最小骨架推进，避免重新发散成大框架。

| 类型/入口 | 建议位置 | 目的 | 不做什么 |
| --- | --- | --- | --- |
| `AgentApplication` | `src/Tau.Agent/Platform/` | 应用侧可直接调用的运行入口，包裹现有 `Agent` facade | 不复制 `AgentRuntime` loop |
| `AgentApplicationBuilder` | `src/Tau.Agent/Platform/` | 统一配置 provider registry、model、system prompt、tools、session id、log sink、初始 messages | 不隐藏所有底层能力，不引入 DI 容器依赖 |
| `AgentRunResult` | `src/Tau.Agent/Platform/` | 直接暴露 final messages、assistant text、usage、stop reason、error/cancel 状态和可审计 events | 不替代底层 `AgentEvent` |
| `DelegateAgentTool` | `src/Tau.Agent/Platform/` | 让应用用 delegate/function 注册工具，避免每个工具都手写 class | 不创建独立 tool runtime |
| `IAgentSessionStore` | `src/Tau.Agent/Platform/` | UI-free conversation 保存/恢复合同，覆盖 messages、metadata、session id、updated time | 不移植 CodingAgent JSONL branch tree UI |
| `AgentSessionSnapshot` | `src/Tau.Agent/Platform/` | session store 的普通数据快照 | 不承诺和 CodingAgent JSONL schema 兼容 |
| `InMemoryAgentSessionStore` | `src/Tau.Agent/Platform/` | 测试和示例可用的最小 session store | 不作为生产持久化唯一方案 |
| test sink/helper | `tests/Tau.Agent.Tests/` | 断言 `ITauLogSink` runtime events 和 correlation/session/message 字段 | 不进入 production API，除非实现中发现有通用价值 |

第一版 public sample 应能表达如下应用侧结构：

```csharp
var app = AgentApplication.CreateBuilder()
    .UseProviderRegistry(registry)
    .UseModel(model)
    .UseSystemPrompt("You are a focused agent.")
    .UseSessionId("session-1")
    .UseLogSink(logSink)
    .AddTool("echo", "Echo", "Echoes text", schema, (ctx, ct) => ...)
    .Build();

var result = await app.PromptAsync("hello", cancellationToken);
```

如果源码审视发现已有等价命名更合适，可以改名；但验收语义不变：外部应用不应再手动拼 provider registry、model、tool class、event collector、session id 和 log context。

## Work packages

这些包是后续实现的默认顺序；除非实际代码审视发现依赖倒置，否则不先做 UI/TUI 工作。

### WP1：Public API boundary review

- 读取 `src/Tau.Agent/**`、`tests/Tau.Agent.Tests/**`、`src/Tau.Ai/**`、`tests/Tau.Ai.Tests/**`。
- 输出第一版 API 命名和文件归属，优先放在 `src/Tau.Agent/Platform/**` 或同等小范围目录。
- 明确哪些 CodingAgent 能力只复用实现思路，不下沉 public type：TreeSelector、interactive settings、CLI command router、TUI selector/session。
- 更新本计划进度；docs-only 边界审视至少跑 `git diff --check`。
- 如果 WP1 后立刻进入 WP2，同一轮应把实际实现、tests、plan/next/history 一起收口。

### WP2：Agent platform API baseline

- 新增应用级 builder/host：创建 Agent、配置 provider/model/system prompt、注册 tools、运行 prompt。
- 新增 delegate/function tool helper：schema、description、argument preparation、cancellation、tool update/result。
- 新增 run result/event adapter：final messages、assistant text、usage、stop reason、error、tool results。
- 新增 targeted tests：
  - fake provider 第一次返回 tool call，delegate tool 执行，第二次返回 final assistant text。
  - `AgentRunResult` 暴露 final assistant text、messages、usage、stop reason 和 tool event summary。
  - cancellation 得到明确 aborted/cancelled 结果，且 session 污染边界按实现合同固定。

### WP3：Session/log baseline

- 新增 UI-free session store 或 adapter：保存/恢复 conversation messages、metadata、session id、updated time。
- 接入 `TauRuntimeLogContext`：correlation id、session id、message id 贯穿 provider run 和 tool execution。
- 固定 session/log redaction 边界：不把 secret、完整 tool arguments/result 写入 runtime log。
- targeted tests 覆盖 save/restore、cancel/error 不污染 session、log sink event 字段。
- runtime log assertions 至少覆盖 `provider/run.start`、`provider/run.end`、`tool/execution.start` 与 `tool/execution.end`，并断言字段只有 provider/model/message/tool count、usage/cost、长度、failure kind、correlation/session/message id 等摘要，不包含 prompt、完整 `arguments` 或 `result`。

### WP4：Examples and smoke

- 新增 Console example：fake provider、delegate tool、stream/run result、session/log 输出。
- 新增 ASP.NET Core 或 Worker example：HTTP/background 入口、tool call、structured response 或 streaming。
- 为两个 example 写 README 或 docs 入口，并新增 PowerShell smoke 命令。

### WP5：Package/release integration

- 明确 library NuGet boundary：默认 `Tau.Ai`、`Tau.Agent`，必要时包含新增 platform/shared library；应用项目仍走 release artifacts。
- 把 example smoke 接到 dedicated verification 或 release contract smoke 中，避免平台示例只停留在源码。
- 更新 `docs/QUALITY_SCORE.md`、`next.md` 和 history；完成后再决定是否恢复 100% pi-mono parity 主线。

## 验证方式

- 命令：
  - `git diff --check`
  - `dotnet test tests\Tau.Agent.Tests\Tau.Agent.Tests.csproj --filter "AgentPublicApiCompileSampleTests|AgentPlatform" --no-restore --verbosity minimal`
  - `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-agent-platform-examples.ps1 -SkipRestore`
  - `dotnet test tests\Tau.Agent.Tests\Tau.Agent.Tests.csproj --no-restore --verbosity minimal`
  - `dotnet test tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --no-restore --verbosity minimal`
  - `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore`
  - `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore -RunSmoke`
- 手工检查：
  - Public API 示例不依赖 `Tau.CodingAgent` UI-only 类型。
  - 新示例能说明最小 Agent 应用结构，而不是产品 UI demo。
  - docs/history/quality/next 与当前实现一致。
- 观测检查：
  - fake provider + tool call 能产出可审计 provider run 和 tool execution runtime log。
  - tool failure/cancellation 不泄漏 secret，不污染 session。
  - correlation/session/message id 在测试 sink 或 JSONL sink 中可追踪。

## 进度记录

- [x] 里程碑 1：目标切换与计划接管。
  - `GOAL.md` 已切换为 Tau Agent Platform /goal。
  - 本 plan 已创建并在验收后归档到 `docs/exec-plans/completed/`。
  - 旧 100% pi-mono parity plan 保留为长期审计路线。
- [x] 里程碑 2：平台 API 与边界审视。
  - 建立 platform surface checklist。
  - 第一版 API 文件归属已冻结到 `src/Tau.Agent/Platform/**`。
  - 已确认现有 `Agent` / `AgentRuntime` / `IAgentTool` / `Faux` provider 是平台底座可复用基线，并由 platform API 包装。
  - 不应暴露为 public SDK 的产品内部类型：CodingAgent command router、TUI selector/session、TreeSelector/metadata UI、interactive settings UI。
- [x] 里程碑 3：Agent SDK / host baseline。
  - 已提供 `AgentApplication` / `AgentApplicationBuilder` / `AgentRunResult` / `DelegateAgentTool` / UI-free session store。
  - 已覆盖 fake provider + tool call + session/log/cancellation 的 public API compile sample 和 platform targeted tests。
- [x] 里程碑 4：应用模板 / 示例。
  - Console Agent example 已落地。
  - ASP.NET Core HTTP Agent example 已落地。
  - `verify-agent-platform-examples.ps1` 已落地并接入 `verify-dotnet.ps1 -RunSmoke`。
- [x] 里程碑 5：平台可靠性、安全边界与交付策略。
  - redaction、cancellation、provider run + tool execution runtime log 和基础 package/example smoke 边界已有代码/测试证据。
  - provider/auth 缺口已明确标注为真实 provider/OAuth e2e 后续边界；release/package 文档收口、全仓 gate、`-RunSmoke` 和 completion audit 已完成。

## 决策记录

- 2026-06-07：用户明确提出先不管 UI，先完整搭建系统，方便用此 Agent 底座搭建 Agent 应用。当前主线从 100% pi-mono product/UI parity 切换为 Agent Platform Baseline。影响是：TUI/UI polish 和完整产品 parity 后置，优先建设 `Tau.Agent` / `Tau.Ai` / tool/session/observability / examples / package 边界。
- 2026-06-07：保留 `2026-05-28-tau-100-percent-pi-mono-parity.md` 和 parity matrix，不归档、不删除。原因是它们仍是长期审计和后续产品 parity 的事实来源；只是当前执行优先级降低。
- 2026-06-07：进一步把计划从方向声明收紧为 WP1/WP2 可执行入口：默认优先在 `Tau.Agent` 增加薄平台层，候选类型为 `AgentApplication`、`AgentApplicationBuilder`、`AgentRunResult`、`DelegateAgentTool`、`IAgentSessionStore`、`AgentSessionSnapshot` 和 `InMemoryAgentSessionStore`。这些类型只作为应用底座包装现有 Agent/Ai 内核，不引入 CLI/TUI product public dependency。
- 2026-06-07：首版 Agent platform API 已落地到 `src/Tau.Agent/Platform/**`，并新增 Console / HTTP examples 与 dedicated smoke。随后把 `verify-agent-platform-examples.ps1` 接入 `verify-dotnet.ps1 -RunSmoke`，让 Agent 底座示例进入仓库级运行态验证链。
- 2026-06-07：Agent platform baseline 完成本地验收并进入 completed plan 状态。验收证据包括 targeted platform tests 5/5、provider run + tool execution runtime log assertions、Console/HTTP example smoke、`Tau.Agent.Tests` 119/119、`Tau.Ai.Tests` 280/280、仓库级 `verify-dotnet.ps1 -SkipRestore` 和 `verify-dotnet.ps1 -SkipRestore -RunSmoke`；真实 provider/OAuth e2e、真实 package registry 发布演练、真实 signing/provenance 演练和后续 product parity 接回仍保留为后续计划，不被 fake provider smoke 覆盖。
