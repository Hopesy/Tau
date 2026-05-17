# Tau.Ai Bedrock ECS container credentials baseline

Date: 2026-05-17

## Summary

Continued the P0 Bedrock AWS credential chain port. After `credential_process` and `web identity`, this turn adds the **ECS / EKS Pod Identity container credentials** path. Tau will now resolve credentials from `AWS_CONTAINER_CREDENTIALS_RELATIVE_URI` / `AWS_CONTAINER_CREDENTIALS_FULL_URI` (and their `BedrockOptions` equivalents), with optional bearer auth from `AWS_CONTAINER_AUTHORIZATION_TOKEN` or `AWS_CONTAINER_AUTHORIZATION_TOKEN_FILE`. Resolved credentials feed straight into the existing SigV4 signer.

## Changes

- Added `src/Tau.Ai/Providers/Bedrock/BedrockEcsContainerResolver.cs`:
  - prefers `_FULL_URI` over `_RELATIVE_URI` (matching the AWS SDK precedence);
  - prefixes a missing leading `/` on the relative URI and joins it onto `http://169.254.170.2`;
  - enforces an SSRF allowlist on the resolved URI: HTTPS is unconditionally allowed; HTTP is only allowed when the host is `169.254.170.2`, `169.254.170.23` (EKS Pod Identity), or a loopback host (`localhost`, `127.x`, `::1`);
  - reads the authorization token from `AWS_CONTAINER_AUTHORIZATION_TOKEN_FILE` (trimmed file content) or `AWS_CONTAINER_AUTHORIZATION_TOKEN`, in that order, and attaches it as the `Authorization` header verbatim;
  - parses the JSON response, extracts `AccessKeyId`/`SecretAccessKey`/`Token`/`Expiration`, rejects already-expired credentials, marks `Source = "ecs"`;
  - reports HTTP failures with status code + body, network failures with the underlying exception message, and returns `NotConfigured` when neither URI env var/option is set.
- Extended `BedrockOptions` with `ContainerCredentialsRelativeUri`, `ContainerCredentialsFullUri`, `ContainerAuthorizationToken`, and `ContainerAuthorizationTokenFile`.
- Reordered `BedrockProvider.ResolveCredentialsAsync` to: static → web identity → credential_process → ECS → fail.
- Updated `BuildMissingCredentialsMessage` to mention the new container-credentials env vars.
- Added `tests/Tau.Ai.Tests/BedrockEcsContainerResolverTests.cs` covering URI resolution (relative, full, missing slash, invalid), the allowlist (Theory inputs for both allowed and disallowed hosts), JSON parsing (success / expired / missing keys), `ResolveAsync` happy path with header propagation, token file reading, HTTPS endpoint override, SSRF rejection, `NotConfigured` short-circuit, and 4xx body passthrough.
- Extended `tests/Tau.Ai.Tests/BedrockProviderTests.cs` with a provider-level integration regression: `_RELATIVE_URI` + bearer token → ECS endpoint → JSON response → SigV4 Bedrock signing with `x-amz-security-token`.
- Synced `next.md`, the active port plan (current-facts checkbox, verification list, new decisions).

## Verification

- `dotnet build .\src\Tau.Ai\Tau.Ai.csproj --no-restore --verbosity minimal` — succeeded.
- `dotnet build .\tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --no-restore --verbosity minimal` — succeeded.
- `dotnet test .\tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --no-restore --no-build --filter "FullyQualifiedName~Bedrock"` — 56 tests passed (21 new ECS, 12 web-identity, 10 credential_process, plus existing Bedrock regressions).

## Decisions

- Enforced an SSRF allowlist on `AWS_CONTAINER_CREDENTIALS_FULL_URI` because the env var is attacker-controllable in many container orchestrators. Without the allowlist, a malicious task definition could redirect the bearer token to an arbitrary host. Limiting HTTP to 169.254.170.2 / 169.254.170.23 / loopback (and unconditionally accepting HTTPS) matches the AWS SDK's behavior and preserves room for self-hosted proxies that terminate TLS.
- Inserted ECS after `credential_process` and before failure, rather than between static and web identity. The rationale is "explicit wins": `credential_process` is opt-in via profile/option, whereas ECS triggers on env vars typically injected by the orchestrator. We prefer explicit operator intent before falling through to environment-driven defaults — and this leaves IMDS to slot in *after* ECS in a later slice without surprising existing config.
- Read the authorization token file with `File.ReadAllTextAsync` and trim, matching the SDK contract (the file is expected to contain a single header value). Surfacing read failures as `Failure` rather than `NotConfigured` so the user sees a real error path instead of silent fallthrough.
- Kept the response JSON parser AOT-friendly (`JsonDocument.Parse` with explicit property reads) and consistent with the credential_process parser shape, so future SSO / IMDS / AssumeRole slices can re-use the same Expiration parsing pattern.
