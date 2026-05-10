# Tau 完整 pi-mono 移植计划

## 目标

把 Tau 从“按模块补关键切片”升级为“以 `pi-mono-main` 为参考源，逐模块追到可评审的功能等价层”。这份 plan 是完整移植的总路线图，现有 `2026-04-23-tau-port-baseline.md` 继续作为已完成基线和决策历史，本文件负责后续 parity closure。

“完整”在这里不是一次性把所有代码机械翻译到 C#，而是每个上游能力都要在 Tau 中落成下列三件事：

- 有明确的 Tau-native 边界和数据模型。
- 有本地可验证的 test 或 smoke。
- 文档、history、quality、next 同步说明已完成/未完成，不把 seam 伪装成完整功能。

## 当前事实

- `Tau.Ai` 已经有多 provider 专用路径、generated model catalog、models.json custom config 和 provider auth resolver，但 OAuth login/refresh、完整 AWS credential chain、dynamic provider API 注册和真实 e2e 仍未闭合。
- `Tau.CodingAgent` 已有平面 session store、settings、slash command router、基础命令和手动 compaction，但仍缺上游 JSONL session tree、resume/fork/full stats、auto-compaction、dynamic prompt/skill/extension command、HTML/share export。
- `Tau.WebUi` 已有可持久化 session、provider/model 选择和最小聊天页，但仍缺 streaming、attachments、rich rendering、auth/settings UX。
- `Tau.Mom` 已有本地 inbox/outbox/events、runtime context、attachment staging、workspace layout、channel log/status/context/last_prompt seams，但仍缺 Slack Socket Mode adapter、queue/backfill/stop、Slack file download、sandbox/tool delegation、skill runtime loader 和端到端 Slack flow。
- `Tau.Pods` 已有 config/probe/exec，仍缺 deploy/stop/restart/health、model lifecycle 和远端 transport hardening。
- Windows 本机验证入口以 `powershell -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore -RunSmoke` 为准；不要把本机 bash/WSL 缺失误判为 Tau 失败。

## 总体顺序

完整移植按“可运行 seam -> adapter/transport -> e2e parity”的顺序推进，不再只按文件数量补代码：

1. **P0：收拢当前 dirty chain**
   - 保持当前 Tau.Mom 迁移链可 build/test/smoke。
   - 所有新 seam 都先走 fake/local transport 测试，再接真实外部 SDK。
   - 本阶段退出标准：`Tau.Mom` 独立 build、`Tau.Agent.Tests`、`verify-dotnet.ps1 -SkipRestore -RunSmoke` 全绿。

2. **P1：Tau.Mom full parity**
   - 建立 Slack-compatible channel message envelope，让 file/events/Slack/backfill 共享同一消息事实。
   - 接 Slack Socket Mode / Web API adapter：mention、DM、message logging、bot message logging、thread response、typing/update/delete/upload。
   - 接 channel queue、startup backfill、old-message logging、busy-state、stop command 和 queue limit。
   - 接 Slack file download 到 `ChannelAttachmentStore`，保留 `original/local/url` 语义。
   - 接 workspace/sandbox/tool delegation：`bash/read/write/edit/attach`，并保证 `scratch/`、`attachments/`、`SYSTEM.md`、`skills/` 的路径 authority 不分叉。
   - 接 custom mom skill runtime loader；现有 skill docs inventory 只作为 prompt 可见，不算 runtime loader 完成。
   - 端到端验证先用 fake Slack transport，再用可配置真实 Slack smoke。

3. **P2：Tau.CodingAgent session / command / extension parity**
   - 移植上游 JSONL SessionManager tree：session entries、branch/fork/resume、labels、full stats。
   - 补 auto-compaction、compaction metadata、token threshold、retry/rollback。
   - 补 dynamic slash command registry、prompt registry、skills/extensions discovery。
   - 补 share/export/import parity：HTML、JSONL、tree、clipboard/rich content。
   - 保持当前 Tau snapshot store 作为迁移/兼容入口，不直接破坏已有平面 session。

4. **P3：Tau.Ai provider/auth/model parity**
   - OAuth/device login/refresh：Anthropic、GitHub Copilot、OpenAI Codex、Gemini CLI/Antigravity。
   - AWS credential chain：SSO、AssumeRole、credential_process、IMDS、ECS、web identity。
   - dynamic provider API 注册：models.json 中未预注册 provider 的 runtime registration。
   - generated model seed 持续同步到 Tau 已支持 API 家族；模型表不能领先于 provider 行为。
   - 建立 provider e2e matrix，把 stub tests 与真实云端 smoke 分层。

5. **P4：Tau.WebUi parity**
   - Streaming message UI，和 `AssistantMessageStream` / agent events 对齐。
   - Attachments upload/download、image/tool result rendering。
   - Thinking/tool timeline/rich markdown/code block rendering。
   - Auth/settings UX：provider status、login entry、models.json/settings 管理。
   - Web session restore、rename、delete、export/import 与 CodingAgent session 语义对齐。

6. **P5：Tau.Tui parity**
   - 真正输入编辑器、选择/历史/快捷键体系。
   - 差分渲染、message area、status area、tool timeline。
   - 与 CodingAgent richer rendering 共享必要抽象，不把 UI 状态塞回 runtime。

7. **P6：Tau.Pods parity**
   - deploy/stop/restart/health lifecycle。
   - model lifecycle、remote command output、failure classification。
   - SSH/HTTP transport hardening，配置校验与安全边界。

8. **P7：release / CI / docs parity**
   - release 产物改成真实 Tau executable/package。
   - CI 接入 PowerShell 或 bash 等价 smoke，覆盖 WebUi/Mom/Pods 高价值运行态。
   - 文档、references、security、supply-chain、release notes 以可审计方式同步。

## 当前执行切片

本轮先做 `Tau.Mom` 的 Slack-compatible message envelope：

- 新增 `MomChannelMessage` / `MomChannelAttachment`，表达上游 Slack event 中的 channel、user、userName、displayName、ts、threadTs、text、attachments、provider/model/title/metadata。
- `FileDelegationProcessor` 的 `.json/.txt/.md` 输入先映射成同一 envelope，再生成 `DelegationRequest`。
- `MomEventProcessor` 的 local events 也映射成同一 envelope，再生成 inbox request。
- 新增 `IMomChannelTransport` / `IMomChannelResponder` / `MomChannelMessageProcessor`，先固定 transport/responder contract、busy-state、stop placeholder、typing、thread response、attachment staging、status/log writeback 和 runner 调用。
- 当前不引入 Slack SDK；下一切片再把真实 Slack Socket Mode/Web API 接到这个 processor。

## 风险与约束

- 不把“本地 seam 已完成”写成“真实 Slack/Sandbox 已完成”。
- 不为追求 1:1 文件数量而牺牲 Tau 当前 AOT/source-gen/零 provider SDK 的边界。
- 外部服务能力必须先有 fake transport/stub handler 测试，再接真实 e2e。
- Windows 本机验证串行执行；不要并行跑会写相同 `bin/obj/output` 的 build/test。

## 验证方式

- `dotnet build src\Tau.Mom\Tau.Mom.csproj --no-restore`
- `dotnet test tests\Tau.Agent.Tests\Tau.Agent.Tests.csproj --no-restore`
- `powershell -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore -RunSmoke`

## 进度记录

- [x] 完整移植总路线图落到 active plan。
- [x] `Tau.Mom` Slack-compatible channel message envelope seam。
- [x] `Tau.Mom` fake Slack transport / responder seam（transport/responder interface + channel processor + fake responder tests）。
- [ ] `Tau.Mom` real Slack Socket Mode adapter。
- [ ] `Tau.Mom` Slack backfill / queue / stop / file download。
- [ ] `Tau.Mom` sandbox/tool delegation。
- [ ] `Tau.CodingAgent` JSONL SessionManager tree。
- [ ] `Tau.Ai` OAuth/device login parity。
- [ ] `Tau.WebUi` streaming/attachments/rich rendering。
- [ ] `Tau.Pods` deploy/stop/restart/health。

## 决策记录

- 2026-05-10：决定新增完整移植总 plan，而不是继续只在 baseline plan 里追加零散条目。原因是用户目标已经明确升级为“完完整整的移植”，需要一个跨模块、跨阶段、可持续评审的 parity closure 路线图；旧 baseline plan 保留为已完成基线和历史决策来源。
- 2026-05-10：决定先把 `Tau.Mom` 的 Slack event 事实抽成 `MomChannelMessage`，而不是直接接 Slack SDK。原因是上游 `SlackEvent`、backfill message、local event 和 file delegation 都会汇入同一 channel log/session/status/attachment 语义；先固定 Tau-native envelope，可以让 file/events/fake Slack/real Slack 共用映射和测试。
- 2026-05-10：决定先落 `IMomChannelTransport` / `IMomChannelResponder` / `MomChannelMessageProcessor`，而不是让真实 Slack adapter 直接调用 `IDelegationAgentRunner`。原因是 busy-state、stop、typing、thread response、status/log writeback 和 attachment staging 都属于 Mom channel runtime 语义；Slack SDK adapter 应只负责收消息、发消息、上传/下载文件。
