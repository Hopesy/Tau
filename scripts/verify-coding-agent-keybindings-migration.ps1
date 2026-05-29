param(
    [switch]$Json,
    [switch]$KeepTemp
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
Set-Location $repoRoot

$script:assertions = @()
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("tau-coding-agent-keybindings-migration-" + [Guid]::NewGuid().ToString('N'))
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

function Invoke-Migration {
    param(
        [Parameter(Mandatory = $true)]
        [string]$AgentDirectory,
        [string]$KeybindingsPath = '',
        [switch]$Apply
    )

    $scriptPath = Join-Path $repoRoot 'scripts/migrate-coding-agent-keybindings.ps1'
    if ($Apply) {
        if ([string]::IsNullOrWhiteSpace($KeybindingsPath)) {
            $output = & $scriptPath -AgentDirectory $AgentDirectory -Apply -Json 2>&1
        }
        else {
            $output = & $scriptPath -AgentDirectory $AgentDirectory -KeybindingsPath $KeybindingsPath -Apply -Json 2>&1
        }
    }
    else {
        if ([string]::IsNullOrWhiteSpace($KeybindingsPath)) {
            $output = & $scriptPath -AgentDirectory $AgentDirectory -Json 2>&1
        }
        else {
            $output = & $scriptPath -AgentDirectory $AgentDirectory -KeybindingsPath $KeybindingsPath -Json 2>&1
        }
    }
    $outputText = ($output -join [Environment]::NewLine)

    try {
        return $outputText | ConvertFrom-Json
    }
    catch {
        throw "migrate-coding-agent-keybindings.ps1 did not return valid JSON. Output: $outputText"
    }
}

function Write-Utf8NoBomText {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Text
    )

    [System.IO.File]::WriteAllText($Path, $Text, [System.Text.UTF8Encoding]::new($false))
}

try {
    $agentDir = Join-Path $tempRoot '.tau'
    New-Item -ItemType Directory -Force -Path $agentDir | Out-Null
    $keybindingsPath = Join-Path $agentDir 'keybindings.json'
    $legacyJson = @'
{
  "cursorUp": "ctrl+k",
  "cursorLeft": ["left", "ctrl+b"],
  "submit": "enter",
  "cycleModelForward": "ctrl+p",
  "selectModel": "ctrl+l",
  "treeFoldOrUp": ["ctrl+left", "alt+left"],
  "app.model.select": "ctrl+m",
  "custom.local": "f13"
}
'@
    Write-Utf8NoBomText -Path $keybindingsPath -Text $legacyJson

    $dryRun = Invoke-Migration -AgentDirectory $agentDir
    Add-Assertion -Name 'dry-run succeeded' -Passed ($dryRun.succeeded -eq $true -and $dryRun.dryRun -eq $true) -Detail 'Expected dry-run keybindings migration to succeed.'
    Add-Assertion -Name 'dry-run migratable count' -Passed ([int]$dryRun.scan.migratable -eq 1) -Detail "Expected one migratable keybindings file, actual $($dryRun.scan.migratable)."
    Add-Assertion -Name 'dry-run changed key count' -Passed ([int]$dryRun.scan.changedKeyCount -eq 5) -Detail "Expected five changed key ids after one conflict skip, actual $($dryRun.scan.changedKeyCount)."
    Add-Assertion -Name 'dry-run conflict count' -Passed ([int]$dryRun.scan.skippedConflictCount -eq 1) -Detail "Expected one legacy key conflict skip, actual $($dryRun.scan.skippedConflictCount)."
    Add-Assertion -Name 'dry-run preserves extra keys count' -Passed ([int]$dryRun.scan.preservedExtraKeyCount -eq 1) -Detail "Expected one preserved extra key, actual $($dryRun.scan.preservedExtraKeyCount)."
    Add-Assertion -Name 'dry-run marks would-migrate' -Passed ($dryRun.keybindings.action -eq 'would-migrate') -Detail 'Expected dry-run action to be would-migrate.'
    Add-Assertion -Name 'dry-run keeps source untouched' -Passed ((Get-Content -LiteralPath $keybindingsPath -Raw) -eq $legacyJson) -Detail 'Dry-run unexpectedly rewrote keybindings.json.'
    Add-Assertion -Name 'dry-run conflict reports canonical key' -Passed (
        @($dryRun.keybindings.skippedConflicts | Where-Object {
            $_.legacyKey -eq 'selectModel' -and $_.canonicalKey -eq 'app.model.select'
        }).Count -eq 1
    ) -Detail 'Expected selectModel legacy key to be skipped when canonical app.model.select already exists.'
    Add-Assertion -Name 'remaining gaps keep runtime parity open' -Passed ((@($dryRun.remainingGaps) -join "`n") -match 'runtime parity') -Detail 'Expected remaining gaps to keep full keybinding runtime parity open.'

    $applied = Invoke-Migration -AgentDirectory $agentDir -Apply
    Add-Assertion -Name 'apply succeeded' -Passed ($applied.succeeded -eq $true -and $applied.applied -eq $true) -Detail 'Expected apply keybindings migration to succeed.'
    Add-Assertion -Name 'apply migrated count' -Passed ([int]$applied.scan.migrated -eq 1) -Detail "Expected apply to migrate one file, actual $($applied.scan.migrated)."
    Add-Assertion -Name 'apply failed count' -Passed ([int]$applied.scan.failed -eq 0) -Detail "Expected apply failures 0, actual $($applied.scan.failed)."

    $rewritten = Get-Content -LiteralPath $keybindingsPath -Raw | ConvertFrom-Json
    Add-Assertion -Name 'apply migrates editor key' -Passed ($rewritten.'tui.editor.cursorUp' -eq 'ctrl+k') -Detail 'Expected cursorUp to migrate to tui.editor.cursorUp.'
    Add-Assertion -Name 'apply migrates array key' -Passed (
        @($rewritten.'tui.editor.cursorLeft')[0] -eq 'left' -and
        @($rewritten.'tui.editor.cursorLeft')[1] -eq 'ctrl+b'
    ) -Detail 'Expected cursorLeft array binding to be preserved under canonical key.'
    Add-Assertion -Name 'apply migrates input submit key' -Passed ($rewritten.'tui.input.submit' -eq 'enter') -Detail 'Expected submit to migrate to tui.input.submit.'
    Add-Assertion -Name 'apply migrates app model cycle key' -Passed ($rewritten.'app.model.cycleForward' -eq 'ctrl+p') -Detail 'Expected cycleModelForward to migrate to app.model.cycleForward.'
    Add-Assertion -Name 'apply keeps canonical conflict winner' -Passed ($rewritten.'app.model.select' -eq 'ctrl+m') -Detail 'Expected canonical app.model.select to win over legacy selectModel.'
    Add-Assertion -Name 'apply removes legacy conflict key' -Passed (-not ($rewritten.PSObject.Properties.Name -contains 'selectModel')) -Detail 'Expected legacy selectModel key to be dropped after conflict.'
    Add-Assertion -Name 'apply preserves custom extra key' -Passed ($rewritten.'custom.local' -eq 'f13') -Detail 'Expected custom extra key to be preserved.'

    $idempotent = Invoke-Migration -AgentDirectory $agentDir -Apply
    Add-Assertion -Name 'idempotent apply' -Passed (
        [int]$idempotent.scan.migrated -eq 0 -and
        [int]$idempotent.scan.skipped -eq 1 -and
        $idempotent.keybindings.reason -eq 'no-legacy-keybindings'
    ) -Detail 'Expected second apply to skip with no-legacy-keybindings.'

    $tauArrayFile = Join-Path $agentDir 'coding-agent-keybindings.json'
    Write-Utf8NoBomText -Path $tauArrayFile -Text @'
{
  "bindings": [
    { "key": "F1", "modifiers": [], "action": "Cancel" }
  ]
}
'@
    $arrayStyle = Invoke-Migration -AgentDirectory $agentDir -KeybindingsPath $tauArrayFile -Apply
    Add-Assertion -Name 'array-style Tau keybindings not rewritten' -Passed (
        [int]$arrayStyle.scan.migrated -eq 0 -and
        $arrayStyle.keybindings.reason -eq 'no-legacy-keybindings' -and
        (Get-Content -LiteralPath $tauArrayFile -Raw) -match '"bindings"'
    ) -Detail 'Expected Tau array-style coding-agent-keybindings.json to remain untouched.'

    $invalidFile = Join-Path $agentDir 'invalid-keybindings.json'
    Write-Utf8NoBomText -Path $invalidFile -Text '{not json'
    $invalid = Invoke-Migration -AgentDirectory $agentDir -KeybindingsPath $invalidFile
    Add-Assertion -Name 'invalid json skipped' -Passed (
        $invalid.succeeded -eq $true -and
        [int]$invalid.scan.skipped -eq 1 -and
        $invalid.keybindings.reason -eq 'invalid-json'
    ) -Detail 'Expected invalid JSON keybindings file to be skipped without failure.'

    $missingAgent = Join-Path $tempRoot 'missing-agent'
    $missing = Invoke-Migration -AgentDirectory $missingAgent
    Add-Assertion -Name 'missing file skipped' -Passed (
        $missing.succeeded -eq $true -and
        [int]$missing.scan.skipped -eq 1 -and
        $missing.keybindings.reason -eq 'file-missing'
    ) -Detail 'Expected missing keybindings file to be skipped.'

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
        Write-Host 'Tau CodingAgent keybindings migration smoke passed'
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
        Write-Host 'Tau CodingAgent keybindings migration smoke failed'
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
