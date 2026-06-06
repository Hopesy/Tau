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

function Assert-Matches {
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

function Assert-NotMatches {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [AllowNull()]
        [string]$Text,
        [Parameter(Mandatory = $true)]
        [string]$Pattern
    )

    Add-Assertion -Name $Name -Passed (-not ($Text -match $Pattern)) -Detail "Expected text not to match '$Pattern'."
}

function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [string[]]$Arguments = @(),
        [switch]$AllowFailure
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
        [string]$WorkingDirectory,
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,
        [switch]$AllowFailure
    )

    return Invoke-Native -FilePath 'git' -Arguments (@('-C', $WorkingDirectory) + $Arguments) -AllowFailure:$AllowFailure
}

function Invoke-PowerShellScript {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ScriptPath,
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,
        [switch]$AllowFailure
    )

    return Invoke-Native -FilePath 'powershell' -Arguments (@('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $ScriptPath) + $Arguments) -AllowFailure:$AllowFailure
}

function ConvertFrom-JsonOutput {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Text
    )

    try {
        return $Text | ConvertFrom-Json
    }
    catch {
        throw "Output was not valid JSON. Output: $Text"
    }
}

function Get-CommandNames {
    param(
        [AllowNull()]
        [object[]]$Items
    )

    return @($Items | ForEach-Object { $_.name })
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("tau-release-provenance-" + [Guid]::NewGuid().ToString('N'))

try {
    $repo = Join-Path $tempRoot 'repo'
    $scriptsDir = Join-Path $repo 'scripts'
    $releaseDir = Join-Path $repo 'artifacts/releases'
    $nugetDir = Join-Path $repo 'artifacts/nuget'
    $fakeBin = Join-Path $tempRoot 'bin'
    New-Item -ItemType Directory -Force -Path $scriptsDir, $releaseDir, $nugetDir, $fakeBin | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts/generate-release-provenance.ps1') -Destination (Join-Path $scriptsDir 'generate-release-provenance.ps1')
    Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts/sign-release-packages.ps1') -Destination (Join-Path $scriptsDir 'sign-release-packages.ps1')

    Set-Content -LiteralPath (Join-Path $repo 'Directory.Build.props') -Value @(
        '<Project>',
        '  <PropertyGroup>',
        '    <VersionPrefix>1.2.3</VersionPrefix>',
        '  </PropertyGroup>',
        '</Project>'
    ) -Encoding ASCII
    Set-Content -LiteralPath (Join-Path $releaseDir 'tau-win-x64.zip') -Value 'fake release archive' -Encoding ASCII
    Set-Content -LiteralPath (Join-Path $nugetDir 'Tau.Ai.1.2.3.nupkg') -Value 'fake package ai' -Encoding ASCII
    Set-Content -LiteralPath (Join-Path $nugetDir 'Tau.Agent.1.2.3.nupkg') -Value 'fake package agent' -Encoding ASCII
    Set-Content -LiteralPath (Join-Path $nugetDir 'Tau.Agent.1.2.3.symbols.nupkg') -Value 'fake symbols package' -Encoding ASCII
    Set-Content -LiteralPath (Join-Path $repo 'cert.pfx') -Value 'fake certificate' -Encoding ASCII

    $expectedArchiveHash = (Get-FileHash -LiteralPath (Join-Path $releaseDir 'tau-win-x64.zip') -Algorithm SHA256).Hash.ToLowerInvariant()
    $expectedPackageHash = (Get-FileHash -LiteralPath (Join-Path $nugetDir 'Tau.Ai.1.2.3.nupkg') -Algorithm SHA256).Hash.ToLowerInvariant()

    $fakeDotnet = Join-Path $fakeBin 'dotnet.ps1'
    $fakeDotnetLog = Join-Path $tempRoot 'fake-dotnet.log'
    Set-Content -LiteralPath $fakeDotnet -Value @(
        '$ErrorActionPreference = "Stop"',
        'Add-Content -LiteralPath $env:TAU_FAKE_DOTNET_LOG -Value ($args -join " ")',
        'Write-Output ($args -join " ")',
        'if ($args.Count -ge 3 -and $args[0] -eq "nuget" -and $args[1] -eq "sign") {',
        '    $package = $args[2]',
        '    $output = ""',
        '    for ($i = 0; $i -lt $args.Count; $i++) {',
        '        if ($args[$i] -eq "--output" -and ($i + 1) -lt $args.Count) {',
        '            $output = $args[$i + 1]',
        '        }',
        '    }',
        '    if (-not [string]::IsNullOrWhiteSpace($output)) {',
        '        New-Item -ItemType Directory -Force -Path $output | Out-Null',
        '        Copy-Item -LiteralPath $package -Destination (Join-Path $output ([System.IO.Path]::GetFileName($package))) -Force',
        '    }',
        '}',
        'exit 0'
    ) -Encoding ASCII

    $init = Invoke-Native -FilePath 'git' -Arguments @('init', '-b', 'main', $repo) -AllowFailure
    if ($init.exitCode -ne 0) {
        Invoke-Native -FilePath 'git' -Arguments @('init', $repo) | Out-Null
        Invoke-Git -WorkingDirectory $repo -Arguments @('checkout', '-b', 'main') | Out-Null
    }

    Invoke-Git -WorkingDirectory $repo -Arguments @('config', 'user.email', 'tau@example.invalid') | Out-Null
    Invoke-Git -WorkingDirectory $repo -Arguments @('config', 'user.name', 'Tau Provenance Smoke') | Out-Null
    Invoke-Git -WorkingDirectory $repo -Arguments @('add', '--', '.') | Out-Null
    Invoke-Git -WorkingDirectory $repo -Arguments @('commit', '-m', 'Initial provenance fixture') | Out-Null

    $provenanceScript = Join-Path $scriptsDir 'generate-release-provenance.ps1'
    $signScript = Join-Path $scriptsDir 'sign-release-packages.ps1'

    $dryRun = Invoke-PowerShellScript -ScriptPath $provenanceScript -Arguments @('-Json')
    $dryRunJson = ConvertFrom-JsonOutput -Text $dryRun.output
    $script:results.provenanceDryRun = [ordered]@{
        exitCode = $dryRun.exitCode
        archiveCount = @($dryRunJson.archives).Count
        packageCount = @($dryRunJson.packages).Count
    }
    Assert-Equal -Name 'provenance dry-run exit code' -Actual $dryRun.exitCode -Expected 0
    Assert-Equal -Name 'provenance schema version' -Actual $dryRunJson.schemaVersion -Expected 1
    Assert-Equal -Name 'provenance dry-run true' -Actual $dryRunJson.dryRun -Expected $true
    Assert-Equal -Name 'provenance archive count' -Actual @($dryRunJson.archives).Count -Expected 1
    Assert-Equal -Name 'provenance package count excludes symbols' -Actual @($dryRunJson.packages).Count -Expected 2
    Assert-Equal -Name 'provenance version' -Actual $dryRunJson.version.value -Expected '1.2.3'
    $aiPackageEntry = @($dryRunJson.packages | Where-Object { $_.path -match 'Tau\.Ai\.1\.2\.3\.nupkg$' }) | Select-Object -First 1
    Assert-Equal -Name 'provenance archive hash' -Actual $dryRunJson.archives[0].sha256 -Expected $expectedArchiveHash
    Assert-Equal -Name 'provenance package hash' -Actual $aiPackageEntry.sha256 -Expected $expectedPackageHash

    $apply = Invoke-PowerShellScript -ScriptPath $provenanceScript -Arguments @('-Apply', '-Json')
    $applyJson = ConvertFrom-JsonOutput -Text $apply.output
    $outputPath = Join-Path $repo 'artifacts/provenance/release-provenance.json'
    $writtenJson = Get-Content -LiteralPath $outputPath -Raw | ConvertFrom-Json
    $script:results.provenanceApply = [ordered]@{
        exitCode = $apply.exitCode
        wroteFile = $applyJson.wroteFile
    }
    Assert-Equal -Name 'provenance apply exit code' -Actual $apply.exitCode -Expected 0
    Assert-Equal -Name 'provenance apply wrote file' -Actual $applyJson.wroteFile -Expected $true
    Assert-Equal -Name 'provenance file hash persisted' -Actual $writtenJson.archives[0].sha256 -Expected $expectedArchiveHash

    Set-Content -LiteralPath (Join-Path $repo 'dirty.txt') -Value 'dirty' -Encoding ASCII
    $dirtyApply = Invoke-PowerShellScript -ScriptPath $provenanceScript -Arguments @('-Apply', '-Json') -AllowFailure
    $script:results.provenanceDirtyApply = [ordered]@{
        exitCode = $dirtyApply.exitCode
    }
    Assert-Equal -Name 'provenance dirty apply blocked' -Actual ($dirtyApply.exitCode -ne 0) -Expected $true
    Assert-Matches -Name 'provenance dirty output has blocked check' -Text $dirtyApply.output -Pattern '"clean-worktree"'

    $noCert = Invoke-PowerShellScript -ScriptPath $signScript -Arguments @('-Json') -AllowFailure
    $noCertJson = ConvertFrom-JsonOutput -Text $noCert.output
    $script:results.signNoCert = [ordered]@{
        exitCode = $noCert.exitCode
        certificateMode = $noCertJson.certificate.mode
    }
    Assert-Equal -Name 'sign no cert dry-run succeeds' -Actual $noCert.exitCode -Expected 0
    Assert-Equal -Name 'sign no cert mode' -Actual $noCertJson.certificate.mode -Expected 'missing'
    Assert-Equal -Name 'sign no cert command count' -Actual @($noCertJson.plannedCommands).Count -Expected 0

    $env:TAU_TEST_SIGN_PASSWORD = 'super-secret-sign-password'
    try {
        $signDryRun = Invoke-PowerShellScript -ScriptPath $signScript -Arguments @(
            '-CertificatePath',
            'cert.pfx',
            '-CertificatePasswordEnv',
            'TAU_TEST_SIGN_PASSWORD',
            '-TimestampUrl',
            'http://timestamp.test',
            '-Json'
        )
    }
    finally {
        Remove-Item Env:TAU_TEST_SIGN_PASSWORD -ErrorAction SilentlyContinue
    }
    $signDryRunJson = ConvertFrom-JsonOutput -Text $signDryRun.output
    $signDryRunCommands = @($signDryRunJson.plannedCommands | ForEach-Object { $_.command }) -join [Environment]::NewLine
    $script:results.signDryRun = [ordered]@{
        exitCode = $signDryRun.exitCode
        commandNames = Get-CommandNames -Items @($signDryRunJson.plannedCommands)
    }
    Assert-Equal -Name 'sign dry-run exit code' -Actual $signDryRun.exitCode -Expected 0
    Assert-Equal -Name 'sign dry-run package count' -Actual @($signDryRunJson.packages).Count -Expected 2
    Assert-Matches -Name 'sign dry-run command uses dotnet nuget sign' -Text $signDryRunCommands -Pattern 'nuget sign .*Tau\.Ai\.1\.2\.3\.nupkg'
    Assert-Matches -Name 'sign dry-run command has certificate path' -Text $signDryRunCommands -Pattern '--certificate-path'
    Assert-Matches -Name 'sign dry-run command has timestamp' -Text $signDryRunCommands -Pattern '--timestamper http://timestamp\.test'
    Assert-Matches -Name 'sign dry-run command redacts password' -Text $signDryRunCommands -Pattern '<redacted:TAU_TEST_SIGN_PASSWORD>'
    Assert-NotMatches -Name 'sign dry-run does not leak password' -Text $signDryRun.output -Pattern 'super-secret-sign-password'

    $fingerprintDryRun = Invoke-PowerShellScript -ScriptPath $signScript -Arguments @(
        '-CertificateFingerprint',
        '0123456789ABCDEF',
        '-TimestampUrl',
        'http://timestamp.test',
        '-Json'
    )
    $fingerprintJson = ConvertFrom-JsonOutput -Text $fingerprintDryRun.output
    $fingerprintCommands = @($fingerprintJson.plannedCommands | ForEach-Object { $_.command }) -join [Environment]::NewLine
    $script:results.signFingerprintDryRun = [ordered]@{
        exitCode = $fingerprintDryRun.exitCode
    }
    Assert-Equal -Name 'sign fingerprint dry-run exit code' -Actual $fingerprintDryRun.exitCode -Expected 0
    Assert-Matches -Name 'sign fingerprint command' -Text $fingerprintCommands -Pattern '--certificate-fingerprint 0123456789ABCDEF'

    $noCertApply = Invoke-PowerShellScript -ScriptPath $signScript -Arguments @('-Apply', '-AllowDirty', '-DotnetCli', $fakeDotnet, '-Json') -AllowFailure
    $script:results.signNoCertApply = [ordered]@{
        exitCode = $noCertApply.exitCode
    }
    Assert-Equal -Name 'sign apply without cert blocked' -Actual ($noCertApply.exitCode -ne 0) -Expected $true
    Assert-Matches -Name 'sign no cert apply blocked check' -Text $noCertApply.output -Pattern '"certificate"'

    $env:TAU_TEST_SIGN_PASSWORD = 'super-secret-sign-password'
    $env:TAU_FAKE_DOTNET_LOG = $fakeDotnetLog
    try {
        $signApply = Invoke-PowerShellScript -ScriptPath $signScript -Arguments @(
            '-CertificatePath',
            'cert.pfx',
            '-CertificatePasswordEnv',
            'TAU_TEST_SIGN_PASSWORD',
            '-TimestampUrl',
            'http://timestamp.test',
            '-DotnetCli',
            $fakeDotnet,
            '-Apply',
            '-AllowDirty',
            '-Json'
        )
    }
    finally {
        Remove-Item Env:TAU_TEST_SIGN_PASSWORD -ErrorAction SilentlyContinue
        Remove-Item Env:TAU_FAKE_DOTNET_LOG -ErrorAction SilentlyContinue
    }
    $signApplyJson = ConvertFrom-JsonOutput -Text $signApply.output
    $fakeDotnetOutput = if (Test-Path -LiteralPath $fakeDotnetLog) { Get-Content -LiteralPath $fakeDotnetLog -Raw } else { '' }
    $script:results.signApply = [ordered]@{
        exitCode = $signApply.exitCode
        commandResultCount = @($signApplyJson.commandResults).Count
    }
    Assert-Equal -Name 'sign apply exit code' -Actual $signApply.exitCode -Expected 0
    Assert-Equal -Name 'sign apply succeeded' -Actual $signApplyJson.succeeded -Expected $true
    Assert-Matches -Name 'fake dotnet ran nuget sign' -Text $fakeDotnetOutput -Pattern 'nuget sign .*Tau\.Ai\.1\.2\.3\.nupkg'
    Assert-Matches -Name 'fake dotnet received password' -Text $fakeDotnetOutput -Pattern 'super-secret-sign-password'
    Assert-Matches -Name 'sign apply command redacted password' -Text (@($signApplyJson.commandResults | ForEach-Object { $_.command }) -join [Environment]::NewLine) -Pattern '<redacted:TAU_TEST_SIGN_PASSWORD>'
    Assert-Matches -Name 'sign apply output preview redacted password' -Text (@($signApplyJson.commandResults | ForEach-Object { $_.outputPreview }) -join [Environment]::NewLine) -Pattern '<redacted:TAU_TEST_SIGN_PASSWORD>'
    Assert-NotMatches -Name 'sign apply json does not leak password' -Text $signApply.output -Pattern 'super-secret-sign-password'

    $finalResult = [ordered]@{
        schemaVersion = 1
        succeeded = $true
        fixtureTempRoot = $tempRoot
        results = $script:results
        assertions = $script:assertions
    }

    if ($Json) {
        $finalResult | ConvertTo-Json -Depth 12
    }
    else {
        Write-Host 'Tau release provenance/signing smoke passed'
        Write-Host "  assertions: $($script:assertions.Count)"
        Write-Host "  fixture: $tempRoot"
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
        $finalResult | ConvertTo-Json -Depth 12
    }
    else {
        Write-Host 'Tau release provenance/signing smoke failed'
        Write-Host $_.Exception.Message
    }

    exit 1
}
