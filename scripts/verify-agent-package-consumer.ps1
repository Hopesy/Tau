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
using Tau.Ai;
using Tau.Ai.Providers;
using Tau.Ai.Providers.Faux;

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

static string ReadText(IReadOnlyList<ContentBlock> content) =>
    string.Join("\n", content.OfType<TextContent>().Select(static block => block.Text));
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
        }))
    .Build();

var result = await app.PromptAsync("run from package consumer").WaitAsync(TimeSpan.FromSeconds(10));
var snapshot = sessions.Load("consumer-session");
var providerRunStarts = sink.Events.Count(static evt => evt.Category == "provider" && evt.Event == "run.start");

Console.WriteLine($"assistant={result.AssistantText}");
Console.WriteLine($"saved={result.SavedSession}");
Console.WriteLine($"messages={result.Messages.Count}");
Console.WriteLine($"toolStarts={result.ToolStarts.Count}");
Console.WriteLine($"toolEnds={result.ToolEnds.Count}");
Console.WriteLine($"providerRuns={providerRunStarts}");
Console.WriteLine($"logSession={result.LogContext.SessionId}");
Console.WriteLine($"snapshot={snapshot?.SessionId}");
Console.WriteLine($"fauxCalls={faux.State.CallCount}");

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
