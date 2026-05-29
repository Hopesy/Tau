param(
    [string]$AgentDirectory = (Join-Path (Get-Location).Path '.tau'),
    [switch]$Apply,
    [switch]$Json
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$invocationDirectory = (Get-Location).Path
$targetDirectoryName = 'coding-agent-sessions'

function Resolve-FullPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [string]$BasePath = $invocationDirectory
    )

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $BasePath $Path))
}

function Convert-ToDisplayPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $rootFull = [System.IO.Path]::GetFullPath($repoRoot)
    $separator = [System.IO.Path]::DirectorySeparatorChar
    $rootPrefix = $rootFull.TrimEnd($separator) + $separator

    if ($fullPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        $rootUri = [System.Uri]::new($rootPrefix)
        $pathUri = [System.Uri]::new($fullPath)
        return [System.Uri]::UnescapeDataString($rootUri.MakeRelativeUri($pathUri).ToString()).Replace('/', $separator)
    }

    return $fullPath
}

function Convert-ToEncodedCwd {
    param([Parameter(Mandatory = $true)][string]$Cwd)

    $trimmed = $Cwd.Trim()
    $withoutLeading = $trimmed -replace '^[\\/]', ''
    $safe = $withoutLeading -replace '[/\\:]', '-'
    return "--$safe--"
}

function Get-JsonPropertyValue {
    param(
        [AllowNull()]
        [object]$InputObject,
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    if ($null -eq $InputObject) {
        return $null
    }

    foreach ($property in $InputObject.PSObject.Properties) {
        if ($property.Name.Equals($Name, [StringComparison]::OrdinalIgnoreCase)) {
            return $property.Value
        }
    }

    return $null
}

function New-SkippedFileResult {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Reason
    )

    return [ordered]@{
        path = Convert-ToDisplayPath -Path $Path
        fileName = [System.IO.Path]::GetFileName($Path)
        cwd = $null
        encodedCwd = $null
        targetDirectory = $null
        targetPath = $null
        action = 'skipped'
        reason = $Reason
    }
}

function Read-FirstLine {
    param([Parameter(Mandatory = $true)][string]$Path)

    $enumerator = $null
    try {
        $enumerator = [System.IO.File]::ReadLines($Path).GetEnumerator()
        if ($enumerator.MoveNext()) {
            return $enumerator.Current
        }

        return $null
    }
    finally {
        if ($null -ne $enumerator) {
            $enumerator.Dispose()
        }
    }
}

function Test-CodingAgentSessionFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fileName = [System.IO.Path]::GetFileName($Path)

    try {
        $firstLine = Read-FirstLine -Path $Path
    }
    catch {
        return New-SkippedFileResult -Path $Path -Reason 'cannot-read-file'
    }

    if ([string]::IsNullOrWhiteSpace($firstLine)) {
        return New-SkippedFileResult -Path $Path -Reason 'empty-session-header'
    }

    try {
        $header = $firstLine | ConvertFrom-Json
    }
    catch {
        return New-SkippedFileResult -Path $Path -Reason 'invalid-json'
    }

    $type = [string](Get-JsonPropertyValue -InputObject $header -Name 'type')
    if (-not $type.Equals('session', [StringComparison]::OrdinalIgnoreCase)) {
        return New-SkippedFileResult -Path $Path -Reason 'not-session-header'
    }

    $cwdValue = Get-JsonPropertyValue -InputObject $header -Name 'cwd'
    $cwd = if ($null -eq $cwdValue) { '' } else { ([string]$cwdValue).Trim() }
    if ([string]::IsNullOrWhiteSpace($cwd)) {
        return New-SkippedFileResult -Path $Path -Reason 'missing-cwd'
    }

    $encodedCwd = Convert-ToEncodedCwd -Cwd $cwd
    $targetDirectory = Join-Path (Join-Path $script:agentRoot $script:targetDirectoryName) $encodedCwd
    $targetPath = Join-Path $targetDirectory $fileName

    if ([System.IO.File]::Exists($targetPath)) {
        return [ordered]@{
            path = Convert-ToDisplayPath -Path $Path
            fileName = $fileName
            cwd = $cwd
            encodedCwd = $encodedCwd
            targetDirectory = Convert-ToDisplayPath -Path $targetDirectory
            targetPath = Convert-ToDisplayPath -Path $targetPath
            action = 'skipped'
            reason = 'target-exists'
        }
    }

    return [ordered]@{
        path = Convert-ToDisplayPath -Path $Path
        fileName = $fileName
        cwd = $cwd
        encodedCwd = $encodedCwd
        targetDirectory = Convert-ToDisplayPath -Path $targetDirectory
        targetPath = Convert-ToDisplayPath -Path $targetPath
        action = if ($Apply) { 'migrated' } else { 'would-migrate' }
        reason = ''
    }
}

function Get-RootSessionFiles {
    param([Parameter(Mandatory = $true)][string]$Root)

    if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
        return @()
    }

    return @(Get-ChildItem -LiteralPath $Root -Filter '*.jsonl' |
        Where-Object { -not $_.PSIsContainer } |
        Sort-Object FullName |
        ForEach-Object { $_.FullName })
}

function Get-PropertyCount {
    param(
        [AllowNull()]
        [object[]]$Items,
        [Parameter(Mandatory = $true)]
        [string]$Action
    )

    if ($null -eq $Items -or $Items.Count -eq 0) {
        return 0
    }

    return @($Items | Where-Object { $_.action -eq $Action }).Count
}

try {
    $script:agentRoot = Resolve-FullPath -Path $AgentDirectory
    $sessionFiles = @(Get-RootSessionFiles -Root $script:agentRoot)
    $fileResults = @()

    foreach ($sessionFile in $sessionFiles) {
        $result = [pscustomobject](Test-CodingAgentSessionFile -Path $sessionFile)
        if ($Apply -and $result.action -eq 'migrated') {
            $absoluteTargetDirectory = Join-Path (Join-Path $script:agentRoot $targetDirectoryName) $result.encodedCwd
            $absoluteTargetPath = Join-Path $absoluteTargetDirectory $result.fileName
            [System.IO.Directory]::CreateDirectory($absoluteTargetDirectory) | Out-Null
            Move-Item -LiteralPath $sessionFile -Destination $absoluteTargetPath
        }

        $fileResults += $result
    }

    $wouldMigrateCount = Get-PropertyCount -Items $fileResults -Action 'would-migrate'
    $migratedCount = Get-PropertyCount -Items $fileResults -Action 'migrated'
    $skippedCount = Get-PropertyCount -Items $fileResults -Action 'skipped'

    $result = [ordered]@{
        schemaVersion = 1
        succeeded = $true
        dryRun = -not $Apply.IsPresent
        applied = $Apply.IsPresent
        agentDirectory = Convert-ToDisplayPath -Path $script:agentRoot
        targetDirectoryName = $targetDirectoryName
        scan = [ordered]@{
            sessionFileCount = $sessionFiles.Count
            migratable = $wouldMigrateCount + $migratedCount
            migrated = $migratedCount
            skipped = $skippedCount
        }
        files = @($fileResults)
        remainingGaps = @(
            'This helper only relocates misplaced root JSONL session files into Tau discoverable coding-agent-sessions/<encoded-cwd>/ directories.',
            'It does not close upstream auth/settings/extensions/keybindings/tools migrations or exact upstream session schema migration parity.'
        )
    }

    if ($Json) {
        $result | ConvertTo-Json -Depth 10
    }
    else {
        Write-Host "Scanned $($result.scan.sessionFileCount) root CodingAgent JSONL session file(s) under $($result.agentDirectory)"
        if ($result.dryRun) {
            Write-Host 'Dry-run only; pass -Apply to move migratable session files.'
        }
        Write-Host "Migratable: $($result.scan.migratable)"
        Write-Host "Migrated: $($result.scan.migrated)"
        Write-Host "Skipped: $($result.scan.skipped)"
        foreach ($file in $result.files) {
            if ($file.action -eq 'skipped') {
                Write-Host "  SKIP $($file.fileName): $($file.reason)"
            }
            else {
                Write-Host "  $($file.action.ToUpperInvariant()) $($file.fileName) -> $($file.targetDirectory)"
            }
        }
    }
}
catch {
    $result = [ordered]@{
        schemaVersion = 1
        succeeded = $false
        dryRun = -not $Apply.IsPresent
        applied = $false
        agentDirectory = $AgentDirectory
        targetDirectoryName = $targetDirectoryName
        error = $_.Exception.Message
    }

    if ($Json) {
        $result | ConvertTo-Json -Depth 6
    }
    else {
        Write-Host 'Tau CodingAgent session migration failed'
        Write-Host $_.Exception.Message
    }

    exit 1
}
