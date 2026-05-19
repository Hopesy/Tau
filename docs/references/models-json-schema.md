# models.json Schema

Tau uses `models.json` to add or override model catalog entries and to provide request-time provider configuration.

Search order:

1. `TAU_MODELS_FILE` environment variable (if set)
2. `./.tau/models.json` (current directory)
3. `~/.tau/models.json` (user home)

## Format

```json
{
  "providers": {
    "<provider-id>": {
      "api": "openai-compatible",
      "baseUrl": "https://api.example.com/v1",
      "apiKey": "EXAMPLE_API_KEY",
      "authHeader": true,
      "headers": {
        "X-Custom": "literal-or-env-or-command"
      },
      "models": [
        {
          "id": "example-model",
          "name": "Example Model",
          "contextWindow": 128000,
          "maxTokens": 16384
        }
      ],
      "modelOverrides": {
        "existing-model-id": {
          "name": "Existing Model Alias"
        }
      }
    }
  }
}
```

## Supported Fields

Provider fields:

- `api`: Tau provider API id. `openai-compatible` and `openai-completions` normalize to `openai-chat-completions`.
- `baseUrl`: Provider base URL.
- `apiKey`: Request credential value or reference.
- `authHeader`: If `true`, Tau adds `Authorization: Bearer <apiKey>` at request time when an API key is available.
- `headers`: Static request headers. Values use the same resolution rules as `apiKey`.
- `compat`: Tau-supported compatibility metadata for OpenAI-compatible routing and request shape.
- `models`: Custom model entries.
- `modelOverrides`: Overrides for existing built-in/generated model entries.

Model fields:

- `id` (required for custom model entries)
- `name`
- `api`
- `baseUrl`
- `reasoning`
- `input` or `inputModalities`
- `cost`
- `contextWindow`
- `maxTokens` or `maxOutputTokens`
- `headers`
- `compat`

## Value Resolution

At request time, `apiKey` and header values resolve as follows:

1. Empty or whitespace values are ignored.
2. Values starting with `!` execute the remaining text as a local shell command and use stdout.
3. If the value matches an environment variable name, Tau uses that environment value.
4. Otherwise Tau treats the value as a literal.

`!command` is intentionally a trusted local configuration feature. Only use it from files you control.

## Security

- The default local file `./.tau/models.json` is ignored by Git because it can contain `apiKey`, `Authorization`, `api-key`, `x-api-key`, `x-goog-api-key`, or token-like header values.
- If `TAU_MODELS_FILE` points elsewhere, keep that path outside version control unless it contains only non-secret catalog metadata.
- Commit examples as docs or `models.example.json`, not as the active local `./.tau/models.json`.
- Tau's auth status surfaces (`/auth` and WebUi auth status) inspect whether credential configuration exists in `models.json`, but they do not resolve environment variables, execute `!command`, or reveal secret values.
- Runtime request dispatch still resolves environment variables and `!command` values immediately before calling the provider.
- `models.json` is not a credential write-back store. OAuth login and refresh credentials are stored in `auth.json`.

## Credential Status Rules

`ProviderAuthResolver.GetStatus(...)` reports `models.json` as configured when the selected provider/model has:

- a non-empty `apiKey`, or
- a non-empty credential-like header such as `Authorization`, `api-key`, `x-api-key`, `x-goog-api-key`, or a header containing `token`.

Non-credential headers such as tracing or routing headers do not make auth status configured by themselves.
