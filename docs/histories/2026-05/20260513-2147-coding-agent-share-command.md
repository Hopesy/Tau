## [2026-05-13 21:47] | Task: coding-agent share command

### 🤖 Execution Context

* **Agent ID**: `Codex`
* **Base Model**: `GPT-5`
* **Runtime**: `Codex CLI / Windows PowerShell`

### 📥 User Query

> 继续推进 Tau 的 pi-mono 迁移。

### 🛠 Changes Overview

**Scope:** `Tau.CodingAgent` share / export command surface

**Key Actions:**

* **[Share command]**: 新增 `/share` 本地命令，复用现有 HTML transcript exporter，把当前 session 先导出为临时 HTML。
* **[GitHub CLI seam]**: 新增 `ICodingAgentShareClient` / `GitHubCliCodingAgentShareClient`，按上游行为检查 `gh auth status` 并执行 `gh gist create --public=false`，预览 URL 支持 `TAU_SHARE_VIEWER_URL`。
* **[HTML deep links]**: 继续补齐 HTML transcript 的消息级 share 语义，把 JSONL message entry id 写入 HTML 节点，加入 per-message copy link、`leafId` / `targetId` URL 构造和 deep-link 定位高亮。
* **[HTML timeline metadata]**: 继续让 HTML transcript 直接消费 current branch JSONL，渲染 `session_info`、`model_change`、`label` 和 `compaction` timeline entries，给消息展示 label badge，并让 branch outline 可滚动到非 message entry。
* **[HTML branch filtering]**: 给 HTML branch outline 增加 `default/no-tools/user-only/labeled-only/all` filter 和 search 控件，复用 `/tree` 已有的过滤心智，方便大型 session export/share 里定位 entry。
* **[Tests/docs]**: 补充 share command 的 fake client 回归，确认临时 HTML 内容、空 session guard 和清理行为，并同步 README、ARCHITECTURE、QUALITY_SCORE、active plan 与 `next.md`。

### 🧠 Design Intent (Why)

上游 CodingAgent 已有 `/share`，当前 Tau 只完成了本地 `/export`。这次没有直接接 GitHub REST API 或自建 share service，而是先对齐上游依赖 GitHub CLI 的最小语义：当前 session 生成 HTML，交给 `gh` 创建 secret gist，再返回 viewer URL。把 gist 创建封装成可注入 client，可以让 router 测试不依赖本机 `gh` 或真实 GitHub 登录。

2026-05-14 继续补 HTML message deep-link / copy-link，而不是直接搬上游完整 HTML template。原因是 Tau 当前 exporter 仍是单文件、无外部 vendor 依赖的 Tau-native baseline；先固定 JSONL entry id 到 HTML message 节点的映射和 `targetId` 定位，后续再补 Tau 专属 share viewer、真实 `gh` smoke 和 richer template。

同日继续补 HTML current-branch timeline，而不是只显示 flat snapshot messages。原因是上游 HTML viewer 的关键价值在 session tree 审计：label、model change 和 compaction 都是用户理解分支历史的重要信息；Tau 先把这些 entry 渲染成可测试的单文件 HTML，完整 Markdown/highlight/theme/custom tool renderer 继续作为后续 richer template 切片。

同日又补 branch outline filter/search，而不是等待完整上游 sidebar。原因是 Tau `/tree` 已经有 `default/no-tools/user-only/labeled-only/all` 和 search 语义，HTML export/share 应先拥有同样的检索入口；虚拟树、折叠、sidebar resize 和完整主题仍留在 richer template backlog。

### 📁 Files Modified

* `src/Tau.CodingAgent/Runtime/CodingAgentShareClient.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentHtmlSessionExporter.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentCommandCatalog.cs`
* `src/Tau.CodingAgent/Runtime/CodingAgentHost.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentCommandRouterTests.cs`
* `tests/Tau.CodingAgent.Tests/CodingAgentHostTests.cs`
* `README.md`
* `docs/ARCHITECTURE.md`
* `docs/QUALITY_SCORE.md`
* `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`
* `next.md`

### ✅ Verification

* `dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore --verbosity minimal`
* `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore --verbosity minimal`
* `powershell -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore -RunSmoke`
* `git diff --check`
