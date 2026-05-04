# Tau

Tau 是 [pi-mono](https://github.com/badlogic/pi-mono) 的 .NET 10 移植仓库，目标是在 C# / .NET 生态中重建其核心 AI Agent 能力，而不是简单包一层兼容壳。

当前仓库已经从单纯 **CLI-first 收口** 进入 **多应用面最小产品切片阶段**：

- `Tau.Ai` / `Tau.Agent` / `Tau.CodingAgent` / `Tau.Tui` 已有可运行核心基线
- `Tau.WebUi` 已从 Hello World 推进到 **可持久化 session + provider/model 选择** 的 Web 宿主
- `Tau.Mom` 已从纯文本 worker 推进到 **支持结构化委派请求** 的本地委派宿主
- `Tau.Pods` 已从静态 config CLI 推进到 **支持 probe + exec** 的运维 CLI

## 当前状态

- 已有：
  - `Tau.Ai`：消息抽象、流事件、EventStream、provider 注册、model catalog、OAuth / env auth 解析
  - `Tau.Agent`：双层循环 runtime、工具执行、状态与事件骨架
  - `Tau.CodingAgent`：最小可运行 CLI、基础文件/命令工具，以及和 `ModelCatalog` 对齐的默认 provider / model 接线；当前还新增了显式 `Create(provider, model, history)` runner 工厂
  - `Tau.WebUi`：最小聊天页、status/catalog/session/messages API、本地 session 持久化、provider/model 选择、runtime-coding-agent 接线
  - `Tau.Mom`：本地文件委派处理链，支持 `.txt/.md/.json` 请求、`provider/model/workingDirectory/metadata` 结构化字段、`--once`、outbox 结果落盘和 archive 归档
  - `Tau.Pods`：`init / list / validate / status / probe / exec` 命令、sample config、validator、主动 endpoint/tcp 探测、SSH 远程命令执行、AOT 友好的 JSON source-gen
- 未完成：
  - 完整 TUI 交互层
  - 完整 Coding Agent 会话/模式/配置系统
  - Web UI 的流式绑定、附件、auth/settings UX
  - Mom 的 Slack / sandbox / workspace 委派
  - Pods 的 deploy / lifecycle 管理
  - 更高层行为回归测试

更细的状态请看：

- `docs/ARCHITECTURE.md`
- `docs/product-specs/tau-port-overview.md`
- `docs/QUALITY_SCORE.md`
- `docs/exec-plans/active/2026-04-23-tau-port-baseline.md`

## 当前应用面最小能力

### Tau.WebUi

- `/` 返回内嵌 HTML/JS 的聊天页
- `/healthz`
- `/api/status`
- `/api/catalog`
- `/api/sessions`
- `/api/sessions/{id}`
- `POST /api/sessions`
- `PUT /api/sessions/{id}`
- `POST /api/sessions/{id}/messages`
- 会话持久化到 `output/webui-sessions.json`
- 每个 session 可独立选择 provider / model

### Tau.Mom

- 默认扫描 `mom/inbox`
- 输出结果到 `mom/outbox`
- 原始请求归档到 `mom/archive`
- 支持 `.txt / .md / .json` 三种输入
- `.json` 请求支持：
  - `prompt`
  - `provider`
  - `model`
  - `workingDirectory`
  - `title`
  - `metadata`
- 支持 `--once`

### Tau.Pods

- `init [path]`
- `list [path]`
- `validate [path]`
- `status [path]`
- `probe [path]`
- `exec [path] <id> <command>`
- sample `tau.pods.json`

当前 `Pods` 行为：

- `probe`
  - 对 `endpoint` 做 HTTP GET 健康探测
  - 对 `sshHost/sshPort` 做 TCP 连通性探测
- `exec`
  - 对 SSH pod 通过系统 `ssh` 客户端执行远程命令
  - 对 endpoint pod 明确返回 unsupported

## 本地开发

仓库标准验证链：

```bash
bash scripts/verify-dotnet.sh
bash scripts/verify-dotnet.sh --skip-restore
```

Windows PowerShell 等价入口：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore
powershell -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore -RunSmoke
```

当前机器上的现场现实：

- 仓库规范和 workflow 仍然统一走 bash
- 但本机 bash 服务可能报 `Bash/Service/CreateInstance/E_ACCESSDENIED`，或者落到 WSL 后缺少 `/bin/bash`
- 因此 Windows 本机优先使用 `scripts/verify-dotnet.ps1`
- 如果两类脚本入口都不可用，再退回等价顺序的 `dotnet build/test/run` 命令

当前已确认的运行 / 验证现实是：

- `Tau.CodingAgent.csproj` 已可独立 `dotnet build --no-restore`
- `Tau.WebUi.csproj` 已可独立 `dotnet build --no-restore`
- `Tau.Mom.csproj` 已可独立 `dotnet build --no-restore`
- `Tau.Pods.csproj` 已可独立 `dotnet build --no-restore`
- `Tau.slnx` 已可通过 `dotnet build Tau.slnx --verbosity minimal`
- `Tau.CodingAgent.Tests` 已通过 54 个测试
- `Tau.Agent.Tests` 已通过 2 个 Mom 结构化委派测试
- `Tau.Pods.Tests` 已通过 7 个测试
- `Tau.Tui.Tests` 已通过 4 个测试
- `Tau.Ai.Tests` 已通过 78 个测试
- 主验证链已经改成 `scripts/verify-dotnet.sh`，按显式项目顺序 restore / build / test
- Windows 本机已补 `scripts/verify-dotnet.ps1`，可完整覆盖同一组 restore / build / test 验证链，并可通过 `-RunSmoke` 额外执行 `WebUi` 与 `Mom --once` 的最小运行态验证

最小启动验证：

```bash
dotnet run --project src/Tau.CodingAgent/Tau.CodingAgent.csproj --no-build
dotnet run --project src/Tau.WebUi/Tau.WebUi.csproj --no-build -- --urls http://127.0.0.1:5088
dotnet run --project src/Tau.Mom/Tau.Mom.csproj --no-build -- --once
dotnet run --project src/Tau.Pods/Tau.Pods.csproj --no-build -- probe tau.pods.json
dotnet run --project src/Tau.Pods/Tau.Pods.csproj --no-build -- exec tau.pods.json <pod-id> <command>
```
