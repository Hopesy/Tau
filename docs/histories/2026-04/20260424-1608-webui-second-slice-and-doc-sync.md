## [2026-04-24 16:08] | Task: webui-second-slice-and-doc-sync

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `gpt-5`
* **Runtime**: `Codex CLI / workspace-write`

### 📥 User Query

> 继续，全部都移植过来；按当前项目目标持续推进，并把真实项目内容同步进文档，尤其是计划文档。

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent` / `Tau.WebUi` / `Tau.Mom` / `Tau.Pods` / 仓库文档

**Key Actions:**

* **[Runtime 宿主边界]**: 扩展 `RuntimeCodingAgentRunner`，新增显式 `Create(provider, model, history)` 工厂，同时保留 `CreateDefault()`。
* **[WebUi 第二层能力]**: 为 `Tau.WebUi` 增加 session 持久化、`/api/catalog`、session 级 provider/model 选择与设置更新入口。
* **[Mom 结构化委派]**: 为 `Tau.Mom` 增加 `DelegationRequest`、JSON source-gen、`.json` 请求解析，以及 `provider/model/workingDirectory/metadata` 的结构化委派链。
* **[Pods 第二层能力]**: 为 `Tau.Pods` 增加 `probe` 与 `exec`，支持 HTTP endpoint / TCP ssh target 健康探测，以及 SSH 远程命令执行。
* **[最小测试]**: 在 `Tau.CodingAgent.Tests` 中新增 `RuntimeCodingAgentRunner` 与 `WebChatStore` 测试；在 `Tau.Agent.Tests` 中新增 `FileDelegationProcessor` 的结构化委派测试；在 `Tau.Pods.Tests` 中新增 probe/exec 测试。
* **[文档同步]**: 更新 `README.md`、`docs/ARCHITECTURE.md`、`docs/QUALITY_SCORE.md`、`docs/CICD.md`、`docs/exec-plans/active/2026-04-23-tau-port-baseline.md`、`next.md`，让仓库知识反映当前真实实现。

### 🧠 Design Intent (Why)

这一轮的核心不是继续把 `WebUi`、`Mom`、`Pods` 写成“能说话的 demo”，而是把它们推进到真正的宿主边界：

- Web session 不能只活在内存里，否则 `WebUi` 每次启动都像一次性页面
- Web provider/model 不能继续偷用全局环境变量，否则 Web 会话就没有自己的配置语义
- `Mom` 不能只吃纯文本 prompt，否则无法自然走向 Slack/workspace/sandbox 风格的委派协议
- `Pods` 不能只读静态配置，否则它还不是运维 CLI，而只是 config viewer
- `Pods` 也不能只做健康探测，至少要具备最小远程命令执行能力，才能开始接近真实 lifecycle
- `RuntimeCodingAgentRunner` 不能只提供 `CreateDefault()`，否则 `WebUi / Mom / CodingAgent` 无法共享同一个 runtime 内核但保留不同宿主配置

所以这轮选择把 runner 工厂、WebUi store/catalog、Mom 结构化请求，以及 Pods probe/exec 一起落地，并同步补齐计划/质量/CI 文档，避免仓库知识继续停留在第一层切片。

### 📁 Files Modified

* `src/Tau.CodingAgent/Runtime/RuntimeCodingAgentRunner.cs`
* `src/Tau.WebUi/Program.cs`
* `src/Tau.WebUi/Tau.WebUi.csproj`
* `src/Tau.WebUi/appsettings.json`
* `src/Tau.WebUi/Contracts/WebChatContracts.cs`
* `src/Tau.WebUi/Services/WebChatService.cs`
* `src/Tau.WebUi/Services/WebChatStore.cs`
* `src/Tau.WebUi/Services/WebUiJsonContext.cs`
* `src/Tau.WebUi/Services/WebUiOptions.cs`
* `src/Tau.WebUi/Services/WebUiRunnerFactory.cs`
* `src/Tau.WebUi/Ui/WebUiPage.cs`
* `src/Tau.Mom/Program.cs`
* `src/Tau.Mom/Tau.Mom.csproj`
* `src/Tau.Mom/MomOptions.cs`
* `src/Tau.Mom/DelegationRequest.cs`
* `src/Tau.Mom/DelegationExecution.cs`
* `src/Tau.Mom/DelegationResult.cs`
* `src/Tau.Mom/IDelegationAgentRunner.cs`
* `src/Tau.Mom/MomJsonContext.cs`
* `src/Tau.Mom/FileDelegationProcessor.cs`
* `src/Tau.Mom/RuntimeDelegationAgentRunner.cs`
* `src/Tau.Mom/appsettings.json`
* `src/Tau.Pods/Cli/PodsCli.cs`
* `src/Tau.Pods/Models/PodProbeResult.cs`
* `src/Tau.Pods/Models/PodExecResult.cs`
* `src/Tau.Pods/Services/PodProbeService.cs`
* `src/Tau.Pods/Services/PodExecService.cs`
* `src/Tau.Pods/Serialization/PodsJsonContext.cs`
* `tests/Tau.CodingAgent.Tests/Tau.CodingAgent.Tests.csproj`
* `tests/Tau.CodingAgent.Tests/RuntimeCodingAgentRunnerTests.cs`
* `tests/Tau.Agent.Tests/Tau.Agent.Tests.csproj`
* `tests/Tau.Agent.Tests/FileDelegationProcessorTests.cs`
* `tests/Tau.Pods.Tests/PodProbeServiceTests.cs`
* `tests/Tau.Pods.Tests/PodExecServiceTests.cs`
* `README.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/CICD.md`
* `docs/exec-plans/active/2026-04-23-tau-port-baseline.md`
* `next.md`

### ✅ Verification

* `dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore`
* `dotnet build src\Tau.WebUi\Tau.WebUi.csproj --no-restore`
* `dotnet build tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore`
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-build --no-restore`
* `dotnet build src\Tau.Mom\Tau.Mom.csproj --no-restore`
* `dotnet build tests\Tau.Agent.Tests\Tau.Agent.Tests.csproj --no-restore`
* `dotnet test tests\Tau.Agent.Tests\Tau.Agent.Tests.csproj --no-build --no-restore`
* `dotnet build src\Tau.Pods\Tau.Pods.csproj --no-restore`
* `dotnet build tests\Tau.Pods.Tests\Tau.Pods.Tests.csproj --no-restore`
* `dotnet test tests\Tau.Pods.Tests\Tau.Pods.Tests.csproj --no-build --no-restore`
* `dotnet run --project src\Tau.WebUi\Tau.WebUi.csproj --no-build -- --urls http://127.0.0.1:5088`
  * `GET /api/status`
  * `GET /api/catalog`
  * `POST /api/sessions`
  * 检查 `output/webui-sessions.json`
* `dotnet run --project src\Tau.Mom\Tau.Mom.csproj --no-build -- --once`
  * 结构化 `.json` 请求进入 `mom/inbox`
  * 检查 `mom/outbox/*.json` 中的 `provider/model/workingDirectory/metadata`
  * 检查 `mom/archive/*`
* `dotnet run --project src\Tau.Pods\Tau.Pods.csproj --no-build -- probe output\tau.pods.probe.json`
  * 本地 HTTP endpoint 返回 `http 200 OK`

### ⚠️ Notes

* 当前本机 `bash` 仍可能报 `Bash/Service/CreateInstance/E_ACCESSDENIED`，这轮验证主要使用等价顺序 `dotnet` 命令完成。
* `Tau.WebUi` / `Tau.Mom` 仍保留 `HintPath` workaround；这轮没有顺手清引用结构，避免再次踩回 solution/metaproj/workload resolver 异常。
* 当前 `Tau.Mom` 的 outbox `error` 仍主要来自外部 provider 网络不可达，而不是本地请求解析或落盘链路错误。
* `Tau.Pods probe` 当前已能做 HTTP/TCP 健康探测，`exec` 已能对 SSH pod 调用系统 `ssh` 客户端；下一层才是 deploy / restart / lifecycle。
