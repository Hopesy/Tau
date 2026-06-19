param(
    [switch]$RunConfigured,
    [switch]$RequireConfigured,
    [switch]$Isolated,
    [string[]]$Provider,
    [int]$TimeoutSeconds = 45,
    [int]$MaxTokens = 16,
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

function Assert-Equal {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [AllowNull()]
        [object]$Actual,
        [AllowNull()]
        [object]$Expected
    )

    $passed = [object]::Equals($Actual, $Expected)
    Add-Assertion -Name $Name -Passed $passed -Detail "Expected '$Expected', actual '$Actual'."
}

function Assert-True {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $true)]
        [bool]$Condition,
        [Parameter(Mandatory = $true)]
        [string]$Detail
    )

    Add-Assertion -Name $Name -Passed $Condition -Detail $Detail
}

function Assert-ContainsAll {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [AllowNull()]
        [object[]]$Actual,
        [Parameter(Mandatory = $true)]
        [string[]]$Expected
    )

    $actualValues = @($Actual)
    foreach ($expectedValue in $Expected) {
        if ($actualValues -notcontains $expectedValue) {
            Add-Assertion -Name $Name -Passed $false -Detail "Expected list to contain '$expectedValue'. Actual: $($actualValues -join ', ')."
        }
    }

    Add-Assertion -Name $Name -Passed $true -Detail "List contains expected value(s): $($Expected -join ', ')."
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

function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [string[]]$Arguments = @(),
        [hashtable]$Environment = @{}
    )

    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $previousValues = @{}
    try {
        foreach ($entry in $Environment.GetEnumerator()) {
            $name = [string]$entry.Key
            $previousValues[$name] = [Environment]::GetEnvironmentVariable($name)
            [Environment]::SetEnvironmentVariable($name, [string]$entry.Value)
        }

        $output = & $FilePath @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        foreach ($entry in $previousValues.GetEnumerator()) {
            [Environment]::SetEnvironmentVariable([string]$entry.Key, [string]$entry.Value)
        }

        $ErrorActionPreference = $previousErrorActionPreference
    }

    return [ordered]@{
        exitCode = $exitCode
        output = ($output -join [Environment]::NewLine)
    }
}

function Invoke-JsonScript {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [string[]]$Arguments = @(),
        [hashtable]$Environment = @{}
    )

    $result = Invoke-Native -FilePath $FilePath -Arguments $Arguments -Environment $Environment
    if ([string]::IsNullOrWhiteSpace($result.output)) {
        throw "Script '$FilePath' did not return any output."
    }

    try {
        $json = $result.output | ConvertFrom-Json
    }
    catch {
        throw "Script '$FilePath' did not return valid JSON. Output: $($result.output)"
    }

    return [ordered]@{
        exitCode = $result.exitCode
        json = $json
        raw = $result.output
    }
}

function Get-DefaultProviders {
    return @(
        'openai',
        'openai-codex',
        'azure-openai-responses',
        'github-copilot',
        'anthropic',
        'google',
        'google-vertex',
        'google-gemini-cli',
        'google-antigravity',
        'mistral',
        'amazon-bedrock'
    )
}

function Get-IsolationEnvironment {
    param(
        [Parameter(Mandatory = $true)]
        [string]$TempRoot
    )

    $authFile = Join-Path $TempRoot 'auth.json'
    $modelsFile = Join-Path $TempRoot 'models.json'
    Write-Utf8NoBom -Path $authFile -Text "{}`n"
    Write-Utf8NoBom -Path $modelsFile -Text "{}`n"

    $blankNames = @(
        'OPENAI_API_KEY',
        'AZURE_OPENAI_API_KEY',
        'COPILOT_GITHUB_TOKEN',
        'GH_TOKEN',
        'GITHUB_TOKEN',
        'ANTHROPIC_OAUTH_TOKEN',
        'ANTHROPIC_API_KEY',
        'GEMINI_API_KEY',
        'GOOGLE_API_KEY',
        'GOOGLE_CLOUD_API_KEY',
        'GOOGLE_CLOUD_PROJECT',
        'GCLOUD_PROJECT',
        'GOOGLE_CLOUD_LOCATION',
        'GOOGLE_APPLICATION_CREDENTIALS',
        'MISTRAL_API_KEY',
        'OPENROUTER_API_KEY',
        'GROQ_API_KEY',
        'CEREBRAS_API_KEY',
        'XAI_API_KEY',
        'ZAI_API_KEY',
        'AI_GATEWAY_API_KEY',
        'MINIMAX_API_KEY',
        'MINIMAX_CN_API_KEY',
        'HF_TOKEN',
        'OPENCODE_API_KEY',
        'KIMI_API_KEY',
        'AWS_PROFILE',
        'AWS_ACCESS_KEY_ID',
        'AWS_SECRET_ACCESS_KEY',
        'AWS_BEARER_TOKEN_BEDROCK',
        'AWS_CONTAINER_CREDENTIALS_RELATIVE_URI',
        'AWS_CONTAINER_CREDENTIALS_FULL_URI',
        'AWS_WEB_IDENTITY_TOKEN_FILE',
        'PI_API_KEY'
    )

    $env = [ordered]@{
        TAU_AUTH_FILE = $authFile
        TAU_MODELS_FILE = $modelsFile
    }

    foreach ($name in $blankNames) {
        $env[$name] = ''
    }

    return $env
}

function New-MatrixHarness {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root,
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot
    )

    $projectPath = Join-Path $Root 'Tau.Ai.ProviderMatrixHarness.csproj'
    $programPath = Join-Path $Root 'Program.cs'
    $tauAiProjectPath = (Join-Path $RepoRoot 'src/Tau.Ai/Tau.Ai.csproj').Replace('&', '&amp;')

    Write-Utf8NoBom -Path $projectPath -Text (@'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="__TAU_AI_PROJECT__" />
  </ItemGroup>
</Project>
'@).Replace('__TAU_AI_PROJECT__', $tauAiProjectPath)

    Write-Utf8NoBom -Path $programPath -Text @'
using System.Text.Json;
using Tau.Ai;
using Tau.Ai.Auth;
using Tau.Ai.Auth.OAuth;
using Tau.Ai.Providers;
using Tau.Ai.Registry;

var mode = args.Length > 0 ? args[0].Trim() : "inspect";
var providerCsv = args.Length > 1 ? args[1] : string.Empty;
var timeoutSeconds = args.Length > 2 && int.TryParse(args[2], out var parsedTimeout) ? parsedTimeout : 45;
var maxTokens = args.Length > 3 && int.TryParse(args[3], out var parsedTokens) ? parsedTokens : 16;
var isolated = args.Length > 4 && (args[4] is "1" or "true" or "True");
var requireConfigured = args.Length > 5 && (args[5] is "1" or "true" or "True");

var requestedProviders = ParseProviders(providerCsv);
if (requestedProviders.Count == 0)
{
    requestedProviders.AddRange(GetDefaultProviders());
}

var modelStore = new ModelConfigurationStore();
var authResolver = new ProviderAuthResolver(new OAuthProviderRegistry(), new OAuthCredentialStore(), null, modelStore);
var modelCatalog = new ModelCatalog(authResolver, modelStore);
var providerRegistry = new ProviderRegistry();
BuiltInProviders.RegisterAll(providerRegistry, modelStore);

var rows = new List<ProviderMatrixRow>();
var succeeded = true;
var runConfigured = mode.Equals("run-configured", StringComparison.OrdinalIgnoreCase);

foreach (var providerId in requestedProviders)
{
    rows.Add(await BuildRowAsync(providerId, modelCatalog, authResolver, providerRegistry, runConfigured, timeoutSeconds, maxTokens));
}

var configuredProviderCount = rows.Count(row => row.Configured);
var attemptedProviderCount = rows.Count(row => row.RunAttempted);
var succeededProviderCount = rows.Count(row => row.RunSucceeded);
var openProviderCount = rows.Count(row => row.CanLogin && !row.Configured);
var hasFailure = rows.Any(row => row.RunStatus is "model-unavailable" or "failed");
var realE2eSatisfied = runConfigured &&
                       !isolated &&
                       attemptedProviderCount > 0 &&
                       attemptedProviderCount == succeededProviderCount &&
                       !hasFailure;
var completionStatus = runConfigured
    ? attemptedProviderCount == 0
        ? "no-configured-providers"
        : hasFailure
            ? "configured-provider-failed"
            : realE2eSatisfied
                ? "real-e2e-verified"
                : "configured-provider-smoke"
    : "inspect-only";
string? gateFailure = null;
if (runConfigured && requireConfigured && configuredProviderCount == 0)
{
    gateFailure = "No configured providers were found. Configure at least one real provider credential before using -RunConfigured -RequireConfigured as final e2e evidence.";
}
else if (runConfigured && requireConfigured && attemptedProviderCount == 0)
{
    gateFailure = "No configured provider run was attempted. Resolve model selection or provider registration before using this as final e2e evidence.";
}
else if (runConfigured && requireConfigured && !realE2eSatisfied)
{
    gateFailure = "Configured provider runs did not produce a non-isolated all-success real e2e result.";
}

if (runConfigured)
{
    succeeded = !hasFailure && gateFailure is null;
}

var result = new
{
    schemaVersion = 1,
    mode,
    isolated,
    runConfigured,
    requireConfigured,
    timeoutSeconds,
    maxTokens,
    providerCount = rows.Count,
    configuredProviderCount,
    attemptedProviderCount,
    succeededProviderCount,
    openProviderCount,
    realE2eSatisfied,
    completionStatus,
    gateFailure,
    succeeded,
    providers = rows,
    remainingGaps = new[]
    {
        "This harness proves the inspect contract and can run configured providers, but isolated mode intentionally avoids real provider calls.",
        "Providers without configured credentials remain external-e2e-needed until a real RunConfigured invocation succeeds against live services.",
        "Use -RunConfigured -RequireConfigured for final provider/OAuth e2e gating; it fails when no live provider credentials are configured or no real run succeeds.",
        "A successful isolated run does not close real provider/OAuth e2e, registry/global install, signing or provenance gaps."
    }
};

Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));

static IReadOnlyList<string> GetDefaultProviders() => new[]
{
    "openai",
    "openai-codex",
    "azure-openai-responses",
    "github-copilot",
    "anthropic",
    "google",
    "google-vertex",
    "google-gemini-cli",
    "google-antigravity",
    "mistral",
    "amazon-bedrock"
};

static List<string> ParseProviders(string providerCsv)
{
    var providers = new List<string>();
    if (string.IsNullOrWhiteSpace(providerCsv))
    {
        return providers;
    }

    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var provider in providerCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (seen.Add(provider))
        {
            providers.Add(provider);
        }
    }

    return providers;
}

static async Task<ProviderMatrixRow> BuildRowAsync(
    string providerId,
    ModelCatalog modelCatalog,
    ProviderAuthResolver authResolver,
    ProviderRegistry providerRegistry,
    bool runConfigured,
    int timeoutSeconds,
    int maxTokens)
{
    var row = new ProviderMatrixRow
    {
        Provider = providerId,
        Configured = false,
        Source = "none",
        UsesOAuth = false,
        CanLogin = false,
        Message = "No status available yet.",
        RunAttempted = false,
        RunSucceeded = false,
        RunStatus = "inspect-only"
    };

    Model? model = null;
    try
    {
        var selection = modelCatalog.ResolveSelection(providerId, null);
        model = modelCatalog.GetModel(selection.Provider, selection.ModelId);
        row.SelectedModel = selection.CanonicalReference;
        row.SelectedApi = model.Api;
    }
    catch
    {
        row.SelectedModel = null;
        row.SelectedApi = null;
    }

    var status = model is null
        ? authResolver.GetStatus(providerId)
        : authResolver.GetStatus(model);

    row.Configured = status.IsConfigured;
    row.Source = status.Source;
    row.UsesOAuth = status.UsesOAuth;
    row.CanLogin = status.CanLogin;
    row.Message = status.Message;

    if (!runConfigured)
    {
        return row;
    }

    if (!row.Configured)
    {
        row.RunStatus = "skipped-unconfigured";
        return row;
    }

    if (model is null)
    {
        row.RunStatus = "model-unavailable";
        return row;
    }

    row.RunAttempted = true;
    try
    {
        var assistant = await StreamFunctions.CompleteAsync(
            providerRegistry,
            model,
            new LlmContext(
                "You are a provider smoke test. Reply with the single word tau.",
                new ChatMessage[]
                {
                    new UserMessage("Reply with tau.")
                },
                null),
            new StreamOptions
            {
                Temperature = 0,
                MaxTokens = maxTokens,
                MaxRetryDelay = TimeSpan.FromSeconds(2),
                Metadata = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["tau"] = "provider-oauth-matrix"
                }
            }).WaitAsync(TimeSpan.FromSeconds(timeoutSeconds));

        row.RunSucceeded = true;
        row.RunStatus = "succeeded";
        row.AssistantText = ReadText(assistant.Content);
        row.StopReason = assistant.StopReason?.ToString();
        row.AssistantProvider = assistant.Provider;
        row.AssistantModel = assistant.Model;
        row.InputTokens = assistant.Usage?.InputTokens;
        row.OutputTokens = assistant.Usage?.OutputTokens;
    }
    catch (Exception ex)
    {
        row.RunSucceeded = false;
        row.RunStatus = "failed";
        row.ErrorCategory = ex.GetType().Name;
        row.ErrorMessage = Summarize(ex.Message);
    }

    return row;
}

static string ReadText(IReadOnlyList<ContentBlock> content) =>
    string.Join("\n", content.OfType<TextContent>().Select(static block => block.Text));

static string Summarize(string? text)
{
    if (string.IsNullOrWhiteSpace(text))
    {
        return string.Empty;
    }

    var singleLine = text.Replace("\r", " ").Replace("\n", " ").Trim();
    return singleLine.Length <= 300 ? singleLine : singleLine[..300];
}

sealed class ProviderMatrixRow
{
    public required string Provider { get; init; }
    public string? SelectedModel { get; set; }
    public string? SelectedApi { get; set; }
    public bool Configured { get; set; }
    public string Source { get; set; } = string.Empty;
    public bool UsesOAuth { get; set; }
    public bool CanLogin { get; set; }
    public string Message { get; set; } = string.Empty;
    public bool RunAttempted { get; set; }
    public bool RunSucceeded { get; set; }
    public string RunStatus { get; set; } = string.Empty;
    public string? AssistantText { get; set; }
    public string? StopReason { get; set; }
    public string? AssistantProvider { get; set; }
    public string? AssistantModel { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public string? ErrorCategory { get; set; }
    public string? ErrorMessage { get; set; }
}
'@

    return $projectPath
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("tau-ai-provider-matrix-" + [Guid]::NewGuid().ToString('N'))
$harnessRoot = Join-Path $tempRoot 'harness'
$projectPath = $null
$harnessDllPath = $null
$environment = @{}
$environmentBackup = @{}

try {
    if ($RequireConfigured -and -not $RunConfigured) {
        throw '-RequireConfigured requires -RunConfigured.'
    }

    New-Item -ItemType Directory -Force -Path $harnessRoot | Out-Null
    $projectPath = New-MatrixHarness -Root $harnessRoot -RepoRoot $repoRoot

    $providerList = if ($Provider -and $Provider.Count -gt 0) { $Provider } else { Get-DefaultProviders }
    $requestedProviders = @($providerList | ForEach-Object { $_.Trim() } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)
    if ($requestedProviders.Count -eq 0) {
        throw 'At least one provider is required.'
    }

    $mode = if ($RunConfigured) { 'run-configured' } else { 'inspect' }
    if ($Isolated) {
        $environment = Get-IsolationEnvironment -TempRoot $tempRoot
    }

    $buildResult = Invoke-Native -FilePath 'dotnet' -Arguments @(
        'build',
        $projectPath,
        '--configuration',
        'Release',
        '--nologo',
        '--verbosity',
        'quiet'
    )
    Add-Assertion -Name 'harness build exit code' -Passed ($buildResult.exitCode -eq 0) -Detail "Harness build failed with exit code $($buildResult.exitCode). Output: $($buildResult.output)"

    $harnessDllPath = Join-Path $harnessRoot 'bin/Release/net10.0/Tau.Ai.ProviderMatrixHarness.dll'
    Add-Assertion -Name 'harness output exists' -Passed (Test-Path -LiteralPath $harnessDllPath -PathType Leaf) -Detail "Expected harness output at $harnessDllPath."

    $runResult = Invoke-JsonScript -FilePath 'dotnet' -Arguments @(
        $harnessDllPath,
        $mode,
        ($requestedProviders -join ','),
        $TimeoutSeconds.ToString(),
        $MaxTokens.ToString(),
        ($(if ($Isolated) { '1' } else { '0' })),
        ($(if ($RequireConfigured) { '1' } else { '0' }))
    ) -Environment $environment

    $raw = $runResult.json
    $script:results.raw = $raw
    $script:results.providerRows = @($raw.providers)

    Assert-Equal -Name 'matrix schema version' -Actual $raw.schemaVersion -Expected 1
    Assert-Equal -Name 'matrix mode' -Actual $raw.mode -Expected $mode
    Assert-Equal -Name 'matrix isolated flag' -Actual $raw.isolated -Expected $Isolated.IsPresent
    Assert-Equal -Name 'matrix runConfigured flag' -Actual $raw.runConfigured -Expected $RunConfigured.IsPresent
    Assert-Equal -Name 'matrix requireConfigured flag' -Actual $raw.requireConfigured -Expected $RequireConfigured.IsPresent
    Assert-Equal -Name 'matrix provider count' -Actual $raw.providerCount -Expected $requestedProviders.Count
    Assert-ContainsAll -Name 'matrix provider names' -Actual @($raw.providers | ForEach-Object { $_.provider }) -Expected @($requestedProviders)

    if ($Isolated) {
        Assert-Equal -Name 'matrix isolated configured count' -Actual $raw.configuredProviderCount -Expected 0
        Assert-Equal -Name 'matrix isolated attempted count' -Actual $raw.attemptedProviderCount -Expected 0
        Assert-Equal -Name 'matrix isolated succeeded count' -Actual $raw.succeededProviderCount -Expected 0
    }

    if (-not $RunConfigured) {
        Assert-Equal -Name 'matrix inspect attempted count' -Actual $raw.attemptedProviderCount -Expected 0
        Assert-Equal -Name 'matrix inspect succeeded count' -Actual $raw.succeededProviderCount -Expected 0
        if ($Isolated) {
            Assert-True -Name 'matrix isolated inspect open providers' -Condition ($raw.openProviderCount -ge 1) -Detail 'Expected at least one OAuth-capable provider in the isolated inspect matrix.'
        }
    }

    $script:results.summary = [ordered]@{
        providerCount = $raw.providerCount
        configuredProviderCount = $raw.configuredProviderCount
        attemptedProviderCount = $raw.attemptedProviderCount
        succeededProviderCount = $raw.succeededProviderCount
        openProviderCount = $raw.openProviderCount
        succeeded = $raw.succeeded
        mode = $raw.mode
        isolated = $raw.isolated
        requireConfigured = $raw.requireConfigured
        realE2eSatisfied = $raw.realE2eSatisfied
        completionStatus = $raw.completionStatus
        gateFailure = $raw.gateFailure
    }

    $script:results.remainingGaps = @($raw.remainingGaps)

    if ($RequireConfigured) {
        Assert-True -Name 'matrix required configured providers present' -Condition ($raw.configuredProviderCount -gt 0) -Detail 'Expected at least one configured provider when -RequireConfigured is set.'
        Assert-True -Name 'matrix required configured run attempted' -Condition ($raw.attemptedProviderCount -gt 0) -Detail 'Expected at least one attempted provider run when -RequireConfigured is set.'
        Assert-Equal -Name 'matrix required configured real e2e satisfied' -Actual $raw.realE2eSatisfied -Expected $true
    }

    $finalSucceeded = [bool]$raw.succeeded
    if ($RunConfigured -and -not $Isolated -and $raw.attemptedProviderCount -gt 0 -and -not $raw.succeeded) {
        $finalSucceeded = $false
    }

    $finalResult = [ordered]@{
        schemaVersion = 1
        succeeded = $finalSucceeded
        mode = $raw.mode
        isolated = $raw.isolated
        runConfigured = $raw.runConfigured
        requireConfigured = $raw.requireConfigured
        realE2eSatisfied = $raw.realE2eSatisfied
        completionStatus = $raw.completionStatus
        timeoutSeconds = $TimeoutSeconds
        maxTokens = $MaxTokens
        providerCount = $raw.providerCount
        results = $script:results
        assertions = $script:assertions
        remainingGaps = @($raw.remainingGaps)
    }

    if ($Json) {
        $finalResult | ConvertTo-Json -Depth 12
    }
    else {
        Write-Host 'Tau AI provider/OAuth matrix smoke passed'
        Write-Host "  mode: $($raw.mode)"
        Write-Host "  isolated: $($raw.isolated)"
        Write-Host "  runConfigured: $($raw.runConfigured)"
        Write-Host "  requireConfigured: $($raw.requireConfigured)"
        Write-Host "  providers: $($raw.providerCount)"
        Write-Host "  configured: $($raw.configuredProviderCount)"
        Write-Host "  attempted: $($raw.attemptedProviderCount)"
        Write-Host "  succeeded: $($raw.succeededProviderCount)"
        Write-Host "  completion: $($raw.completionStatus)"
        Write-Host "  assertions: $($script:assertions.Count)"
    }
}
catch {
    $finalResult = [ordered]@{
        schemaVersion = 1
        succeeded = $false
        mode = if ($RunConfigured) { 'run-configured' } else { 'inspect' }
        isolated = $Isolated.IsPresent
        runConfigured = $RunConfigured.IsPresent
        requireConfigured = $RequireConfigured.IsPresent
        timeoutSeconds = $TimeoutSeconds
        maxTokens = $MaxTokens
        results = $script:results
        assertions = $script:assertions
        error = $_.Exception.Message
    }

    if ($Json) {
        $finalResult | ConvertTo-Json -Depth 12
    }
    else {
        Write-Host 'Tau AI provider/OAuth matrix smoke failed'
        Write-Host $_.Exception.Message
    }

    exit 1
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if (-not $finalSucceeded) {
    exit 1
}
