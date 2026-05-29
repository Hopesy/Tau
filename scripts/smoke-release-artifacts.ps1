param(
    [string]$ArtifactRoot = '',
    [int]$TimeoutSeconds = 25
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)

function Get-DefaultRuntimeIdentifier {
    $runtimeInfo = [System.Runtime.InteropServices.RuntimeInformation]
    $osPlatform = [System.Runtime.InteropServices.OSPlatform]

    if ($runtimeInfo::IsOSPlatform($osPlatform::Windows)) {
        $os = 'win'
    }
    elseif ($runtimeInfo::IsOSPlatform($osPlatform::OSX)) {
        $os = 'osx'
    }
    elseif ($runtimeInfo::IsOSPlatform($osPlatform::Linux)) {
        $os = 'linux'
    }
    else {
        throw 'Unable to infer runtime identifier for this operating system. Pass -ArtifactRoot explicitly.'
    }

    $arch = $runtimeInfo::ProcessArchitecture.ToString().ToLowerInvariant()
    switch ($arch) {
        'x64' { return "$os-x64" }
        'arm64' { return "$os-arm64" }
        'x86' { return "$os-x86" }
        'arm' { return "$os-arm" }
        default { throw "Unable to infer runtime identifier for architecture '$arch'. Pass -ArtifactRoot explicitly." }
    }
}

function Convert-ToFullPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$BasePath
    )

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $BasePath $Path))
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
        [Parameter(Mandatory = $true)]
        [string]$StdoutPath,
        [Parameter(Mandatory = $true)]
        [string]$StderrPath,
        [int]$TimeoutSeconds = 25
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

    $stdout = if (Test-Path -LiteralPath $StdoutPath) { Get-Content -LiteralPath $StdoutPath -Raw } else { '' }
    $stderr = if (Test-Path -LiteralPath $StderrPath) { Get-Content -LiteralPath $StderrPath -Raw } else { '' }
    throw "Timed out waiting for $Url. stdout=$stdout stderr=$stderr"
}

function Resolve-CommandWrapper {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $candidates = @(
        (Join-Path $script:artifactRoot "bin/$Name.cmd"),
        (Join-Path $script:artifactRoot "bin/$Name.ps1"),
        (Join-Path $script:artifactRoot "bin/$Name")
    )

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return [System.IO.Path]::GetFullPath($candidate)
        }
    }

    throw "Release command wrapper not found: $Name"
}

function Resolve-AppEntrypoint {
    param(
        [Parameter(Mandatory = $true)]
        [string]$AppDir,
        [Parameter(Mandatory = $true)]
        [string]$AssemblyName
    )

    $candidates = @(
        (Join-Path $script:artifactRoot "$AppDir/$AssemblyName.exe"),
        (Join-Path $script:artifactRoot "$AppDir/$AssemblyName")
    )

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return [System.IO.Path]::GetFullPath($candidate)
        }
    }

    throw "Release app entrypoint not found: $AppDir/$AssemblyName"
}

function ConvertTo-CmdArgument {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    if ($Value -notmatch '[\s"&<>|^]') {
        return $Value
    }

    return '"' + ($Value -replace '"', '\"') + '"'
}

function ConvertTo-ProcessArgument {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    if ($Value -notmatch '[\s"]') {
        return $Value
    }

    return '"' + ($Value -replace '\\(?=")', '$0\' -replace '"', '\"') + '"'
}

function Invoke-ArtifactProcess {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [string[]]$Arguments = @(),
        [string]$InputText = '',
        [string]$InputFile = '',
        [string]$WorkingDirectory = $script:artifactRoot,
        [hashtable]$Environment = @{},
        [int]$TimeoutSeconds = 30
    )

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.RedirectStandardInput = -not [string]::IsNullOrEmpty($InputText)
    $psi.WorkingDirectory = $WorkingDirectory

    if ([System.IO.Path]::GetExtension($FilePath).Equals('.cmd', [StringComparison]::OrdinalIgnoreCase)) {
        $psi.FileName = if ($env:ComSpec) { $env:ComSpec } else { 'cmd.exe' }
        $commandLine = '"' + $FilePath + '"'
        if ($Arguments.Count -gt 0) {
            $commandLine += ' ' + (($Arguments | ForEach-Object { ConvertTo-CmdArgument $_ }) -join ' ')
        }
        if (-not [string]::IsNullOrWhiteSpace($InputFile)) {
            $commandLine += ' < "' + $InputFile + '"'
        }
        $psi.Arguments = '/d /s /c "' + $commandLine + '"'
    }
    else {
        $psi.FileName = $FilePath
        $psi.Arguments = ($Arguments | ForEach-Object { ConvertTo-ProcessArgument $_ }) -join ' '
    }

    foreach ($entry in $Environment.GetEnumerator()) {
        if ($null -eq $entry.Value) {
            $psi.EnvironmentVariables.Remove($entry.Key) | Out-Null
        }
        else {
            $psi.EnvironmentVariables[$entry.Key] = [string]$entry.Value
        }
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $psi
    if (-not $process.Start()) {
        throw "Failed to start process: $FilePath"
    }

    if (-not [string]::IsNullOrEmpty($InputText)) {
        $process.StandardInput.Write($InputText)
        if (-not $InputText.EndsWith([Environment]::NewLine, [StringComparison]::Ordinal)) {
            $process.StandardInput.WriteLine()
        }
        $process.StandardInput.Close()
    }

    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        $process.Kill()
        throw "Timed out running $FilePath $($Arguments -join ' ')"
    }

    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()

    return [pscustomobject]@{
        ExitCode = $process.ExitCode
        StdOut = $stdout
        StdErr = $stderr
        Output = ($stdout + $stderr)
    }
}

function Assert-SuccessOutput {
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$Result,
        [Parameter(Mandatory = $true)]
        [string]$Label,
        [string[]]$Patterns = @()
    )

    if ($Result.ExitCode -ne 0) {
        throw "$Label failed with exit code $($Result.ExitCode). Output: $($Result.Output)"
    }

    foreach ($pattern in $Patterns) {
        if ($Result.Output -notmatch $pattern) {
            throw "$Label output did not match '$pattern'. Output: $($Result.Output)"
        }
    }
}

function Invoke-CodingAgentRpcSmoke {
    $workRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("tau-release-pi-rpc-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $workRoot | Out-Null
    $inputPath = Join-Path $workRoot 'rpc-input.jsonl'
    [System.IO.File]::WriteAllText($inputPath, '{"id":"release-smoke","type":"get_state"}' + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))

    $envMap = @{
        TAU_CODING_AGENT_SESSION_FILE = Join-Path $workRoot 'session.json'
        TAU_CODING_AGENT_TREE_SESSION_FILE = Join-Path $workRoot 'session.jsonl'
        TAU_CODING_AGENT_SETTINGS_FILE = Join-Path $workRoot 'settings.json'
        TAU_CODING_AGENT_HISTORY_FILE = Join-Path $workRoot 'history'
        TAU_CODING_AGENT_KEYBINDINGS_FILE = Join-Path $workRoot 'keybindings.json'
        TAU_AUTH_FILE = Join-Path $workRoot 'auth.json'
    }

    $pi = Resolve-CommandWrapper -Name 'pi'
    $result = Invoke-ArtifactProcess `
        -FilePath $pi `
        -Arguments @('--mode', 'rpc', '--no-context-files', '--no-themes') `
        -InputFile $inputPath `
        -WorkingDirectory $workRoot `
        -Environment $envMap `
        -TimeoutSeconds 30

    Assert-SuccessOutput -Result $result -Label 'pi rpc get_state' -Patterns @('"command":"get_state"', '"success":true')
}

function Invoke-WebUiSmoke {
    $smokeRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("tau-release-webui-" + [Guid]::NewGuid().ToString('N'))
    $sessionsPath = Join-Path $smokeRoot 'webui-sessions.json'
    $stdoutPath = Join-Path $smokeRoot 'webui-stdout.log'
    $stderrPath = Join-Path $smokeRoot 'webui-stderr.log'
    $port = Get-FreeTcpPort
    $baseUrl = "http://127.0.0.1:$port"
    $entrypoint = Resolve-AppEntrypoint -AppDir 'apps/tau-web-ui' -AssemblyName 'Tau.WebUi'

    New-Item -ItemType Directory -Force -Path $smokeRoot | Out-Null

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $entrypoint
    $psi.Arguments = (ConvertTo-ProcessArgument '--urls') + ' ' + (ConvertTo-ProcessArgument $baseUrl)
    $psi.WorkingDirectory = $script:artifactRoot
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.EnvironmentVariables['WebUi__SessionsPath'] = $sessionsPath

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $psi
    $stdoutTask = $null
    $stderrTask = $null

    try {
        if (-not $process.Start()) {
            throw 'Failed to start Tau.WebUi release app.'
        }

        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()

        Wait-HttpReady -Url "$baseUrl/healthz" -StdoutPath $stdoutPath -StderrPath $stderrPath -TimeoutSeconds $TimeoutSeconds
        Invoke-RestMethod -Uri "$baseUrl/api/status" -Method Get | Out-Null
        Invoke-RestMethod -Uri "$baseUrl/api/catalog" -Method Get | Out-Null
        $session = Invoke-RestMethod -Uri "$baseUrl/api/sessions" -Method Post -ContentType 'application/json' -Body '{"title":"Release Smoke Session"}'

        if (-not (Test-Path -LiteralPath $sessionsPath)) {
            throw "WebUi release smoke did not create sessions store at $sessionsPath"
        }

        $sessionsJson = Get-Content -LiteralPath $sessionsPath -Raw
        if ($sessionsJson -notmatch [Regex]::Escape($session.id)) {
            throw "WebUi release smoke sessions store does not contain created session id $($session.id)"
        }
    }
    finally {
        if ($process -and -not $process.HasExited) {
            $process.Kill()
            $process.WaitForExit()
        }

        if ($stdoutTask) {
            [System.IO.File]::WriteAllText($stdoutPath, $stdoutTask.GetAwaiter().GetResult(), [System.Text.UTF8Encoding]::new($false))
        }
        if ($stderrTask) {
            [System.IO.File]::WriteAllText($stderrPath, $stderrTask.GetAwaiter().GetResult(), [System.Text.UTF8Encoding]::new($false))
        }
    }
}

function Invoke-MomSmoke {
    $smokeRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("tau-release-mom-" + [Guid]::NewGuid().ToString('N'))
    $inboxPath = Join-Path $smokeRoot 'inbox'
    $outboxPath = Join-Path $smokeRoot 'outbox'
    $archivePath = Join-Path $smokeRoot 'archive'
    $eventsPath = Join-Path $smokeRoot 'events'
    $workingDirectory = Join-Path $smokeRoot 'workdir'
    $channelDirectory = Join-Path $workingDirectory 'smoke-channel'
    $runtimeLogPath = Join-Path $smokeRoot 'tau-runtime-log.jsonl'

    New-Item -ItemType Directory -Force -Path $inboxPath, $outboxPath, $archivePath, $eventsPath, $workingDirectory | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $eventsPath 'smoke-event.json'), '{"type":"immediate","channelId":"smoke-channel","text":"smoke request"}', [System.Text.UTF8Encoding]::new($false))

    $envMap = @{
        Mom__InboxPath = $inboxPath
        Mom__OutboxPath = $outboxPath
        Mom__ArchivePath = $archivePath
        Mom__EventsPath = $eventsPath
        Mom__DefaultWorkingDirectory = $workingDirectory
        TAU_LOG_FILE = $runtimeLogPath
    }

    $mom = Resolve-CommandWrapper -Name 'mom'
    $result = Invoke-ArtifactProcess -FilePath $mom -Arguments @('--once') -WorkingDirectory $script:artifactRoot -Environment $envMap -TimeoutSeconds 45
    Assert-SuccessOutput -Result $result -Label 'mom --once'

    $outboxFiles = Get-ChildItem -Path $outboxPath -Filter *.json
    $archiveFiles = Get-ChildItem -Path $archivePath -File
    $statusPath = Join-Path $channelDirectory 'status.json'
    if ($outboxFiles.Count -ne 1) {
        throw "Mom release smoke expected exactly one outbox json but found $($outboxFiles.Count)"
    }

    if ($archiveFiles.Count -ne 1) {
        throw "Mom release smoke expected exactly one archived request but found $($archiveFiles.Count)"
    }

    if (Test-Path -LiteralPath (Join-Path $eventsPath 'smoke-event.json')) {
        throw 'Mom release smoke did not consume immediate event file'
    }

    if (-not (Test-Path -LiteralPath $statusPath)) {
        throw "Mom release smoke did not create channel status at $statusPath"
    }

    foreach ($requiredPath in @(
        (Join-Path $channelDirectory 'scratch'),
        (Join-Path $workingDirectory 'skills'),
        (Join-Path $channelDirectory 'skills')
    )) {
        if (-not (Test-Path -LiteralPath $requiredPath)) {
            throw "Mom release smoke did not create required path $requiredPath"
        }
    }

    $statusJson = Get-Content -LiteralPath $statusPath -Raw
    if (($statusJson -notmatch '"state": "completed"' -and $statusJson -notmatch '"state": "failed"') -or
        $statusJson -notmatch '"requestFile": "event_smoke-event_') {
        throw 'Mom release smoke channel status did not record final delegation state'
    }

    $logPath = Join-Path $channelDirectory 'log.jsonl'
    if (-not (Test-Path -LiteralPath $logPath)) {
        throw "Mom release smoke did not create channel log at $logPath"
    }

    $logContent = Get-Content -LiteralPath $logPath -Raw
    if ($logContent -notmatch 'smoke request' -or $logContent -notmatch 'EVENT:smoke-event.json:immediate' -or $logContent -notmatch '"isBot":true') {
        throw 'Mom release smoke channel log did not include request and bot result entries'
    }

    $promptDebugPath = Join-Path $channelDirectory 'last_prompt.jsonl'
    if (-not (Test-Path -LiteralPath $promptDebugPath)) {
        throw "Mom release smoke did not create prompt debug snapshot at $promptDebugPath"
    }

    $promptDebugContent = Get-Content -LiteralPath $promptDebugPath -Raw
    if ($promptDebugContent -notmatch '"systemPrompt":' -or
        $promptDebugContent -notmatch '"runnerInput":' -or
        $promptDebugContent -notmatch 'smoke request' -or
        $promptDebugContent -notmatch '"restoredMessageCount":') {
        throw 'Mom release smoke prompt debug snapshot did not include expected prompt context'
    }

    if (-not (Test-Path -LiteralPath $runtimeLogPath)) {
        throw "Mom release smoke did not create Tau runtime log at $runtimeLogPath"
    }

    $runtimeLogContent = Get-Content -LiteralPath $runtimeLogPath -Raw
    if ($runtimeLogContent -notmatch '"event":"delegation.start"' -or
        $runtimeLogContent -notmatch '"event":"delegation.end"') {
        throw 'Mom release smoke Tau runtime log did not include delegation start/end events'
    }
}

if ([string]::IsNullOrWhiteSpace($ArtifactRoot)) {
    $ArtifactRoot = Join-Path $repoRoot ("artifacts/tau-" + (Get-DefaultRuntimeIdentifier))
}

$script:artifactRoot = Convert-ToFullPath -Path $ArtifactRoot -BasePath $repoRoot
if (-not (Test-Path -LiteralPath $script:artifactRoot)) {
    throw "Artifact root not found: $script:artifactRoot"
}

$manifestPath = Join-Path $script:artifactRoot 'manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "Artifact manifest not found: $manifestPath"
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
foreach ($command in @('pi', 'tau-ai', 'pi-ai', 'mom', 'pi-pods', 'tau-web-ui')) {
    Resolve-CommandWrapper -Name $command | Out-Null
}

foreach ($entrypoint in @(
    @{ AppDir = 'apps/pi'; AssemblyName = 'Tau.CodingAgent' },
    @{ AppDir = 'apps/tau-ai'; AssemblyName = 'Tau.Ai.Cli' },
    @{ AppDir = 'apps/mom'; AssemblyName = 'Tau.Mom' },
    @{ AppDir = 'apps/pi-pods'; AssemblyName = 'Tau.Pods' },
    @{ AppDir = 'apps/tau-web-ui'; AssemblyName = 'Tau.WebUi' }
)) {
    Resolve-AppEntrypoint -AppDir $entrypoint.AppDir -AssemblyName $entrypoint.AssemblyName | Out-Null
}

if ($manifest.schemaVersion -ne 1) {
    throw "Unsupported artifact manifest schema version: $($manifest.schemaVersion)"
}

$payloads = @($manifest.releasePayload)
$requiredPayloads = @(
    @{ Name = 'readme'; Status = 'included'; Destination = 'README.md' },
    @{ Name = 'license'; Status = 'included'; Destination = 'LICENSE' },
    @{ Name = 'docs'; Status = 'included'; Destination = 'docs' },
    @{ Name = 'examples'; Status = 'missing' },
    @{ Name = 'changelog'; Status = 'tau-native-docs'; Destination = 'docs/releases/feature-release-notes.md' },
    @{ Name = 'package-json'; Status = 'tau-native-manifest'; Destination = 'manifest.json' },
    @{ Name = 'photon-wasm'; Status = 'missing' },
    @{ Name = 'theme'; Status = 'tau-native-inline' },
    @{ Name = 'export-html'; Status = 'tau-native-inline' },
    @{ Name = 'interactive-assets'; Status = 'missing' },
    @{ Name = 'koffi-windows-native'; Status = 'not-applicable' }
)

foreach ($requiredPayload in $requiredPayloads) {
    $entry = $payloads | Where-Object {
        $_.name -eq $requiredPayload.Name -and $_.status -eq $requiredPayload.Status
    } | Select-Object -First 1

    if ($null -eq $entry) {
        throw "Release manifest missing payload '$($requiredPayload.Name)' with status '$($requiredPayload.Status)'"
    }

    if ($requiredPayload.ContainsKey('Destination')) {
        $payloadPath = Join-Path $script:artifactRoot $requiredPayload.Destination
        if (-not (Test-Path -LiteralPath $payloadPath)) {
            throw "Release payload '$($requiredPayload.Name)' was not found at $payloadPath"
        }
    }
}

$releaseNotesPath = Join-Path $script:artifactRoot 'docs/releases/feature-release-notes.md'
if (-not (Test-Path -LiteralPath $releaseNotesPath)) {
    throw "Release docs payload missing feature release notes: $releaseNotesPath"
}

Write-Host "==> smoke tau-ai list"
$tauAi = Resolve-CommandWrapper -Name 'tau-ai'
$tauAiResult = Invoke-ArtifactProcess -FilePath $tauAi -Arguments @('list') -TimeoutSeconds 20
Assert-SuccessOutput -Result $tauAiResult -Label 'tau-ai list' -Patterns @('Available OAuth providers:', 'anthropic', 'openai-codex')

Write-Host "==> smoke pi-ai alias"
$piAi = Resolve-CommandWrapper -Name 'pi-ai'
$piAiResult = Invoke-ArtifactProcess -FilePath $piAi -Arguments @('list') -TimeoutSeconds 20
Assert-SuccessOutput -Result $piAiResult -Label 'pi-ai list' -Patterns @('Available OAuth providers:', 'anthropic', 'openai-codex')

Write-Host "==> smoke pi rpc"
Invoke-CodingAgentRpcSmoke

Write-Host "==> smoke pi-pods help"
$piPods = Resolve-CommandWrapper -Name 'pi-pods'
$podsResult = Invoke-ArtifactProcess -FilePath $piPods -Arguments @('--help') -TimeoutSeconds 20
Assert-SuccessOutput -Result $podsResult -Label 'pi-pods --help' -Patterns @('Tau\.Pods commands:')

Write-Host "==> smoke webui"
Invoke-WebUiSmoke

Write-Host "==> smoke mom"
Invoke-MomSmoke

Write-Host "Tau release artifact smoke passed: $script:artifactRoot"
