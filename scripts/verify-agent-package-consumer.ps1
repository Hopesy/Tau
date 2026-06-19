param(
    [switch]$SkipRestore,
    [switch]$Json
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
Set-Location $repoRoot

$env:DOTNET_CLI_HOME = if ($env:DOTNET_CLI_HOME) { $env:DOTNET_CLI_HOME } else { Join-Path $repoRoot '.dotnet' }
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = if ($env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE) { $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE } else { '1' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = if ($env:DOTNET_CLI_TELEMETRY_OPTOUT) { $env:DOTNET_CLI_TELEMETRY_OPTOUT } else { '1' }
$env:DOTNET_NOLOGO = if ($env:DOTNET_NOLOGO) { $env:DOTNET_NOLOGO } else { '1' }

New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME | Out-Null

$script:assertions = @()
$script:results = [ordered]@{}

function Add-Assertion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $true)]
        [bool]$Passed,
        [Parameter(Mandatory = $true)]
        [string]$Detail
    )

    $script:assertions += [ordered]@{
        name = $Name
        passed = $Passed
        detail = $Detail
    }

    if (-not $Passed) {
        throw $Detail
    }
}

function Assert-Equal {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [AllowNull()]
        [object]$Actual,
        [AllowNull()]
        [object]$Expected
    )

    Add-Assertion -Name $Name -Passed ([object]::Equals($Actual, $Expected)) -Detail "Expected '$Expected', actual '$Actual'."
}

function Assert-Matches {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [AllowNull()]
        [string]$Text,
        [Parameter(Mandatory = $true)]
        [string]$Pattern
    )

    Add-Assertion -Name $Name -Passed ($Text -match $Pattern) -Detail "Expected text to match '$Pattern'."
}

function Assert-NotMatches {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [AllowNull()]
        [string]$Text,
        [Parameter(Mandatory = $true)]
        [string]$Pattern
    )

    Add-Assertion -Name $Name -Passed (-not ($Text -match $Pattern)) -Detail "Expected text not to match '$Pattern'."
}

function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [string[]]$Arguments = @()
    )

    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $FilePath @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    $text = ($output -join [Environment]::NewLine)
    if ($exitCode -ne 0) {
        throw "$FilePath $($Arguments -join ' ') failed with exit code $exitCode. Output: $text"
    }

    return [ordered]@{
        exitCode = $exitCode
        output = $text
    }
}

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Text
    )

    [System.IO.File]::WriteAllText($Path, $Text, [System.Text.UTF8Encoding]::new($false))
}

function Get-TauVersion {
    [xml]$props = Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Build.props') -Raw
    $node = $props.SelectSingleNode("//*[local-name()='VersionPrefix']")
    if ($null -eq $node -or [string]::IsNullOrWhiteSpace($node.InnerText)) {
        throw 'Directory.Build.props does not define VersionPrefix.'
    }

    return $node.InnerText.Trim()
}

function Get-NuspecXml {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PackagePath
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $entry = @($zip.Entries | Where-Object { $_.FullName.EndsWith('.nuspec', [StringComparison]::OrdinalIgnoreCase) })[0]
        if ($null -eq $entry) {
            throw "Package $PackagePath does not contain a nuspec file."
        }

        $stream = $entry.Open()
        try {
            $reader = [System.IO.StreamReader]::new($stream)
            try {
                return [xml]$reader.ReadToEnd()
            }
            finally {
                $reader.Dispose()
            }
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $zip.Dispose()
    }
}

function Test-PackageContainsEntry {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PackagePath,
        [Parameter(Mandatory = $true)]
        [string]$EntryName
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        return [bool]@($zip.Entries | Where-Object { $_.FullName -eq $EntryName }).Count
    }
    finally {
        $zip.Dispose()
    }
}

function Get-NuspecText {
    param(
        [Parameter(Mandatory = $true)]
        [xml]$Nuspec,
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $node = $Nuspec.SelectSingleNode("//*[local-name()='$Name']")
    if ($null -eq $node) {
        return ''
    }

    return $node.InnerText.Trim()
}

function Write-AiConsumerProject {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ConsumerDir,
        [Parameter(Mandatory = $true)]
        [string]$PackagesDir,
        [Parameter(Mandatory = $true)]
        [string]$Version
    )

    $escapedPackagesDir = [System.Security.SecurityElement]::Escape($PackagesDir)
    Write-Utf8NoBom -Path (Join-Path $ConsumerDir 'NuGet.config') -Text @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="tau-local" value="$escapedPackagesDir" />
  </packageSources>
</configuration>
"@

    Write-Utf8NoBom -Path (Join-Path $ConsumerDir 'Consumer.csproj') -Text @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Tau.Ai" Version="$Version" />
  </ItemGroup>
</Project>
"@

    Write-Utf8NoBom -Path (Join-Path $ConsumerDir 'Program.cs') -Text @'
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Tau.Ai;
using Tau.Ai.Auth;
using Tau.Ai.Auth.OAuth;
using Tau.Ai.Providers;
using Tau.Ai.Providers.Anthropic;
using Tau.Ai.Providers.Faux;
using Tau.Ai.Providers.Google;
using Tau.Ai.Providers.Mistral;
using Tau.Ai.Providers.OpenAiResponses;
using Tau.Ai.Registry;
using Tau.Ai.Streaming;

var registry = new ProviderRegistry();
var faux = Faux.Register(registry);
faux.SetResponses(new FauxResponseStep[]
{
    Faux.AssistantMessage("ai package complete")
});

var result = await StreamFunctions.CompleteAsync(
        registry,
        faux.GetModel(),
        new LlmContext(null, new ChatMessage[] { new UserMessage("run ai package") }, null),
        new StreamOptions())
    .WaitAsync(TimeSpan.FromSeconds(10));

Console.WriteLine($"assistant={ReadText(result.Content)}");
Console.WriteLine($"provider={result.Provider}");
Console.WriteLine($"model={result.Model}");
Console.WriteLine($"calls={faux.State.CallCount}");

var aliasProvider = new AliasProvider("google-generative-language");
registry.Register("google-generative-language", () => aliasProvider, sourceId: "consumer-alias");
var aliasModel = new Model
{
    Id = "consumer-alias-model",
    Name = "Consumer Alias Model",
    Provider = "consumer-alias-provider",
    Api = "google-generative-ai"
};
var aliasResult = await StreamFunctions.CompleteSimpleAsync(
        registry,
        aliasModel,
        new LlmContext(null, new ChatMessage[] { new UserMessage("run api alias package consumer") }, null),
        new SimpleStreamOptions())
    .WaitAsync(TimeSpan.FromSeconds(10));
Console.WriteLine($"aliasAssistant={ReadText(aliasResult.Content)}");
Console.WriteLine($"aliasRequestedApi={aliasResult.Api}");
Console.WriteLine($"aliasRegisteredApi={aliasProvider.Api}");

var configRoot = Path.Combine(Environment.CurrentDirectory, ".tau");
Directory.CreateDirectory(configRoot);
var authPath = Path.Combine(configRoot, "auth.json");
var modelsPath = Path.Combine(configRoot, "models.json");
await File.WriteAllTextAsync(authPath, "{}");
await File.WriteAllTextAsync(modelsPath, """
{
  "providers": {
    "consumer-config-provider": {
      "api": "consumer-config-api",
      "apiKind": "openai-compatible",
      "baseUrl": "https://consumer.example.test/v1",
      "apiKey": "TAU_CONSUMER_DYNAMIC_API_KEY",
      "authHeader": true,
      "headers": {
        "X-Provider": "TAU_CONSUMER_PROVIDER_HEADER"
      },
      "options": {
        "temperature": 0.2,
        "maxTokens": 111,
        "topP": 0.7,
        "transport": "websocket",
        "cacheRetention": "long",
        "sessionId": "provider-session",
        "maxRetryDelayMs": 2500,
        "reasoning": "high",
        "thinkingBudgets": {
          "minimal": 100,
          "low": 200,
          "medium": 300,
          "high": 400
        },
        "metadata": {
          "scope": "provider",
          "shared": "provider"
        },
        "serviceTier": "flex",
        "reasoningEffort": "low",
        "reasoningSummary": "concise",
        "textVerbosity": "high",
        "azureApiVersion": "2026-01-01-preview",
        "azureResourceName": "configured-resource",
        "azureBaseUrl": "https://configured.openai.azure.com/openai/v1",
        "azureDeploymentName": "configured-deployment"
      },
      "modelOverrides": {
        "consumer-dynamic-model": {
          "options": {
            "temperature": 0.3,
            "metadata": {
              "shared": "override"
            }
          }
        }
      },
      "models": [
        {
          "id": "consumer-dynamic-model",
          "name": "Consumer Dynamic Model",
          "headers": {
            "X-Model": "TAU_CONSUMER_MODEL_HEADER"
          },
          "options": {
            "topP": 0.6,
            "sessionId": "model-session",
            "metadata": {
              "shared": "model",
              "modelOnly": 7
            }
          }
        }
      ]
    },
    "consumer-mistral-provider": {
      "apiKey": "mistral-config-key",
      "options": {
        "maxTokens": 321,
        "toolChoice": {
          "type": "function",
          "function": { "name": "consumer_tool" }
        },
        "promptMode": "reasoning",
        "reasoningEffort": "high"
      },
      "models": [
        {
          "id": "mistral-small-latest",
          "name": "Consumer Mistral Model"
        }
      ]
    },
    "consumer-anthropic-provider": {
      "apiKey": "anthropic-config-key",
      "options": {
        "maxTokens": 654,
        "thinkingEnabled": true,
        "thinkingBudgetTokens": 2345,
        "effort": "high",
        "thinkingDisplay": "omitted",
        "interleavedThinking": true,
        "toolChoice": {
          "type": "tool",
          "name": "read_file"
        }
      },
      "models": [
        {
          "id": "claude-sonnet-4-5-20250929",
          "name": "Consumer Anthropic Model"
        }
      ]
    },
    "consumer-google-provider": {
      "apiKey": "google-config-key",
      "options": {
        "maxTokens": 765,
        "toolChoice": "any",
        "thinkingEnabled": true,
        "thinkingBudgetTokens": 8765
      },
      "models": [
        {
          "id": "gemini-2.5-flash",
          "name": "Consumer Google Model"
        }
      ]
    }
  }
}
""");

Environment.SetEnvironmentVariable("TAU_MODELS_FILE", modelsPath);
Environment.SetEnvironmentVariable("TAU_AUTH_FILE", authPath);
Environment.SetEnvironmentVariable("TAU_CONSUMER_DYNAMIC_API_KEY", "consumer-dynamic-key");
Environment.SetEnvironmentVariable("TAU_CONSUMER_PROVIDER_HEADER", "provider-header-value");
Environment.SetEnvironmentVariable("TAU_CONSUMER_MODEL_HEADER", "model-header-value");

var configurationStore = new ModelConfigurationStore([modelsPath]);
var authResolver = new ProviderAuthResolver(
    credentialStore: new OAuthCredentialStore([authPath]),
    configurationStore: configurationStore);
var configuredCatalog = new ModelCatalog(authResolver: authResolver, configurationStore: configurationStore);
var configuredModel = configuredCatalog.GetModel("consumer-config-provider", "consumer-dynamic-model");
var configuredProviderStatus = authResolver.GetStatus("consumer-config-provider");
var configuredStatus = authResolver.GetStatus(configuredModel);
var configuredClientHandler = new CapturingHandler();
using var configuredClient = new HttpClient(configuredClientHandler);
BuiltInProviders.RegisterConfiguredProviders(registry, configurationStore, configuredClient);
var optionsProvider = new OptionsCapturingProvider();
registry.Register("consumer-config-options-api", () => optionsProvider, sourceId: "consumer-options");

var configuredResult = await StreamFunctions.CompleteSimpleAsync(
        registry,
        configuredModel,
        new LlmContext(null, new ChatMessage[] { new UserMessage("run configured package consumer") }, null),
        new SimpleStreamOptions
        {
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["X-Explicit"] = "explicit-header",
                ["X-Provider"] = "explicit-provider-header"
            }
        },
        configurationStore,
        authResolver)
    .WaitAsync(TimeSpan.FromSeconds(10));

var optionsModel = configuredModel with { Api = "consumer-config-options-api" };
await StreamFunctions.CompleteSimpleAsync(
        registry,
        optionsModel,
        new LlmContext(null, new ChatMessage[] { new UserMessage("run configured options package consumer") }, null),
        new SimpleStreamOptions(),
        configurationStore,
        authResolver)
    .WaitAsync(TimeSpan.FromSeconds(10));
var responsesProvider = new ResponsesOptionsCapturingProvider();
registry.Register("openai-responses", () => responsesProvider, sourceId: "consumer-responses-options");
var responsesModel = new Model
{
    Id = "consumer-responses-model",
    Name = "Consumer Responses Model",
    Provider = "consumer-config-provider",
    Api = "openai-responses",
    Reasoning = true,
    MaxOutputTokens = 123
};
await StreamFunctions.CompleteSimpleAsync(
        registry,
        responsesModel,
        new LlmContext(null, new ChatMessage[] { new UserMessage("run configured responses options package consumer") }, null),
        new SimpleStreamOptions(),
        configurationStore,
        authResolver)
    .WaitAsync(TimeSpan.FromSeconds(10));
var mistralProvider = new MistralOptionsCapturingProvider();
registry.Register("mistral-conversations", () => mistralProvider, sourceId: "consumer-mistral-options");
var mistralModel = new Model
{
    Id = "mistral-small-latest",
    Name = "Consumer Mistral Model",
    Provider = "consumer-mistral-provider",
    Api = "mistral-conversations",
    Reasoning = true,
    MaxOutputTokens = 321
};
await StreamFunctions.CompleteSimpleAsync(
        registry,
        mistralModel,
        new LlmContext(null, new ChatMessage[] { new UserMessage("run configured mistral options package consumer") }, null),
        new SimpleStreamOptions(),
        configurationStore,
        authResolver)
    .WaitAsync(TimeSpan.FromSeconds(10));
var anthropicProvider = new AnthropicOptionsCapturingProvider();
registry.Register("anthropic-messages", () => anthropicProvider, sourceId: "consumer-anthropic-options");
var anthropicModel = new Model
{
    Id = "claude-sonnet-4-5-20250929",
    Name = "Consumer Anthropic Model",
    Provider = "consumer-anthropic-provider",
    Api = "anthropic-messages",
    Reasoning = true,
    MaxOutputTokens = 654
};
await StreamFunctions.CompleteSimpleAsync(
        registry,
        anthropicModel,
        new LlmContext(null, new ChatMessage[] { new UserMessage("run configured anthropic options package consumer") }, null),
        new SimpleStreamOptions(),
        configurationStore,
        authResolver)
    .WaitAsync(TimeSpan.FromSeconds(10));
var googleProvider = new GoogleOptionsCapturingProvider();
registry.Register("google-generative-language", () => googleProvider, sourceId: "consumer-google-options");
var googleModel = new Model
{
    Id = "gemini-2.5-flash",
    Name = "Consumer Google Model",
    Provider = "consumer-google-provider",
    Api = "google-generative-language",
    Reasoning = true,
    MaxOutputTokens = 765
};
await StreamFunctions.CompleteSimpleAsync(
        registry,
        googleModel,
        new LlmContext(null, new ChatMessage[] { new UserMessage("run configured google options package consumer") }, null),
        new SimpleStreamOptions(),
        configurationStore,
        authResolver)
    .WaitAsync(TimeSpan.FromSeconds(10));

Console.WriteLine($"configuredStatus={configuredStatus.Source}:{configuredStatus.IsConfigured}");
Console.WriteLine($"configuredProviderStatus={configuredProviderStatus.Source}:{configuredProviderStatus.IsConfigured}");
Console.WriteLine($"configuredAssistant={ReadText(configuredResult.Content)}");
Console.WriteLine($"configuredApi={configuredResult.Api}");
Console.WriteLine($"configuredHost={configuredClientHandler.RequestUri?.Host}");
Console.WriteLine($"configuredAuth={configuredClientHandler.AuthorizationScheme} {configuredClientHandler.AuthorizationParameter}");
Console.WriteLine($"configuredProviderHeader={configuredClientHandler.GetHeader("X-Provider")}");
Console.WriteLine($"configuredModelHeader={configuredClientHandler.GetHeader("X-Model")}");
Console.WriteLine($"configuredExplicitHeader={configuredClientHandler.GetHeader("X-Explicit")}");
Console.WriteLine($"configuredPath={configuredClientHandler.RequestUri?.AbsolutePath}");
var capturedOptions = optionsProvider.CapturedOptions!;
Console.WriteLine($"configuredOptionsTemperature={capturedOptions.Temperature:0.0}");
Console.WriteLine($"configuredOptionsMaxTokens={capturedOptions.MaxTokens}");
Console.WriteLine($"configuredOptionsTopP={capturedOptions.TopP:0.0}");
Console.WriteLine($"configuredOptionsTransport={capturedOptions.Transport}");
Console.WriteLine($"configuredOptionsCacheRetention={capturedOptions.CacheRetention}");
Console.WriteLine($"configuredOptionsSessionId={capturedOptions.SessionId}");
Console.WriteLine($"configuredOptionsMaxRetryDelayMs={capturedOptions.MaxRetryDelay?.TotalMilliseconds:0}");
Console.WriteLine($"configuredOptionsReasoning={capturedOptions.Reasoning}");
Console.WriteLine($"configuredOptionsThinkingHigh={capturedOptions.ThinkingBudgets?.High}");
Console.WriteLine($"configuredOptionsMetadataShared={ReadMetadata(capturedOptions.Metadata, "shared")}");
Console.WriteLine($"configuredOptionsMetadataModelOnly={ReadMetadata(capturedOptions.Metadata, "modelOnly")}");
var capturedResponsesOptions = responsesProvider.CapturedOptions!;
Console.WriteLine($"configuredResponsesOptionsType={capturedResponsesOptions.GetType().Name}");
Console.WriteLine($"configuredResponsesOptionsServiceTier={capturedResponsesOptions.ServiceTier}");
Console.WriteLine($"configuredResponsesOptionsReasoningEffort={capturedResponsesOptions.ReasoningEffort}");
Console.WriteLine($"configuredResponsesOptionsReasoningSummary={capturedResponsesOptions.ReasoningSummary}");
Console.WriteLine($"configuredResponsesOptionsMaxTokens={capturedResponsesOptions.MaxTokens}");
var capturedMistralOptions = mistralProvider.CapturedOptions!;
Console.WriteLine($"configuredMistralOptionsType={capturedMistralOptions.GetType().Name}");
Console.WriteLine($"configuredMistralOptionsToolChoice={capturedMistralOptions.ToolChoice}");
Console.WriteLine($"configuredMistralOptionsToolChoiceKind={capturedMistralOptions.ToolChoice?.Kind}");
Console.WriteLine($"configuredMistralOptionsToolChoiceFunction={capturedMistralOptions.ToolChoice?.FunctionName}");
Console.WriteLine($"configuredMistralOptionsPromptMode={capturedMistralOptions.PromptMode}");
Console.WriteLine($"configuredMistralOptionsReasoningEffort={capturedMistralOptions.ReasoningEffort}");
Console.WriteLine($"configuredMistralOptionsMaxTokens={capturedMistralOptions.MaxTokens}");
var capturedAnthropicOptions = anthropicProvider.CapturedOptions!;
Console.WriteLine($"configuredAnthropicOptionsType={capturedAnthropicOptions.GetType().Name}");
Console.WriteLine($"configuredAnthropicOptionsThinkingEnabled={capturedAnthropicOptions.ThinkingEnabled}");
Console.WriteLine($"configuredAnthropicOptionsThinkingBudgetTokens={capturedAnthropicOptions.ThinkingBudgetTokens}");
Console.WriteLine($"configuredAnthropicOptionsEffort={capturedAnthropicOptions.Effort}");
Console.WriteLine($"configuredAnthropicOptionsThinkingDisplay={capturedAnthropicOptions.ThinkingDisplay}");
Console.WriteLine($"configuredAnthropicOptionsInterleavedThinking={capturedAnthropicOptions.InterleavedThinking}");
Console.WriteLine($"configuredAnthropicOptionsToolChoice={capturedAnthropicOptions.ToolChoice}");
Console.WriteLine($"configuredAnthropicOptionsToolChoiceKind={capturedAnthropicOptions.ToolChoice?.Kind}");
Console.WriteLine($"configuredAnthropicOptionsToolChoiceName={capturedAnthropicOptions.ToolChoice?.Name}");
Console.WriteLine($"configuredAnthropicOptionsMaxTokens={capturedAnthropicOptions.MaxTokens}");
var capturedGoogleOptions = googleProvider.CapturedOptions!;
Console.WriteLine($"configuredGoogleOptionsType={capturedGoogleOptions.GetType().Name}");
Console.WriteLine($"configuredGoogleOptionsToolChoice={capturedGoogleOptions.ToolChoice}");
Console.WriteLine($"configuredGoogleOptionsThinkingEnabled={capturedGoogleOptions.Thinking?.Enabled}");
Console.WriteLine($"configuredGoogleOptionsThinkingBudgetTokens={capturedGoogleOptions.Thinking?.BudgetTokens}");
Console.WriteLine($"configuredGoogleOptionsMaxTokens={capturedGoogleOptions.MaxTokens}");

static string ReadText(IReadOnlyList<ContentBlock> content) =>
    string.Join("\n", content.OfType<TextContent>().Select(static block => block.Text));

static string? ReadMetadata(IDictionary<string, object>? metadata, string key)
{
    if (metadata is null || !metadata.TryGetValue(key, out var value))
    {
        return null;
    }

    return value is JsonElement element
        ? element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => element.GetRawText()
        }
        : Convert.ToString(value);
}

file sealed class CapturingHandler : HttpMessageHandler
{
    public Uri? RequestUri { get; private set; }
    public string? AuthorizationScheme { get; private set; }
    public string? AuthorizationParameter { get; private set; }
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestUri = request.RequestUri;
        AuthorizationScheme = request.Headers.Authorization?.Scheme;
        AuthorizationParameter = request.Headers.Authorization?.Parameter;

        foreach (var header in request.Headers)
        {
            Headers[header.Key] = string.Join(",", header.Value);
        }

        var body = """
data: {"choices":[{"delta":{"content":"configured package consumer complete"},"finish_reason":null}]}

data: {"choices":[{"delta":{},"finish_reason":"stop"}]}

data: [DONE]

""";
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
        };
        return Task.FromResult(response);
    }

    public string? GetHeader(string name) =>
        Headers.TryGetValue(name, out var value) ? value : null;
}

file sealed class AliasProvider(string api) : IStreamProvider
{
    public string Api { get; } = api;

    public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options) =>
        Complete(model);

    public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options) =>
        Complete(model);

    private static AssistantMessageStream Complete(Model model)
    {
        var stream = new AssistantMessageStream();
        stream.Push(new DoneEvent(new AssistantMessage([new TextContent("alias package consumer complete")])
        {
            Api = model.Api,
            Provider = model.Provider,
            Model = model.Id,
            StopReason = StopReason.EndTurn
        }));
        return stream;
    }
}

file sealed class OptionsCapturingProvider : IStreamProvider
{
    public string Api => "consumer-config-options-api";

    public SimpleStreamOptions? CapturedOptions { get; private set; }

    public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options)
    {
        var stream = new AssistantMessageStream();
        stream.Push(new DoneEvent(new AssistantMessage([new TextContent("configured options package consumer complete")])
        {
            Api = model.Api,
            Provider = model.Provider,
            Model = model.Id,
            StopReason = StopReason.EndTurn
        }));
        return stream;
    }

    public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options)
    {
        CapturedOptions = options;
        return Stream(model, context, options);
    }
}

file sealed class ResponsesOptionsCapturingProvider : IStreamProvider
{
    public string Api => "openai-responses";

    public OpenAiResponsesOptions? CapturedOptions { get; private set; }

    public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options)
    {
        CapturedOptions = (OpenAiResponsesOptions)options;
        var stream = new AssistantMessageStream();
        stream.Push(new DoneEvent(new AssistantMessage([new TextContent("configured responses options package consumer complete")])
        {
            Api = model.Api,
            Provider = model.Provider,
            Model = model.Id,
            StopReason = StopReason.EndTurn
        }));
        return stream;
    }

    public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options) =>
        throw new InvalidOperationException("Provider-specific models.json options must dispatch through Stream with typed options.");
}

file sealed class MistralOptionsCapturingProvider : IStreamProvider
{
    public string Api => "mistral-conversations";

    public MistralOptions? CapturedOptions { get; private set; }

    public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options)
    {
        CapturedOptions = (MistralOptions)options;
        var stream = new AssistantMessageStream();
        stream.Push(new DoneEvent(new AssistantMessage([new TextContent("configured mistral options package consumer complete")])
        {
            Api = model.Api,
            Provider = model.Provider,
            Model = model.Id,
            StopReason = StopReason.EndTurn
        }));
        return stream;
    }

    public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options) =>
        throw new InvalidOperationException("Provider-specific models.json options must dispatch through Stream with typed options.");
}

file sealed class AnthropicOptionsCapturingProvider : IStreamProvider
{
    public string Api => "anthropic-messages";

    public AnthropicOptions? CapturedOptions { get; private set; }

    public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options)
    {
        CapturedOptions = (AnthropicOptions)options;
        var stream = new AssistantMessageStream();
        stream.Push(new DoneEvent(new AssistantMessage([new TextContent("configured anthropic options package consumer complete")])
        {
            Api = model.Api,
            Provider = model.Provider,
            Model = model.Id,
            StopReason = StopReason.EndTurn
        }));
        return stream;
    }

    public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options) =>
        throw new InvalidOperationException("Provider-specific models.json options must dispatch through Stream with typed options.");
}

file sealed class GoogleOptionsCapturingProvider : IStreamProvider
{
    public string Api => "google-generative-language";

    public GoogleOptions? CapturedOptions { get; private set; }

    public AssistantMessageStream Stream(Model model, LlmContext context, StreamOptions options)
    {
        CapturedOptions = (GoogleOptions)options;
        var stream = new AssistantMessageStream();
        stream.Push(new DoneEvent(new AssistantMessage([new TextContent("configured google options package consumer complete")])
        {
            Api = model.Api,
            Provider = model.Provider,
            Model = model.Id,
            StopReason = StopReason.EndTurn
        }));
        return stream;
    }

    public AssistantMessageStream StreamSimple(Model model, LlmContext context, SimpleStreamOptions options) =>
        throw new InvalidOperationException("Provider-specific models.json options must dispatch through Stream with typed options.");
}
'@
}

function Write-AgentConsumerProject {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ConsumerDir,
        [Parameter(Mandatory = $true)]
        [string]$PackagesDir,
        [Parameter(Mandatory = $true)]
        [string]$Version
    )

    $escapedPackagesDir = [System.Security.SecurityElement]::Escape($PackagesDir)
    Write-Utf8NoBom -Path (Join-Path $ConsumerDir 'NuGet.config') -Text @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="tau-local" value="$escapedPackagesDir" />
  </packageSources>
</configuration>
"@

    Write-Utf8NoBom -Path (Join-Path $ConsumerDir 'Consumer.csproj') -Text @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Tau.Agent" Version="$Version" />
  </ItemGroup>
</Project>
"@

    Write-Utf8NoBom -Path (Join-Path $ConsumerDir 'Program.cs') -Text @'
using System.Text.Json;
using Tau.Agent;
using Tau.Agent.Platform;
using Tau.Ai;
using Tau.Ai.Observability;
using Tau.Ai.Providers;
using Tau.Ai.Providers.Faux;

var registry = new ProviderRegistry();
var faux = Faux.Register(registry);
faux.SetResponses(new FauxResponseStep[]
{
    Faux.AssistantMessage(
        new ContentBlock[] { Faux.ToolCall("echo", """{"text":"package consumer"}""", "pkg-call") },
        stopReason: StopReason.ToolUse),
    Faux.AssistantMessage("package consumer complete")
});

var sessions = new InMemoryAgentSessionStore();
var sink = new CapturingTauLogSink();
using var schema = JsonDocument.Parse("""
{
  "type": "object",
  "properties": {
    "text": { "type": "string" }
  },
  "required": ["text"]
}
""");

var app = AgentApplication.CreateBuilder()
    .UseProviderRegistry(registry)
    .UseModel(faux.GetModel())
    .UseSystemPrompt("You are running from a package consumer.")
    .UseSessionId("consumer-session")
    .UseSessionStore(sessions)
    .UseLogSink(sink)
    .UseLogContext(new TauRuntimeLogContext("consumer-correlation"))
    .AddMetadata("source", "package-consumer")
    .AddTool(
        "echo",
        "Echo",
        "Echoes text.",
        schema.RootElement.Clone(),
        (context, _) => new ToolResult(new ContentBlock[]
        {
            new TextContent(context.Arguments.GetProperty("text").GetString() ?? string.Empty)
        }),
        prepareArguments: (rawArgs, _) => new ValueTask<JsonElement>(PreparePackageConsumerArgs(rawArgs)))
    .Build();

var result = await app.PromptAsync("run from package consumer").WaitAsync(TimeSpan.FromSeconds(10));
var snapshot = sessions.Load("consumer-session");
var providerRunStarts = sink.Events.Count(static evt => evt.Category == "provider" && evt.Event == "run.start");
var toolResult = result.Messages.OfType<ToolResultMessage>().Single();

Console.WriteLine($"assistant={result.AssistantText}");
Console.WriteLine($"saved={result.SavedSession}");
Console.WriteLine($"messages={result.Messages.Count}");
Console.WriteLine($"toolResult={ReadText(toolResult.Content)}");
Console.WriteLine($"toolStarts={result.ToolStarts.Count}");
Console.WriteLine($"toolEnds={result.ToolEnds.Count}");
Console.WriteLine($"providerRuns={providerRunStarts}");
Console.WriteLine($"logSession={result.LogContext.SessionId}");
Console.WriteLine($"snapshot={snapshot?.SessionId}");
Console.WriteLine($"fauxCalls={faux.State.CallCount}");

static JsonElement PreparePackageConsumerArgs(JsonElement rawArgs)
{
    if (!rawArgs.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
    {
        return rawArgs;
    }

    var prepared = JsonSerializer.SerializeToElement(new Dictionary<string, string>
    {
        ["text"] = $"prepared {text.GetString()}"
    });

    return prepared;
}

static string ReadText(IReadOnlyList<ContentBlock> content) =>
    string.Join("\n", content.OfType<TextContent>().Select(static block => block.Text));

public sealed class CapturingTauLogSink : ITauLogSink
{
    public List<TauLogEvent> Events { get; } = new();

    public void Log(TauLogEvent evt) => Events.Add(evt);
}
'@
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("tau-agent-package-consumer-" + [Guid]::NewGuid().ToString('N'))

try {
    $version = Get-TauVersion
    $packagesDir = Join-Path $tempRoot 'packages'
    $aiConsumerDir = Join-Path $tempRoot 'ai-consumer'
    $agentConsumerDir = Join-Path $tempRoot 'agent-consumer'
    $aiConsumerPackageCache = Join-Path $tempRoot 'ai-nuget-cache'
    $agentConsumerPackageCache = Join-Path $tempRoot 'agent-nuget-cache'
    New-Item -ItemType Directory -Force -Path $packagesDir, $aiConsumerDir, $agentConsumerDir, $aiConsumerPackageCache, $agentConsumerPackageCache | Out-Null

    $packArgs = @('pack', 'src/Tau.Ai/Tau.Ai.csproj', '--configuration', 'Release', '--output', $packagesDir, '--verbosity', 'minimal')
    if ($SkipRestore) {
        $packArgs += '--no-restore'
    }
    $packAi = Invoke-Native -FilePath 'dotnet' -Arguments $packArgs

    $packArgs = @('pack', 'src/Tau.Agent/Tau.Agent.csproj', '--configuration', 'Release', '--output', $packagesDir, '--verbosity', 'minimal')
    if ($SkipRestore) {
        $packArgs += '--no-restore'
    }
    $packAgent = Invoke-Native -FilePath 'dotnet' -Arguments $packArgs

    $aiPackage = Join-Path $packagesDir "Tau.Ai.$version.nupkg"
    $agentPackage = Join-Path $packagesDir "Tau.Agent.$version.nupkg"
    Add-Assertion -Name 'Tau.Ai package exists' -Passed (Test-Path -LiteralPath $aiPackage) -Detail "Expected package at $aiPackage."
    Add-Assertion -Name 'Tau.Agent package exists' -Passed (Test-Path -LiteralPath $agentPackage) -Detail "Expected package at $agentPackage."

    $aiNuspec = Get-NuspecXml -PackagePath $aiPackage
    $agentNuspec = Get-NuspecXml -PackagePath $agentPackage
    $agentDependencies = @($agentNuspec.SelectNodes("//*[local-name()='dependency']"))
    $agentTauAiDependency = @($agentDependencies | Where-Object { $_.id -eq 'Tau.Ai' -and $_.version -eq $version })

    Add-Assertion -Name 'Tau.Agent depends on Tau.Ai package' -Passed ($agentTauAiDependency.Count -eq 1) -Detail 'Tau.Agent nuspec does not declare the expected Tau.Ai dependency.'
    Add-Assertion -Name 'Tau.Ai package has README' -Passed (Test-PackageContainsEntry -PackagePath $aiPackage -EntryName 'README.md') -Detail 'Tau.Ai nupkg does not include README.md.'
    Add-Assertion -Name 'Tau.Agent package has README' -Passed (Test-PackageContainsEntry -PackagePath $agentPackage -EntryName 'README.md') -Detail 'Tau.Agent nupkg does not include README.md.'
    Assert-Matches -Name 'Tau.Ai package description' -Text (Get-NuspecText -Nuspec $aiNuspec -Name 'description') -Pattern 'AI provider.*foundation'
    Assert-Matches -Name 'Tau.Agent package description' -Text (Get-NuspecText -Nuspec $agentNuspec -Name 'description') -Pattern 'reusable agent runtime'
    Assert-NotMatches -Name 'Tau.Ai package is not placeholder' -Text (Get-NuspecText -Nuspec $aiNuspec -Name 'description') -Pattern '^Package Description$'
    Assert-NotMatches -Name 'Tau.Agent package is not placeholder' -Text (Get-NuspecText -Nuspec $agentNuspec -Name 'description') -Pattern '^Package Description$'

    Write-AiConsumerProject -ConsumerDir $aiConsumerDir -PackagesDir $packagesDir -Version $version
    $aiConsumerProject = Join-Path $aiConsumerDir 'Consumer.csproj'
    $aiNugetConfig = Join-Path $aiConsumerDir 'NuGet.config'
    $aiRestore = Invoke-Native -FilePath 'dotnet' -Arguments @(
        'restore',
        $aiConsumerProject,
        '--configfile',
        $aiNugetConfig,
        '--packages',
        $aiConsumerPackageCache,
        '--verbosity',
        'minimal'
    )
    $aiBuild = Invoke-Native -FilePath 'dotnet' -Arguments @('build', $aiConsumerProject, '--no-restore', '--verbosity', 'minimal')
    $aiRun = Invoke-Native -FilePath 'dotnet' -Arguments @('run', '--project', $aiConsumerProject, '--no-build', '--no-restore')
    $aiRunOutput = $aiRun.output

    Assert-Matches -Name 'ai consumer assistant output' -Text $aiRunOutput -Pattern 'assistant=ai package complete'
    Assert-Matches -Name 'ai consumer provider output' -Text $aiRunOutput -Pattern 'provider=faux'
    Assert-Matches -Name 'ai consumer model output' -Text $aiRunOutput -Pattern 'model=faux'
    Assert-Matches -Name 'ai consumer faux call count' -Text $aiRunOutput -Pattern 'calls=1'
    Assert-Matches -Name 'ai consumer api alias assistant output' -Text $aiRunOutput -Pattern 'aliasAssistant=alias package consumer complete'
    Assert-Matches -Name 'ai consumer api alias requested output' -Text $aiRunOutput -Pattern 'aliasRequestedApi=google-generative-ai'
    Assert-Matches -Name 'ai consumer api alias registered output' -Text $aiRunOutput -Pattern 'aliasRegisteredApi=google-generative-language'
    Assert-Matches -Name 'ai consumer configured status output' -Text $aiRunOutput -Pattern 'configuredStatus=models\.json:True'
    Assert-Matches -Name 'ai consumer configured provider status output' -Text $aiRunOutput -Pattern 'configuredProviderStatus=models\.json:True'
    Assert-Matches -Name 'ai consumer configured assistant output' -Text $aiRunOutput -Pattern 'configuredAssistant=configured package consumer complete'
    Assert-Matches -Name 'ai consumer configured api output' -Text $aiRunOutput -Pattern 'configuredApi=consumer-config-api'
    Assert-Matches -Name 'ai consumer configured host output' -Text $aiRunOutput -Pattern 'configuredHost=consumer\.example\.test'
    Assert-Matches -Name 'ai consumer configured auth output' -Text $aiRunOutput -Pattern 'configuredAuth=Bearer consumer-dynamic-key'
    Assert-Matches -Name 'ai consumer configured provider header output' -Text $aiRunOutput -Pattern 'configuredProviderHeader=explicit-provider-header'
    Assert-Matches -Name 'ai consumer configured model header output' -Text $aiRunOutput -Pattern 'configuredModelHeader=model-header-value'
    Assert-Matches -Name 'ai consumer configured explicit header output' -Text $aiRunOutput -Pattern 'configuredExplicitHeader=explicit-header'
    Assert-Matches -Name 'ai consumer configured path output' -Text $aiRunOutput -Pattern 'configuredPath=/v1/chat/completions'
    Assert-Matches -Name 'ai consumer configured options temperature output' -Text $aiRunOutput -Pattern 'configuredOptionsTemperature=0\.3'
    Assert-Matches -Name 'ai consumer configured options max tokens output' -Text $aiRunOutput -Pattern 'configuredOptionsMaxTokens=111'
    Assert-Matches -Name 'ai consumer configured options top-p output' -Text $aiRunOutput -Pattern 'configuredOptionsTopP=0\.6'
    Assert-Matches -Name 'ai consumer configured options transport output' -Text $aiRunOutput -Pattern 'configuredOptionsTransport=WebSocket'
    Assert-Matches -Name 'ai consumer configured options cache output' -Text $aiRunOutput -Pattern 'configuredOptionsCacheRetention=Long'
    Assert-Matches -Name 'ai consumer configured options session output' -Text $aiRunOutput -Pattern 'configuredOptionsSessionId=model-session'
    Assert-Matches -Name 'ai consumer configured options retry output' -Text $aiRunOutput -Pattern 'configuredOptionsMaxRetryDelayMs=2500'
    Assert-Matches -Name 'ai consumer configured options reasoning output' -Text $aiRunOutput -Pattern 'configuredOptionsReasoning=High'
    Assert-Matches -Name 'ai consumer configured options thinking output' -Text $aiRunOutput -Pattern 'configuredOptionsThinkingHigh=400'
    Assert-Matches -Name 'ai consumer configured options metadata shared output' -Text $aiRunOutput -Pattern 'configuredOptionsMetadataShared=model'
    Assert-Matches -Name 'ai consumer configured options metadata model output' -Text $aiRunOutput -Pattern 'configuredOptionsMetadataModelOnly=7'
    Assert-Matches -Name 'ai consumer configured responses options type output' -Text $aiRunOutput -Pattern 'configuredResponsesOptionsType=OpenAiResponsesOptions'
    Assert-Matches -Name 'ai consumer configured responses options service tier output' -Text $aiRunOutput -Pattern 'configuredResponsesOptionsServiceTier=flex'
    Assert-Matches -Name 'ai consumer configured responses options reasoning effort output' -Text $aiRunOutput -Pattern 'configuredResponsesOptionsReasoningEffort=low'
    Assert-Matches -Name 'ai consumer configured responses options reasoning summary output' -Text $aiRunOutput -Pattern 'configuredResponsesOptionsReasoningSummary=concise'
    Assert-Matches -Name 'ai consumer configured responses options max tokens output' -Text $aiRunOutput -Pattern 'configuredResponsesOptionsMaxTokens=111'
    Assert-Matches -Name 'ai consumer configured mistral options type output' -Text $aiRunOutput -Pattern 'configuredMistralOptionsType=MistralOptions'
    Assert-Matches -Name 'ai consumer configured mistral options tool choice output' -Text $aiRunOutput -Pattern 'configuredMistralOptionsToolChoice=function:consumer_tool'
    Assert-Matches -Name 'ai consumer configured mistral options tool choice kind output' -Text $aiRunOutput -Pattern 'configuredMistralOptionsToolChoiceKind=function'
    Assert-Matches -Name 'ai consumer configured mistral options tool choice function output' -Text $aiRunOutput -Pattern 'configuredMistralOptionsToolChoiceFunction=consumer_tool'
    Assert-Matches -Name 'ai consumer configured mistral options prompt mode output' -Text $aiRunOutput -Pattern 'configuredMistralOptionsPromptMode=reasoning'
    Assert-Matches -Name 'ai consumer configured mistral options reasoning effort output' -Text $aiRunOutput -Pattern 'configuredMistralOptionsReasoningEffort=high'
    Assert-Matches -Name 'ai consumer configured mistral options max tokens output' -Text $aiRunOutput -Pattern 'configuredMistralOptionsMaxTokens=321'
    Assert-Matches -Name 'ai consumer configured anthropic options type output' -Text $aiRunOutput -Pattern 'configuredAnthropicOptionsType=AnthropicOptions'
    Assert-Matches -Name 'ai consumer configured anthropic options thinking enabled output' -Text $aiRunOutput -Pattern 'configuredAnthropicOptionsThinkingEnabled=True'
    Assert-Matches -Name 'ai consumer configured anthropic options thinking budget output' -Text $aiRunOutput -Pattern 'configuredAnthropicOptionsThinkingBudgetTokens=2345'
    Assert-Matches -Name 'ai consumer configured anthropic options effort output' -Text $aiRunOutput -Pattern 'configuredAnthropicOptionsEffort=high'
    Assert-Matches -Name 'ai consumer configured anthropic options thinking display output' -Text $aiRunOutput -Pattern 'configuredAnthropicOptionsThinkingDisplay=omitted'
    Assert-Matches -Name 'ai consumer configured anthropic options interleaved thinking output' -Text $aiRunOutput -Pattern 'configuredAnthropicOptionsInterleavedThinking=True'
    Assert-Matches -Name 'ai consumer configured anthropic options tool choice output' -Text $aiRunOutput -Pattern 'configuredAnthropicOptionsToolChoice=tool:read_file'
    Assert-Matches -Name 'ai consumer configured anthropic options tool choice kind output' -Text $aiRunOutput -Pattern 'configuredAnthropicOptionsToolChoiceKind=tool'
    Assert-Matches -Name 'ai consumer configured anthropic options tool choice name output' -Text $aiRunOutput -Pattern 'configuredAnthropicOptionsToolChoiceName=read_file'
    Assert-Matches -Name 'ai consumer configured anthropic options max tokens output' -Text $aiRunOutput -Pattern 'configuredAnthropicOptionsMaxTokens=654'
    Assert-Matches -Name 'ai consumer configured google options type output' -Text $aiRunOutput -Pattern 'configuredGoogleOptionsType=GoogleOptions'
    Assert-Matches -Name 'ai consumer configured google options tool choice output' -Text $aiRunOutput -Pattern 'configuredGoogleOptionsToolChoice=any'
    Assert-Matches -Name 'ai consumer configured google options thinking enabled output' -Text $aiRunOutput -Pattern 'configuredGoogleOptionsThinkingEnabled=True'
    Assert-Matches -Name 'ai consumer configured google options thinking budget output' -Text $aiRunOutput -Pattern 'configuredGoogleOptionsThinkingBudgetTokens=8765'
    Assert-Matches -Name 'ai consumer configured google options max tokens output' -Text $aiRunOutput -Pattern 'configuredGoogleOptionsMaxTokens=765'

    Write-AgentConsumerProject -ConsumerDir $agentConsumerDir -PackagesDir $packagesDir -Version $version
    $consumerProject = Join-Path $agentConsumerDir 'Consumer.csproj'
    $nugetConfig = Join-Path $agentConsumerDir 'NuGet.config'

    $agentRestore = Invoke-Native -FilePath 'dotnet' -Arguments @(
        'restore',
        $consumerProject,
        '--configfile',
        $nugetConfig,
        '--packages',
        $agentConsumerPackageCache,
        '--verbosity',
        'minimal'
    )
    $agentBuild = Invoke-Native -FilePath 'dotnet' -Arguments @('build', $consumerProject, '--no-restore', '--verbosity', 'minimal')
    $agentRun = Invoke-Native -FilePath 'dotnet' -Arguments @('run', '--project', $consumerProject, '--no-build', '--no-restore')
    $runOutput = $agentRun.output

    Assert-Matches -Name 'consumer assistant output' -Text $runOutput -Pattern 'assistant=package consumer complete'
    Assert-Matches -Name 'consumer saved session' -Text $runOutput -Pattern 'saved=True'
    Assert-Matches -Name 'consumer message count' -Text $runOutput -Pattern 'messages=4'
    Assert-Matches -Name 'consumer prepared tool result' -Text $runOutput -Pattern 'prepared package consumer'
    Assert-Matches -Name 'consumer tool start count' -Text $runOutput -Pattern 'toolStarts=1'
    Assert-Matches -Name 'consumer tool end count' -Text $runOutput -Pattern 'toolEnds=1'
    Assert-Matches -Name 'consumer provider run count' -Text $runOutput -Pattern 'providerRuns=2'
    Assert-Matches -Name 'consumer log session' -Text $runOutput -Pattern 'logSession=consumer-session'
    Assert-Matches -Name 'consumer session snapshot' -Text $runOutput -Pattern 'snapshot=consumer-session'
    Assert-Matches -Name 'consumer faux call count' -Text $runOutput -Pattern 'fauxCalls=2'

    $script:results.pack = [ordered]@{
        aiExitCode = $packAi.exitCode
        agentExitCode = $packAgent.exitCode
        packageDirectory = $packagesDir
    }
    $script:results.aiConsumer = [ordered]@{
        restoreExitCode = $aiRestore.exitCode
        buildExitCode = $aiBuild.exitCode
        runExitCode = $aiRun.exitCode
        output = $aiRunOutput
    }
    $script:results.agentConsumer = [ordered]@{
        restoreExitCode = $agentRestore.exitCode
        buildExitCode = $agentBuild.exitCode
        runExitCode = $agentRun.exitCode
        output = $runOutput
    }
    $script:results.consumer = $script:results.agentConsumer

    $finalResult = [ordered]@{
        schemaVersion = 1
        succeeded = $true
        version = $version
        tempRoot = $tempRoot
        packages = @($aiPackage, $agentPackage)
        results = $script:results
        assertions = $script:assertions
    }

    if ($Json) {
        $finalResult | ConvertTo-Json -Depth 10
    }
    else {
        Write-Host 'Tau AI/Agent package consumer smoke passed'
        Write-Host "  version: $version"
        Write-Host "  assertions: $($script:assertions.Count)"
        Write-Host "  fixture: $tempRoot"
    }
}
catch {
    $finalResult = [ordered]@{
        schemaVersion = 1
        succeeded = $false
        tempRoot = $tempRoot
        results = $script:results
        assertions = $script:assertions
        error = $_.Exception.Message
    }

    if ($Json) {
        $finalResult | ConvertTo-Json -Depth 10
    }
    else {
        Write-Host 'Tau Agent package consumer smoke failed'
        Write-Host $_.Exception.Message
    }

    exit 1
}
