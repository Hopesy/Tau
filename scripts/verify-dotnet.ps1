param(
    [switch]$SkipRestore,
    [switch]$RunSmoke
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
Set-Location $repoRoot

$env:DOTNET_CLI_HOME = if ($env:DOTNET_CLI_HOME) { $env:DOTNET_CLI_HOME } else { Join-Path $repoRoot '.dotnet' }
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = if ($env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE) { $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE } else { '1' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = if ($env:DOTNET_CLI_TELEMETRY_OPTOUT) { $env:DOTNET_CLI_TELEMETRY_OPTOUT } else { '1' }
$env:DOTNET_NOLOGO = if ($env:DOTNET_NOLOGO) { $env:DOTNET_NOLOGO } else { '1' }

New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME | Out-Null

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

function Invoke-AiCliSmoke {
    $output = & dotnet run --project 'src/Tau.Ai.Cli/Tau.Ai.Cli.csproj' --no-build -- list 2>&1
    if ($LASTEXITCODE -ne 0) {
        $joined = ($output -join [Environment]::NewLine)
        throw "tau-ai list smoke failed with exit code $LASTEXITCODE. Output: $joined"
    }

    $text = $output -join [Environment]::NewLine
    if ($text -notmatch 'Available OAuth providers:' -or
        $text -notmatch 'anthropic' -or
        $text -notmatch 'openai-codex') {
        throw "tau-ai list smoke output did not include expected provider list"
    }
}

function Invoke-WebUiSmoke {
    $smokeRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("tau-webui-smoke-" + [Guid]::NewGuid().ToString("N"))
    $sessionsPath = Join-Path $smokeRoot 'webui-sessions.json'
    $stdoutPath = Join-Path $smokeRoot 'webui-stdout.log'
    $stderrPath = Join-Path $smokeRoot 'webui-stderr.log'
    $port = Get-FreeTcpPort
    $baseUrl = "http://127.0.0.1:$port"

    New-Item -ItemType Directory -Force -Path $smokeRoot | Out-Null

    $previousSessionsPath = $env:WebUi__SessionsPath
    $env:WebUi__SessionsPath = $sessionsPath
    $process = $null

    try {
        $process = Start-Process `
            -FilePath 'dotnet' `
            -ArgumentList @('run', '--project', 'src/Tau.WebUi/Tau.WebUi.csproj', '--no-build', '--', '--urls', $baseUrl) `
            -WorkingDirectory $repoRoot `
            -RedirectStandardOutput $stdoutPath `
            -RedirectStandardError $stderrPath `
            -WindowStyle Hidden `
            -PassThru

        Wait-HttpReady -Url "$baseUrl/healthz"
        Invoke-RestMethod -Uri "$baseUrl/api/status" -Method Get | Out-Null
        Invoke-RestMethod -Uri "$baseUrl/api/catalog" -Method Get | Out-Null
        $session = Invoke-RestMethod -Uri "$baseUrl/api/sessions" -Method Post -ContentType 'application/json' -Body '{"title":"Smoke Session"}'

        if (-not (Test-Path $sessionsPath)) {
            throw "WebUi smoke did not create sessions store at $sessionsPath"
        }

        $sessionsJson = Get-Content -Path $sessionsPath -Raw
        if ($sessionsJson -notmatch [Regex]::Escape($session.id)) {
            throw "WebUi smoke sessions store does not contain created session id $($session.id)"
        }
    }
    finally {
        if ($process -and -not $process.HasExited) {
            Stop-Process -Id $process.Id -Force
        }

        if ($null -eq $previousSessionsPath) {
            Remove-Item Env:WebUi__SessionsPath -ErrorAction SilentlyContinue
        }
        else {
            $env:WebUi__SessionsPath = $previousSessionsPath
        }
    }
}

function Invoke-MomSmoke {
    $smokeRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("tau-mom-smoke-" + [Guid]::NewGuid().ToString("N"))
    $inboxPath = Join-Path $smokeRoot 'inbox'
    $outboxPath = Join-Path $smokeRoot 'outbox'
    $archivePath = Join-Path $smokeRoot 'archive'
    $eventsPath = Join-Path $smokeRoot 'events'
    $workingDirectory = Join-Path $smokeRoot 'workdir'
    $channelDirectory = Join-Path $workingDirectory 'smoke-channel'
    $runtimeLogPath = Join-Path $smokeRoot 'tau-runtime-log.jsonl'

    New-Item -ItemType Directory -Force -Path $inboxPath, $outboxPath, $archivePath, $eventsPath, $workingDirectory | Out-Null
    Set-Content -Path (Join-Path $eventsPath 'smoke-event.json') -Value '{"type":"immediate","channelId":"smoke-channel","text":"smoke request"}'

    $previousInbox = $env:Mom__InboxPath
    $previousOutbox = $env:Mom__OutboxPath
    $previousArchive = $env:Mom__ArchivePath
    $previousEvents = $env:Mom__EventsPath
    $previousWorkdir = $env:Mom__DefaultWorkingDirectory
    $previousTauLogFile = $env:TAU_LOG_FILE

    try {
        $env:Mom__InboxPath = $inboxPath
        $env:Mom__OutboxPath = $outboxPath
        $env:Mom__ArchivePath = $archivePath
        $env:Mom__EventsPath = $eventsPath
        $env:Mom__DefaultWorkingDirectory = $workingDirectory
        $env:TAU_LOG_FILE = $runtimeLogPath

        Invoke-DotnetCommand -Arguments @('run', '--project', 'src/Tau.Mom/Tau.Mom.csproj', '--no-build', '--', '--once')

        $outboxFiles = Get-ChildItem -Path $outboxPath -Filter *.json
        $archiveFiles = Get-ChildItem -Path $archivePath -File
        $statusPath = Join-Path $channelDirectory 'status.json'
        if ($outboxFiles.Count -ne 1) {
            throw "Mom smoke expected exactly one outbox json but found $($outboxFiles.Count)"
        }

        if ($archiveFiles.Count -ne 1) {
            throw "Mom smoke expected exactly one archived request but found $($archiveFiles.Count)"
        }

        if (Test-Path (Join-Path $eventsPath 'smoke-event.json')) {
            throw "Mom smoke did not consume immediate event file"
        }

        if (-not (Test-Path $statusPath)) {
            throw "Mom smoke did not create channel status at $statusPath"
        }

        if (-not (Test-Path (Join-Path $channelDirectory 'scratch'))) {
            throw "Mom smoke did not create channel scratch directory"
        }

        if (-not (Test-Path (Join-Path $workingDirectory 'skills'))) {
            throw "Mom smoke did not create workspace skills directory"
        }

        if (-not (Test-Path (Join-Path $channelDirectory 'skills'))) {
            throw "Mom smoke did not create channel skills directory"
        }

        $statusJson = Get-Content -Path $statusPath -Raw
        if (($statusJson -notmatch '"state": "completed"' -and $statusJson -notmatch '"state": "failed"') -or
            $statusJson -notmatch '"requestFile": "event_smoke-event_') {
            throw "Mom smoke channel status did not record final delegation state"
        }

        $logPath = Join-Path $channelDirectory 'log.jsonl'
        if (-not (Test-Path $logPath)) {
            throw "Mom smoke did not create channel log at $logPath"
        }

        $logContent = Get-Content -Path $logPath -Raw
        if ($logContent -notmatch 'smoke request' -or $logContent -notmatch 'EVENT:smoke-event.json:immediate' -or $logContent -notmatch '"isBot":true') {
            throw "Mom smoke channel log did not include request and bot result entries"
        }

        $promptDebugPath = Join-Path $channelDirectory 'last_prompt.jsonl'
        if (-not (Test-Path $promptDebugPath)) {
            throw "Mom smoke did not create prompt debug snapshot at $promptDebugPath"
        }

        $promptDebugContent = Get-Content -Path $promptDebugPath -Raw
        if ($promptDebugContent -notmatch '"systemPrompt":' -or
            $promptDebugContent -notmatch '"runnerInput":' -or
            $promptDebugContent -notmatch 'smoke request' -or
            $promptDebugContent -notmatch '"restoredMessageCount":') {
            throw "Mom smoke prompt debug snapshot did not include expected prompt context"
        }

        if (-not (Test-Path $runtimeLogPath)) {
            throw "Mom smoke did not create Tau runtime log at $runtimeLogPath"
        }

        $runtimeLogContent = Get-Content -Path $runtimeLogPath -Raw
        if ($runtimeLogContent -notmatch '"event":"delegation.start"' -or
            $runtimeLogContent -notmatch '"event":"delegation.end"') {
            throw "Mom smoke Tau runtime log did not include delegation start/end events"
        }
    }
    finally {
        if ($null -eq $previousInbox) { Remove-Item Env:Mom__InboxPath -ErrorAction SilentlyContinue } else { $env:Mom__InboxPath = $previousInbox }
        if ($null -eq $previousOutbox) { Remove-Item Env:Mom__OutboxPath -ErrorAction SilentlyContinue } else { $env:Mom__OutboxPath = $previousOutbox }
        if ($null -eq $previousArchive) { Remove-Item Env:Mom__ArchivePath -ErrorAction SilentlyContinue } else { $env:Mom__ArchivePath = $previousArchive }
        if ($null -eq $previousEvents) { Remove-Item Env:Mom__EventsPath -ErrorAction SilentlyContinue } else { $env:Mom__EventsPath = $previousEvents }
        if ($null -eq $previousWorkdir) { Remove-Item Env:Mom__DefaultWorkingDirectory -ErrorAction SilentlyContinue } else { $env:Mom__DefaultWorkingDirectory = $previousWorkdir }
        if ($null -eq $previousTauLogFile) { Remove-Item Env:TAU_LOG_FILE -ErrorAction SilentlyContinue } else { $env:TAU_LOG_FILE = $previousTauLogFile }
    }
}

$sourceProjects = @(
    'src/Tau.Ai/Tau.Ai.csproj',
    'src/Tau.Ai.Cli/Tau.Ai.Cli.csproj',
    'src/Tau.Agent/Tau.Agent.csproj',
    'src/Tau.Tui/Tau.Tui.csproj',
    'src/Tau.CodingAgent/Tau.CodingAgent.csproj',
    'src/Tau.WebUi/Tau.WebUi.csproj',
    'src/Tau.Mom/Tau.Mom.csproj',
    'src/Tau.Pods/Tau.Pods.csproj'
)

$testProjects = @(
    'tests/Tau.Ai.Tests/Tau.Ai.Tests.csproj',
    'tests/Tau.Agent.Tests/Tau.Agent.Tests.csproj',
    'tests/Tau.Tui.Tests/Tau.Tui.Tests.csproj',
    'tests/Tau.CodingAgent.Tests/Tau.CodingAgent.Tests.csproj',
    'tests/Tau.WebUi.Tests/Tau.WebUi.Tests.csproj',
    'tests/Tau.Pods.Tests/Tau.Pods.Tests.csproj'
)

if (-not $SkipRestore) {
    Write-Host '==> restore'
    foreach ($project in @($sourceProjects + $testProjects)) {
        Write-Host "dotnet restore $project"
        Invoke-DotnetCommand -Arguments @('restore', $project, '--verbosity', 'minimal')
    }
}

Write-Host '==> build src'
foreach ($project in $sourceProjects) {
    Write-Host "dotnet build $project"
    Invoke-DotnetCommand -Arguments @('build', $project, '--no-restore', '--verbosity', 'minimal')
}

Write-Host '==> build tests'
foreach ($project in $testProjects) {
    Write-Host "dotnet build $project"
    Invoke-DotnetCommand -Arguments @('build', $project, '--no-restore', '--verbosity', 'minimal')
}

Write-Host '==> test'
foreach ($project in $testProjects) {
    Write-Host "dotnet test $project"
    Invoke-DotnetCommand -Arguments @('test', $project, '--no-build', '--no-restore', '--verbosity', 'minimal')
}

if ($RunSmoke) {
    Write-Host '==> smoke tau-ai'
    Invoke-AiCliSmoke
    Write-Host '==> smoke webui'
    Invoke-WebUiSmoke
    Write-Host '==> smoke mom'
    Invoke-MomSmoke
}

Write-Host 'Tau .NET project-level validation passed'
