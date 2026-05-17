# Tau.Ai Bedrock web identity baseline

Date: 2026-05-17

## Summary

Continued the P0 Bedrock AWS credential chain port. After the `credential_process` slice this turn adds the **STS AssumeRoleWithWebIdentity** path: when the env vars `AWS_WEB_IDENTITY_TOKEN_FILE` + `AWS_ROLE_ARN` are set (or the active profile contains `web_identity_token_file` + `role_arn`, or `BedrockOptions.WebIdentityTokenFile/RoleArn/RoleSessionName` is passed directly), Tau will read the OIDC token, POST `Action=AssumeRoleWithWebIdentity` to the regional STS endpoint, parse the XML `Credentials` block, and feed the resulting `AccessKeyId/SecretAccessKey/SessionToken/Expiration` into the existing SigV4 signer.

## Changes

- Added `src/Tau.Ai/Providers/Bedrock/BedrockWebIdentityResolver.cs`:
  - reads token file path + role ARN + optional session name from `BedrockOptions`, env vars, and `BedrockProfileSnapshot`;
  - generates a deterministic default session name (`tau-bedrock-<unix-ts>`) when none is provided;
  - resolves the STS endpoint to `sts.<region>.amazonaws.com` and supports `BedrockOptions.StsEndpoint`, `AWS_ENDPOINT_URL_STS`, and `AWS_STS_ENDPOINT_URL` overrides (falls back to global `sts.amazonaws.com` if region is empty);
  - POSTs URL-encoded `Action=AssumeRoleWithWebIdentity&Version=2011-06-15&RoleArn=…&RoleSessionName=…&WebIdentityToken=…`;
  - parses the response with `XDocument` (namespace-agnostic local-name lookup), rejects already-expired credentials, marks `Source = "web_identity"`;
  - extracts `Error/Code` + `Error/Message` from STS error XML, falls back to raw body when the XML is unfamiliar;
  - reports `NotConfigured` when token file / role ARN are missing so the caller can fall through to the next resolver.
- Extended `BedrockOptions` with `WebIdentityTokenFile`, `WebIdentityRoleArn`, `WebIdentityRoleSessionName`, and `StsEndpoint` fields.
- Reordered `BedrockProvider.ResolveCredentialsAsync` to: static → web identity → credential_process → fail, passed `region` through so the STS endpoint can be derived without re-reading env vars.
- Updated `BuildMissingCredentialsMessage` to mention the new `AWS_WEB_IDENTITY_TOKEN_FILE` option.
- Added `tests/Tau.Ai.Tests/BedrockWebIdentityResolverTests.cs` covering: XML success parsing, missing `Credentials` block, expired credentials, STS error code/message extraction, full `ResolveAsync` happy path (verifies URL-encoded body + regional `sts.<region>.amazonaws.com` host), `NotConfigured` when inputs are absent, missing token file, empty token file, STS 4xx error passthrough, and `StsEndpoint` override.
- Extended `tests/Tau.Ai.Tests/BedrockProviderTests.cs` with two provider-level integration regressions:
  - end-to-end: token file + role ARN → STS POST → SigV4 Bedrock signing with `x-amz-security-token`;
  - STS error path: Bedrock is **not** called when `AssumeRoleWithWebIdentity` returns a 4xx with an `<ErrorResponse>` body.
- Synced `next.md`, `docs/exec-plans/active/2026-05-10-tau-complete-pi-mono-port.md` (current-facts, progress checkbox, verification list, two new decisions).

## Verification

- `dotnet build .\src\Tau.Ai\Tau.Ai.csproj --no-restore --verbosity minimal` — succeeded.
- `dotnet build .\tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --no-restore --verbosity minimal` — succeeded.
- `dotnet test .\tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --no-restore --no-build --filter "FullyQualifiedName~Bedrock"` — 34 tests passed (12 new web-identity tests + existing Bedrock regressions).
- `dotnet test .\tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --no-restore --no-build` — 126 tests passed.

## Decisions

- Picked **web identity** as the second credential-chain slice after `credential_process` because STS `AssumeRoleWithWebIdentity` is the only AWS chain entry that needs no SigV4 (the OIDC token self-authenticates) and no AWS SDK calls. The whole flow stays inside `HttpClient` + Tau-native XML parsing, so it can be exercised end-to-end with `StubHandler` in tests.
- Used `XDocument` for STS XML parsing instead of raw `XmlReader`. Earlier `ReadElementContentAsString` attempts mis-positioned the reader; `XDocument` with namespace-agnostic `LocalName` lookup is short, AOT-friendly, and reuses the same helper for the success and error responses.
- Modeled STS endpoint resolution to mirror the AWS SDK precedence (regional → global) plus `AWS_ENDPOINT_URL_STS` / `AWS_STS_ENDPOINT_URL` overrides. The override is also exposed as `BedrockOptions.StsEndpoint` so callers (Tau host, future Pods adapters, smoke tests) can point at FIPS / VPC endpoints without env mutation.
- Picked `tau-bedrock-<unix-ts>` as the default `RoleSessionName` to keep AWS CloudTrail logs traceable to Tau invocations and to satisfy the STS uniqueness requirement without forcing users to set env vars.
- Made web identity sit **before** `credential_process` in the resolver. AWS SDKs do the same when env vars are set; this also keeps `credential_process` as the last process-spawn-driven fallback, where it can stay opt-in via profile or option overrides.
