param(
    [switch]$Json,
    [switch]$KeepTemp
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
Set-Location $repoRoot

$script:assertions = @()
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("tau-coding-agent-deprecated-extension-dirs-audit-" + [Guid]::NewGuid().ToString('N'))
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

function Invoke-Audit {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$BaseDirectories,
        [string[]]$Labels = @()
    )

    $scriptPath = Join-Path $repoRoot 'scripts/audit-coding-agent-deprecated-extension-dirs.ps1'
    if ($Labels.Count -gt 0) {
        $output = & $scriptPath -BaseDirectory $BaseDirectories -Label $Labels -Json 2>&1
    }
    else {
        $output = & $scriptPath -BaseDirectory $BaseDirectories -Json 2>&1
    }
    $outputText = ($output -join [Environment]::NewLine)

    try {
        return $outputText | ConvertFrom-Json
    }
    catch {
        throw "audit-coding-agent-deprecated-extension-dirs.ps1 did not return valid JSON. Output: $outputText"
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

function Get-DirectoryAudit {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Summary,
        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    $matches = @($Summary.directories | Where-Object { $_.label -eq $Label })
    if ($matches.Count -ne 1) {
        throw "Expected exactly one audit result for label $Label, actual $($matches.Count)."
    }

    return $matches[0]
}

try {
    $globalBase = Join-Path $tempRoot 'global\.tau'
    $projectBase = Join-Path $tempRoot 'project\.tau'
    $managedOnlyBase = Join-Path $tempRoot 'managed-only\.tau'
    $fileToolsBase = Join-Path $tempRoot 'file-tools\.tau'
    $missingBase = Join-Path $tempRoot 'missing\.tau'

    New-Item -ItemType Directory -Force -Path (Join-Path $globalBase 'hooks') | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $globalBase 'tools') | Out-Null
    Write-Utf8NoBomText -Path (Join-Path (Join-Path $globalBase 'tools') 'rg') -Text 'managed rg'
    Write-Utf8NoBomText -Path (Join-Path (Join-Path $globalBase 'tools') 'deploy.ps1') -Text 'custom global tool'
    Write-Utf8NoBomText -Path (Join-Path (Join-Path $globalBase 'tools') '.DS_Store') -Text 'ignored hidden'

    New-Item -ItemType Directory -Force -Path (Join-Path $projectBase 'tools') | Out-Null
    Write-Utf8NoBomText -Path (Join-Path (Join-Path $projectBase 'tools') 'custom-tool') -Text 'custom project tool'

    New-Item -ItemType Directory -Force -Path (Join-Path $managedOnlyBase 'tools') | Out-Null
    Write-Utf8NoBomText -Path (Join-Path (Join-Path $managedOnlyBase 'tools') 'fd') -Text 'managed fd'
    Write-Utf8NoBomText -Path (Join-Path (Join-Path $managedOnlyBase 'tools') 'RG.EXE') -Text 'managed rg exe'
    Write-Utf8NoBomText -Path (Join-Path (Join-Path $managedOnlyBase 'tools') '.ignored') -Text 'ignored hidden'

    New-Item -ItemType Directory -Force -Path $fileToolsBase | Out-Null
    Write-Utf8NoBomText -Path (Join-Path $fileToolsBase 'tools') -Text 'not a directory'

    $baseDirectories = @($globalBase, $projectBase, $managedOnlyBase, $fileToolsBase, $missingBase)
    $labels = @('Global', 'Project', 'ManagedOnly', 'FileTools', 'Missing')
    $audit = Invoke-Audit -BaseDirectories $baseDirectories -Labels $labels

    Add-Assertion -Name 'audit succeeded' -Passed ($audit.succeeded -eq $true) -Detail 'Expected deprecated extension dirs audit to succeed.'
    Add-Assertion -Name 'audit base count' -Passed ([int]$audit.baseDirectoryCount -eq 5) -Detail "Expected 5 base directories, actual $($audit.baseDirectoryCount)."
    Add-Assertion -Name 'warning count' -Passed ([int]$audit.scan.warningCount -eq 3) -Detail "Expected 3 warnings, actual $($audit.scan.warningCount)."
    Add-Assertion -Name 'hook warning count' -Passed ([int]$audit.scan.hookWarnings -eq 1) -Detail "Expected 1 hooks warning, actual $($audit.scan.hookWarnings)."
    Add-Assertion -Name 'custom tools warning count' -Passed ([int]$audit.scan.customToolsWarnings -eq 2) -Detail "Expected 2 custom tools warnings, actual $($audit.scan.customToolsWarnings)."
    Add-Assertion -Name 'tools read error count' -Passed ([int]$audit.scan.toolsReadErrors -eq 1) -Detail "Expected 1 tools read error for file source, actual $($audit.scan.toolsReadErrors)."

    $globalAudit = Get-DirectoryAudit -Summary $audit -Label 'Global'
    $projectAudit = Get-DirectoryAudit -Summary $audit -Label 'Project'
    $managedOnlyAudit = Get-DirectoryAudit -Summary $audit -Label 'ManagedOnly'
    $fileToolsAudit = Get-DirectoryAudit -Summary $audit -Label 'FileTools'
    $missingAudit = Get-DirectoryAudit -Summary $audit -Label 'Missing'

    Add-Assertion -Name 'global hooks warning' -Passed ((@($globalAudit.warnings) -join "`n") -match 'Global hooks/ directory found') -Detail 'Expected global hooks warning.'
    Add-Assertion -Name 'global custom tools warning' -Passed ((@($globalAudit.warnings) -join "`n") -match 'Global tools/ directory contains custom tools') -Detail 'Expected global custom tools warning.'
    Add-Assertion -Name 'global custom tool listed' -Passed ((@($globalAudit.customTools) -contains 'deploy.ps1') -and [int]$globalAudit.customToolCount -eq 1) -Detail 'Expected deploy.ps1 as the only global custom tool.'
    Add-Assertion -Name 'global managed and hidden ignored' -Passed ((@($globalAudit.ignoredManagedBinaries) -contains 'rg') -and (@($globalAudit.ignoredHiddenEntries) -contains '.DS_Store')) -Detail 'Expected managed rg and hidden file to be ignored.'

    Add-Assertion -Name 'project custom tool warning' -Passed ((@($projectAudit.warnings) -join "`n") -match 'Project tools/ directory contains custom tools') -Detail 'Expected project custom tools warning.'
    Add-Assertion -Name 'project no hooks warning' -Passed (-not ((@($projectAudit.warnings) -join "`n") -match 'hooks/')) -Detail 'Project should not warn about hooks.'

    Add-Assertion -Name 'managed-only no warnings' -Passed ([int]$managedOnlyAudit.warningCount -eq 0) -Detail 'Managed-only tools should not produce warnings.'
    Add-Assertion -Name 'managed-only case insensitive managed names' -Passed ((@($managedOnlyAudit.ignoredManagedBinaries) -contains 'RG.EXE') -and [int]$managedOnlyAudit.customToolCount -eq 0) -Detail 'Expected RG.EXE to be treated as a managed binary.'
    Add-Assertion -Name 'file tools path not custom warning' -Passed ([int]$fileToolsAudit.warningCount -eq 0 -and $fileToolsAudit.toolsReadError -eq 'tools-path-not-directory') -Detail 'File named tools should not be treated as custom tools warning.'
    Add-Assertion -Name 'missing base no warning' -Passed ([int]$missingAudit.warningCount -eq 0 -and $missingAudit.baseExists -eq $false) -Detail 'Missing base should not produce warnings.'

    Add-Assertion -Name 'guide urls included' -Passed (
        $audit.migrationGuideUrl -match 'CHANGELOG\.md#extensions-migration' -and
        $audit.extensionsDocUrl -match 'docs/extensions\.md'
    ) -Detail 'Expected upstream migration guide and extension doc URLs.'
    Add-Assertion -Name 'remaining gaps preserve migration boundary' -Passed ((@($audit.remainingGaps) -join "`n") -match 'does not migrate hooks or custom tools') -Detail 'Expected remaining gaps to keep mutation out of scope.'
    Add-Assertion -Name 'audit does not mutate hooks' -Passed (Test-Path -LiteralPath (Join-Path $globalBase 'hooks') -PathType Container) -Detail 'Audit unexpectedly removed hooks directory.'
    Add-Assertion -Name 'audit does not mutate custom tools' -Passed (Test-Path -LiteralPath (Join-Path (Join-Path $globalBase 'tools') 'deploy.ps1') -PathType Leaf) -Detail 'Audit unexpectedly removed custom tool.'

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
        Write-Host 'Tau CodingAgent deprecated extension dirs audit smoke passed'
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
        Write-Host 'Tau CodingAgent deprecated extension dirs audit smoke failed'
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
