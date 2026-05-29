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

function Invoke-SyncScript {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,
        [int]$ExpectedExitCode = 0
    )

    $scriptPath = Join-Path $repoRoot 'scripts/sync-release-versions.ps1'
    $output = & powershell -NoProfile -ExecutionPolicy Bypass -File $scriptPath @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    $outputText = ($output -join [Environment]::NewLine)
    if ($exitCode -ne $ExpectedExitCode) {
        throw "sync-release-versions.ps1 exited $exitCode, expected $ExpectedExitCode. Output: $outputText"
    }

    try {
        return $outputText | ConvertFrom-Json
    }
    catch {
        throw "sync-release-versions.ps1 did not return valid JSON. Output: $outputText"
    }
}

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Text
    )

    [System.IO.File]::WriteAllText($Path, $Text, [System.Text.UTF8Encoding]::new($false))
}

try {
    $repoAudit = Invoke-SyncScript -Arguments @('-Json')
    Add-Assertion -Name 'repo sync succeeded' -Passed ($repoAudit.succeeded -eq $true) -Detail 'Current repository release versions are out of sync.'
    Add-Assertion -Name 'repo version source' -Passed ($repoAudit.versionSource.path -eq 'Directory.Build.props') -Detail "Unexpected repo version source path: $($repoAudit.versionSource.path)"
    Add-Assertion -Name 'repo project count' -Passed ([int]$repoAudit.projectCount -ge 8) -Detail "Expected at least 8 src projects, actual $($repoAudit.projectCount)."
    Add-Assertion -Name 'repo out of sync count' -Passed ([int]$repoAudit.outOfSyncProjectCount -eq 0) -Detail "Expected no repo project version drift, actual $($repoAudit.outOfSyncProjectCount)."

    $fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("tau-version-sync-" + [Guid]::NewGuid().ToString('N'))
    $fixtureSrc = Join-Path $fixtureRoot 'src'
    $fixtureApp = Join-Path $fixtureSrc 'App'
    $fixtureLib = Join-Path $fixtureSrc 'Lib'
    New-Item -ItemType Directory -Force -Path $fixtureApp, $fixtureLib | Out-Null

    $propsPath = Join-Path $fixtureRoot 'Directory.Build.props'
    $appPath = Join-Path $fixtureApp 'App.csproj'
    $libPath = Join-Path $fixtureLib 'Lib.csproj'
    Write-Utf8NoBom -Path $propsPath -Text @"
<Project>
  <PropertyGroup>
    <VersionPrefix>1.2.3</VersionPrefix>
  </PropertyGroup>
</Project>
"@
    Write-Utf8NoBom -Path $appPath -Text @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Version>1.0.0</Version>
  </PropertyGroup>
</Project>
"@
    Write-Utf8NoBom -Path $libPath -Text @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\App\App.csproj" />
  </ItemGroup>
</Project>
"@

    $fixtureAudit = Invoke-SyncScript -Arguments @('-PropsPath', $propsPath, '-ProjectRoots', $fixtureSrc, '-Json') -ExpectedExitCode 1
    Add-Assertion -Name 'fixture dry-run fails on drift' -Passed ($fixtureAudit.succeeded -eq $false) -Detail 'Expected fixture dry-run to report drift.'
    Add-Assertion -Name 'fixture drift count' -Passed ([int]$fixtureAudit.outOfSyncProjectCount -eq 1) -Detail "Expected one drifted project, actual $($fixtureAudit.outOfSyncProjectCount)."
    Add-Assertion -Name 'fixture project references audited' -Passed ([int]$fixtureAudit.projectReferenceCount -eq 1) -Detail "Expected one ProjectReference, actual $($fixtureAudit.projectReferenceCount)."

    $fixtureApply = Invoke-SyncScript -Arguments @('-PropsPath', $propsPath, '-ProjectRoots', $fixtureSrc, '-Apply', '-Json')
    Add-Assertion -Name 'fixture apply succeeds' -Passed ($fixtureApply.succeeded -eq $true -and $fixtureApply.applied -eq $true) -Detail 'Expected fixture apply to succeed.'
    Add-Assertion -Name 'fixture apply updated one project' -Passed ([int]$fixtureApply.updatedProjectCount -eq 1) -Detail "Expected one updated project, actual $($fixtureApply.updatedProjectCount)."
    $updatedApp = Get-Content -LiteralPath $appPath -Raw
    Add-Assertion -Name 'fixture app version updated' -Passed ($updatedApp -match '<Version>1\.2\.3</Version>') -Detail 'Fixture project version was not updated to 1.2.3.'

    $fixturePostApply = Invoke-SyncScript -Arguments @('-PropsPath', $propsPath, '-ProjectRoots', $fixtureSrc, '-Json')
    Add-Assertion -Name 'fixture post-apply clean' -Passed ($fixturePostApply.succeeded -eq $true -and [int]$fixturePostApply.outOfSyncProjectCount -eq 0) -Detail 'Fixture remained out of sync after apply.'

    Remove-Item -LiteralPath $fixtureRoot -Recurse -Force

    $result = [ordered]@{
        schemaVersion = 1
        succeeded = $true
        assertions = $script:assertions
        repoVersion = $repoAudit.version
        repoProjectCount = $repoAudit.projectCount
    }

    if ($Json) {
        $result | ConvertTo-Json -Depth 8
    }
    else {
        Write-Host 'Tau release version sync smoke passed'
        Write-Host "  assertions: $($script:assertions.Count)"
        Write-Host "  repo version: $($repoAudit.version)"
        Write-Host "  repo projects: $($repoAudit.projectCount)"
    }
}
catch {
    if ($fixtureRoot -and (Test-Path -LiteralPath $fixtureRoot)) {
        Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
    }

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
        Write-Host 'Tau release version sync smoke failed'
        Write-Host $_.Exception.Message
    }

    exit 1
}
