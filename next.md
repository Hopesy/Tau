# Tau next

这份文件只记录当前还没做完、后续需要继续推进的缺口，方便 `/goal` 续跑时快速选择下一刀。详细 inventory 以 active parity matrix 为准：

- `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
- `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
- `GOAL.md`

## 当前直接执行入口

1. Foundation-first gate 已在本地完成验证：`scripts/verify-agent-package-consumer.ps1` 74 assertions、`scripts/verify-release-contracts.ps1 -Json`、`scripts/verify-dotnet.ps1 -SkipRestore -RunSmoke` 均已通过；`Tau.Ai` consumer 还证明了显式 `ModelConfigurationStore` / `ProviderAuthResolver` 注入、`models.json` 动态 provider、model-level/provider-level auth status、header 覆盖链路、上游 `google-generative-ai` API 名可调度到 Tau canonical `google-generative-language` provider，provider/model/default `models.json options` 可注入 `temperature`、`maxTokens`、`topP`、`transport`、`cacheRetention`、`sessionId`、`maxRetryDelayMs`、`metadata`、`reasoning` 和 `thinkingBudgets`，OpenAI Responses provider-specific options 可投影到 typed `OpenAiResponsesOptions`，Mistral provider-specific options 可投影到 typed `MistralOptions`，Anthropic provider-specific options 可投影到 typed `AnthropicOptions`，Google provider-specific options 可投影到 typed `GoogleOptions`。2026-06-18 又补了 Windows Vertex ADC path、Copilot token precedence、Bedrock ambient marker、扩展 provider env aliases、expired OAuth / login-available status 语义、`OAuthProviderRegistry` built-in restore/custom register/reset/helper contract、provider-only `models.json` credential status、`headers/hash/overflow/typebox` helper rows verified、public Faux provider `Signal` / `OnResponse` 本地合同 verified、shared stream options `onPayload` / `onResponse` / `thinkingBudgets` 本地合同 verified，provider-wide `StreamOptions.Signal` cancellation local contract verified，`openai-completions` / `openai-compatible` / `google-generative-ai` API alias adapter，Responses 家族 `serviceTier` / `reasoningEffort` / `reasoningSummary` / Codex `textVerbosity` / Azure `azure*` options 本地合同，Mistral 字符串型 `toolChoice`、对象型 function `toolChoice`、`promptMode`、`reasoningEffort` 本地合同，Anthropic `thinkingEnabled` / `thinkingBudgetTokens` / adaptive `effort` / `thinkingDisplay` / `interleavedThinking` / tool `toolChoice` 本地合同，以及 Google `toolChoice` / `thinkingEnabled` / `thinkingBudgetTokens` / `thinkingLevel` / Vertex `project/location` / Gemini CLI `projectId` 本地合同；当前完整 `Tau.Ai.Tests` 为 444/444。
2. Agent stream proxy local server path 已补验证：`scripts/verify-agent-proxy-server-e2e.ps1` 覆盖真实 loopback HTTP/SSE `/api/stream` server path、缺 terminal event 和 malformed SSE JSON，并已接入 `verify-dotnet.ps1 -RunSmoke` / release contract。
3. `Tau.Ai.Cli` 本地 dotnet tool install alias 已补验证：`scripts/verify-ai-cli-tool-install.ps1` 会 pack 临时 `pi-ai` / `tau-ai` tool 包、从临时 package source 安装到 tool-path，并验证 `--help` / `list` 命令名和 provider 输出。
4. `Tau.Agent.Platform.AgentApplicationBuilder.AddTool(..., prepareArguments:)` 已补透传到 `DelegateAgentTool`，平台层可在 tool schema validation 前改写 raw args；对应测试和 public API compile sample 也已补。
5. `publish-release-packages.ps1` 的默认本地发布回放已覆盖 `Tau.Ai` / `Tau.Agent` / `Tau.Tui` 三个库包和 `Tau.Ai.Cli.PiAiTool` / `Tau.Ai.Cli.TauAiTool` 两个 dotnet tool 包；`verify-release-package-publish.ps1` 固定 3+2 包边界、`PackAsTool=true`、`ToolCommandName=pi-ai|tau-ai`、API key redaction 和 dirty apply 阻断。
6. `Tau.Ai` / `Tau.Agent` public export shape decision 已收口为 .NET-native assembly/package surface：`docs/AI_AGENT_EXPORT_SHAPE.md` 固定上游 AI 12 个 package exports、13 个 AI index export groups 和 4 个 Agent index exports 的 Tau 映射；`scripts/verify-ai-agent-export-shape.ps1` 固定 75 assertions，并已接入 `plan-release.ps1` / `verify-release-contracts.ps1`。
7. `scripts/verify-ai-provider-e2e-matrix.ps1` 已提供 AI provider/OAuth 可审计矩阵：默认 inspect、`-Isolated` 空 auth/models/env、`-RunConfigured` 最小真实调用；当前已接入 `verify-release-contracts.ps1` 的 isolated contract，不把 inspect/isolated 误记成真实 e2e 完成。
8. 下一轮继续从 parity matrix 的 `Phase 2 Candidate Queue` 领取一条互斥切片；不要重开 broad inventory。
9. 下一批高价值切片优先级：真实 provider/OAuth e2e、真实 registry/signing/provenance rehearsal、更完整 runtime config UX、CodingAgent/Tui 运行态 contract、WebUi branch/tree session parity、Mom Slack/Docker smoke、Pods SSH/HF/GPU/vLLM smoke。`Tau.Ai` 的 env/auth status 基础合同、OpenAI Responses 家族 provider-specific option 第一批本地合同、Mistral 第一批 provider-specific option 本地合同（含 function `toolChoice`）、Anthropic thinking/tool-choice provider-specific option 本地合同和 Google/Vertex/Gemini CLI provider-specific option 本地合同已继续收口，后续更值得推进的是剩余 provider-specific option map、OAuth refresh/login 诊断与真实外部 e2e，而不是重复补同类 env 映射。

## 已完成前置能力

- Agent platform baseline 已完成并归档：`docs/exec-plans/completed/2026-06-07-tau-agent-platform-baseline.md`。
- `Tau.Agent.Platform` 首版 public surface 已存在：`AgentApplication`、builder、delegate tool、session store、runtime log context、Console/HTTP examples。
- 本轮 foundation-first WIP 增加本地 NuGet-style package consumer smoke：临时外部 app 可只引用 `Tau.Ai` 并通过 `Tau.Ai.Providers.Faux` / `ProviderRegistry` / `StreamFunctions` 完成一次 LLM 调用；该 consumer 还会显式注入 `ModelConfigurationStore` / `ProviderAuthResolver`，验证 `models.json` 动态 provider、model-level/provider-level auth status、header override 链路、上游 `google-generative-ai` API 名 alias 调度到 canonical `google-generative-language` provider、provider/model/default request options 配置注入、OpenAI Responses provider-specific options typed dispatch、Mistral provider-specific options typed dispatch（含 function `toolChoice` 对象）、Anthropic provider-specific options typed dispatch，以及 Google provider-specific options typed dispatch。另一个临时外部 app 可只引用 `Tau.Agent`，通过传递依赖使用 `Tau.Ai` 并运行 `AgentApplication`、delegate tool、session store 和 runtime log 回合；当前 smoke 还会打印 `toolResult=prepared package consumer`，证明 `AgentApplicationBuilder.AddTool(..., prepareArguments:)` 在外部 package consumer 中真实生效。
- 上述 package consumer boundary 只证明本地 package source 下的外部 .NET consumer 形态；真实 NuGet registry 发布、签名/溯源、global install alias 和真实 provider/OAuth 仍保持 open；TypeScript/npm compatibility package 只有在未来明确要求时才作为新 product surface 处理。
- `Tau.Agent` proxy provider 已有本地 loopback server e2e：临时 TCP server 接收真实 HTTP POST `/api/stream`、校验 bearer/body，并返回 SSE，客户端重建 assistant message；异常路径覆盖 HTTP error、缺 terminal event 和 malformed SSE JSON。
- `Tau.Ai.Cli` 已有本地 dotnet tool install rehearsal：临时 package source 中安装 `pi-ai` / `tau-ai` 两个 tool alias 并验证 command-name help/list 行为；这不是真实 NuGet registry 发布、签名或 provenance。
- package publish 回放现在把 AI CLI tool package 纳入默认发布面：dry-run/apply 都会计划并执行 `Tau.Ai.Cli` 的 `pi-ai` / `tau-ai` tool pack，再把两个 `.nupkg` 纳入 push entries；这仍是本地 fake/dry-run contract，不是真实 feed promotion。
- `Tau.Agent.Platform` builder facade 已暴露 delegate tool `prepareArguments` pass-through，外部应用可以通过 `AgentApplicationBuilder.AddTool(..., prepareArguments:)` 使用参数预处理；这只是 builder/API 面收口，不是 provider/OAuth 或真实 registry/signing/provenance 完成。
- `Tau.Ai` / `Tau.Agent` package/index export shape 已决策为 Tau-native .NET assembly surface，不新增 TypeScript/npm compatibility shim；`verify-ai-agent-export-shape.ps1` 证明 mapping 文档和实现/测试/smoke 证据文件仍一致。
- `Tau.Ai` OAuth registry/helper 本地合同已收口：built-ins 默认加载、custom provider register/unregister、built-in provider unregister 后恢复、reset 恢复 built-ins、provider info listing、`RefreshOAuthTokenAsync` 和 `GetOAuthApiKeyAsync` 均有 tests 与 public API sample 覆盖；真实 OAuth provider/browser/cloud e2e 仍单独保持 open。
- `Tau.Ai` helper utilities 本地合同已继续收口：`AiHeaderUtilities`、`ShortHash`、`ContextOverflowDetector` 和 `JsonSchemaHelpers.StringEnum` 对应的四个 file-level rows 为 `verified`；aggregate helper utilities 仍因 `json-parse.ts` future provider edge audit、TypeBox export/runtime schema 和真实 provider e2e 保持 `partial`。
- `Tau.Ai.Providers.Faux` 本地 public test provider 合同已继续收口：registration/unregistration、queued/factory responses、model-aware factory、usage/cache estimate、streamed deltas、terminal error/aborted message、`StreamOptions.OnResponse` 和 `StreamOptions.Signal` 均由 `FauxProviderTests` 与 public API sample 覆盖；这是纯本地 provider row，不代表真实云端 provider/OAuth e2e 完成。
- `Tau.Ai` shared stream options 本地合同已继续收口：`StreamOptions.OnPayload` / `OnResponse`、`ProviderResponse`、`SimpleStreamOptions.ThinkingBudgets` 和 `StreamOptionHelpers` 覆盖上游 `onPayload`、`onResponse`、`thinkingBudgets`、reasoning clamp 与 token-budget provider 映射；focused shared-options gate 39/39 和当时完整 `Tau.Ai.Tests` 406/406 已通过。provider cancellation 已由下一条收口，真实 provider/OAuth e2e 和真实云端 callback/cancellation timing correlation 仍保持 open。
- `Tau.Ai` provider cancellation 本地合同已继续收口：`StreamOptions.Signal` 已进入 OpenAI、OpenAI-compatible、OpenAI Responses/Codex/Azure、Anthropic、Google/Gemini CLI/Vertex、Mistral、Bedrock、Vertex ADC token exchange、SSE/WebSocket/EventStream parser 和 retry delay 路径；pre-cancel 与用户触发取消都会产生 `Request was aborted` terminal assistant，`StopReason=Aborted`。focused cancellation/provider gate 44/44 和完整 `Tau.Ai.Tests` 410/410 已通过。真实 provider/OAuth e2e 与真实云端 cancellation timing correlation 仍保持 open。
- `Tau.Ai` KnownApi alias adapter 已继续收口：外部 direct model 或 `models.json` 可以使用上游公开 API 名 `openai-completions`、`openai-compatible` 和 `google-generative-ai`；Tau 会在 registry/config 层规范化到内部 `openai-chat-completions` / `google-generative-language`，不重复注册 alias key，也不改变 provider 协议。focused alias/config/registry gate 52/52 和外部 package consumer 36 assertions 已通过。真实 provider/OAuth e2e 和字节级 TypeScript API surface 仍保持 open。
- `Tau.Ai` models.json request options 本地合同已继续收口：provider-level、`modelOverrides.<model>.options` 和 `models[].options` 会按 provider -> modelOverride -> model -> explicit code options 合并进 `StreamOptions` / `SimpleStreamOptions`，覆盖 `temperature`、`maxTokens`、`topP`、`transport`、`cacheRetention`、`sessionId`、`maxRetryDelayMs`、`metadata`、`reasoning` 和 `thinkingBudgets`。代码显式参数保持最高优先级；`Signal`、`OnPayload`、`OnResponse` 不从 JSON 配置读取。随后 Responses 家族 provider-specific options 第一批也已本地收口：`openai-responses` 支持 `serviceTier`、`reasoningEffort`、`reasoningSummary`，`openai-codex-responses` 额外支持 `textVerbosity`，`azure-openai-responses` 额外支持 `azureApiVersion`、`azureResourceName`、`azureBaseUrl`、`azureDeploymentName`，并由 `StreamFunctions` 投影到 typed provider options。Mistral 第一批 provider-specific options 也已本地收口：`mistral-conversations` 支持字符串型 `toolChoice`、对象型 function `toolChoice`、`promptMode` 和 `reasoningEffort`，并投影到 typed `MistralOptions`。Anthropic 第一批 provider-specific options 也已本地收口：`anthropic-messages` 支持 `thinkingEnabled`、`thinkingBudgetTokens`、adaptive `effort`、`thinkingDisplay`、`interleavedThinking` 和字符串/tool 对象型 `toolChoice`，并投影到 typed `AnthropicOptions`。Google 第一批 provider-specific options 也已本地收口：`google-generative-language` 支持 `toolChoice`、`thinkingEnabled`、`thinkingBudgetTokens`，`google-vertex` 额外支持 `project/location`，`google-gemini-cli` 额外支持 `thinkingLevel` 和 `projectId`，并投影到 typed `GoogleOptions` / `GoogleVertexOptions` / `GoogleGeminiCliOptions`。focused Google/config/public API gate 39/39、完整 `Tau.Ai.Tests` 444/444 和外部 package consumer 74 assertions 已通过。其它 provider-specific option map 与真实 provider/OAuth e2e 仍保持 open。
- AI provider/OAuth matrix 现在已有仓库内脚本化 contract：`verify-ai-provider-e2e-matrix.ps1 -Isolated` 会清空常见 provider env 并指向临时空 auth/models file，固定 inspect contract；只有后续显式 `-RunConfigured` 且真实凭证可用时，才会把某个 provider 行从 `external-e2e-needed` 往前推进。

## P0 backlog

### Tau.Ai / Tau.Agent

- [ ] 真实 provider/OAuth e2e：OpenAI/Responses/Codex/Azure/Copilot、Anthropic、Google/Vertex/Gemini CLI/Antigravity、Mistral、Bedrock/AWS credential chain。
- [ ] 真实 registry/signing/provenance rehearsal：`Tau.Ai` / `Tau.Agent` / `Tau.Tui` / `Tau.Ai.Cli` 已有本地 package publish command-shape 回放，AI/Agent 已有 package consumer，AI CLI 已有 tool install rehearsal；下一步仍要证明真实 registry source、签名/溯源、package source credential 边界和发布回滚边界。
- [ ] 更完整 runtime config UX：通用 `models.json` request options、Responses 家族 provider-specific options 第一批、Mistral 字符串/function `toolChoice` provider-specific options、Anthropic thinking/tool-choice provider-specific options 和 Google/Vertex/Gemini CLI provider-specific options 已有本地合同；后续仍要补剩余 provider-specific option map、OAuth refresh/login 诊断、provider-specific edge cases，以及真实 provider callback/cancellation timing correlation。

### Tau.CodingAgent / Tau.Tui

- [ ] 完整上游 TreeSelector、session `listAll` / cross-project fork prompt、session schema/selection、metadata inspector、多选和 branch switching summarization hooks。
- [ ] 完整 `@mariozechner/jiti` import/alias/virtualModules、custom tools/wrappers/migration、session/provider/context/input/user_bash/model/resource/before hooks、运行中 runner tool hot-swap。
- [ ] 完整 TUI terminal host、overlay compositor、scrollback/viewport 接入、hardware cursor、theme rendering、resource selector、Markdown/highlight/custom tool renderer。
- [ ] image/clipboard/vision e2e：桌面/OSC52 image clipboard、Photon-like resize/convert 质量、provider vision e2e。
- [ ] package/bin/config parity：最终 `pi` package/bin identity、真实 npm/git package smoke、启动 telemetry/changelog 真实路径。

### Tau.WebUi

- [ ] CodingAgent JSONL branch/tree true persistence 和语义导入，不只做 preview-derived conservative import。
- [ ] reusable component package、IndexedDB/provider-key/custom-provider UI、auth/settings UX、artifact message reconstruction 和 richer renderers。
- [ ] browser/release/static smoke：证明 release artifact 下 WebUi 静态/浏览器流程可运行。

### Tau.Mom

- [ ] 真实 Slack smoke：receive/respond/thread/file/history/backfill/session sync。
- [ ] 真实 Docker sandbox smoke 和 workspace/session 多任务委派模型。
- [ ] fs-watch event runtime、Slack session schema parity、timestamp migration 后续实战验证。

### Tau.Pods

- [ ] 真实 SSH/HF download/setup/GPU/vLLM startup health smoke。
- [ ] pod prompt e2e、rollout/rollback state、多版本部署和更完整 remote transport hardening。
- [ ] systemd/nohup fallback 在真实远端环境下的长期健康、日志 follow 和 failure classification。

### Release / CI / scripts

- [ ] 非宿主平台 executable smoke、Unix wrapper/auth-backup parity、release/static browser smoke。
- [ ] 真实 NuGet/package registry 发布演练、package signing/provenance rehearsal、真实远端 release dry-run 或 staging run。
- [ ] 上游 root utility 的剩余差异：session transcript analyze 子 agent 模式、真实 provider usage-cost samples、edit/tool/runtime e2e、TUI first-frame profiling/CPU profile。

## 当前已知环境现实

- Windows 本机继续以 PowerShell gate 为权威验证链：`powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` 和 `-RunSmoke`。
- 当前 Windows 环境下 `bash scripts/verify-dotnet.sh --skip-restore` 可能落到 WSL 并失败于缺少 `/bin/bash`；这属于本机 shell 环境现实，不等同于 Tau build failure。
- 没有真实云服务、Slack、Docker、SSH/GPU pod、NuGet registry 或 signing credentials 时，对应行保持 open，不能用 fixture smoke 伪造成最终完成。
