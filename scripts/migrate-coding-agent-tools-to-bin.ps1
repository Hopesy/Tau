param(
    [string]$AgentDirectory = (Join-Path $HOME '.tau'),
    [switch]$Apply,
    [switch]$Json
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$invocationDirectory = (Get-Location).Path
$managedBinaries = @('fd', 'rg', 'fd.exe', 'rg.exe')

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

function Get-SourceKind {
    param([Parameter(Mandatory = $true)]$Item)

    $isDirectory = (($Item.Attributes -band [System.IO.FileAttributes]::Directory) -ne 0)
    $isReparsePoint = (($Item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)

    if ($isDirectory -and $isReparsePoint) {
        return 'directory-reparse-point'
    }

    if ($isDirectory) {
        return 'directory'
    }

    if ($isReparsePoint) {
        return 'file-reparse-point'
    }

    return 'file'
}

function New-BinaryResult {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $true)]
        [string]$Action,
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Reason,
        [string]$SourceKind = 'none',
        [AllowEmptyString()]
        [string]$ErrorMessage = ''
    )

    $oldPath = Join-Path $script:toolsDirectory $Name
    $newPath = Join-Path $script:binDirectory $Name

    $result = [ordered]@{
        name = $Name
        oldPath = Convert-ToDisplayPath -Path $oldPath
        newPath = Convert-ToDisplayPath -Path $newPath
        sourceKind = $SourceKind
        action = $Action
        reason = $Reason
    }

    if (-not [string]::IsNullOrWhiteSpace($ErrorMessage)) {
        $result.error = $ErrorMessage
    }

    return $result
}

function Test-ManagedBinaryMigrationTarget {
    param([Parameter(Mandatory = $true)][string]$Name)

    if (-not (Test-Path -LiteralPath $script:toolsDirectory -PathType Container)) {
        return New-BinaryResult -Name $Name -Action 'skipped' -Reason 'tools-directory-missing'
    }

    $oldPath = Join-Path $script:toolsDirectory $Name
    $newPath = Join-Path $script:binDirectory $Name

    if (-not (Test-Path -LiteralPath $oldPath)) {
        return New-BinaryResult -Name $Name -Action 'skipped' -Reason 'source-missing'
    }

    try {
        $sourceItem = Get-Item -LiteralPath $oldPath -Force
    }
    catch {
        return New-BinaryResult -Name $Name -Action 'skipped' -Reason 'cannot-read-source' -ErrorMessage $_.Exception.Message
    }

    $sourceKind = Get-SourceKind -Item $sourceItem
    if (($sourceItem.Attributes -band [System.IO.FileAttributes]::Directory) -ne 0) {
        return New-BinaryResult -Name $Name -Action 'skipped' -Reason 'source-not-file' -SourceKind $sourceKind
    }

    if (Test-Path -LiteralPath $newPath) {
        return New-BinaryResult -Name $Name -Action $(if ($Apply) { 'removed-old' } else { 'would-remove-old' }) -Reason 'target-exists' -SourceKind $sourceKind
    }

    return New-BinaryResult -Name $Name -Action $(if ($Apply) { 'moved' } else { 'would-move' }) -Reason '' -SourceKind $sourceKind
}

function Get-ActionCount {
    param(
        [AllowNull()]
        [object[]]$Items,
        [Parameter(Mandatory = $true)]
        [string[]]$Actions
    )

    if ($null -eq $Items -or $Items.Count -eq 0) {
        return 0
    }

    return @($Items | Where-Object { $Actions -contains $_.action }).Count
}

try {
    $script:agentRoot = Resolve-FullPath -Path $AgentDirectory
    $script:toolsDirectory = Join-Path $script:agentRoot 'tools'
    $script:binDirectory = Join-Path $script:agentRoot 'bin'
    $binaryResults = @()

    foreach ($binary in $managedBinaries) {
        $result = [pscustomobject](Test-ManagedBinaryMigrationTarget -Name $binary)

        if ($Apply -and $result.action -eq 'moved') {
            try {
                [System.IO.Directory]::CreateDirectory($script:binDirectory) | Out-Null
                Move-Item -LiteralPath (Join-Path $script:toolsDirectory $binary) -Destination (Join-Path $script:binDirectory $binary)
            }
            catch {
                $result.action = 'failed'
                $result.reason = 'move-failed'
                $result | Add-Member -NotePropertyName error -NotePropertyValue $_.Exception.Message -Force
            }
        }
        elseif ($Apply -and $result.action -eq 'removed-old') {
            try {
                Remove-Item -LiteralPath (Join-Path $script:toolsDirectory $binary) -Force
            }
            catch {
                $result.action = 'failed'
                $result.reason = 'remove-old-failed'
                $result | Add-Member -NotePropertyName error -NotePropertyValue $_.Exception.Message -Force
            }
        }

        $binaryResults += $result
    }

    $wouldMoveCount = Get-ActionCount -Items $binaryResults -Actions @('would-move')
    $wouldRemoveOldCount = Get-ActionCount -Items $binaryResults -Actions @('would-remove-old')
    $movedCount = Get-ActionCount -Items $binaryResults -Actions @('moved')
    $removedOldCount = Get-ActionCount -Items $binaryResults -Actions @('removed-old')
    $skippedCount = Get-ActionCount -Items $binaryResults -Actions @('skipped')
    $failedCount = Get-ActionCount -Items $binaryResults -Actions @('failed')

    $result = [ordered]@{
        schemaVersion = 1
        succeeded = ($failedCount -eq 0)
        dryRun = -not $Apply.IsPresent
        applied = $Apply.IsPresent
        agentDirectory = Convert-ToDisplayPath -Path $script:agentRoot
        toolsDirectory = Convert-ToDisplayPath -Path $script:toolsDirectory
        binDirectory = Convert-ToDisplayPath -Path $script:binDirectory
        scan = [ordered]@{
            managedBinaryCount = $managedBinaries.Count
            migratable = $wouldMoveCount + $wouldRemoveOldCount + $movedCount + $removedOldCount
            wouldMove = $wouldMoveCount
            wouldRemoveOld = $wouldRemoveOldCount
            moved = $movedCount
            removedOld = $removedOldCount
            skipped = $skippedCount
            failed = $failedCount
        }
        binaries = @($binaryResults)
        remainingGaps = @(
            'This helper only migrates managed fd/rg binaries from a CodingAgent tools/ directory to bin/.',
            'It does not close auth/settings/session/keybindings migrations, hooks/tools deprecation warnings, or general custom tool migration parity.'
        )
    }

    if ($Json) {
        $result | ConvertTo-Json -Depth 10
    }
    else {
        Write-Host "Scanned $($result.scan.managedBinaryCount) managed CodingAgent binary name(s) under $($result.toolsDirectory)."
        if ($result.dryRun) {
            Write-Host 'Dry-run only; pass -Apply to move fd/rg binaries or remove old duplicates.'
        }
        Write-Host "Migratable: $($result.scan.migratable)"
        Write-Host "Moved: $($result.scan.moved)"
        Write-Host "Removed old duplicates: $($result.scan.removedOld)"
        Write-Host "Skipped: $($result.scan.skipped)"
        Write-Host "Failed: $($result.scan.failed)"
        foreach ($binary in $result.binaries) {
            if ($binary.action -eq 'skipped') {
                Write-Host "  SKIP $($binary.name): $($binary.reason)"
            }
            elseif ($binary.action -eq 'failed') {
                Write-Host "  FAIL $($binary.name): $($binary.error)"
            }
            else {
                Write-Host "  $($binary.action.ToUpperInvariant()) $($binary.oldPath) -> $($binary.newPath)"
            }
        }
    }

    if ($failedCount -gt 0) {
        exit 1
    }
}
catch {
    $result = [ordered]@{
        schemaVersion = 1
        succeeded = $false
        dryRun = -not $Apply.IsPresent
        applied = $false
        agentDirectory = $AgentDirectory
        error = $_.Exception.Message
    }

    if ($Json) {
        $result | ConvertTo-Json -Depth 6
    }
    else {
        Write-Host 'Tau CodingAgent tools-to-bin migration failed'
        Write-Host $_.Exception.Message
    }

    exit 1
}
