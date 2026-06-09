param(
    [switch]$Json,
    [switch]$KeepTemp
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
Set-Location $repoRoot

$script:assertions = @()
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("tau-session-audit-" + [Guid]::NewGuid().ToString('N'))
$scriptSucceeded = $false

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

function Invoke-JsonScript {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $true)]
        [string]$ScriptPath,
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    $output = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repoRoot $ScriptPath) @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    $outputText = ($output -join [Environment]::NewLine)
    if ($exitCode -ne 0) {
        throw "$Name failed with exit code $exitCode. Output: $outputText"
    }

    try {
        return $outputText | ConvertFrom-Json
    }
    catch {
        throw "$Name did not return valid JSON. Output: $outputText"
    }
}

try {
    New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
    $sessionPath = Join-Path $tempRoot 'tau-session-smoke.jsonl'
    $outputDirectory = Join-Path $tempRoot 'out'

    $lines = @(
        '{"type":"session","version":3,"id":"session-smoke","timestamp":"2026-05-29T01:00:00Z","cwd":"C:\\tmp\\tau-smoke"}',
        '{"type":"message","id":"u1","timestamp":"2026-05-29T01:01:00Z","message":{"role":"user","content":[{"type":"text","text":"hello tau"}]}}',
        '{"type":"message","id":"a1","parentId":"u1","timestamp":"2026-05-29T01:02:00Z","provider":"openai","message":{"role":"assistant","provider":"openai","content":[{"type":"text","text":"hi user"}],"usage":{"inputTokens":10,"outputTokens":20,"cacheReadTokens":3,"cacheWriteTokens":4,"cost":{"total":0.1234,"input":0.01,"output":0.1,"cacheRead":0.003,"cacheWrite":0.0104}}}}',
        '{"type":"message","id":"u2","parentId":"a1","timestamp":"2026-05-29T01:03:00Z","message":{"role":"user","content":"string content from upstream shape"}}',
        '{"type":"message","id":"a2","parentId":"u2","timestamp":"2026-05-29T01:04:00Z","provider":"anthropic","message":{"role":"assistant","content":[{"type":"text","text":"token only"}],"usage":{"inputTokens":7,"outputTokens":8}}}',
        '{"type":"message","id":"t1","parentId":"a2","timestamp":"2026-05-29T01:05:00Z","message":{"role":"toolResult","content":[{"type":"text","text":"ignored"}]}}',
        'not-json'
    )
    [System.IO.File]::WriteAllLines($sessionPath, $lines, [System.Text.UTF8Encoding]::new($false))

    $transcript = Invoke-JsonScript `
        -Name 'export-session-transcripts' `
        -ScriptPath 'scripts/export-session-transcripts.ps1' `
        -Arguments @('-SessionPath', $sessionPath, '-OutputDirectory', $outputDirectory, '-MaxCharsPerFile', '120', '-Json')
    $cost = Invoke-JsonScript `
        -Name 'report-session-costs' `
        -ScriptPath 'scripts/report-session-costs.ps1' `
        -Arguments @('-Directory', $tempRoot, '-Days', '36500', '-SessionPath', $sessionPath, '-Json')

    Add-Assertion -Name 'transcript succeeded' -Passed ($transcript.succeeded -eq $true) -Detail 'Transcript export did not succeed.'
    Add-Assertion -Name 'transcript message count' -Passed ($transcript.messageCount -eq 4) -Detail "Expected 4 transcript messages, actual $($transcript.messageCount)."
    Add-Assertion -Name 'transcript bad line count' -Passed ($transcript.malformedLineCount -eq 1) -Detail "Expected 1 malformed transcript line, actual $($transcript.malformedLineCount)."
    Add-Assertion -Name 'transcript output file count' -Passed (@($transcript.outputFiles).Count -eq 1) -Detail "Expected 1 transcript output file, actual $(@($transcript.outputFiles).Count)."

    $transcriptFile = Join-Path $outputDirectory 'session-transcripts-000.txt'
    Add-Assertion -Name 'transcript file exists' -Passed (Test-Path -LiteralPath $transcriptFile -PathType Leaf) -Detail "Transcript file was not written: $transcriptFile"
    $transcriptText = Get-Content -LiteralPath $transcriptFile -Raw
    Add-Assertion -Name 'transcript includes user text' -Passed ($transcriptText -match '\[USER\]\s+hello tau') -Detail 'Transcript file did not include user text.'
    Add-Assertion -Name 'transcript includes upstream string content' -Passed ($transcriptText -match 'string content from upstream shape') -Detail 'Transcript file did not include string content.'
    Add-Assertion -Name 'transcript ignores tool result' -Passed ($transcriptText -notmatch 'ignored') -Detail 'Transcript file included tool result content.'

    Add-Assertion -Name 'cost succeeded' -Passed ($cost.succeeded -eq $true) -Detail 'Cost report did not succeed.'
    Add-Assertion -Name 'cost record count' -Passed ($cost.costRecords -eq 1) -Detail "Expected 1 cost record, actual $($cost.costRecords)."
    Add-Assertion -Name 'token record count' -Passed ($cost.tokenRecords -eq 2) -Detail "Expected 2 token records, actual $($cost.tokenRecords)."
    Add-Assertion -Name 'cost total' -Passed ([decimal]$cost.grandTotal -eq [decimal]0.1234) -Detail "Expected grand total 0.1234, actual $($cost.grandTotal)."
    Add-Assertion -Name 'cost day array shape' -Passed (@($cost.daysBreakdown).Count -eq 1) -Detail 'Expected daysBreakdown to remain an array with one day.'
    Add-Assertion -Name 'cost provider array shape' -Passed (@($cost.daysBreakdown[0].providers).Count -eq 2) -Detail 'Expected providers to remain an array with two providers.'
    Add-Assertion -Name 'cost bad line count' -Passed ($cost.malformedLineCount -eq 1) -Detail "Expected 1 malformed cost line, actual $($cost.malformedLineCount)."

    $scriptSucceeded = $true
    $result = [ordered]@{
        schemaVersion = 1
        succeeded = $true
        tempRoot = $tempRoot
        assertions = $script:assertions
    }

    if ($Json) {
        $result | ConvertTo-Json -Depth 8
    }
    else {
        Write-Host 'Tau session audit script smoke passed'
        Write-Host "  assertions: $($script:assertions.Count)"
        Write-Host "  temp root: $tempRoot"
    }
}
catch {
    $result = [ordered]@{
        schemaVersion = 1
        succeeded = $false
        tempRoot = $tempRoot
        assertions = $script:assertions
        error = $_.Exception.Message
    }

    if ($Json) {
        $result | ConvertTo-Json -Depth 8
    }
    else {
        Write-Host 'Tau session audit script smoke failed'
        Write-Host $_.Exception.Message
        Write-Host "temp root: $tempRoot"
    }

    exit 1
}
finally {
    if ($scriptSucceeded -and -not $KeepTemp) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
