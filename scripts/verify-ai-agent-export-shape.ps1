param(
    [switch]$Json
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
Set-Location $repoRoot

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

function Assert-FileExists {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $fullPath = Join-Path $repoRoot $Path
    Add-Assertion -Name "file exists: $Path" -Passed (Test-Path -LiteralPath $fullPath -PathType Leaf) -Detail "Expected file to exist: $Path"
}

function Assert-TextContains {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $true)]
        [string]$Text,
        [Parameter(Mandatory = $true)]
        [string]$Needle
    )

    Add-Assertion -Name $Name -Passed ($Text.Contains($Needle)) -Detail "Expected text to contain: $Needle"
}

try {
    $docPath = Join-Path $repoRoot 'docs/AI_AGENT_EXPORT_SHAPE.md'
    Assert-FileExists -Path 'docs/AI_AGENT_EXPORT_SHAPE.md'
    $doc = Get-Content -LiteralPath $docPath -Raw

    $aiPackageExports = @(
        '.',
        './anthropic',
        './azure-openai-responses',
        './google',
        './google-gemini-cli',
        './google-vertex',
        './mistral',
        './openai-codex-responses',
        './openai-completions',
        './openai-responses',
        './oauth',
        './bedrock-provider'
    )

    $aiIndexExports = @(
        '@sinclair/typebox',
        './api-registry.js',
        './env-api-keys.js',
        './models.js',
        './providers/faux.js',
        './providers/register-builtins.js',
        './stream.js',
        './types.js',
        './utils/event-stream.js',
        './utils/json-parse.js',
        './utils/overflow.js',
        './utils/typebox-helpers.js',
        './utils/validation.js'
    )

    $agentIndexExports = @(
        './agent.js',
        './agent-loop.js',
        './proxy.js',
        './types.js'
    )

    foreach ($export in $aiPackageExports) {
        Assert-TextContains -Name "documented AI package export: $export" -Text $doc -Needle $export
    }

    foreach ($export in $aiIndexExports) {
        Assert-TextContains -Name "documented AI index export: $export" -Text $doc -Needle $export
    }

    foreach ($export in $agentIndexExports) {
        Assert-TextContains -Name "documented Agent index export: $export" -Text $doc -Needle $export
    }

    foreach ($decisionNeedle in @(
        'Machine-readable decision: no TypeScript/npm compatibility shim.',
        'No same-name .NET export',
        'Tau.Agent.Platform',
        'PackageReference Include="Tau.Agent"',
        'scripts/verify-agent-package-consumer.ps1',
        'scripts/verify-ai-cli-tool-install.ps1',
        'scripts/verify-release-package-publish.ps1',
        'scripts/verify-agent-proxy-server-e2e.ps1'
    )) {
        Assert-TextContains -Name "documented decision: $decisionNeedle" -Text $doc -Needle $decisionNeedle
    }

    $requiredFiles = @(
        'src/Tau.Ai/Tau.Ai.csproj',
        'src/Tau.Ai.Cli/Tau.Ai.Cli.csproj',
        'src/Tau.Ai.Cli/AiCliRunner.cs',
        'src/Tau.Ai/Providers/Anthropic/AnthropicProvider.cs',
        'src/Tau.Ai/Providers/OpenAiResponses/AzureOpenAiResponsesProvider.cs',
        'src/Tau.Ai/Providers/Google/GoogleProvider.cs',
        'src/Tau.Ai/Providers/Google/GoogleGeminiCliProvider.cs',
        'src/Tau.Ai/Providers/Google/GoogleVertexProvider.cs',
        'src/Tau.Ai/Providers/Mistral/MistralProvider.cs',
        'src/Tau.Ai/Providers/OpenAiResponses/OpenAiCodexResponsesProvider.cs',
        'src/Tau.Ai/Providers/OpenAi/OpenAiProvider.cs',
        'src/Tau.Ai/Providers/OpenAiCompat/OpenAiCompatibleProvider.cs',
        'src/Tau.Ai/Providers/OpenAiResponses/OpenAiResponsesProvider.cs',
        'src/Tau.Ai/Auth/OAuth/OAuthProviderRegistry.cs',
        'src/Tau.Ai/Auth/OAuth/BuiltInOAuthProviders.cs',
        'src/Tau.Ai/Providers/Bedrock/BedrockProvider.cs',
        'src/Tau.Ai/Providers/ProviderRegistry.cs',
        'src/Tau.Ai/Providers/StreamFunctions.cs',
        'src/Tau.Ai/Validation/ToolArgumentValidator.cs',
        'src/Tau.Ai/Utilities/JsonSchemaHelpers.cs',
        'src/Tau.Ai/Utilities/StreamingJsonParser.cs',
        'src/Tau.Agent/Tau.Agent.csproj',
        'src/Tau.Agent/Agent.cs',
        'src/Tau.Agent/Runtime/AgentRuntime.cs',
        'src/Tau.Agent/Runtime/AgentLoopConfig.cs',
        'src/Tau.Agent/Proxy/ProxyStreamProvider.cs',
        'src/Tau.Agent/Abstractions/AgentEvents.cs',
        'src/Tau.Agent/Abstractions/IAgentTool.cs',
        'src/Tau.Agent/Platform/AgentApplication.cs',
        'src/Tau.Agent/Platform/AgentApplicationBuilder.cs',
        'tests/Tau.Ai.Tests/AiPublicApiCompileSampleTests.cs',
        'tests/Tau.Agent.Tests/AgentPublicApiCompileSampleTests.cs',
        'scripts/verify-agent-package-consumer.ps1',
        'scripts/verify-ai-cli-tool-install.ps1',
        'scripts/verify-release-package-publish.ps1',
        'scripts/verify-agent-proxy-server-e2e.ps1',
        'scripts/publish-release-packages.ps1'
    )

    foreach ($path in $requiredFiles) {
        Assert-FileExists -Path $path
    }

    $script:results = [ordered]@{
        aiPackageExportCount = $aiPackageExports.Count
        aiIndexExportCount = $aiIndexExports.Count
        agentIndexExportCount = $agentIndexExports.Count
        evidenceFileCount = $requiredFiles.Count
    }

    $finalResult = [ordered]@{
        schemaVersion = 1
        succeeded = $true
        results = $script:results
        assertions = $script:assertions
        remainingGaps = @(
            'This verifies the local Tau-native export shape decision, not real provider/OAuth e2e.',
            'This verifies package/export mapping documentation and evidence files, not real registry promotion, signing or provenance.',
            'A future TypeScript/npm compatibility package would be a separate product surface.'
        )
    }

    if ($Json) {
        $finalResult | ConvertTo-Json -Depth 10
    }
    else {
        Write-Host 'Tau AI/Agent export shape smoke passed'
        Write-Host "  AI package exports: $($script:results.aiPackageExportCount)"
        Write-Host "  AI index exports: $($script:results.aiIndexExportCount)"
        Write-Host "  Agent index exports: $($script:results.agentIndexExportCount)"
        Write-Host "  evidence files: $($script:results.evidenceFileCount)"
        Write-Host "  assertions: $($script:assertions.Count)"
    }
}
catch {
    $finalResult = [ordered]@{
        schemaVersion = 1
        succeeded = $false
        results = $script:results
        assertions = $script:assertions
        error = $_.Exception.Message
    }

    if ($Json) {
        $finalResult | ConvertTo-Json -Depth 10
    }
    else {
        Write-Host 'Tau AI/Agent export shape smoke failed'
        Write-Host $_.Exception.Message
    }

    exit 1
}
