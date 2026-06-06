param(
    [string[]]$PackagePath = @(),
    [string]$PackageRoot = 'artifacts/nuget',
    [string]$OutputDirectory = 'artifacts/signed-nuget',
    [string]$CertificatePath = '',
    [string]$CertificateFingerprint = '',
    [string]$CertificatePasswordEnv = 'NUGET_SIGN_CERT_PASSWORD',
    [string]$TimestampUrl = '',
    [ValidateSet('SHA256', 'SHA384', 'SHA512')]
    [string]$HashAlgorithm = 'SHA256',
    [ValidateSet('SHA256', 'SHA384', 'SHA512')]
    [string]$TimestampHashAlgorithm = 'SHA256',
    [string]$DotnetCli = 'dotnet',
    [switch]$Overwrite,
    [switch]$Apply,
    [switch]$AllowDirty,
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
        [string]$BasePath = $repoRoot
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

function Protect-SecretText {
    param(
        [AllowNull()]
        [string]$Text,
        [AllowNull()]
        [string]$Secret,
        [string]$EnvironmentName = ''
    )

    if ([string]::IsNullOrEmpty($Text) -or [string]::IsNullOrEmpty($Secret)) {
        return $Text
    }

    $replacement = if ([string]::IsNullOrWhiteSpace($EnvironmentName)) { '<redacted>' } else { "<redacted:$EnvironmentName>" }
    return $Text.Replace($Secret, $replacement)
}

function Join-CommandDisplay {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [string[]]$Arguments = @(),
        [string]$PasswordEnvironmentName = ''
    )

    $displayParts = @($FilePath)
    $redactNext = $false
    foreach ($part in $Arguments) {
        $displayPart = $part
        if ($redactNext) {
            $displayPart = if ([string]::IsNullOrWhiteSpace($PasswordEnvironmentName)) { '<redacted>' } else { "<redacted:$PasswordEnvironmentName>" }
            $redactNext = $false
        }
        elseif ($part -eq '--certificate-password') {
            $redactNext = $true
        }

        if ($displayPart -match '\s') {
            $displayParts += '"' + ($displayPart -replace '"', '\"') + '"'
        }
        else {
            $displayParts += $displayPart
        }
    }

    return ($displayParts -join ' ')
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

function Invoke-SignStep {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [string[]]$Arguments = @(),
        [string]$PasswordEnvironmentName = '',
        [string]$PasswordValue = ''
    )

    $display = Join-CommandDisplay -FilePath $FilePath -Arguments $Arguments -PasswordEnvironmentName $PasswordEnvironmentName
    $startedAt = Get-Date

    if (-not $Json) {
        Write-Host "==> $Name"
        Write-Host "    $display"
    }

    $result = Invoke-ProcessText -FilePath $FilePath -Arguments $Arguments
    $durationMs = [int]((Get-Date) - $startedAt).TotalMilliseconds
    $redactedOutput = Protect-SecretText -Text $result.output -Secret $PasswordValue -EnvironmentName $PasswordEnvironmentName

    if (-not $Json) {
        if (-not [string]::IsNullOrWhiteSpace($redactedOutput)) {
            Write-Host $redactedOutput
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
        outputPreview = Get-OutputPreview -Output $redactedOutput
        outputLength = $redactedOutput.Length
    }
    $script:commandResults += $stepResult

    if ($result.exitCode -ne 0) {
        throw "Package signing step '$Name' failed with exit code $($result.exitCode)."
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

function Resolve-PackageInputs {
    $files = @()
    if ($PackagePath.Count -gt 0) {
        foreach ($path in $PackagePath) {
            if ([string]::IsNullOrWhiteSpace($path)) {
                continue
            }

            if ([System.Management.Automation.WildcardPattern]::ContainsWildcardCharacters($path)) {
                $files += Get-ChildItem -Path $path -File -ErrorAction SilentlyContinue
                continue
            }

            $fullPath = Convert-ToFullPath -Path $path
            if (Test-Path -LiteralPath $fullPath -PathType Leaf) {
                $files += Get-Item -LiteralPath $fullPath
            }
            else {
                $files += [ordered]@{
                    FullName = $fullPath
                    Missing = $true
                }
            }
        }

        return @($files)
    }

    $root = Convert-ToFullPath -Path $PackageRoot
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        return @()
    }

    return @(Get-ChildItem -LiteralPath $root -Filter '*.nupkg' -File |
        Where-Object { -not $_.Name.EndsWith('.symbols.nupkg', [StringComparison]::OrdinalIgnoreCase) } |
        Sort-Object Name)
}

function New-PackageEntry {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $fullPath = Convert-ToFullPath -Path $Path
    return [ordered]@{
        path = Convert-ToRepoRelativePath -Path $fullPath
        fullPath = $fullPath
        exists = (Test-Path -LiteralPath $fullPath -PathType Leaf)
    }
}

function New-SignArguments {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PackageFullPath,
        [string]$PasswordValue = ''
    )

    $args = @('nuget', 'sign', $PackageFullPath, '--hash-algorithm', $HashAlgorithm, '--timestamp-hash-algorithm', $TimestampHashAlgorithm, '--output', (Convert-ToFullPath -Path $OutputDirectory))
    if (-not [string]::IsNullOrWhiteSpace($CertificatePath)) {
        $args += @('--certificate-path', (Convert-ToFullPath -Path $CertificatePath))
    }
    elseif (-not [string]::IsNullOrWhiteSpace($CertificateFingerprint)) {
        $args += @('--certificate-fingerprint', $CertificateFingerprint)
    }

    if (-not [string]::IsNullOrWhiteSpace($PasswordValue)) {
        $args += @('--certificate-password', $PasswordValue)
    }
    if (-not [string]::IsNullOrWhiteSpace($TimestampUrl)) {
        $args += @('--timestamper', $TimestampUrl)
    }
    if ($Overwrite) {
        $args += '--overwrite'
    }

    return $args
}

function New-Result {
    param(
        [object[]]$Packages,
        [object[]]$PlannedCommands,
        [bool]$Succeeded = $false
    )

    return [ordered]@{
        schemaVersion = 1
        dryRun = -not $Apply.IsPresent
        applied = $Apply.IsPresent
        succeeded = $Succeeded
        packageRoot = Convert-ToRepoRelativePath -Path (Convert-ToFullPath -Path $PackageRoot)
        outputDirectory = Convert-ToRepoRelativePath -Path (Convert-ToFullPath -Path $OutputDirectory)
        certificate = [ordered]@{
            mode = if (-not [string]::IsNullOrWhiteSpace($CertificatePath)) { 'path' } elseif (-not [string]::IsNullOrWhiteSpace($CertificateFingerprint)) { 'fingerprint' } else { 'missing' }
            path = if (-not [string]::IsNullOrWhiteSpace($CertificatePath)) { Convert-ToRepoRelativePath -Path (Convert-ToFullPath -Path $CertificatePath) } else { '' }
            fingerprintPresent = -not [string]::IsNullOrWhiteSpace($CertificateFingerprint)
            passwordEnv = $CertificatePasswordEnv
            passwordPresent = -not [string]::IsNullOrWhiteSpace($certificatePassword)
            passwordValue = '<redacted>'
        }
        signing = [ordered]@{
            timestampUrl = $TimestampUrl
            hashAlgorithm = $HashAlgorithm
            timestampHashAlgorithm = $TimestampHashAlgorithm
            overwrite = $Overwrite.IsPresent
        }
        git = [ordered]@{
            clean = $gitStatus.clean
            allowDirty = $AllowDirty.IsPresent
            statusCount = @($gitStatus.entries).Count
            status = @($gitStatus.entries)
        }
        packages = @($Packages | ForEach-Object {
            [ordered]@{
                path = $_.path
                exists = $_.exists
            }
        })
        checks = $script:checks
        plannedCommands = @($PlannedCommands)
        commandResults = $script:commandResults
        remainingGaps = @(
            'Real package signing remains unverified until sign-release-packages.ps1 is run with -Apply, a real code-signing certificate and the intended timestamp server.',
            'This script signs NuGet packages only; release archive signing, registry publish and external release e2e remain separate Phase 5 gates.',
            'Provenance manifest generation is handled separately by scripts/generate-release-provenance.ps1.'
        )
    }
}

try {
    $certificatePassword = if ([string]::IsNullOrWhiteSpace($CertificatePasswordEnv)) { '' } else { [Environment]::GetEnvironmentVariable($CertificatePasswordEnv) }
    $gitStatus = Get-GitStatus
    if (-not $gitStatus.available) {
        $status = if ($Apply) { 'blocked' } else { 'warning' }
        Add-Check -Name 'clean-worktree' -Status $status -Detail "Could not read git status: $($gitStatus.error)"
    }
    elseif ($gitStatus.clean) {
        Add-Check -Name 'clean-worktree' -Status 'passed' -Detail 'Working tree is clean before package signing.'
    }
    elseif ($Apply -and -not $AllowDirty) {
        Add-Check -Name 'clean-worktree' -Status 'blocked' -Detail "Working tree has $($gitStatus.entries.Count) uncommitted item(s). Package signing requires a clean worktree unless -AllowDirty is explicit."
    }
    else {
        Add-Check -Name 'clean-worktree' -Status 'warning' -Detail "Working tree has $($gitStatus.entries.Count) uncommitted item(s); dry-run remains read-only or -AllowDirty was explicit."
    }

    if (-not [string]::IsNullOrWhiteSpace($CertificatePath) -and -not [string]::IsNullOrWhiteSpace($CertificateFingerprint)) {
        Add-Check -Name 'certificate' -Status 'blocked' -Detail 'Use either -CertificatePath or -CertificateFingerprint, not both.'
    }
    elseif ([string]::IsNullOrWhiteSpace($CertificatePath) -and [string]::IsNullOrWhiteSpace($CertificateFingerprint)) {
        $status = if ($Apply) { 'blocked' } else { 'warning' }
        Add-Check -Name 'certificate' -Status $status -Detail 'Package signing requires -CertificatePath or -CertificateFingerprint before -Apply can execute.'
    }
    elseif (-not [string]::IsNullOrWhiteSpace($CertificatePath)) {
        $certificateFullPath = Convert-ToFullPath -Path $CertificatePath
        if ($Apply -and -not (Test-Path -LiteralPath $certificateFullPath -PathType Leaf)) {
            Add-Check -Name 'certificate' -Status 'blocked' -Detail "Certificate path does not exist: $(Convert-ToRepoRelativePath -Path $certificateFullPath)"
        }
        elseif (Test-Path -LiteralPath $certificateFullPath -PathType Leaf) {
            Add-Check -Name 'certificate' -Status 'passed' -Detail "Certificate path configured: $(Convert-ToRepoRelativePath -Path $certificateFullPath)"
        }
        else {
            Add-Check -Name 'certificate' -Status 'warning' -Detail "Certificate path does not exist yet; dry-run remains read-only: $(Convert-ToRepoRelativePath -Path $certificateFullPath)"
        }
    }
    else {
        Add-Check -Name 'certificate' -Status 'passed' -Detail 'Certificate fingerprint configured.'
    }

    if ([string]::IsNullOrWhiteSpace($TimestampUrl)) {
        Add-Check -Name 'timestamp' -Status 'warning' -Detail 'No timestamp server was configured; signatures may not remain valid after certificate expiration.'
    }
    else {
        Add-Check -Name 'timestamp' -Status 'passed' -Detail "Timestamp server configured: $TimestampUrl"
    }

    $packageInputs = Resolve-PackageInputs
    $missingPackageInputs = @($packageInputs | Where-Object { $_.Missing })
    if ($missingPackageInputs.Count -gt 0) {
        Add-Check -Name 'packages' -Status 'blocked' -Detail "Package path(s) do not exist: $(@($missingPackageInputs | ForEach-Object { Convert-ToRepoRelativePath -Path $_.FullName }) -join ', ')"
    }
    $existingPackageInputs = @($packageInputs | Where-Object { -not $_.Missing })
    $packageEntries = @($existingPackageInputs | ForEach-Object { New-PackageEntry -Path $_.FullName })
    if ($packageEntries.Count -eq 0) {
        Add-Check -Name 'packages' -Status 'blocked' -Detail 'No NuGet package files were found or supplied for signing.'
    }
    else {
        Add-Check -Name 'packages' -Status 'passed' -Detail "NuGet package count: $($packageEntries.Count)."
    }

    $plannedCommands = @()
    if ($packageEntries.Count -gt 0 -and (-not [string]::IsNullOrWhiteSpace($CertificatePath) -or -not [string]::IsNullOrWhiteSpace($CertificateFingerprint))) {
        foreach ($package in $packageEntries) {
            $signArgs = New-SignArguments -PackageFullPath $package.fullPath -PasswordValue $certificatePassword
            $plannedCommands += [ordered]@{
                name = "sign-$([System.IO.Path]::GetFileNameWithoutExtension($package.fullPath))"
                command = Join-CommandDisplay -FilePath $DotnetCli -Arguments $signArgs -PasswordEnvironmentName $CertificatePasswordEnv
                executedWhenApply = $true
            }
        }
    }

    if ($script:hardPreflightFailure) {
        $result = New-Result -Packages $packageEntries -PlannedCommands $plannedCommands -Succeeded $false
        if ($Json) {
            $result | ConvertTo-Json -Depth 12
        }
        else {
            Write-Host 'Tau release package signing blocked'
            foreach ($check in $script:checks) {
                Write-Host "  [$($check.status)] $($check.name): $($check.detail)"
            }
        }
        exit 1
    }

    if ($Apply) {
        New-Item -ItemType Directory -Force -Path (Convert-ToFullPath -Path $OutputDirectory) | Out-Null
        foreach ($package in $packageEntries) {
            Invoke-SignStep -Name "sign-$([System.IO.Path]::GetFileNameWithoutExtension($package.fullPath))" -FilePath $DotnetCli -Arguments (New-SignArguments -PackageFullPath $package.fullPath -PasswordValue $certificatePassword) -PasswordEnvironmentName $CertificatePasswordEnv -PasswordValue $certificatePassword
        }
    }

    $result = New-Result -Packages $packageEntries -PlannedCommands $plannedCommands -Succeeded $true
    if ($Json) {
        $result | ConvertTo-Json -Depth 12
    }
    else {
        Write-Host 'Tau release package signing'
        Write-Host "  mode: $(if ($Apply) { 'applied' } else { 'dry-run' })"
        Write-Host "  packages: $($packageEntries.Count)"
        Write-Host "  output: $($result.outputDirectory)"
        Write-Host ''
        Write-Host 'Checks:'
        foreach ($check in $script:checks) {
            Write-Host "  [$($check.status)] $($check.name): $($check.detail)"
        }
        Write-Host ''
        Write-Host 'Package signing steps:'
        foreach ($command in $plannedCommands) {
            Write-Host "  - $($command.command)"
        }
        Write-Host ''
        Write-Host 'Remaining gaps:'
        foreach ($gap in $result.remainingGaps) {
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
        checks = $script:checks
        commandResults = $script:commandResults
        error = $_.Exception.Message
    }

    if ($Json) {
        $result | ConvertTo-Json -Depth 12
    }
    else {
        Write-Host 'Tau release package signing failed'
        Write-Host $_.Exception.Message
    }

    exit 1
}
