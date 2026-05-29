param(
    [ValidateSet('rpc')]
    [string]$Mode = 'rpc',
    [int]$Runs = 1,
    [int]$Warmup = 0,
    [string]$Configuration = 'Debug',
    [string]$AgentDir = '',
    [switch]$IsolatedAgentDir,
    [switch]$NoOffline,
    [switch]$NoBuild,
    [int]$TimeoutSeconds = 30,
    [switch]$Json
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$invocationDirectory = (Get-Location).Path
$script:warnings = @()

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

function ConvertTo-ShArgument {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    return "'" + ($Value -replace "'", "'\''") + "'"
}

function Resolve-CodingAgentAssembly {
    $assemblyPath = Join-Path $repoRoot "src/Tau.CodingAgent/bin/$Configuration/net10.0/Tau.CodingAgent.dll"
    if (Test-Path -LiteralPath $assemblyPath -PathType Leaf) {
        return $assemblyPath
    }

    if ($NoBuild) {
        throw "Tau.CodingAgent assembly not found: $assemblyPath"
    }

    $buildOutput = & dotnet build (Join-Path $repoRoot 'src/Tau.CodingAgent/Tau.CodingAgent.csproj') --no-restore --configuration $Configuration --verbosity minimal 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Tau.CodingAgent build failed with exit code $LASTEXITCODE. Output: $($buildOutput -join [Environment]::NewLine)"
    }

    if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) {
        throw "Tau.CodingAgent assembly was not produced: $assemblyPath"
    }

    return $assemblyPath
}

function Set-BenchmarkEnvironment {
    param(
        [Parameter(Mandatory = $true)]
        [System.Collections.Specialized.StringDictionary]$Environment,
        [Parameter(Mandatory = $true)]
        [string]$StateRoot,
        [string]$EffectiveAgentDir
    )

    $Environment['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    $Environment['DOTNET_SKIP_FIRST_TIME_EXPERIENCE'] = '1'
    $Environment['DOTNET_NOLOGO'] = '1'
    $Environment['TAU_AUTH_FILE'] = Join-Path $StateRoot 'auth.json'
    $Environment['TAU_MODELS_FILE'] = Join-Path $StateRoot 'models.json'
    $Environment['TAU_CODING_AGENT_SESSION_FILE'] = Join-Path $StateRoot 'session.json'
    $Environment['TAU_CODING_AGENT_TREE_SESSION_FILE'] = Join-Path $StateRoot 'session.jsonl'
    $Environment['TAU_CODING_AGENT_SETTINGS_FILE'] = Join-Path $StateRoot 'settings.json'
    $Environment['TAU_CODING_AGENT_HISTORY_FILE'] = Join-Path $StateRoot 'history'
    $Environment['TAU_CODING_AGENT_KEYBINDINGS_FILE'] = Join-Path $StateRoot 'keybindings.json'
    $Environment['TAU_LOG_FILE'] = Join-Path $StateRoot 'log.jsonl'
    $Environment['TAU_CODING_AGENT_PROMPT_PATHS'] = ''
    $Environment['TAU_CODING_AGENT_SKILL_PATHS'] = ''
    $Environment['TAU_CODING_AGENT_EXTENSION_PATHS'] = ''
    $Environment['TAU_CODING_AGENT_THEME_PATHS'] = ''

    if (-not $NoOffline) {
        $Environment['PI_OFFLINE'] = '1'
        $Environment['PI_SKIP_VERSION_CHECK'] = '1'
        $Environment['TAU_NO_LOCAL_LLM'] = '1'
        $Environment['PI_NO_LOCAL_LLM'] = '1'
    }

    if (-not [string]::IsNullOrWhiteSpace($EffectiveAgentDir)) {
        $Environment['TAU_CODING_AGENT_DIR'] = $EffectiveAgentDir
    }
}

function Invoke-RpcStartupRun {
    param(
        [Parameter(Mandatory = $true)]
        [string]$AssemblyPath,
        [Parameter(Mandatory = $true)]
        [string]$StateRoot,
        [Parameter(Mandatory = $true)]
        [string]$RequestId,
        [string]$EffectiveAgentDir
    )

    New-Item -ItemType Directory -Force -Path $StateRoot | Out-Null
    $authPath = Join-Path $StateRoot 'auth.json'
    $modelsPath = Join-Path $StateRoot 'models.json'
    if (-not (Test-Path -LiteralPath $authPath)) {
        [System.IO.File]::WriteAllText($authPath, '{}', [System.Text.UTF8Encoding]::new($false))
    }
    if (-not (Test-Path -LiteralPath $modelsPath)) {
        [System.IO.File]::WriteAllText($modelsPath, '{}', [System.Text.UTF8Encoding]::new($false))
    }
    $inputPath = Join-Path $StateRoot 'rpc-input.jsonl'
    $requestJson = @{
        id = $RequestId
        type = 'get_state'
    } | ConvertTo-Json -Compress
    [System.IO.File]::WriteAllText($inputPath, $requestJson + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.WorkingDirectory = $repoRoot

    $dotnetPath = (Get-Command dotnet -ErrorAction Stop).Source
    $agentArguments = @(
        $AssemblyPath,
        '--mode',
        'rpc',
        '--no-context-files',
        '--no-themes'
    )
    $isWindows = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)
    if ($isWindows) {
        $psi.FileName = if ($env:ComSpec) { $env:ComSpec } else { 'cmd.exe' }
        $commandLine = (ConvertTo-CmdArgument $dotnetPath)
        if ($agentArguments.Count -gt 0) {
            $commandLine += ' ' + (($agentArguments | ForEach-Object { ConvertTo-CmdArgument $_ }) -join ' ')
        }
        $commandLine += ' < ' + (ConvertTo-CmdArgument $inputPath)
        $psi.Arguments = '/d /s /c "' + $commandLine + '"'
    }
    else {
        $psi.FileName = '/bin/sh'
        $commandLine = (ConvertTo-ShArgument $dotnetPath)
        if ($agentArguments.Count -gt 0) {
            $commandLine += ' ' + (($agentArguments | ForEach-Object { ConvertTo-ShArgument $_ }) -join ' ')
        }
        $commandLine += ' < ' + (ConvertTo-ShArgument $inputPath)
        $psi.Arguments = '-c ' + (ConvertTo-ProcessArgument $commandLine)
    }
    Set-BenchmarkEnvironment -Environment $psi.EnvironmentVariables -StateRoot $StateRoot -EffectiveAgentDir $EffectiveAgentDir

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $psi
    $stdoutBuilder = [System.Text.StringBuilder]::new()

    $startedAt = [System.Diagnostics.Stopwatch]::StartNew()
    if (-not $process.Start()) {
        throw 'Failed to start Tau.CodingAgent RPC process.'
    }
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $stdoutLineTask = $process.StandardOutput.ReadLineAsync()

    $readyMs = $null
    $response = $null
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        while ($stdoutLineTask.IsCompleted) {
            $line = $stdoutLineTask.Result
            if ($null -eq $line) {
                break
            }

            [void]$stdoutBuilder.AppendLine($line)
            if ([string]::IsNullOrWhiteSpace($line)) {
                $stdoutLineTask = $process.StandardOutput.ReadLineAsync()
                continue
            }

            try {
                $parsed = $line | ConvertFrom-Json
            }
            catch {
                $stdoutLineTask = $process.StandardOutput.ReadLineAsync()
                continue
            }

            if ($parsed.type -eq 'response' -and
                $parsed.id -eq $RequestId -and
                $parsed.command -eq 'get_state') {
                $readyMs = $startedAt.Elapsed.TotalMilliseconds
                $response = $parsed
                break
            }

            $stdoutLineTask = $process.StandardOutput.ReadLineAsync()
        }

        if ($null -ne $readyMs) {
            break
        }

        if ($process.HasExited) {
            break
        }

        Start-Sleep -Milliseconds 10
    }

    if ($null -eq $readyMs) {
        try {
            $process.Kill()
        }
        catch {
        }

        $stderr = if ($stderrTask.Wait(1000)) { $stderrTask.Result } else { '' }
        throw "RPC startup benchmark did not receive get_state response within $TimeoutSeconds seconds. stderr=$stderr stdout=$($stdoutBuilder.ToString())"
    }

    if (-not $process.WaitForExit(5000)) {
        $process.Kill()
        throw 'Tau.CodingAgent RPC process did not exit after stdin closed.'
    }

    $stderr = if ($stderrTask.Wait(1000)) { $stderrTask.Result } else { '' }
    if ($process.ExitCode -ne 0) {
        throw "Tau.CodingAgent RPC process exited with code $($process.ExitCode). stderr=$stderr stdout=$($stdoutBuilder.ToString())"
    }

    if ($response.success -ne $true) {
        throw "RPC get_state response was not successful. stdout=$($stdoutBuilder.ToString())"
    }

    return [ordered]@{
        elapsedMs = [Math]::Round($readyMs, 1)
        stdoutBytes = [System.Text.Encoding]::UTF8.GetByteCount($stdoutBuilder.ToString())
        stderrBytes = [System.Text.Encoding]::UTF8.GetByteCount($stderr)
        exitCode = $process.ExitCode
    }
}

function Get-Summary {
    param(
        [Parameter(Mandatory = $true)]
        [decimal[]]$Values
    )

    $sorted = @($Values | Sort-Object)
    $sum = [decimal]0
    foreach ($value in $sorted) {
        $sum += $value
    }

    $middle = [int][Math]::Floor($sorted.Count / 2)
    $median = if (($sorted.Count % 2) -eq 0) {
        ($sorted[$middle - 1] + $sorted[$middle]) / 2
    }
    else {
        $sorted[$middle]
    }

    return [ordered]@{
        min = [Math]::Round($sorted[0], 1)
        median = [Math]::Round($median, 1)
        average = [Math]::Round($sum / $sorted.Count, 1)
        max = [Math]::Round($sorted[-1], 1)
    }
}

try {
    if ($Runs -le 0) {
        throw 'Runs must be greater than zero.'
    }
    if ($Warmup -lt 0) {
        throw 'Warmup must be zero or greater.'
    }
    if ($TimeoutSeconds -le 0) {
        throw 'TimeoutSeconds must be greater than zero.'
    }
    if (-not [string]::IsNullOrWhiteSpace($AgentDir) -and $IsolatedAgentDir) {
        throw '-AgentDir and -IsolatedAgentDir cannot be combined.'
    }

    $assemblyPath = Resolve-CodingAgentAssembly
    $profileRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("tau-coding-agent-profile-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $profileRoot | Out-Null
    $effectiveAgentDir = if (-not [string]::IsNullOrWhiteSpace($AgentDir)) {
        Convert-ToFullPath -Path $AgentDir -BasePath $invocationDirectory
    }
    elseif ($IsolatedAgentDir) {
        Join-Path $profileRoot 'agent'
    }
    else {
        ''
    }
    if (-not [string]::IsNullOrWhiteSpace($effectiveAgentDir)) {
        New-Item -ItemType Directory -Force -Path $effectiveAgentDir | Out-Null
    }

    $allRuns = @()
    $measuredRuns = @()
    $totalRuns = $Warmup + $Runs
    for ($runIndex = 0; $runIndex -lt $totalRuns; $runIndex++) {
        $isWarmup = $runIndex -lt $Warmup
        $stateRoot = Join-Path $profileRoot ('state-{0:000}' -f ($runIndex + 1))
        $requestId = 'startup-profile-{0:000}' -f ($runIndex + 1)
        $run = Invoke-RpcStartupRun -AssemblyPath $assemblyPath -StateRoot $stateRoot -RequestId $requestId -EffectiveAgentDir $effectiveAgentDir
        $entry = [ordered]@{
            index = $runIndex + 1
            measured = (-not $isWarmup)
            elapsedMs = $run.elapsedMs
            stdoutBytes = $run.stdoutBytes
            stderrBytes = $run.stderrBytes
            exitCode = $run.exitCode
        }
        $allRuns += $entry
        if (-not $isWarmup) {
            $measuredRuns += $entry
        }
    }

    $elapsedValues = @($measuredRuns | ForEach-Object { [decimal]$_.elapsedMs })
    $summary = Get-Summary -Values $elapsedValues
    $result = [ordered]@{
        schemaVersion = 1
        succeeded = $true
        mode = $Mode
        configuration = $Configuration
        assemblyPath = $assemblyPath
        runs = $Runs
        warmup = $Warmup
        offline = (-not $NoOffline.IsPresent)
        isolatedAgentDir = $IsolatedAgentDir.IsPresent
        agentDir = $effectiveAgentDir
        timeoutSeconds = $TimeoutSeconds
        summary = $summary
        measuredRuns = @($measuredRuns)
        allRuns = @($allRuns)
        warnings = $script:warnings
        remainingGaps = @(
            'TUI first-frame startup profiling still needs a Tau equivalent to upstream PI_STARTUP_BENCHMARK exit timing.',
            'This profiler measures dotnet Tau.CodingAgent.dll RPC startup, not dotnet run project evaluation time.'
        )
    }

    if ($Json) {
        $result | ConvertTo-Json -Depth 10
    }
    else {
        Write-Host 'Tau CodingAgent startup profile'
        Write-Host "  mode: $Mode"
        Write-Host "  assembly: $assemblyPath"
        Write-Host "  runs: $Runs warmup: $Warmup"
        Write-Host "  elapsed min: $($summary.min)ms"
        Write-Host "  elapsed median: $($summary.median)ms"
        Write-Host "  elapsed average: $($summary.average)ms"
        Write-Host "  elapsed max: $($summary.max)ms"
        Write-Host "METRIC startup_time_ms=$($summary.median)"
    }
}
catch {
    $result = [ordered]@{
        schemaVersion = 1
        succeeded = $false
        mode = $Mode
        runs = $Runs
        warmup = $Warmup
        error = $_.Exception.Message
        warnings = $script:warnings
    }

    if ($Json) {
        $result | ConvertTo-Json -Depth 8
    }
    else {
        Write-Host 'Tau CodingAgent startup profile failed'
        Write-Host $_.Exception.Message
    }

    exit 1
}
