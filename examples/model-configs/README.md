# Tau model configuration examples

This directory contains copy-ready `models.json` examples for the protocols supported by Tau. The files contain placeholder values and environment variable names only. They do not contain real credentials.

## Usage

Choose one file and point Tau at it with `TAU_MODELS_FILE`:

```powershell
$env:TAU_MODELS_FILE = (Resolve-Path .\examples\model-configs\openai-responses.json).Path
$env:OPENAI_API_KEY = "your OpenAI API key"

dotnet run --project .\src\Tau.CodingAgent\Tau.CodingAgent.csproj -- `
  --provider openai-responses-demo `
  --model gpt-5.4 `
  -p "Reply with: Tau model connection test succeeded"
```

To enter the interactive terminal, omit `-p`:

```powershell
dotnet run --project .\src\Tau.CodingAgent\Tau.CodingAgent.csproj -- `
  --provider openai-responses-demo `
  --model gpt-5.4
```

The `apiKey` value is an environment variable name. For example:

```json
"apiKey": "OPENAI_API_KEY"
```

Tau reads the value of `OPENAI_API_KEY` at request time. Do not commit real credentials to Git. You can also use `%USERPROFILE%\\.tau\\auth.json` or pass a temporary key with `--api-key`.

## Files

| File | Protocol | Request path | Credential |
| --- | --- | --- | --- |
| `openai-chat-completions.json` | OpenAI Chat Completions | `<baseUrl>/chat/completions` | `OPENAI_API_KEY` |
| `openai-responses.json` | OpenAI Responses | `<baseUrl>/responses` | `OPENAI_API_KEY` |
| `anthropic-messages.json` | Anthropic Messages | `<baseUrl>/v1/messages` | `ANTHROPIC_API_KEY` |
| `google-generative-language.json` | Google Generative Language | `<baseUrl>/v1beta/models/{id}:streamGenerateContent?alt=sse` | `GEMINI_API_KEY` |
| `azure-openai-responses.json` | Azure OpenAI Responses | `<baseUrl>/responses?api-version=v1` | `AZURE_OPENAI_API_KEY` |
| `local-ollama-openai-compatible.json` | OpenAI-compatible Chat Completions | `<baseUrl>/chat/completions` | Placeholder `ollama` |

## Custom gateways

For a gateway that implements OpenAI Chat Completions, use:

```json
{
  "api": "openai-chat-completions",
  "baseUrl": "https://gateway.example.com/v1"
}
```

For a gateway that implements OpenAI Responses, use:

```json
{
  "api": "openai-responses",
  "baseUrl": "https://gateway.example.com/v1"
}
```

The `baseUrl` must stop before `/chat/completions` or `/responses`; Tau appends the protocol-specific path. The gateway must implement the matching request body and streaming SSE event format. A server that returns Chat Completions events cannot be configured as a Responses endpoint.

## Google URL rule

The current Tau Google provider appends `/v1beta/models/{id}:streamGenerateContent?alt=sse`. Therefore the example uses `https://generativelanguage.googleapis.com` without `/v1beta`. A proxy `baseUrl` must accept this complete appended path.

## Azure URL rule

The Azure example uses the Azure OpenAI Responses `/openai/v1` root URL. Replace `YOUR_RESOURCE`, `YOUR_DEPLOYMENT_NAME`, and `AZURE_OPENAI_API_KEY`. The Azure deployment name is normally different from the model catalog ID.
