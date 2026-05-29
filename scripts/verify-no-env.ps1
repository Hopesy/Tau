param(
    [switch]$SkipRestore,
    [switch]$RunSmoke,
    [string]$ArtifactRoot = '',
    [switch]$KeepTemp
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
Set-Location $repoRoot

$invokeNoEnv = Join-Path $repoRoot 'scripts/invoke-no-env.ps1'
$stateRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("tau-no-env-" + [Guid]::NewGuid().ToString('N'))
$scriptSucceeded = $false

function Invoke-NoEnvProcess {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [string[]]$Arguments = @(),
        [string]$InputFile = '',
        [switch]$Capture,
        [int]$TimeoutSeconds = 0
    )

    $baseArguments = @(
        '-NoProfile',
        '-ExecutionPolicy',
        'Bypass',
        '-File',
        $script:invokeNoEnv,
        '-WorkingDirectory',
        $script:repoRoot,
        '-TauStateRoot',
        $script:stateRoot
    )

    if (-not [string]::IsNullOrWhiteSpace($InputFile)) {
        $baseArguments += @('-InputFile', $InputFile)
    }

    if ($TimeoutSeconds -gt 0) {
        $baseArguments += @('-TimeoutSeconds', [string]$TimeoutSeconds)
    }

    $baseArguments += @('-FilePath', $FilePath)
    if ($Arguments.Count -gt 0) {
        $argumentJson = ConvertTo-Json -InputObject $Arguments -Compress
        $argumentBase64 = [System.Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($argumentJson))
        $baseArguments += @('-ArgumentListBase64', $argumentBase64)
    }

    if ($Capture) {
        $output = & powershell @baseArguments 2>&1
        return [pscustomobject]@{
            ExitCode = $LASTEXITCODE
            Output = ($output -join [Environment]::NewLine)
        }
    }

    & powershell @baseArguments 2>&1 | ForEach-Object { Write-Host $_ }
    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output = ''
    }
}

function Assert-Success {
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

try {
    New-Item -ItemType Directory -Force -Path $stateRoot | Out-Null

    Write-Host '==> no-env dotnet validation'
    $verifyArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $repoRoot 'scripts/verify-dotnet.ps1'))
    if ($SkipRestore) {
        $verifyArgs += '-SkipRestore'
    }
    if ($RunSmoke) {
        $verifyArgs += '-RunSmoke'
    }

    $verifyResult = Invoke-NoEnvProcess -FilePath 'powershell' -Arguments $verifyArgs
    if ($verifyResult.ExitCode -ne 0) {
        exit $verifyResult.ExitCode
    }

    if ($RunSmoke) {
        Write-Host '==> no-env tau-ai list'
        $aiResult = Invoke-NoEnvProcess `
            -FilePath 'dotnet' `
            -Arguments @('run', '--project', 'src/Tau.Ai.Cli/Tau.Ai.Cli.csproj', '--no-build', '--', 'list') `
            -Capture `
            -TimeoutSeconds 30
        Assert-Success -Result $aiResult -Label 'no-env tau-ai list' -Patterns @('Available OAuth providers:', 'anthropic', 'openai-codex')

        Write-Host '==> no-env coding-agent rpc'
        $rpcInputPath = Join-Path $stateRoot 'rpc-input.jsonl'
        [System.IO.File]::WriteAllText($rpcInputPath, '{"id":"no-env-smoke","type":"get_state"}' + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
        $rpcChildArgumentsJson = ConvertTo-Json -InputObject @('--mode', 'rpc', '--no-context-files', '--no-themes') -Compress
        $rpcChildArgumentsBase64 = [System.Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($rpcChildArgumentsJson))
        $rpcResult = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repoRoot 'scripts/pi-test.ps1') `
            --no-env `
            --no-build `
            --tau-state-root $stateRoot `
            --input-file $rpcInputPath `
            --timeout-seconds 30 `
            --argument-list-base64 $rpcChildArgumentsBase64 2>&1
        $rpcResult = [pscustomobject]@{
            ExitCode = $LASTEXITCODE
            Output = ($rpcResult -join [Environment]::NewLine)
        }
        Assert-Success -Result $rpcResult -Label 'no-env coding-agent rpc get_state' -Patterns @('"command":"get_state"', '"success":true')

        if (-not [string]::IsNullOrWhiteSpace($ArtifactRoot)) {
            Write-Host '==> no-env release artifact smoke'
            $artifactResult = Invoke-NoEnvProcess `
                -FilePath 'powershell' `
                -Arguments @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $repoRoot 'scripts/smoke-release-artifacts.ps1'), '-ArtifactRoot', $ArtifactRoot) `
                -TimeoutSeconds 120
            if ($artifactResult.ExitCode -ne 0) {
                exit $artifactResult.ExitCode
            }
        }
    }

    $authPath = Join-Path $stateRoot 'auth.json'
    $modelsPath = Join-Path $stateRoot 'models.json'
    if (-not (Test-Path -LiteralPath $authPath)) {
        throw "no-env validation did not create isolated auth file: $authPath"
    }
    if (-not (Test-Path -LiteralPath $modelsPath)) {
        throw "no-env validation did not create isolated models file: $modelsPath"
    }

    $authContent = Get-Content -LiteralPath $authPath -Raw
    if ($authContent -notmatch '^\s*\{\s*\}\s*$') {
        throw 'no-env validation expected isolated auth.json to remain empty'
    }

    $scriptSucceeded = $true
    Write-Host 'Tau no-env validation passed'
}
finally {
    if ($scriptSucceeded -and -not $KeepTemp) {
        Remove-Item -LiteralPath $stateRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    elseif (-not $scriptSucceeded) {
        Write-Host "no-env temp state kept for inspection: $stateRoot"
    }
}
