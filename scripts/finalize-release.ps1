param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$ReleaseTag,
    [string]$Branch = 'main',
    [string]$Remote = 'origin',
    [string]$ArchiveRoot = 'artifacts/releases',
    [string[]]$ArchivePath = @(),
    [string]$NotesFile = 'docs/releases/feature-release-notes.md',
    [string]$GitHubCli = 'gh',
    [string]$GitHubReleaseTitle = '',
    [switch]$Apply,
    [switch]$AllowDirty,
    [switch]$SkipArchiveCheck,
    [switch]$CreateGitHubRelease,
    [switch]$Draft,
    [switch]$Prerelease,
    [switch]$Json
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
Set-Location $repoRoot

$script:checks = @()
$script:commandResults = @()
$script:hardPreflightFailure = $false

function Add-Check {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $true)]
        [ValidateSet('passed', 'warning', 'blocked')]
        [string]$Status,
        [Parameter(Mandatory = $true)]
        [string]$Detail
    )

    $script:checks += [ordered]@{
        name = $Name
        status = $Status
        detail = $Detail
    }

    if ($Status -eq 'blocked') {
        $script:hardPreflightFailure = $true
    }
}

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

function Convert-ToRepoRelativePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

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

function Join-CommandDisplay {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [string[]]$Arguments = @()
    )

    $parts = @($FilePath) + $Arguments
    $displayParts = @()
    foreach ($part in $parts) {
        if ($part -match '\s') {
            $displayParts += '"' + ($part -replace '"', '\"') + '"'
        }
        else {
            $displayParts += $part
        }
    }

    return ($displayParts -join ' ')
}

function Get-OutputPreview {
    param(
        [AllowNull()]
        [string]$Output,
        [int]$MaxLength = 8000
    )

    if ([string]::IsNullOrEmpty($Output)) {
        return ''
    }

    if ($Output.Length -le $MaxLength) {
        return $Output
    }

    return $Output.Substring(0, $MaxLength) + "`n... <truncated $($Output.Length - $MaxLength) chars>"
}

function Invoke-ProcessText {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [string[]]$Arguments = @()
    )

    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $FilePath @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    return [ordered]@{
        exitCode = $exitCode
        output = ($output -join [Environment]::NewLine)
    }
}

function Invoke-ReleaseStep {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [string[]]$Arguments = @()
    )

    $display = Join-CommandDisplay -FilePath $FilePath -Arguments $Arguments
    $startedAt = Get-Date

    if (-not $Json) {
        Write-Host "==> $Name"
        Write-Host "    $display"
    }

    $result = Invoke-ProcessText -FilePath $FilePath -Arguments $Arguments
    $durationMs = [int]((Get-Date) - $startedAt).TotalMilliseconds

    if (-not $Json) {
        if (-not [string]::IsNullOrWhiteSpace($result.output)) {
            Write-Host $result.output
        }

        if ($result.exitCode -eq 0) {
            Write-Host "==> $Name passed"
        }
        else {
            Write-Host "==> $Name failed with exit code $($result.exitCode)"
        }
        Write-Host ''
    }

    $stepResult = [ordered]@{
        name = $Name
        command = $display
        exitCode = $result.exitCode
        durationMs = $durationMs
        outputPreview = Get-OutputPreview -Output $result.output
        outputLength = $result.output.Length
    }
    $script:commandResults += $stepResult

    if ($result.exitCode -ne 0) {
        throw "Release finalize step '$Name' failed with exit code $($result.exitCode)."
    }
}

function Get-GitStatus {
    $result = Invoke-ProcessText -FilePath 'git' -Arguments @('status', '--porcelain')
    if ($result.exitCode -ne 0) {
        return [ordered]@{
            available = $false
            clean = $false
            entries = @()
            error = $result.output
        }
    }

    $entries = @($result.output -split "\r?\n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    return [ordered]@{
        available = $true
        clean = ($entries.Count -eq 0)
        entries = $entries
        error = ''
    }
}

function Get-GitText {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    $result = Invoke-ProcessText -FilePath 'git' -Arguments $Arguments
    if ($result.exitCode -ne 0) {
        return [ordered]@{
            succeeded = $false
            value = ''
            error = $result.output
        }
    }

    return [ordered]@{
        succeeded = $true
        value = $result.output.Trim()
        error = ''
    }
}

function Get-ArchiveCandidates {
    $paths = @()

    if ($ArchivePath.Count -gt 0) {
        foreach ($path in $ArchivePath) {
            $paths += (Convert-ToFullPath -Path $path -BasePath $repoRoot)
        }
        return @($paths)
    }

    if ($SkipArchiveCheck) {
        return @()
    }

    $archiveRootFull = Convert-ToFullPath -Path $ArchiveRoot -BasePath $repoRoot
    if (-not (Test-Path -LiteralPath $archiveRootFull)) {
        return @()
    }

    return @(
        Get-ChildItem -LiteralPath $archiveRootFull -File |
            Where-Object {
                $_.Name.EndsWith('.zip', [StringComparison]::OrdinalIgnoreCase) -or
                $_.Name.EndsWith('.tar.gz', [StringComparison]::OrdinalIgnoreCase)
            } |
            Sort-Object Name |
            ForEach-Object { $_.FullName }
    )
}

function New-Result {
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$ArchiveCandidates,
        [Parameter(Mandatory = $true)]
        [object[]]$PlannedCommands,
        [string[]]$RemainingGaps = @(),
        [bool]$Succeeded = $false
    )

    return [ordered]@{
        schemaVersion = 1
        dryRun = -not $Apply.IsPresent
        applied = $Apply.IsPresent
        succeeded = $Succeeded
        releaseTag = $ReleaseTag
        branch = $Branch
        remote = $Remote
        options = [ordered]@{
            allowDirty = $AllowDirty.IsPresent
            skipArchiveCheck = $SkipArchiveCheck.IsPresent
            createGitHubRelease = $CreateGitHubRelease.IsPresent
            draft = $Draft.IsPresent
            prerelease = $Prerelease.IsPresent
        }
        archives = @($ArchiveCandidates | ForEach-Object {
            [ordered]@{
                path = Convert-ToRepoRelativePath -Path $_
                exists = (Test-Path -LiteralPath $_ -PathType Leaf)
            }
        })
        checks = $script:checks
        plannedCommands = @($PlannedCommands)
        commandResults = $script:commandResults
        remainingGaps = @($RemainingGaps)
    }
}

$plannedCommands = @()
$remainingGaps = @(
    'NuGet/package registry publish synchronization remains a separate release decision.',
    'Real external provider/Slack/Docker/SSH/HF/GPU/vLLM release e2e remains open.',
    'Non-host runner executable smoke and examples/Photon/interactive asset parity remain open.'
)

try {
    if ($ReleaseTag -notmatch '^v\d+\.\d+\.\d+$') {
        Add-Check -Name 'release-tag-format' -Status 'blocked' -Detail "Release tag must use vX.Y.Z format. Actual: $ReleaseTag"
    }
    else {
        Add-Check -Name 'release-tag-format' -Status 'passed' -Detail "Release tag $ReleaseTag uses vX.Y.Z format."
    }

    $gitVersion = Get-GitText -Arguments @('--version')
    if (-not $gitVersion.succeeded) {
        Add-Check -Name 'git' -Status 'blocked' -Detail "git is not available: $($gitVersion.error)"
    }
    else {
        Add-Check -Name 'git' -Status 'passed' -Detail $gitVersion.value
    }

    $gitStatus = Get-GitStatus
    if (-not $gitStatus.available) {
        Add-Check -Name 'clean-worktree' -Status 'blocked' -Detail "Could not read git status: $($gitStatus.error)"
    }
    elseif ($gitStatus.clean) {
        Add-Check -Name 'clean-worktree' -Status 'passed' -Detail 'Working tree is clean before release finalization.'
    }
    elseif ($Apply -and -not $AllowDirty) {
        Add-Check -Name 'clean-worktree' -Status 'blocked' -Detail "Working tree has $($gitStatus.entries.Count) uncommitted item(s). Release finalization requires a clean worktree unless -AllowDirty is explicit."
    }
    else {
        Add-Check -Name 'clean-worktree' -Status 'warning' -Detail "Working tree has $($gitStatus.entries.Count) uncommitted item(s); dry-run remains read-only or -AllowDirty was explicit."
    }

    $remoteUrl = Get-GitText -Arguments @('remote', 'get-url', $Remote)
    if (-not $remoteUrl.succeeded) {
        Add-Check -Name 'remote' -Status 'blocked' -Detail "Remote '$Remote' is not configured: $($remoteUrl.error)"
    }
    else {
        Add-Check -Name 'remote' -Status 'passed' -Detail "Remote '$Remote' is configured."
    }

    $branchCommit = Get-GitText -Arguments @('rev-parse', '--verify', "refs/heads/$Branch")
    if (-not $branchCommit.succeeded) {
        Add-Check -Name 'branch' -Status 'blocked' -Detail "Local branch '$Branch' was not found."
    }
    else {
        Add-Check -Name 'branch' -Status 'passed' -Detail "Local branch '$Branch' resolves to $($branchCommit.value)."
    }

    $tagCommit = Get-GitText -Arguments @('rev-parse', '--verify', "refs/tags/$ReleaseTag^{}")
    if (-not $tagCommit.succeeded) {
        Add-Check -Name 'release-tag' -Status 'blocked' -Detail "Local release tag '$ReleaseTag' was not found."
    }
    else {
        Add-Check -Name 'release-tag' -Status 'passed' -Detail "Local release tag '$ReleaseTag' resolves to $($tagCommit.value)."
    }

    if ($branchCommit.succeeded -and $tagCommit.succeeded) {
        if ($branchCommit.value -eq $tagCommit.value) {
            Add-Check -Name 'tag-branch-alignment' -Status 'passed' -Detail "Tag $ReleaseTag points at branch $Branch tip."
        }
        else {
            Add-Check -Name 'tag-branch-alignment' -Status 'blocked' -Detail "Tag $ReleaseTag ($($tagCommit.value)) does not point at branch $Branch tip ($($branchCommit.value))."
        }
    }

    $archiveCandidates = Get-ArchiveCandidates
    $missingArchives = @($archiveCandidates | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Leaf) })
    if ($SkipArchiveCheck) {
        Add-Check -Name 'release-archives' -Status 'warning' -Detail 'Release archive check skipped by explicit option.'
    }
    elseif ($archiveCandidates.Count -eq 0) {
        Add-Check -Name 'release-archives' -Status 'blocked' -Detail "No release archive was found. Build/package artifacts first or pass -ArchivePath explicitly."
    }
    elseif ($missingArchives.Count -gt 0) {
        Add-Check -Name 'release-archives' -Status 'blocked' -Detail "Release archive path(s) do not exist: $($missingArchives -join ', ')"
    }
    else {
        Add-Check -Name 'release-archives' -Status 'passed' -Detail "Found $($archiveCandidates.Count) release archive(s)."
    }

    if ($CreateGitHubRelease) {
        $notesFull = Convert-ToFullPath -Path $NotesFile -BasePath $repoRoot
        if (Test-Path -LiteralPath $notesFull -PathType Leaf) {
            Add-Check -Name 'github-release-notes' -Status 'passed' -Detail "GitHub release notes source exists at $(Convert-ToRepoRelativePath -Path $notesFull)."
        }
        else {
            Add-Check -Name 'github-release-notes' -Status 'blocked' -Detail "GitHub release notes source was not found: $NotesFile"
        }

        try {
            $ghCommand = Get-Command -Name $GitHubCli -ErrorAction Stop
            Add-Check -Name 'github-cli' -Status 'passed' -Detail "GitHub CLI command found: $($ghCommand.Source)"
        }
        catch {
            Add-Check -Name 'github-cli' -Status 'blocked' -Detail "GitHub CLI command '$GitHubCli' was not found."
        }
    }
    else {
        Add-Check -Name 'github-release' -Status 'warning' -Detail 'GitHub release creation is not enabled; pass -CreateGitHubRelease to upload archives with gh.'
    }

    $plannedCommands += [ordered]@{
        name = 'release-push-branch'
        command = "git push $Remote $Branch"
        executedWhenApply = $true
    }
    $plannedCommands += [ordered]@{
        name = 'release-push-tag'
        command = "git push $Remote $ReleaseTag"
        executedWhenApply = $true
    }

    if ($CreateGitHubRelease) {
        $title = if ([string]::IsNullOrWhiteSpace($GitHubReleaseTitle)) { $ReleaseTag } else { $GitHubReleaseTitle }
        $githubDisplayArgs = @('release', 'create', $ReleaseTag)
        if ($archiveCandidates.Count -eq 0) {
            $githubDisplayArgs += '<release-archives>'
        }
        else {
            foreach ($archive in $archiveCandidates) {
                $githubDisplayArgs += (Convert-ToRepoRelativePath -Path $archive)
            }
        }
        $githubDisplayArgs += @('--title', $title, '--notes-file', $NotesFile)
        if ($Draft) {
            $githubDisplayArgs += '--draft'
        }
        if ($Prerelease) {
            $githubDisplayArgs += '--prerelease'
        }

        $plannedCommands += [ordered]@{
            name = 'github-release-create'
            command = Join-CommandDisplay -FilePath $GitHubCli -Arguments $githubDisplayArgs
            executedWhenApply = $true
        }
    }

    if ($script:hardPreflightFailure) {
        $result = New-Result -ArchiveCandidates $archiveCandidates -PlannedCommands $plannedCommands -RemainingGaps $remainingGaps -Succeeded $false
        if ($Json) {
            $result | ConvertTo-Json -Depth 12
        }
        else {
            Write-Host 'Tau release finalization blocked'
            foreach ($check in $script:checks) {
                Write-Host "  [$($check.status)] $($check.name): $($check.detail)"
            }
        }
        exit 1
    }

    if ($Apply) {
        Invoke-ReleaseStep -Name 'release-push-branch' -FilePath 'git' -Arguments @('push', $Remote, $Branch)
        Invoke-ReleaseStep -Name 'release-push-tag' -FilePath 'git' -Arguments @('push', $Remote, $ReleaseTag)

        if ($CreateGitHubRelease) {
            $notesFull = Convert-ToFullPath -Path $NotesFile -BasePath $repoRoot
            $title = if ([string]::IsNullOrWhiteSpace($GitHubReleaseTitle)) { $ReleaseTag } else { $GitHubReleaseTitle }
            $ghArgs = @('release', 'create', $ReleaseTag)
            foreach ($archive in $archiveCandidates) {
                $ghArgs += $archive
            }
            $ghArgs += @('--title', $title, '--notes-file', $notesFull)
            if ($Draft) {
                $ghArgs += '--draft'
            }
            if ($Prerelease) {
                $ghArgs += '--prerelease'
            }

            Invoke-ReleaseStep -Name 'github-release-create' -FilePath $GitHubCli -Arguments $ghArgs
        }
    }

    $result = New-Result -ArchiveCandidates $archiveCandidates -PlannedCommands $plannedCommands -RemainingGaps $remainingGaps -Succeeded $true
    if ($Json) {
        $result | ConvertTo-Json -Depth 12
    }
    else {
        Write-Host 'Tau release finalization'
        Write-Host "  tag: $ReleaseTag"
        Write-Host "  branch: $Branch"
        Write-Host "  remote: $Remote"
        Write-Host "  mode: $(if ($Apply) { 'applied' } else { 'dry-run' })"
        Write-Host ''
        Write-Host 'Checks:'
        foreach ($check in $script:checks) {
            Write-Host "  [$($check.status)] $($check.name): $($check.detail)"
        }
        Write-Host ''
        Write-Host 'Finalize steps:'
        foreach ($command in $plannedCommands) {
            $state = if ($command.executedWhenApply) { 'apply' } else { 'skipped' }
            Write-Host "  - [$state] $($command.command)"
        }
        Write-Host ''
        Write-Host 'Remaining gaps:'
        foreach ($gap in $remainingGaps) {
            Write-Host "  - $gap"
        }
    }
}
catch {
    $result = [ordered]@{
        schemaVersion = 1
        dryRun = -not $Apply.IsPresent
        applied = $false
        succeeded = $false
        releaseTag = $ReleaseTag
        branch = $Branch
        remote = $Remote
        checks = $script:checks
        commandResults = $script:commandResults
        error = $_.Exception.Message
    }

    if ($Json) {
        $result | ConvertTo-Json -Depth 12
    }
    else {
        Write-Host 'Tau release finalization failed'
        Write-Host $_.Exception.Message
    }

    exit 1
}
