param(
    [switch]$SkipRestore
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
Set-Location $repoRoot

$env:DOTNET_CLI_HOME = if ($env:DOTNET_CLI_HOME) { $env:DOTNET_CLI_HOME } else { Join-Path $repoRoot '.dotnet' }
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = if ($env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE) { $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE } else { '1' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = if ($env:DOTNET_CLI_TELEMETRY_OPTOUT) { $env:DOTNET_CLI_TELEMETRY_OPTOUT } else { '1' }
$env:DOTNET_NOLOGO = if ($env:DOTNET_NOLOGO) { $env:DOTNET_NOLOGO } else { '1' }

New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME | Out-Null

$consoleProject = 'examples/Tau.Agent.ConsoleExample/Tau.Agent.ConsoleExample.csproj'
$httpProject = 'examples/Tau.Agent.HttpExample/Tau.Agent.HttpExample.csproj'

function Invoke-DotnetCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

function Get-FreeTcpPort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    try {
        $listener.Start()
        return $listener.LocalEndpoint.Port
    }
    finally {
        $listener.Stop()
    }
}

function Wait-HttpReady {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Url,
        [int]$TimeoutSeconds = 20
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            Invoke-RestMethod -Uri $Url -Method Get | Out-Null
            return
        }
        catch {
            Start-Sleep -Milliseconds 250
        }
    }

    throw "Timed out waiting for $Url"
}

if (-not $SkipRestore) {
    Write-Host '==> restore examples'
    Invoke-DotnetCommand -Arguments @('restore', $consoleProject, '--verbosity', 'minimal')
    Invoke-DotnetCommand -Arguments @('restore', $httpProject, '--verbosity', 'minimal')
}

Write-Host '==> build examples'
Invoke-DotnetCommand -Arguments @('build', $consoleProject, '--no-restore', '--verbosity', 'minimal')
Invoke-DotnetCommand -Arguments @('build', $httpProject, '--no-restore', '--verbosity', 'minimal')

Write-Host '==> smoke console example'
$consoleOutput = & dotnet run --project $consoleProject --no-build --no-restore 2>&1
if ($LASTEXITCODE -ne 0) {
    $joined = $consoleOutput -join [Environment]::NewLine
    throw "Console Agent example failed with exit code $LASTEXITCODE. Output: $joined"
}

$consoleText = $consoleOutput -join [Environment]::NewLine
if ($consoleText -notmatch 'assistant: console example complete' -or
    $consoleText -notmatch 'savedSession: True' -or
    $consoleText -notmatch 'sessionMessages: 4' -or
    $consoleText -notmatch 'log: tool/execution.start' -or
    $consoleText -notmatch 'log: tool/execution.end') {
    throw "Console Agent example output did not include expected run/session/log evidence. Output: $consoleText"
}

Write-Host '==> smoke HTTP example'
$smokeRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("tau-agent-http-example-" + [Guid]::NewGuid().ToString("N"))
$stdoutPath = Join-Path $smokeRoot 'stdout.log'
$stderrPath = Join-Path $smokeRoot 'stderr.log'
$port = Get-FreeTcpPort
$baseUrl = "http://127.0.0.1:$port"
$process = $null

New-Item -ItemType Directory -Force -Path $smokeRoot | Out-Null
try {
    $process = Start-Process `
        -FilePath 'dotnet' `
        -ArgumentList @('run', '--project', $httpProject, '--no-build', '--no-restore', '--', '--urls', $baseUrl) `
        -WorkingDirectory $repoRoot `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath `
        -WindowStyle Hidden `
        -PassThru

    Wait-HttpReady -Url "$baseUrl/healthz"
    $response = Invoke-RestMethod `
        -Uri "$baseUrl/agent" `
        -Method Post `
        -ContentType 'application/json' `
        -Body '{"prompt":"hello from http smoke","sessionId":"http-smoke"}'

    if ($response.assistantText -ne 'http example complete: hello from http smoke' -or
        $response.messageCount -ne 4 -or
        $response.toolStartCount -ne 1 -or
        $response.toolEndCount -ne 1 -or
        -not $response.success -or
        -not $response.savedSession -or
        $response.sessionId -ne 'http-smoke' -or
        [string]::IsNullOrWhiteSpace($response.correlationId) -or
        [string]::IsNullOrWhiteSpace($response.messageId)) {
        throw "HTTP Agent example response did not include expected run evidence: $($response | ConvertTo-Json -Depth 5)"
    }
}
finally {
    if ($process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
    }
}

Write-Host 'Tau Agent platform examples validation passed'
