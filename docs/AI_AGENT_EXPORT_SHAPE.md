# Tau.Ai / Tau.Agent export shape decision

本文固定 `packages/ai` 和 `packages/agent` 的上游 TypeScript package export / bin 形状在 Tau 中的 .NET-native 对应关系。它解决的是“是否还缺一个 TypeScript barrel/subpath 等价层”的决策问题，不替代真实 provider/OAuth e2e、真实 NuGet feed promotion、签名、provenance 或外部 package registry 验收。

## 结论

- Tau 不新增 TypeScript/npm 兼容 shim。`Tau.Ai` 和 `Tau.Agent` 的正式公共面是 .NET assembly + namespaces + NuGet package metadata。
- Machine-readable decision: no TypeScript/npm compatibility shim.
- 上游 `@mariozechner/pi-ai` 的 subpath exports 在 Tau 中映射为 `Tau.Ai` 单 assembly 下的 provider/auth/registry/streaming namespaces；外部 .NET consumer 通过 `PackageReference Include="Tau.Ai"` 消费。
- 上游 `@mariozechner/pi-agent-core` 的 `src/index.ts` barrel 在 Tau 中映射为 `Tau.Agent` 单 assembly 下的 facade/runtime/proxy/platform namespaces；外部 .NET consumer 通过 `PackageReference Include="Tau.Agent"` 消费，并由 NuGet dependency 传递获得 `Tau.Ai`。
- 上游 `pi-ai` bin 在 Tau 中映射为 release wrapper `pi-ai` 和 dotnet tool package `Tau.Ai.Cli.PiAiTool` / `ToolCommandName=pi-ai`；Tau-native alias `tau-ai` 继续保留。
- 上游 re-export 的 `@sinclair/typebox` `Type` / `Static` / `TSchema` 不做 .NET 同名等价导出；Tau-native 等价是 `System.Text.Json.JsonElement` schema、`JsonSchemaHelpers` 和 `ToolArgumentValidator`。完整 AJV/TypeBox keyword/runtime-codegen/CSP fallback 行为继续由 `ToolArgumentValidator` / schema validation backlog 跟踪，不归入 package export shape。

## Upstream AI package snapshot

上游 `packages/ai/package.json` 当前公开这些 package exports：

| Upstream export | Tau decision | Tau evidence |
| --- | --- | --- |
| `.` | `Tau.Ai` assembly root public namespaces, not a TypeScript barrel | `src/Tau.Ai/Tau.Ai.csproj`, `tests/Tau.Ai.Tests/AiPublicApiCompileSampleTests.cs` |
| `./anthropic` | `Tau.Ai.Providers.Anthropic` namespace | `src/Tau.Ai/Providers/Anthropic/AnthropicProvider.cs` |
| `./azure-openai-responses` | `Tau.Ai.Providers.OpenAiResponses` Azure provider | `src/Tau.Ai/Providers/OpenAiResponses/AzureOpenAiResponsesProvider.cs` |
| `./google` | `Tau.Ai.Providers.Google` namespace | `src/Tau.Ai/Providers/Google/GoogleProvider.cs` |
| `./google-gemini-cli` | `Tau.Ai.Providers.Google` Gemini CLI provider | `src/Tau.Ai/Providers/Google/GoogleGeminiCliProvider.cs` |
| `./google-vertex` | `Tau.Ai.Providers.Google` Vertex provider | `src/Tau.Ai/Providers/Google/GoogleVertexProvider.cs` |
| `./mistral` | `Tau.Ai.Providers.Mistral` namespace | `src/Tau.Ai/Providers/Mistral/MistralProvider.cs` |
| `./openai-codex-responses` | `Tau.Ai.Providers.OpenAiResponses` Codex provider | `src/Tau.Ai/Providers/OpenAiResponses/OpenAiCodexResponsesProvider.cs` |
| `./openai-completions` | `Tau.Ai.Providers.OpenAi` / OpenAI-compatible provider path | `src/Tau.Ai/Providers/OpenAi/OpenAiProvider.cs`, `src/Tau.Ai/Providers/OpenAiCompat/OpenAiCompatibleProvider.cs` |
| `./openai-responses` | `Tau.Ai.Providers.OpenAiResponses` provider | `src/Tau.Ai/Providers/OpenAiResponses/OpenAiResponsesProvider.cs` |
| `./oauth` | `Tau.Ai.Auth.OAuth` namespace | `src/Tau.Ai/Auth/OAuth/OAuthProviderRegistry.cs`, `src/Tau.Ai/Auth/OAuth/BuiltInOAuthProviders.cs` |
| `./bedrock-provider` | `Tau.Ai.Providers.Bedrock` namespace | `src/Tau.Ai/Providers/Bedrock/BedrockProvider.cs` |

上游 `packages/ai/src/index.ts` 还 re-export 这些 surface，Tau 决策如下：

| Upstream index export | Tau decision |
| --- | --- |
| `@sinclair/typebox` `Type` / `Static` / `TSchema` | No same-name .NET export; use `JsonElement` schemas, `JsonSchemaHelpers`, and `ToolArgumentValidator` |
| `./api-registry.js` | `Tau.Ai.Providers.ProviderRegistry` |
| `./env-api-keys.js` | `Tau.Ai.Auth.EnvironmentApiKeyResolver` and `ProviderAuthResolver` |
| `./models.js` | `Tau.Ai.Registry.ModelCatalog`, `Model`, `ModelCompatibility`, generated model catalog |
| provider option type exports | Provider-specific option records/classes under `Tau.Ai.Providers.*` |
| `./providers/faux.js` | `Tau.Ai.Providers.Faux` public scripted provider |
| `./providers/register-builtins.js` | `Tau.Ai.Providers.BuiltInProviders` |
| `./stream.js` | `Tau.Ai.Providers.StreamFunctions` |
| `./types.js` | `Tau.Ai.Abstractions` records for messages, content blocks, models, options, tools, usage and stream events |
| `./utils/event-stream.js` | `Tau.Ai.Streaming.EventStream` and `AssistantMessageStream` |
| `./utils/json-parse.js` | `Tau.Ai.Streaming.StreamingJsonParser` |
| OAuth type exports | `Tau.Ai.Auth.OAuth` contracts and credential records |
| `./utils/overflow.js` | `Tau.Ai.ContextOverflowDetector` |
| `./utils/typebox-helpers.js` | `Tau.Ai.JsonSchemaHelpers` |
| `./utils/validation.js` | `Tau.Ai.ToolArgumentValidator` |

上游 `packages/ai/package.json` 的 `bin.pi-ai` 在 Tau 中由以下证据固定：

- `src/Tau.Ai.Cli/Tau.Ai.Cli.csproj`
- `src/Tau.Ai.Cli/AiCliRunner.cs`
- `scripts/verify-ai-cli-tool-install.ps1`
- `scripts/publish-release-packages.ps1`
- `scripts/verify-release-package-publish.ps1`

## Upstream Agent package snapshot

上游 `packages/agent/package.json` 没有额外 subpath exports；其 public entry 是 `main/types` 指向 `dist/index.*`，并依赖 `@mariozechner/pi-ai`。上游 `packages/agent/src/index.ts` re-export：

| Upstream index export | Tau decision | Tau evidence |
| --- | --- | --- |
| `./agent.js` | `Tau.Agent.Agent` facade and `AgentOptions` | `src/Tau.Agent/Agent.cs`, `tests/Tau.Agent.Tests/AgentPublicApiCompileSampleTests.cs` |
| `./agent-loop.js` | `Tau.Agent.Runtime.AgentRuntime`, `AgentLoopConfig`, `ToolExecutor` | `src/Tau.Agent/Runtime/AgentRuntime.cs`, `src/Tau.Agent/Runtime/AgentLoopConfig.cs` |
| `./proxy.js` | `Tau.Agent.Proxy.ProxyStreamProvider`, `ProxyStreamOptions` | `src/Tau.Agent/Proxy/ProxyStreamProvider.cs`, `scripts/verify-agent-proxy-server-e2e.ps1` |
| `./types.js` | `Tau.Agent.Abstractions` event/tool/state records and interfaces | `src/Tau.Agent/Abstractions/AgentEvents.cs`, `src/Tau.Agent/Abstractions/IAgentTool.cs` |

Tau also exposes `Tau.Agent.Platform` as a .NET-native addition for reusable app hosting:

- `AgentApplication`
- `AgentApplicationBuilder`
- `DelegateAgentTool`
- `AgentRunResult`
- `IAgentSessionStore`
- `AgentSessionSnapshot`
- `InMemoryAgentSessionStore`

The package dependency shape is verified by `scripts/verify-agent-package-consumer.ps1`: a temp external app can reference only `Tau.Agent`, restore the transitive `Tau.Ai` dependency, and run an `AgentApplication` round with `Tau.Ai.Providers.Faux`.

## Verification contract

`scripts/verify-ai-agent-export-shape.ps1` is the local contract for this decision. It asserts:

- this document contains every upstream AI package export, every important AI index re-export group, and every Agent index re-export group listed above;
- the Tau implementation and sample files referenced as evidence exist in the current repository;
- the existing package consumer, tool install, package publish and proxy server smoke scripts are present.

This script is deliberately local and deterministic. It does not touch real provider credentials, real package registries, signing certificates, GitHub Releases or network services.

## Remaining non-local gates

- Real provider/OAuth e2e remains open for OpenAI/Responses/Codex/Azure/Copilot, Anthropic, Google/Vertex/Gemini CLI/Antigravity, Mistral and Bedrock/AWS.
- Real registry/global install promotion remains open until packages/tools are pushed to the intended feed and installed from that feed.
- Real package signing/provenance remains open until it runs with the intended certificate, timestamp server and release artifacts.
- If a future requirement demands an actual TypeScript/npm compatibility package, that is a new product surface, not part of the current .NET-native foundation completion gate.
