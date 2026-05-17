# Tau.Ai Bedrock AssumeRole (source_profile) baseline

Date: 2026-05-17

## Summary

Continued the P0 Bedrock AWS credential chain port. After `credential_process`, `web identity`, `ECS container`, and `IMDS v2`, this turn adds **STS AssumeRole** driven by the active profile's `role_arn` + `source_profile`. This covers the most common `~/.aws/config` pattern: a static "base" profile that the user assumes into a per-environment role.

Along the way, this slice generalizes the SigV4 signer to support arbitrary AWS services and extracts the shared STS XML response parser, which the remaining SSO slice will reuse.

## Changes

- Generalized `BedrockSigV4Signer.Sign` to accept a service name (preserving the original Bedrock-only overload as a wrapper). STS now uses `service=sts`, while existing Bedrock callers are untouched.
- Added `src/Tau.Ai/Providers/Bedrock/BedrockStsResponseParser.cs` with `ParseCredentialsResponse(xml, sourceLabel, clock)`, `ExtractErrorMessage(xml)`, and `ResolveStsEndpoint(optionsOverride, region)` helpers — used by both web identity and AssumeRole.
- Refactored `BedrockWebIdentityResolver` to delegate XML parsing and endpoint resolution to `BedrockStsResponseParser` (no behavior change).
- Added `src/Tau.Ai/Providers/Bedrock/BedrockAssumeRoleResolver.cs`:
  - returns `NotConfigured` when the profile does not have `role_arn`;
  - returns a clear failure when `role_arn` is present but `source_profile` is missing (a follow-up slice will cover `credential_source`-based AssumeRole);
  - reloads the source profile via `BedrockProfileCredentialsResolver.Load(options, sourceProfileName)`, requires static keys on it, and surfaces "did not provide static credentials" if missing;
  - builds the STS form body (`Action=AssumeRole&Version=2011-06-15&RoleArn=…&RoleSessionName=…[&ExternalId=…]`), URL-encoded;
  - signs the request with the source credentials using SigV4 (service=sts) and POSTs to the regional STS endpoint (honoring `BedrockOptions.StsEndpoint` / `AWS_ENDPOINT_URL_STS` / `AWS_STS_ENDPOINT_URL`);
  - parses the response via `BedrockStsResponseParser.ParseCredentialsResponse(..., "assume_role", clock)`;
  - surfaces STS error code/message verbatim on non-2xx.
- Updated `BedrockProvider.ResolveStaticCredentials` to skip profile-static credentials when the active profile defines `role_arn` — they are the AssumeRole *source*, not the profile's own creds. This matches AWS SDK semantics.
- Inserted the AssumeRole resolver in `BedrockProvider.ResolveCredentialsAsync` between web identity and credential_process. Updated chain order: static → web identity → AssumeRole → credential_process → ECS → IMDS → fail.
- Added `tests/Tau.Ai.Tests/BedrockAssumeRoleResolverTests.cs` covering: `NotConfigured` when `role_arn` is absent, "credential_source not supported" hard error when source_profile is missing, full success path (config + credentials INI on disk → SigV4-signed STS POST → `<Credentials>` parsed and marked `assume_role`), missing source_profile static keys, and STS 4xx error code/message passthrough.
- Extended `tests/Tau.Ai.Tests/BedrockProviderTests.cs` with a provider-level integration regression: profile `dev` with `role_arn` + `source_profile=base` → STS POST signed with base keys → Bedrock request signed with the returned `ASIAASSUMED` session credentials, including `x-amz-security-token` propagation.
- Synced `next.md`, the active port plan (current-facts checkbox, verification list, three new decisions).

## Verification

- `dotnet build .\src\Tau.Ai\Tau.Ai.csproj --no-restore --verbosity minimal` — succeeded.
- `dotnet build .\tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --no-restore --verbosity minimal` — succeeded.
- `dotnet test .\tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --no-restore --no-build --filter "FullyQualifiedName~Bedrock"` — 72 tests passed (5 new AssumeRole, plus 1 new provider integration regression, plus existing IMDS / ECS / web identity / credential_process / SigV4 regressions).

## Decisions

- Started with `source_profile`-only AssumeRole, leaving `credential_source` (Environment / EcsContainer / Ec2InstanceMetadata) and recursive source_profile chains for a follow-up slice. Rationale: the static `source_profile` flow is by far the most common shape in real config files; tackling it first lets us validate the generalized SigV4 signer and the shared STS XML parser without introducing recursive resolution or per-source orchestration logic in the same change.
- Made `ResolveStaticCredentials` skip the active profile's static keys when `role_arn` is set. AWS SDKs treat those keys as the AssumeRole source, not the profile's identity, and returning them would short-circuit AssumeRole and silently sign Bedrock requests with the wrong principal. The explicit skip matches that contract and keeps the chain ordering deterministic.
- Generalized `BedrockSigV4Signer.Sign(...)` rather than cloning a STS-specific signer. The v4 algorithm is identical across AWS services; the only service-specific bit is the credential scope/signing-key derivation, which now takes `service` as a parameter. The Bedrock-only overload is kept so existing callers compile without churn, but new resolvers (STS today, SSO later) pass the right service in.
- Extracted `BedrockStsResponseParser` rather than duplicating XML parsing in each resolver. Both `AssumeRoleWithWebIdentity` and `AssumeRole` produce `<Credentials>` blocks with identical schema and share `<ErrorResponse>` semantics; one parser keeps behavior coherent and lets the future SSO slice plug in the same way.
- Reported STS errors as `Code: Message` so operators can act on `AccessDenied`, `ExpiredToken`, etc. without reading raw XML, while still falling back to `body.Trim()` when the response is not a well-formed STS error document.
