param(
    [switch]$SkipRestore,
    [switch]$NoBuild,
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

$testProject = 'tests/Tau.Agent.Tests/Tau.Agent.Tests.csproj'
$filter = 'FullyQualifiedName~ProxyStreamProviderTests'
$arguments = @(
    'test',
    $testProject,
    '--verbosity',
    'minimal',
    '-m:1',
    '--filter',
    $filter
)

if ($NoBuild) {
    $arguments += '--no-build'
    $arguments += '--no-restore'
}
elseif ($SkipRestore) {
    $arguments += '--no-restore'
}

$output = & dotnet @arguments 2>&1
$exitCode = $LASTEXITCODE
$outputText = ($output -join [Environment]::NewLine)

$result = [ordered]@{
    schemaVersion = 1
    succeeded = ($exitCode -eq 0)
    testProject = $testProject
    filter = $filter
    command = 'dotnet ' + ($arguments -join ' ')
    exitCode = $exitCode
    output = $outputText
    assertions = @(
        [ordered]@{
            name = 'agent proxy loopback server test command'
            passed = ($exitCode -eq 0)
            detail = "dotnet test exit code $exitCode"
        }
    )
}

if ($Json) {
    $result | ConvertTo-Json -Depth 8
}
else {
    if (-not [string]::IsNullOrWhiteSpace($outputText)) {
        Write-Host $outputText
    }

    if ($exitCode -eq 0) {
        Write-Host 'Tau Agent proxy-server e2e smoke passed'
    }
    else {
        Write-Host 'Tau Agent proxy-server e2e smoke failed'
    }
}

if ($exitCode -ne 0) {
    exit $exitCode
}
