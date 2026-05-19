# Bedrock profile snapshot build fix

Date: 2026-05-16

## Summary

Fixed the Bedrock shared profile credential migration after the resolver was expanded from static credentials to a richer profile snapshot. `BedrockProvider` still referenced the removed `BedrockProfileCredentials` type, which broke `Tau.Ai` compilation.

## Changes

- Updated `BedrockProvider` to consume `BedrockProfileSnapshot` from `BedrockProfileCredentialsResolver.Load()`.
- Updated the missing credential message so it reflects the current shared AWS profile static-credential support instead of claiming shared profile loading is unimplemented.

## Verification

- `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-dotnet.ps1 -SkipRestore` — passed.

## Design intent

The provider should keep the new profile snapshot as the single shared profile data model so later AWS SSO, AssumeRole, credential process, IMDS, ECS, and web identity work can extend that same boundary without reintroducing a parallel profile type.

## Files modified

- `src/Tau.Ai/Providers/Bedrock/BedrockProvider.cs`
- `docs/histories/2026-05/20260516-1753-bedrock-profile-snapshot-build-fix.md`
