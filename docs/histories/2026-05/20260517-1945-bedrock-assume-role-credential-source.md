# Tau.Ai Bedrock AssumeRole credential_source baseline

Date: 2026-05-17

## Summary

Closed the remaining `credential_source` gap in the Bedrock AssumeRole path. `BedrockAssumeRoleResolver` now resolves source credentials from three places when the profile has `role_arn` but no `source_profile`:

- `credential_source = Environment` — reads `AWS_ACCESS_KEY_ID` / `AWS_SECRET_ACCESS_KEY` / `AWS_SESSION_TOKEN` directly.
- `credential_source = EcsContainer` — delegates to `BedrockEcsContainerResolver` (same SSRF-allowlisted HTTP fetch).
- `credential_source = Ec2InstanceMetadata` — delegates to `BedrockInstanceMetadataResolver`, but forces IMDS opt-in even when the global default is opt-out, because the profile explicitly asked for it.

The remaining "credential_source not supported" hard-error message in `next.md` and the plan is also retired.

## Changes

- `src/Tau.Ai/Providers/Bedrock/BedrockAssumeRoleResolver.cs`: split source resolution into `LoadSourceProfileAsync` and `LoadCredentialSourceAsync`; the latter dispatches to env / ECS / IMDS; an unknown `credential_source` returns a clear `not supported; use Environment, EcsContainer, or Ec2InstanceMetadata` failure.
- `tests/Tau.Ai.Tests/BedrockAssumeRoleResolverTests.cs`: replaced the "credential_source-based not yet supported" assertion with `neither source_profile nor credential_source`; added three new tests covering Environment-source AssumeRole (verifies the STS request is SigV4-signed with the env keys), unknown-source rejection, and EcsContainer-source AssumeRole (verifies ECS endpoint is called first and STS request is signed with the returned ASIA creds).
- `next.md` and the active port plan: chain description now lists both `source_profile` and `credential_source (Environment / EcsContainer / Ec2InstanceMetadata)` as covered, and adds a decision note for the IMDS opt-out override.

## Verification

- `dotnet build .\src\Tau.Ai\Tau.Ai.csproj --no-restore --verbosity minimal` — succeeded.
- `dotnet test .\tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --no-restore --verbosity minimal --filter "FullyQualifiedName~AssumeRole"` — 15 tests passed.
- `dotnet test .\tests\Tau.Ai.Tests\Tau.Ai.Tests.csproj --no-restore --no-build --verbosity minimal` — 174 tests passed (no regressions).

## Decisions

- Reused the existing ECS and IMDS resolvers as the credential_source sources rather than building a parallel mini-resolver. The HTTP paths, SSRF allowlists, and JSON parsing already match the AWS contract; sharing them keeps the credential chain behaviorally consistent regardless of whether the source comes from a top-level chain step or from AssumeRole.
- Forced IMDS opt-in when `credential_source = Ec2InstanceMetadata` is declared on the profile. A profile that names IMDS as the AssumeRole source is an explicit operator-level decision; the global `AWS_EC2_METADATA_DISABLED` opt-out should not silently void it. Achieved via `options with { Ec2MetadataDisabled = false }` so the override is scoped to this resolver call and never leaks back to the chain.
- Did not implement recursive `source_profile` resolution (e.g., profile A → source_profile B → source_profile C). That is rare in practice and adds cycle-detection and depth-tracking complexity; left as a separate follow-up if the need arises.
