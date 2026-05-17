# Tau.Ai Bedrock credential_process baseline

Date: 2026-05-17

## Summary

Continued the pi-mono port by closing the first slice of the P0 Bedrock AWS credential chain: `credential_process`. Until now Tau's Bedrock provider only consumed static credentials (options/env vars or shared-profile static keys) and the profile parser already recorded `credential_process`, `role_arn`, `web_identity_token_file`, etc. without consuming them. This slice wires the existing profile field plus an explicit `BedrockOptions.CredentialProcess` override through a stub-friendly subprocess seam, so users with `credential_process = ...` in `~/.aws/config` (or `~/.aws/credentials`) can drive the Bedrock SigV4 path without bearer tokens or static keys.

## Changes

- Added `IBedrockProcessRunner` / `BedrockProcessRequest` / `BedrockProcessResult` and a `DefaultBedrockProcessRunner` that shells out via `argv`-style `ProcessStartInfo.ArgumentList` (no shell wrapper).
- Added `BedrockCredentialProcessResolver` with:
  - quoted-argv tokenizer (handles `"…"`, `'…'`, backslash-escaped quotes inside double quotes, fails fast on unterminated quotes);
  - JSON contract validation (`Version=1`, required `AccessKeyId` / `SecretAccessKey`, optional `SessionToken` / `Expiration`);
  - clock-aware expiration check that rejects already-expired credentials;
  - explicit failure messages for non-zero exit (with stderr passthrough), timeout, non-JSON output, parse failures, and unsupported `Version`.
- Extended `BedrockProvider` with an internal constructor that accepts an `IBedrockProcessRunner`, a new `ResolveCredentialsAsync` path that prefers explicit/profile static credentials, falls back to `credential_process` when the profile or options provide it, and surfaces resolver errors directly through `ErrorEvent`.
- Added `BedrockOptions.CredentialProcess` and an updated `BuildMissingCredentialsMessage` covering the new fallback.
- Marked `BedrockAwsCredentials.Source` for the static (`static` / `profile:<name>`) and process (`credential_process`) cases so future chain slices can keep observability.
- Added `InternalsVisibleTo` for `Tau.Ai.Tests` so the seam can be exercised without making it part of Tau.Ai's public surface.
- Added `BedrockCredentialProcessResolverTests` covering the tokenizer, success path, exit/stderr passthrough, JSON validation (missing keys, unsupported `Version`, parse error), expiration rejection, timeout reporting, and the "not configured" branch.
- Extended `BedrockProviderTests` with three regressions: option-driven `credential_process` -> SigV4, profile-driven `credential_process` -> SigV4 with region/host derived from the profile, and provider error passthrough on helper failure.
- Updated `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md`, `next.md` to reflect the new `[~]` state of the AWS credential chain, decisions for picking `credential_process` first and propagating helper errors verbatim, and the new verification list entries.

## Verification

- `dotnet build .\src\Tau.Ai\Tau.Ai.csproj --no-restore --verbosity minimal` — succeeded.
- `dotnet build .\tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --no-restore --verbosity minimal` — succeeded.
- `dotnet test .\tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --no-restore --no-build --filter "FullyQualifiedName~Bedrock"` — 22 tests passed.
- `dotnet test .\tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --no-restore --no-build` — 114 tests passed.

## Decisions

- Started the AWS credential chain with `credential_process` because it is the most self-contained source (subprocess + JSON), the profile parser already reads the field, and a single `IBedrockProcessRunner` seam unlocks deterministic local tests today plus a shared abstraction for the IMDS / ECS / web-identity / SSO / AssumeRole slices that come next.
- Parsed the `credential_process` command with a Tau-native quoted-argv tokenizer (no shell wrapper) to stay close to the AWS SDK behavior and to avoid shell metacharacter injection when the command is sourced from a config file we don't own.
- Surfaced helper failures (`exited with status …: <stderr>`, timeout, parse error) directly in `ErrorEvent` instead of collapsing to the generic missing-credentials message: when a helper is configured, the actionable signal is the exit code / stderr, not a "no credentials" hint.
- Kept the `BedrockProvider` public constructor unchanged and introduced an internal constructor + `InternalsVisibleTo`, so the seam stays test-only and we don't expand Tau.Ai's public API surface for a transitional credential source.
