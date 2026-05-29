param(
    [switch]$Json,
    [switch]$KeepTemp
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
Set-Location $repoRoot

$script:assertions = @()
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("tau-edit-tool-stats-" + [Guid]::NewGuid().ToString('N'))
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
        [string[]]$Arguments
    )

    $scriptPath = Join-Path $repoRoot 'scripts/report-edit-tool-stats.ps1'
    $output = & powershell -NoProfile -ExecutionPolicy Bypass -File $scriptPath @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    $outputText = ($output -join [Environment]::NewLine)
    if ($exitCode -ne 0) {
        throw "report-edit-tool-stats.ps1 failed with exit code $exitCode. Output: $outputText"
    }

    try {
        return $outputText | ConvertFrom-Json
    }
    catch {
        throw "report-edit-tool-stats.ps1 did not return valid JSON. Output: $outputText"
    }
}

function Write-Utf8NoBomLines {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string[]]$Lines
    )

    [System.IO.File]::WriteAllLines($Path, $Lines, [System.Text.UTF8Encoding]::new($false))
}

try {
    New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
    $sessionPath = Join-Path $tempRoot 'tau-edit-stats-smoke.jsonl'

    $tauEditArgs1 = (@{
        path = 'src/App.cs'
        old_string = "public class App {`n    void Run() { }`n}"
        new_string = "public class App {`n    void Run() { Console.WriteLine(42); }`n}"
    } | ConvertTo-Json -Compress)
    $tauEditArgs2 = (@{
        path = 'src/App.cs'
        old_string = 'return 1;'
        new_string = 'return 2;'
    } | ConvertTo-Json -Compress)
    $upstreamEditArgs = (@{
        path = 'src/lib.ts'
        edits = @(
            @{ oldText = 'const a = 1;'; newText = 'const a = 2;' },
            @{ oldText = 'const b = 1;'; newText = 'const b = 2;' }
        )
    } | ConvertTo-Json -Depth 6 -Compress)
    $failedArgs = (@{
        path = 'README.md'
        old_string = 'missing'
        new_string = 'present'
    } | ConvertTo-Json -Compress)

    $lines = @(
        '{"type":"session","version":3,"id":"edit-stats-smoke","timestamp":"2026-05-29T11:00:00Z","cwd":"C:\\tmp\\tau-edit-stats"}',
        ('{{"type":"message","id":"a1","timestamp":"2026-05-29T11:01:00Z","message":{{"role":"assistant","provider":"openai","model":"gpt-5.4","content":[{{"type":"toolCall","id":"tool-1","name":"edit_file","arguments":{0}}},{{"type":"toolCall","id":"tool-2","name":"edit_file","arguments":{1}}}]}}}}' -f ($tauEditArgs1 | ConvertTo-Json -Compress), ($tauEditArgs2 | ConvertTo-Json -Compress)),
        '{"type":"message","id":"r1","parentId":"a1","timestamp":"2026-05-29T11:01:05Z","message":{"role":"toolResult","toolCallId":"tool-1","isError":false,"content":[{"type":"text","text":"Successfully edited src/App.cs"}]}}',
        '{"type":"message","id":"r2","parentId":"a1","timestamp":"2026-05-29T11:02:05Z","message":{"role":"toolResult","toolCallId":"tool-2","isError":false,"content":[{"type":"text","text":"Successfully edited src/App.cs"}]}}',
        ('{{"type":"message","id":"a3","timestamp":"2026-05-29T11:03:00Z","message":{{"role":"assistant","provider":"anthropic","model":"claude-sonnet-4","content":[{{"type":"toolCall","id":"tool-3","name":"edit","arguments":{0}}}]}}}}' -f ($upstreamEditArgs | ConvertTo-Json -Compress)),
        '{"type":"message","id":"r3","parentId":"a3","timestamp":"2026-05-29T11:03:05Z","message":{"role":"toolResult","toolCallId":"tool-3","isError":false,"content":[{"type":"text","text":"Updated src/lib.ts"}]}}',
        ('{{"type":"message","id":"a4","timestamp":"2026-05-29T11:04:00Z","message":{{"role":"assistant","provider":"openai","model":"gpt-5.4","content":[{{"type":"toolCall","id":"tool-4","name":"edit_file","arguments":{0}}}]}}}}' -f ($failedArgs | ConvertTo-Json -Compress)),
        '{"type":"message","id":"r4","parentId":"a4","timestamp":"2026-05-29T11:04:05Z","message":{"role":"toolResult","toolCallId":"tool-4","isError":true,"content":[{"type":"text","text":"old_string not found in the file."}]}}',
        'not-json'
    )
    Write-Utf8NoBomLines -Path $sessionPath -Lines $lines

    $summary = Invoke-JsonScript -Arguments @('-SessionPath', $sessionPath, '-Json', '-IncludeRecords')
    Add-Assertion -Name 'summary succeeded' -Passed ($summary.succeeded -eq $true) -Detail 'Expected edit stats summary to succeed.'
    Add-Assertion -Name 'call count' -Passed ([int]$summary.counts.totalEditCalls -eq 4) -Detail "Expected 4 edit calls, actual $($summary.counts.totalEditCalls)."
    Add-Assertion -Name 'success count' -Passed ([int]$summary.counts.success -eq 3) -Detail "Expected 3 successful edit calls, actual $($summary.counts.success)."
    Add-Assertion -Name 'failed count' -Passed ([int]$summary.counts.failed -eq 1) -Detail "Expected 1 failed edit call, actual $($summary.counts.failed)."
    Add-Assertion -Name 'single count' -Passed ([int]$summary.counts.single -eq 3) -Detail "Expected 3 single edit calls, actual $($summary.counts.single)."
    Add-Assertion -Name 'multi count' -Passed ([int]$summary.counts.multi -eq 1) -Detail "Expected 1 multi edit call, actual $($summary.counts.multi)."
    Add-Assertion -Name 'malformed line count' -Passed ([int]$summary.scan.malformedLineCount -eq 1) -Detail "Expected 1 malformed line, actual $($summary.scan.malformedLineCount)."
    Add-Assertion -Name 'same file cluster count' -Passed ([int]$summary.sameFileClusters.clustersCount -eq 1) -Detail "Expected one same-file cluster, actual $($summary.sameFileClusters.clustersCount)."
    Add-Assertion -Name 'failure kind' -Passed (@($summary.failureKinds | Where-Object { $_.kind -eq 'not_found_exact_text' -and [int]$_.count -eq 1 }).Count -eq 1) -Detail 'Expected not_found_exact_text failure kind.'
    Add-Assertion -Name 'tool names include Tau and upstream' -Passed (
        @($summary.toolNameStats | Where-Object { $_.key -eq 'edit_file' -and [int]$_.calls -eq 3 }).Count -eq 1 -and
        @($summary.toolNameStats | Where-Object { $_.key -eq 'edit' -and [int]$_.calls -eq 1 }).Count -eq 1
    ) -Detail 'Expected edit_file and edit tool-name groups.'
    Add-Assertion -Name 'extension stats include cs' -Passed (@($summary.extensionStats | Where-Object { $_.key -eq '.cs' -and [int]$_.calls -eq 2 }).Count -eq 1) -Detail 'Expected .cs extension stats.'
    Add-Assertion -Name 'records included' -Passed (@($summary.records).Count -eq 4) -Detail "Expected 4 included records, actual $(@($summary.records).Count)."

    $failedOnly = Invoke-JsonScript -Arguments @('-SessionPath', $sessionPath, '-FailedOnly', '-Json')
    Add-Assertion -Name 'failed-only count' -Passed ([int]$failedOnly.counts.totalEditCalls -eq 1 -and [int]$failedOnly.counts.failed -eq 1) -Detail 'Expected failed-only report to keep one failed call.'

    $tsOnly = Invoke-JsonScript -Arguments @('-SessionPath', $sessionPath, '-Extension', '.ts', '-Json')
    Add-Assertion -Name 'extension filter count' -Passed ([int]$tsOnly.counts.totalEditCalls -eq 1 -and [int]$tsOnly.counts.multi -eq 1) -Detail 'Expected .ts filter to keep one multi-edit call.'

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
        Write-Host 'Tau edit tool stats smoke passed'
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
        Write-Host 'Tau edit tool stats smoke failed'
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
