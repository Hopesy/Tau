# Tau.Ai OAuth login/refresh real implementations

Date: 2026-05-16

## Summary

Ported all 5 OAuth provider login and token refresh flows from pi-mono-main to Tau.Ai, replacing the previous `NotSupportedException` stubs with real implementations. The `/login` command in Tau.CodingAgent now performs actual OAuth authentication.

## Changes

### Shared OAuth utilities (new files)

- `src/Tau.Ai/Auth/OAuth/OAuthPkce.cs` — PKCE code verifier + SHA-256 challenge generation using `System.Security.Cryptography`
- `src/Tau.Ai/Auth/OAuth/OAuthCallbackServer.cs` — lightweight `HttpListener`-based local server for OAuth redirect callbacks, with state validation and HTML success/error pages
- `src/Tau.Ai/Auth/OAuth/OAuthLoginCallbacks.cs` — `IOAuthLoginCallbacks` interface for driving interactive login flows (OnAuth, OnPrompt, OnProgress, OnManualCodeInput)
- `src/Tau.Ai/Auth/OAuth/OAuthInputParser.cs` — shared parser for authorization code input (URL, query string, or raw code)

### Provider implementations (new files)

- `src/Tau.Ai/Auth/OAuth/Providers/AnthropicOAuthProvider.cs` — authorization code + PKCE flow via local callback server on port 53692
- `src/Tau.Ai/Auth/OAuth/Providers/GitHubCopilotOAuthProvider.cs` — device code flow with polling, GitHub Enterprise support, proxy-ep base URL extraction
- `src/Tau.Ai/Auth/OAuth/Providers/OpenAICodexOAuthProvider.cs` — authorization code + PKCE flow via local callback server on port 1455, JWT accountId extraction
- `src/Tau.Ai/Auth/OAuth/Providers/GeminiCliOAuthProvider.cs` — authorization code + PKCE flow via local callback server on port 8085, Google Cloud project discovery
- `src/Tau.Ai/Auth/OAuth/Providers/AntigravityOAuthProvider.cs` — authorization code + PKCE flow via local callback server on port 51121, Google Cloud project discovery
- `src/Tau.Ai/Auth/OAuth/Providers/GoogleProjectDiscovery.cs` — shared Google Cloud Code Assist project discovery and user email retrieval

### Modified files

- `src/Tau.Ai/Auth/OAuth/IOAuthProvider.cs` — changed `LoginAsync` signature to accept `IOAuthLoginCallbacks`
- `src/Tau.Ai/Auth/OAuth/BuiltInOAuthProviders.cs` — replaced stubs with real provider instances
- `src/Tau.Ai/Auth/OAuth/OAuthCredentialStore.cs` — added `Save(providerId, credentials)` method for write-back after login/refresh
- `src/Tau.Ai/Auth/ProviderAuthResolver.cs` — added `GetOAuthProvider`, `SaveOAuthCredentials`; token refresh now saves back to auth.json
- `src/Tau.CodingAgent/Runtime/ICodingAgentRunner.cs` — added `GetOAuthProvider`, `SaveOAuthCredentials` to interface
- `src/Tau.CodingAgent/Runtime/RuntimeCodingAgentRunner.cs` — implemented new interface methods
- `src/Tau.CodingAgent/Runtime/CodingAgentCommandRouter.cs` — `/login` now performs real OAuth flow via `ConsoleOAuthLoginCallbacks`
- `src/Tau.CodingAgent/Runtime/ConsoleOAuthLoginCallbacks.cs` — console-based callbacks with browser auto-open

### Tests

- `tests/Tau.Ai.Tests/OAuthProviderTests.cs` — 9 new tests covering PKCE generation, GitHub Copilot domain normalization, proxy-ep extraction, OpenAI JWT parsing, credential store save/reload
- Updated `tests/Tau.Ai.Tests/ProviderAuthResolverTests.cs` — adapted stub to new interface
- Updated `tests/Tau.CodingAgent.Tests/CodingAgentHostTests.cs` — adapted login test to new behavior
- Updated `tests/Tau.CodingAgent.Tests/FakeCodingAgentRunner.cs` — added new interface members

## Verification

- `dotnet build src\Tau.Ai\Tau.Ai.csproj --no-restore` — 0 errors
- `dotnet build src\Tau.CodingAgent\Tau.CodingAgent.csproj --no-restore` — 0 errors
- `dotnet test tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --no-restore` — 95 tests pass
- `dotnet test tests\Tau.CodingAgent.Tests\Tau.CodingAgent.Tests.csproj --no-restore` — 115 tests pass

## Decisions

- Used `HttpListener` for callback servers rather than Kestrel/ASP.NET Core to avoid adding framework dependencies to Tau.Ai.
- Used `Utf8JsonWriter` for JSON body construction instead of `JsonSerializer.Serialize` with anonymous types to maintain AOT/trimming compatibility.
- Made `ExtractAccountId` public on OpenAI Codex provider for testability.
- Token refresh in `ProviderAuthResolver.ResolveApiKey()` now saves refreshed credentials back to auth.json automatically.
- Console login callbacks attempt browser auto-open via `Process.Start` with platform detection.
