# CodingAgent `--help` Examples / Environment Variables / Available Tools sections

## [2026-06-12] | Task: extend CodingAgent CLI help body to upstream section parity

### Scope

`src/Tau.CodingAgent/Runtime/CodingAgentCliHelp.cs`, `tests/Tau.CodingAgent.Tests/CodingAgentInitialMessageBuilderTests.cs`

### 审视

对照上游 `packages/coding-agent/src/cli/args.ts` `printHelp(...)`，Tau 的 `--help` 输出此前只渲染 header / Usage / Commands / Options / Extension CLI Flags，缺少上游 help 体里同样面向用户的 `Examples:`、`Environment Variables:` 和 `Available Tools (default: ...)` 三个段落。`--help` 是 documented user-visible CLI contract surface，缺这三段属于合同缺口（`contract` class）。

确认事实面：
- Examples 段每行命令都对应 Tau 已实现的 flag/behavior（`-p`、`--continue`、`--model provider/id`、`--model sonnet:high`、`--tools read,grep,find,ls`、`--export ... output.html`、`@file` 初始消息）。
- Available Tools 段对应上游 CLI 工具名集合 `read,bash,edit,write,grep,find,ls`，与 `CodingAgentCliArguments.CliToolNameToTauToolName` 的 key 集一致，默认子集说明与上游一致。
- Environment Variables 段只列 Tau 真正消费的变量：以 `src/Tau.Ai/Auth/EnvironmentApiKeyResolver.cs` 为权威 provider-key 列表（Anthropic / OpenAI / Azure / Gemini / Groq / Cerebras / xAI / OpenRouter / Vercel AI Gateway / ZAI / Mistral / MiniMax / OpenCode / Kimi / GitHub Copilot / Bedrock 凭证链），再加 CodingAgent runtime 实际消费的 `PI_OFFLINE`、`PI_TELEMETRY`。上游 help 中 Tau 无消费者的变量（如 `PI_PACKAGE_DIR`、`PI_SHARE_VIEWER_URL`、`AZURE_OPENAI_*` 细分映射、`PI_AI_ANTIGRAVITY_VERSION` 等）有意不列，避免谎报支持。

### 执行

- `CodingAgentCliHelp.BuildHelpText(...)` 在 Extension CLI Flags 段之后追加 `Examples:`、`Environment Variables:`、`Available Tools (...)` 三段，命令名使用传入的 `commandName`（默认 `pi`，可由 `TAU_CODING_AGENT_COMMAND_NAME` 覆盖）。
- 新增 `BuildHelpText_IncludesExamplesEnvVarsAndToolSections` 断言三段标题、代表性命令示例、代表性 env var 和工具行，并确认未列入无 Tau 消费者的 `PI_PACKAGE_DIR`。

### 验证

- `dotnet build src/Tau.CodingAgent/Tau.CodingAgent.csproj`：0 warning / 0 error。
- focused help 测试 3/3 通过。
- 全仓 `scripts/verify-dotnet.ps1 -SkipRestore`：Ai 287、Agent 123、Tui 251、CodingAgent 630、WebUi 61、Pods 216 全通过。

### 边界 / 仍 open

- 本切片只关闭 help 文案的 section parity；不改变任何运行时行为。
- 未复刻上游 help 中 Tau 尚无消费者的 env vars 与精确 Azure deployment map 说明；这些随对应 provider/runtime parity 推进时再补，不在 help 里提前声明。
- `--help` 的最终 package/bin identity（命令名 `pi`）和真实 provider/auth e2e 仍属后续。
