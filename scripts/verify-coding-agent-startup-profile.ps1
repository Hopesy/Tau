param(
    [switch]$Json
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
Set-Location $repoRoot

$script:assertions = @()

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

try {
    $output = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repoRoot 'scripts/profile-coding-agent-startup.ps1') `
        -Mode rpc `
        -Runs 1 `
        -Warmup 1 `
        -IsolatedAgentDir `
        -TimeoutSeconds 30 `
        -Json 2>&1
    $exitCode = $LASTEXITCODE
    $outputText = ($output -join [Environment]::NewLine)
    if ($exitCode -ne 0) {
        throw "profile-coding-agent-startup.ps1 failed with exit code $exitCode. Output: $outputText"
    }

    $profile = $outputText | ConvertFrom-Json
    Add-Assertion -Name 'profile succeeded' -Passed ($profile.succeeded -eq $true) -Detail 'Startup profile did not succeed.'
    Add-Assertion -Name 'profile mode' -Passed ($profile.mode -eq 'rpc') -Detail "Expected rpc mode, actual $($profile.mode)."
    Add-Assertion -Name 'profile measured run count' -Passed (@($profile.measuredRuns).Count -eq 1) -Detail "Expected 1 measured run, actual $(@($profile.measuredRuns).Count)."
    Add-Assertion -Name 'profile all run count' -Passed (@($profile.allRuns).Count -eq 2) -Detail "Expected 2 total runs, actual $(@($profile.allRuns).Count)."
    Add-Assertion -Name 'profile warmup flag' -Passed ($profile.allRuns[0].measured -eq $false -and $profile.allRuns[1].measured -eq $true) -Detail 'Warmup/measured flags were not preserved.'
    Add-Assertion -Name 'profile positive elapsed' -Passed ([decimal]$profile.summary.median -gt 0) -Detail "Expected positive median, actual $($profile.summary.median)."
    Add-Assertion -Name 'profile assembly path' -Passed (([string]$profile.assemblyPath).EndsWith('Tau.CodingAgent.dll', [StringComparison]::OrdinalIgnoreCase)) -Detail "Unexpected assembly path: $($profile.assemblyPath)"
    Add-Assertion -Name 'profile gap audit' -Passed ((@($profile.remainingGaps) -join "`n") -match 'TUI') -Detail 'Expected TUI startup profiling gap to remain explicit.'

    $result = [ordered]@{
        schemaVersion = 1
        succeeded = $true
        assertions = $script:assertions
        medianMs = $profile.summary.median
    }

    if ($Json) {
        $result | ConvertTo-Json -Depth 8
    }
    else {
        Write-Host 'Tau CodingAgent startup profile smoke passed'
        Write-Host "  assertions: $($script:assertions.Count)"
        Write-Host "  median: $($profile.summary.median)ms"
    }
}
catch {
    $result = [ordered]@{
        schemaVersion = 1
        succeeded = $false
        assertions = $script:assertions
        error = $_.Exception.Message
    }

    if ($Json) {
        $result | ConvertTo-Json -Depth 8
    }
    else {
        Write-Host 'Tau CodingAgent startup profile smoke failed'
        Write-Host $_.Exception.Message
    }

    exit 1
}
