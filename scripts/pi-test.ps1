$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$invokeNoEnv = Join-Path $repoRoot 'scripts/invoke-no-env.ps1'

$env:DOTNET_CLI_HOME = if ($env:DOTNET_CLI_HOME) { $env:DOTNET_CLI_HOME } else { Join-Path $repoRoot '.dotnet' }
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = if ($env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE) { $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE } else { '1' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = if ($env:DOTNET_CLI_TELEMETRY_OPTOUT) { $env:DOTNET_CLI_TELEMETRY_OPTOUT } else { '1' }
$env:DOTNET_NOLOGO = if ($env:DOTNET_NOLOGO) { $env:DOTNET_NOLOGO } else { '1' }

New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME | Out-Null

function ConvertTo-ProcessArgument {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    if ($Value -notmatch '[\s"]') {
        return $Value
    }

    $escaped = $Value -replace '(\\*)"', '$1$1\"'
    $escaped = $escaped -replace '(\\+)$', '$1$1'
    return '"' + $escaped + '"'
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

function Get-OptionValue {
    param(
        [string[]]$Items,
        [int]$Index,
        [string]$Name
    )

    if ($Index + 1 -ge $Items.Count) {
        throw "Missing value for $Name"
    }

    return [string]$Items[$Index + 1]
}

function ConvertFrom-ArgumentListJson {
    param(
        [string]$Json,
        [string]$Base64
    )

    if (-not [string]::IsNullOrWhiteSpace($Base64)) {
        $bytes = [System.Convert]::FromBase64String($Base64)
        $Json = [System.Text.Encoding]::UTF8.GetString($bytes)
    }

    if ([string]::IsNullOrWhiteSpace($Json)) {
        return $null
    }

    $items = $Json | ConvertFrom-Json
    if ($null -eq $items) {
        return @()
    }

    if ($items -isnot [System.Array]) {
        throw 'Argument list must be a JSON array of strings.'
    }

    $result = @()
    foreach ($item in $items) {
        if ($null -eq $item) {
            $result += ''
        }
        else {
            $result += [string]$item
        }
    }

    return $result
}

function Invoke-DirectProcess {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [string[]]$Arguments = @(),
        [string]$InputFile = '',
        [string]$WorkingDirectory = '',
        [int]$TimeoutSeconds = 0
    )

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $false
    $psi.RedirectStandardOutput = $false
    $psi.RedirectStandardError = $false
    $psi.WorkingDirectory = $WorkingDirectory

    if (-not [string]::IsNullOrWhiteSpace($InputFile)) {
        $inputFileFull = [System.IO.Path]::GetFullPath($InputFile)
        if (-not (Test-Path -LiteralPath $inputFileFull)) {
            throw "Input file not found: $InputFile"
        }

        $psi.FileName = if ($env:ComSpec) { $env:ComSpec } else { 'cmd.exe' }
        $commandLine = '"' + $FilePath + '"'
        if ($Arguments.Count -gt 0) {
            $commandLine += ' ' + (($Arguments | ForEach-Object { ConvertTo-CmdArgument $_ }) -join ' ')
        }
        $commandLine += ' < "' + $inputFileFull + '"'
        $psi.Arguments = '/d /s /c "' + $commandLine + '"'
    }
    else {
        $psi.FileName = $FilePath
        $psi.Arguments = ($Arguments | ForEach-Object { ConvertTo-ProcessArgument $_ }) -join ' '
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $psi
    if (-not $process.Start()) {
        throw "Failed to start process: $FilePath"
    }

    if ($TimeoutSeconds -gt 0) {
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill()
            throw "Timed out running $FilePath $($Arguments -join ' ')"
        }
    }
    else {
        $process.WaitForExit()
    }

    return $process.ExitCode
}

$noEnv = $false
$inputFile = ''
$workingDirectory = $repoRoot
$tauStateRoot = ''
$timeoutSeconds = 0
$keepTemp = $false
$noBuild = $false
$childArgumentListJson = ''
$childArgumentListBase64 = ''
$childArguments = @()
$rawArguments = @($args)

for ($i = 0; $i -lt $rawArguments.Count; $i++) {
    $arg = [string]$rawArguments[$i]
    $lower = $arg.ToLowerInvariant()

    if ($arg -eq '--') {
        for ($j = $i + 1; $j -lt $rawArguments.Count; $j++) {
            $childArguments += [string]$rawArguments[$j]
        }
        break
    }

    if ($lower -eq '--no-env' -or $lower -eq '-noenv') {
        $noEnv = $true
        continue
    }

    if ($lower -eq '--keep-temp' -or $lower -eq '-keeptemp') {
        $keepTemp = $true
        continue
    }

    if ($lower -eq '--no-build' -or $lower -eq '-nobuild') {
        $noBuild = $true
        continue
    }

    if ($lower -eq '--input-file' -or $lower -eq '-inputfile') {
        $inputFile = Get-OptionValue -Items $rawArguments -Index $i -Name $arg
        $i++
        continue
    }

    if ($lower.StartsWith('--input-file=')) {
        $inputFile = $arg.Substring('--input-file='.Length)
        continue
    }

    if ($lower -eq '--working-directory' -or $lower -eq '-workingdirectory') {
        $workingDirectory = Get-OptionValue -Items $rawArguments -Index $i -Name $arg
        $i++
        continue
    }

    if ($lower.StartsWith('--working-directory=')) {
        $workingDirectory = $arg.Substring('--working-directory='.Length)
        continue
    }

    if ($lower -eq '--tau-state-root' -or $lower -eq '-taustateroot') {
        $tauStateRoot = Get-OptionValue -Items $rawArguments -Index $i -Name $arg
        $i++
        continue
    }

    if ($lower.StartsWith('--tau-state-root=')) {
        $tauStateRoot = $arg.Substring('--tau-state-root='.Length)
        continue
    }

    if ($lower -eq '--timeout-seconds' -or $lower -eq '-timeoutseconds') {
        $timeoutValue = Get-OptionValue -Items $rawArguments -Index $i -Name $arg
        $timeoutSeconds = [int]$timeoutValue
        $i++
        continue
    }

    if ($lower.StartsWith('--timeout-seconds=')) {
        $timeoutSeconds = [int]$arg.Substring('--timeout-seconds='.Length)
        continue
    }

    if ($lower -eq '--argument-list-json' -or $lower -eq '-argumentlistjson') {
        $childArgumentListJson = Get-OptionValue -Items $rawArguments -Index $i -Name $arg
        $i++
        continue
    }

    if ($lower.StartsWith('--argument-list-json=')) {
        $childArgumentListJson = $arg.Substring('--argument-list-json='.Length)
        continue
    }

    if ($lower -eq '--argument-list-base64' -or $lower -eq '-argumentlistbase64') {
        $childArgumentListBase64 = Get-OptionValue -Items $rawArguments -Index $i -Name $arg
        $i++
        continue
    }

    if ($lower.StartsWith('--argument-list-base64=')) {
        $childArgumentListBase64 = $arg.Substring('--argument-list-base64='.Length)
        continue
    }

    $childArguments += $arg
}

$jsonChildArguments = ConvertFrom-ArgumentListJson -Json $childArgumentListJson -Base64 $childArgumentListBase64
if ($null -ne $jsonChildArguments) {
    $childArguments = @($jsonChildArguments)
}

$workingDirectory = [System.IO.Path]::GetFullPath($workingDirectory)
$inputFileFull = ''
if (-not [string]::IsNullOrWhiteSpace($inputFile)) {
    $inputFileFull = [System.IO.Path]::GetFullPath($inputFile)
}

$dotnetArguments = @(
    'run',
    '--project',
    (Join-Path $repoRoot 'src/Tau.CodingAgent/Tau.CodingAgent.csproj')
)

if ($noBuild) {
    $dotnetArguments += '--no-build'
}

$dotnetArguments += '--'
$dotnetArguments += $childArguments

if (-not $noEnv) {
    exit (Invoke-DirectProcess -FilePath 'dotnet' -Arguments $dotnetArguments -InputFile $inputFileFull -WorkingDirectory $workingDirectory -TimeoutSeconds $timeoutSeconds)
}

$generatedStateRoot = $false
if ([string]::IsNullOrWhiteSpace($tauStateRoot)) {
    $tauStateRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("tau-pi-test-" + [Guid]::NewGuid().ToString('N'))
    $generatedStateRoot = $true
}

$tauStateRoot = [System.IO.Path]::GetFullPath($tauStateRoot)
$scriptSucceeded = $false

try {
    Write-Host 'Running Tau CodingAgent without API keys...'

    $argumentJson = ConvertTo-Json -InputObject $dotnetArguments -Compress
    $argumentBase64 = [System.Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($argumentJson))
    $invokeArguments = @(
        '-NoProfile',
        '-ExecutionPolicy',
        'Bypass',
        '-File',
        $invokeNoEnv,
        '-WorkingDirectory',
        $workingDirectory,
        '-TauStateRoot',
        $tauStateRoot,
        '-FilePath',
        'dotnet',
        '-ArgumentListBase64',
        $argumentBase64
    )

    if (-not [string]::IsNullOrWhiteSpace($inputFileFull)) {
        $invokeArguments += @('-InputFile', $inputFileFull)
    }

    if ($timeoutSeconds -gt 0) {
        $invokeArguments += @('-TimeoutSeconds', [string]$timeoutSeconds)
    }

    & powershell @invokeArguments
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        exit $exitCode
    }

    $scriptSucceeded = $true
}
finally {
    if ($generatedStateRoot -and $scriptSucceeded -and -not $keepTemp) {
        Remove-Item -LiteralPath $tauStateRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    elseif ($generatedStateRoot -and -not $scriptSucceeded) {
        Write-Host "pi-test no-env temp state kept for inspection: $tauStateRoot"
    }
}
