param(
    [switch]$Json
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
Set-Location $repoRoot

$script:assertions = @()
$script:results = [ordered]@{}

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

function Assert-Text {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [AllowNull()]
        [string]$Text,
        [Parameter(Mandatory = $true)]
        [string]$Pattern
    )

    Add-Assertion -Name $Name -Passed ($Text -match $Pattern) -Detail "Expected text to match '$Pattern'."
}

function Assert-Equal {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [AllowNull()]
        [object]$Actual,
        [AllowNull()]
        [object]$Expected
    )

    Add-Assertion -Name $Name -Passed ([object]::Equals($Actual, $Expected)) -Detail "Expected '$Expected', actual '$Actual'."
}

function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [string[]]$Arguments = @(),
        [string]$WorkingDirectory = '',
        [switch]$AllowFailure
    )

    $previousLocation = Get-Location
    if (-not [string]::IsNullOrWhiteSpace($WorkingDirectory)) {
        Set-Location $WorkingDirectory
    }

    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $FilePath @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
        if (-not [string]::IsNullOrWhiteSpace($WorkingDirectory)) {
            Set-Location $previousLocation
        }
    }

    $text = ($output -join [Environment]::NewLine)
    if ($exitCode -ne 0 -and -not $AllowFailure) {
        throw "$FilePath $($Arguments -join ' ') failed with exit code $exitCode. Output: $text"
    }

    return [ordered]@{
        exitCode = $exitCode
        output = $text
    }
}

function Invoke-Git {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,
        [Parameter(Mandatory = $true)]
        [string]$WorkingDirectory,
        [switch]$AllowFailure
    )

    return Invoke-Native -FilePath 'git' -Arguments (@('-C', $WorkingDirectory) + $Arguments) -AllowFailure:$AllowFailure
}

function Invoke-GitDir {
    param(
        [Parameter(Mandatory = $true)]
        [string]$GitDirectory,
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    $result = Invoke-Native -FilePath 'git' -Arguments (@('--git-dir', $GitDirectory) + $Arguments)
    return $result.output.Trim()
}

function Invoke-Finalize {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ScriptPath,
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,
        [switch]$AllowFailure
    )

    return Invoke-Native -FilePath 'powershell' -Arguments (@('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $ScriptPath) + $Arguments) -AllowFailure:$AllowFailure
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("tau-release-finalize-" + [Guid]::NewGuid().ToString('N'))

try {
    $repo = Join-Path $tempRoot 'repo'
    $remote = Join-Path $tempRoot 'remote.git'
    $fakeBin = Join-Path $tempRoot 'bin'
    $scriptsDir = Join-Path $repo 'scripts'
    $releaseDocsDir = Join-Path $repo 'docs/releases'
    $archiveDir = Join-Path $repo 'artifacts/releases'

    New-Item -ItemType Directory -Force -Path $scriptsDir, $releaseDocsDir, $archiveDir, $fakeBin | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts/finalize-release.ps1') -Destination (Join-Path $scriptsDir 'finalize-release.ps1')

    $init = Invoke-Native -FilePath 'git' -Arguments @('init', '-b', 'main', $repo) -AllowFailure
    if ($init.exitCode -ne 0) {
        Invoke-Native -FilePath 'git' -Arguments @('init', $repo) | Out-Null
        Invoke-Git -WorkingDirectory $repo -Arguments @('checkout', '-b', 'main') | Out-Null
    }

    Invoke-Git -WorkingDirectory $repo -Arguments @('config', 'user.email', 'tau@example.invalid') | Out-Null
    Invoke-Git -WorkingDirectory $repo -Arguments @('config', 'user.name', 'Tau Release Smoke') | Out-Null

    Set-Content -LiteralPath (Join-Path $repo '.gitignore') -Value @('artifacts/', '') -Encoding ASCII
    Set-Content -LiteralPath (Join-Path $repo 'README.md') -Value '# Tau fixture' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $releaseDocsDir 'feature-release-notes.md') -Value '# Feature release notes' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $archiveDir 'tau-win-x64.zip') -Value 'fake archive' -Encoding UTF8

    Invoke-Git -WorkingDirectory $repo -Arguments @('add', '--', '.gitignore', 'README.md', 'docs/releases/feature-release-notes.md', 'scripts/finalize-release.ps1') | Out-Null
    Invoke-Git -WorkingDirectory $repo -Arguments @('commit', '-m', 'Initial release fixture') | Out-Null
    Invoke-Git -WorkingDirectory $repo -Arguments @('tag', 'v0.1.1') | Out-Null
    Invoke-Native -FilePath 'git' -Arguments @('init', '--bare', $remote) | Out-Null
    Invoke-Git -WorkingDirectory $repo -Arguments @('remote', 'add', 'origin', $remote) | Out-Null

    $finalizeScript = Join-Path $scriptsDir 'finalize-release.ps1'
    $archivePath = Join-Path $archiveDir 'tau-win-x64.zip'

    $dryRun = Invoke-Finalize -ScriptPath $finalizeScript -Arguments @('v0.1.1', '-ArchivePath', $archivePath, '-Json')
    $script:results.dryRun = [ordered]@{
        exitCode = $dryRun.exitCode
        outputLength = $dryRun.output.Length
    }
    Assert-Equal -Name 'dry-run exit code' -Actual $dryRun.exitCode -Expected 0
    Assert-Text -Name 'dry-run schema' -Text $dryRun.output -Pattern '"schemaVersion"\s*:\s*1'
    Assert-Text -Name 'dry-run true' -Text $dryRun.output -Pattern '"dryRun"\s*:\s*true'
    Assert-Text -Name 'dry-run push branch plan' -Text $dryRun.output -Pattern 'release-push-branch'
    Assert-Text -Name 'dry-run push tag plan' -Text $dryRun.output -Pattern 'release-push-tag'

    $apply = Invoke-Finalize -ScriptPath $finalizeScript -Arguments @('v0.1.1', '-ArchivePath', $archivePath, '-Apply', '-Json')
    $remoteMain = Invoke-GitDir -GitDirectory $remote -Arguments @('rev-parse', 'refs/heads/main')
    $remoteTag = Invoke-GitDir -GitDirectory $remote -Arguments @('rev-parse', 'refs/tags/v0.1.1^{}')
    $script:results.apply = [ordered]@{
        exitCode = $apply.exitCode
        remoteMain = $remoteMain
        remoteTag = $remoteTag
    }
    Assert-Equal -Name 'apply exit code' -Actual $apply.exitCode -Expected 0
    Assert-Text -Name 'apply succeeded' -Text $apply.output -Pattern '"succeeded"\s*:\s*true'
    Assert-Text -Name 'apply pushed branch' -Text $apply.output -Pattern 'release-push-branch'
    Assert-Text -Name 'apply pushed tag' -Text $apply.output -Pattern 'release-push-tag'
    Assert-Equal -Name 'remote branch tag alignment' -Actual $remoteMain -Expected $remoteTag

    $fakeGh = Join-Path $fakeBin 'gh.cmd'
    $fakeGhLog = Join-Path $tempRoot 'fake-gh.log'
    Set-Content -LiteralPath $fakeGh -Value @(
        '@echo off',
        'echo %*>>"%TAU_FAKE_GH_LOG%"',
        'exit /b 0'
    ) -Encoding ASCII

    $githubPlan = Invoke-Finalize -ScriptPath $finalizeScript -Arguments @(
        'v0.1.1',
        '-ArchivePath',
        $archivePath,
        '-CreateGitHubRelease',
        '-GitHubCli',
        $fakeGh,
        '-GitHubReleaseTitle',
        'Tau v0.1.1',
        '-Draft',
        '-Prerelease',
        '-Json'
    )
    $script:results.githubPlan = [ordered]@{
        exitCode = $githubPlan.exitCode
        outputLength = $githubPlan.output.Length
    }
    Assert-Equal -Name 'github plan exit code' -Actual $githubPlan.exitCode -Expected 0
    Assert-Text -Name 'github plan draft flag' -Text $githubPlan.output -Pattern '--draft'
    Assert-Text -Name 'github plan prerelease flag' -Text $githubPlan.output -Pattern '--prerelease'

    $env:TAU_FAKE_GH_LOG = $fakeGhLog
    try {
        $githubRelease = Invoke-Finalize -ScriptPath $finalizeScript -Arguments @(
            'v0.1.1',
            '-ArchivePath',
            $archivePath,
            '-Apply',
            '-CreateGitHubRelease',
            '-GitHubCli',
            $fakeGh,
            '-GitHubReleaseTitle',
            'Tau v0.1.1',
            '-Draft',
            '-Prerelease',
            '-Json'
        )
    }
    finally {
        Remove-Item Env:TAU_FAKE_GH_LOG -ErrorAction SilentlyContinue
    }

    $fakeGhOutput = if (Test-Path -LiteralPath $fakeGhLog) { Get-Content -LiteralPath $fakeGhLog -Raw } else { '' }
    $script:results.githubRelease = [ordered]@{
        exitCode = $githubRelease.exitCode
        fakeGhOutputLength = $fakeGhOutput.Length
    }
    Assert-Equal -Name 'github release exit code' -Actual $githubRelease.exitCode -Expected 0
    Assert-Text -Name 'github release command result' -Text $githubRelease.output -Pattern 'github-release-create'
    Assert-Text -Name 'fake gh release create' -Text $fakeGhOutput -Pattern 'release create v0\.1\.1'
    Assert-Text -Name 'fake gh archive argument' -Text $fakeGhOutput -Pattern 'tau-win-x64\.zip'
    Assert-Text -Name 'fake gh draft flag' -Text $fakeGhOutput -Pattern '--draft'
    Assert-Text -Name 'fake gh prerelease flag' -Text $fakeGhOutput -Pattern '--prerelease'

    Set-Content -LiteralPath (Join-Path $repo 'dirty.txt') -Value 'dirty' -Encoding UTF8
    $dirtyApply = Invoke-Finalize -ScriptPath $finalizeScript -Arguments @('v0.1.1', '-ArchivePath', $archivePath, '-Apply', '-Json') -AllowFailure
    $script:results.dirtyApply = [ordered]@{
        exitCode = $dirtyApply.exitCode
        outputLength = $dirtyApply.output.Length
    }
    Assert-Equal -Name 'dirty apply blocked' -Actual ($dirtyApply.exitCode -ne 0) -Expected $true
    Assert-Text -Name 'dirty apply clean worktree check' -Text $dirtyApply.output -Pattern '"clean-worktree"'
    Assert-Text -Name 'dirty apply blocked status' -Text $dirtyApply.output -Pattern '"blocked"'

    $finalResult = [ordered]@{
        schemaVersion = 1
        succeeded = $true
        fixtureTempRoot = $tempRoot
        results = $script:results
        assertions = $script:assertions
    }

    if ($Json) {
        $finalResult | ConvertTo-Json -Depth 8
    }
    else {
        Write-Host 'Tau release finalize smoke passed'
        Write-Host "  assertions: $($script:assertions.Count)"
        Write-Host "  remote main: $remoteMain"
        Write-Host "  remote tag: $remoteTag"
    }
}
catch {
    $finalResult = [ordered]@{
        schemaVersion = 1
        succeeded = $false
        fixtureTempRoot = $tempRoot
        results = $script:results
        assertions = $script:assertions
        error = $_.Exception.Message
    }

    if ($Json) {
        $finalResult | ConvertTo-Json -Depth 8
    }
    else {
        Write-Host 'Tau release finalize smoke failed'
        Write-Host $_.Exception.Message
    }

    exit 1
}
