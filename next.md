# Tau next

这份文件只记录当前还没做完、后续需要继续推进的缺口，方便 `/goal` 续跑时快速选择下一刀。详细 inventory 以 active parity matrix 为准：

- `docs/exec-plans/active/2026-05-28-tau-100-percent-pi-mono-parity.md`
- `docs/exec-plans/active/2026-05-28-pi-mono-parity-matrix.md`
- `GOAL.md`

## 当前直接执行入口

1. Foundation-first gate 已在本地完成验证：`scripts/verify-agent-package-consumer.ps1` 22 assertions、`scripts/verify-release-contracts.ps1 -Json`、`scripts/verify-dotnet.ps1 -SkipRestore -RunSmoke` 均已通过。
2. Agent stream proxy local server path 已补验证：`scripts/verify-agent-proxy-server-e2e.ps1` 覆盖真实 loopback HTTP/SSE `/api/stream` server path、缺 terminal event 和 malformed SSE JSON，并已接入 `verify-dotnet.ps1 -RunSmoke` / release contract。
3. 下一轮继续从 parity matrix 的 `Phase 2 Candidate Queue` 领取一条互斥切片；不要重开 broad inventory。
4. 下一批高价值切片优先级：真实 provider/OAuth e2e、`Tau.Ai` / `Tau.Agent` package/global install alias 或真实 registry/signing/provenance rehearsal、CodingAgent/Tui 运行态 contract、WebUi branch/tree session parity、Mom Slack/Docker smoke、Pods SSH/HF/GPU/vLLM smoke。

## 已完成前置能力

- Agent platform baseline 已完成并归档：`docs/exec-plans/completed/2026-06-07-tau-agent-platform-baseline.md`。
- `Tau.Agent.Platform` 首版 public surface 已存在：`AgentApplication`、builder、delegate tool、session store、runtime log context、Console/HTTP examples。
- 本轮 foundation-first WIP 增加本地 NuGet-style package consumer smoke：临时外部 app 可只引用 `Tau.Ai` 并通过 `Tau.Ai.Providers.Faux` / `ProviderRegistry` / `StreamFunctions` 完成一次 LLM 调用；另一个临时外部 app 可只引用 `Tau.Agent`，通过传递依赖使用 `Tau.Ai` 并运行 `AgentApplication`、delegate tool、session store 和 runtime log 回合。
- 上述 package consumer boundary 只证明本地 package source 下的外部 .NET consumer 形态；真实 NuGet registry 发布、签名/溯源、global install alias、真实 provider/OAuth 和 TypeScript export/subpath exact parity 仍保持 open。
- `Tau.Agent` proxy provider 已有本地 loopback server e2e：临时 TCP server 接收真实 HTTP POST `/api/stream`、校验 bearer/body，并返回 SSE，客户端重建 assistant message；异常路径覆盖 HTTP error、缺 terminal event 和 malformed SSE JSON。

## P0 backlog

### Tau.Ai / Tau.Agent

- [ ] 真实 provider/OAuth e2e：OpenAI/Responses/Codex/Azure/Copilot、Anthropic、Google/Vertex/Gemini CLI/Antigravity、Mistral、Bedrock/AWS credential chain。
- [ ] `Tau.Ai` / `Tau.Agent` package/global install alias：证明真实 registry 或等价 rehearsal、signing/provenance、package source credential 边界、global tool/package alias。
- [ ] public export shape 决策：保留 .NET-native assembly surface 的同时，明确上游 TypeScript export/subpath 中没有 Tau-native 等价的项目如何记录为差异或 non-goal。
- [ ] 更完整 runtime config UX：models/auth/provider option map、OAuth refresh/login 诊断和 provider-specific edge cases。

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
