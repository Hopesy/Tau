# auth.json Schema

Tau stores OAuth and API key credentials in `auth.json`. The file is searched in this order:

1. `TAU_AUTH_FILE` environment variable (if set)
2. `./.tau/auth.json` (current directory)
3. `~/.tau/auth.json` (user home)

## Format

```json
{
  "<provider-id>": { ... credential entry ... }
}
```

Provider IDs match Tau's registered providers: `anthropic`, `github-copilot`, `google-gemini-cli`, `google-antigravity`, `openai-codex`, `openrouter`, etc.

## Entry Types

### OAuth Credential

```json
{
  "type": "oauth",
  "refresh": "<refresh-token>",
  "access": "<access-token>",
  "expiresAt": "2030-01-01T00:00:00Z",
  "projectId": "optional-metadata",
  "email": "optional@example.com",
  "enterpriseUrl": "optional-enterprise-domain"
}
```

Fields:
- `type` (required): `"oauth"`
- `refresh` (required): Refresh token for obtaining new access tokens
- `access` (required): Current access token
- `expiresAt` (required): ISO 8601 timestamp or Unix milliseconds when token expires
- Additional fields: Provider-specific metadata (e.g., `projectId` for Google, `enterpriseUrl` for GitHub Copilot, `accountId` for OpenAI Codex)

### API Key Credential

```json
{
  "type": "api_key",
  "key": "<api-key-value>"
}
```

Fields:
- `type` (required): `"api_key"` or `"apiKey"`
- `key` (required): API key value (also accepted as `apiKey`)

### Implicit API Key (legacy)

```json
{
  "key": "<api-key-value>"
}
```

If an entry has a `key` field but no `access` field and no explicit `type`, it's treated as an API key.

## Provider-Specific Metadata

| Provider | Metadata Fields |
|----------|----------------|
| `anthropic` | (none) |
| `github-copilot` | `enterpriseUrl` (optional GitHub Enterprise domain) |
| `google-gemini-cli` | `projectId` (required), `email` (optional) |
| `google-antigravity` | `projectId` (required), `email` (optional) |
| `openai-codex` | `accountId` (required, extracted from JWT) |

## Security

- Tau writes `auth.json` with owner-only permissions on Unix (`0600`).
- The default local file `./.tau/auth.json` is ignored by Git; if `TAU_AUTH_FILE` points elsewhere, keep that path outside version control as well.
- Never commit `auth.json` to version control.
- Tau's `/auth` command shows credential status without revealing secret values.
- Token refresh automatically saves updated credentials back to the file.
- Provider metadata fields named `type`, `refresh`, `access`, `expires`, `expiresAt`, `key`, or `apiKey` are reserved and are not written from OAuth metadata; this keeps metadata from shadowing credential fields.

## Resolution Priority

When resolving credentials for a provider, Tau checks in order:
1. Explicit API key (passed via CLI or runtime)
2. Environment variables (`ANTHROPIC_API_KEY`, `OPENAI_API_KEY`, etc.)
3. auth.json entries (API key or OAuth with auto-refresh)
4. models.json provider configuration (`apiKey` field)

For the `models.json` side of the same resolution chain, see `docs/references/models-json-schema.md`.
