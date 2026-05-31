param(
    [string]$AgentDirectory = (Join-Path $HOME '.tau'),
    [string]$AuthPath = '',
    [string]$OAuthPath = '',
    [string]$SettingsPath = '',
    [switch]$Apply,
    [switch]$Json
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$invocationDirectory = (Get-Location).Path

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

function Get-JsonObjectProperties {
    param([Parameter(Mandatory = $true)]$Value)

    if ($Value -isnot [pscustomobject]) {
        return $null
    }

    return @($Value.PSObject.Properties)
}

function Get-JsonPropertyExact {
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$Object,
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    foreach ($property in $Object.PSObject.Properties) {
        if ([string]::Equals($property.Name, $Name, [StringComparison]::Ordinal)) {
            return $property
        }
    }

    return $null
}

function Test-ProviderMigrated {
    param(
        [Parameter(Mandatory = $true)]
        [System.Collections.Specialized.OrderedDictionary]$Entries,
        [Parameter(Mandatory = $true)]
        [string]$Provider
    )

    foreach ($key in $Entries.Keys) {
        if ([string]::Equals([string]$key, $Provider, [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

function New-ProviderCredentialObject {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet('oauth', 'api_key')]
        [string]$Type,
        $Source = $null,
        [string]$ApiKey = ''
    )

    $entry = [ordered]@{
        type = $Type
    }

    if ($Type -eq 'api_key') {
        $entry.key = $ApiKey
        return $entry
    }

    foreach ($property in (Get-JsonObjectProperties -Value $Source)) {
        if ([string]::Equals($property.Name, 'type', [StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        $entry[$property.Name] = $property.Value
    }

    return $entry
}

function Convert-SettingsWithoutApiKeys {
    param([Parameter(Mandatory = $true)][pscustomobject]$Settings)

    $next = [ordered]@{}
    foreach ($property in $Settings.PSObject.Properties) {
        if ([string]::Equals($property.Name, 'apiKeys', [StringComparison]::Ordinal)) {
            continue
        }

        $next[$property.Name] = $property.Value
    }

    return $next
}

function Write-Utf8JsonFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        $Value,
        [switch]$RestrictToOwner
    )

    $directory = [System.IO.Path]::GetDirectoryName($Path)
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        [System.IO.Directory]::CreateDirectory($directory) | Out-Null
    }

    $jsonText = ($Value | ConvertTo-Json -Depth 40) + [Environment]::NewLine
    $tempPath = "$Path.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        [System.IO.File]::WriteAllText($tempPath, $jsonText, [System.Text.UTF8Encoding]::new($false))
        if ($RestrictToOwner -and -not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) {
            [System.IO.File]::SetUnixFileMode($tempPath, [System.IO.UnixFileMode]::UserRead -bor [System.IO.UnixFileMode]::UserWrite)
        }

        if (Test-Path -LiteralPath $Path) {
            Remove-Item -LiteralPath $Path -Force
        }

        Move-Item -LiteralPath $tempPath -Destination $Path

        if ($RestrictToOwner -and -not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) {
            [System.IO.File]::SetUnixFileMode($Path, [System.IO.UnixFileMode]::UserRead -bor [System.IO.UnixFileMode]::UserWrite)
        }
    }
    finally {
        if (Test-Path -LiteralPath $tempPath) {
            Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue
        }
    }
}

function New-FileScanResult {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    return [ordered]@{
        path = Convert-ToDisplayPath -Path $Path
        sourceKind = 'none'
        action = 'skipped'
        reason = ''
        providerCount = 0
        providers = @()
        skippedProviders = @()
        error = ''
    }
}

try {
    $resolvedAgentDirectory = Resolve-FullPath -Path $AgentDirectory
    $resolvedAuthPath = if ([string]::IsNullOrWhiteSpace($AuthPath)) {
        Join-Path $resolvedAgentDirectory 'auth.json'
    }
    else {
        Resolve-FullPath -Path $AuthPath
    }
    $resolvedOAuthPath = if ([string]::IsNullOrWhiteSpace($OAuthPath)) {
        Join-Path $resolvedAgentDirectory 'oauth.json'
    }
    else {
        Resolve-FullPath -Path $OAuthPath
    }
    $resolvedSettingsPath = if ([string]::IsNullOrWhiteSpace($SettingsPath)) {
        Join-Path $resolvedAgentDirectory 'settings.json'
    }
    else {
        Resolve-FullPath -Path $SettingsPath
    }
    $resolvedOAuthMigratedPath = "$resolvedOAuthPath.migrated"

    $migratedEntries = [ordered]@{}
    $oauth = New-FileScanResult -Path $resolvedOAuthPath
    $settings = New-FileScanResult -Path $resolvedSettingsPath
    $settingsWithoutApiKeys = $null
    $settingsShouldUpdate = $false
    $authAction = 'skipped'
    $authReason = ''
    $failedCount = 0

    if (Test-Path -LiteralPath $resolvedAuthPath) {
        $authAction = 'skipped'
        $authReason = 'auth-exists'
        $oauth.reason = 'auth-exists'
        $settings.reason = 'auth-exists'
    }
    else {
        if (-not (Test-Path -LiteralPath $resolvedOAuthPath)) {
            $oauth.reason = 'file-missing'
        }
        else {
            try {
                $oauthItem = Get-Item -LiteralPath $resolvedOAuthPath -Force
                $oauth.sourceKind = Get-SourceKind -Item $oauthItem
                if (($oauthItem.Attributes -band [System.IO.FileAttributes]::Directory) -ne 0) {
                    $oauth.reason = 'source-not-file'
                }
                else {
                    try {
                        $parsedOAuth = Get-Content -LiteralPath $resolvedOAuthPath -Raw | ConvertFrom-Json
                        $oauthProperties = Get-JsonObjectProperties -Value $parsedOAuth
                        if ($null -eq $oauthProperties) {
                            $oauth.reason = 'non-object-json'
                        }
                        else {
                            foreach ($property in $oauthProperties) {
                                $credentialProperties = Get-JsonObjectProperties -Value $property.Value
                                if ($null -eq $credentialProperties) {
                                    $oauth.skippedProviders += [ordered]@{
                                        name = $property.Name
                                        reason = 'credential-not-object'
                                    }
                                    continue
                                }

                                $migratedEntries[$property.Name] = New-ProviderCredentialObject -Type oauth -Source $property.Value
                                $oauth.providers += [ordered]@{
                                    name = $property.Name
                                    credentialKind = 'oauth'
                                }
                            }

                            $oauth.providerCount = @($oauth.providers).Count
                            if ($oauth.providerCount -eq 0) {
                                $oauth.reason = 'no-provider-credentials'
                            }
                            elseif (Test-Path -LiteralPath $resolvedOAuthMigratedPath) {
                                $oauth.action = 'blocked'
                                $oauth.reason = 'migrated-target-exists'
                                $failedCount++
                            }
                            else {
                                $oauth.action = if ($Apply) { 'pending' } else { 'would-migrate' }
                            }
                        }
                    }
                    catch {
                        $oauth.reason = 'invalid-json'
                        $oauth.error = $_.Exception.Message
                    }
                }
            }
            catch {
                $oauth.reason = 'cannot-read-file'
                $oauth.error = $_.Exception.Message
            }
        }

        if (-not (Test-Path -LiteralPath $resolvedSettingsPath)) {
            $settings.reason = 'file-missing'
        }
        else {
            try {
                $settingsItem = Get-Item -LiteralPath $resolvedSettingsPath -Force
                $settings.sourceKind = Get-SourceKind -Item $settingsItem
                if (($settingsItem.Attributes -band [System.IO.FileAttributes]::Directory) -ne 0) {
                    $settings.reason = 'source-not-file'
                }
                else {
                    try {
                        $parsedSettings = Get-Content -LiteralPath $resolvedSettingsPath -Raw | ConvertFrom-Json
                        $settingsProperties = Get-JsonObjectProperties -Value $parsedSettings
                        if ($null -eq $settingsProperties) {
                            $settings.reason = 'non-object-json'
                        }
                        else {
                            $apiKeysProperty = Get-JsonPropertyExact -Object $parsedSettings -Name 'apiKeys'
                            if ($null -eq $apiKeysProperty) {
                                $settings.reason = 'api-keys-missing'
                            }
                            else {
                                $apiKeysProperties = Get-JsonObjectProperties -Value $apiKeysProperty.Value
                                if ($null -eq $apiKeysProperties) {
                                    $settings.reason = 'api-keys-not-object'
                                }
                                else {
                                    foreach ($property in $apiKeysProperties) {
                                        if ($property.Value -isnot [string]) {
                                            $settings.skippedProviders += [ordered]@{
                                                name = $property.Name
                                                reason = 'api-key-not-string'
                                            }
                                            continue
                                        }

                                        if (Test-ProviderMigrated -Entries $migratedEntries -Provider $property.Name) {
                                            $settings.skippedProviders += [ordered]@{
                                                name = $property.Name
                                                reason = 'oauth-wins'
                                            }
                                            continue
                                        }

                                        $migratedEntries[$property.Name] = New-ProviderCredentialObject -Type api_key -ApiKey $property.Value
                                        $settings.providers += [ordered]@{
                                            name = $property.Name
                                            credentialKind = 'api_key'
                                        }
                                    }

                                    $settings.providerCount = @($settings.providers).Count
                                    $settingsWithoutApiKeys = Convert-SettingsWithoutApiKeys -Settings $parsedSettings
                                    $settingsShouldUpdate = $true
                                    $settings.action = if ($Apply) { 'pending' } else { 'would-update' }
                                }
                            }
                        }
                    }
                    catch {
                        $settings.reason = 'invalid-json'
                        $settings.error = $_.Exception.Message
                    }
                }
            }
            catch {
                $settings.reason = 'cannot-read-file'
                $settings.error = $_.Exception.Message
            }
        }

        if ($failedCount -eq 0 -and $Apply) {
            if ($migratedEntries.Count -gt 0) {
                try {
                    Write-Utf8JsonFile -Path $resolvedAuthPath -Value $migratedEntries -RestrictToOwner
                    $authAction = 'written'
                }
                catch {
                    $authAction = 'failed'
                    $authReason = 'write-failed'
                    $failedCount++
                    $authError = $_.Exception.Message
                }
            }
            else {
                $authAction = 'skipped'
                $authReason = 'no-credentials'
            }

            if ($failedCount -eq 0 -and $oauth.action -eq 'pending') {
                try {
                    Move-Item -LiteralPath $resolvedOAuthPath -Destination $resolvedOAuthMigratedPath
                    $oauth.action = 'migrated'
                }
                catch {
                    $oauth.action = 'failed'
                    $oauth.reason = 'rename-failed'
                    $oauth.error = $_.Exception.Message
                    $failedCount++
                }
            }

            if ($failedCount -eq 0 -and $settingsShouldUpdate) {
                try {
                    Write-Utf8JsonFile -Path $resolvedSettingsPath -Value $settingsWithoutApiKeys
                    $settings.action = 'updated'
                }
                catch {
                    $settings.action = 'failed'
                    $settings.reason = 'write-failed'
                    $settings.error = $_.Exception.Message
                    $failedCount++
                }
            }
        }
        elseif ($failedCount -eq 0 -and -not $Apply) {
            $authAction = if ($migratedEntries.Count -gt 0) { 'would-write' } else { 'skipped' }
            $authReason = if ($migratedEntries.Count -gt 0) { '' } else { 'no-credentials' }
        }
    }

    $migratedProviderSummaries = @()
    foreach ($key in $migratedEntries.Keys) {
        $entry = $migratedEntries[$key]
        $kind = if ($entry.type -eq 'api_key') { 'api_key' } else { 'oauth' }
        $migratedProviderSummaries += [ordered]@{
            name = [string]$key
            credentialKind = $kind
        }
    }

    $result = [ordered]@{
        schemaVersion = 1
        succeeded = ($failedCount -eq 0)
        dryRun = -not $Apply.IsPresent
        applied = $Apply.IsPresent
        agentDirectory = Convert-ToDisplayPath -Path $resolvedAgentDirectory
        authPath = Convert-ToDisplayPath -Path $resolvedAuthPath
        oauthPath = Convert-ToDisplayPath -Path $resolvedOAuthPath
        settingsPath = Convert-ToDisplayPath -Path $resolvedSettingsPath
        scan = [ordered]@{
            providerCount = $migratedEntries.Count
            oauthProviderCount = @($oauth.providers).Count
            apiKeyProviderCount = @($settings.providers).Count
            skippedProviderCount = @($oauth.skippedProviders).Count + @($settings.skippedProviders).Count
            failed = $failedCount
        }
        auth = [ordered]@{
            path = Convert-ToDisplayPath -Path $resolvedAuthPath
            action = $authAction
            reason = $authReason
            providers = $migratedProviderSummaries
        }
        oauth = $oauth
        settings = $settings
        remainingGaps = @(
            'This helper only migrates legacy oauth.json provider objects and settings.json apiKeys into auth.json.',
            'It does not migrate Tau runtime coding-agent-settings.json, close full auth/settings runtime parity, or prove real OAuth e2e.'
        )
    }

    if ($authAction -eq 'failed' -and $authError) {
        $result.auth.error = $authError
    }
    if ([string]::IsNullOrWhiteSpace($result.oauth.error)) {
        $result.oauth.Remove('error')
    }
    if ([string]::IsNullOrWhiteSpace($result.settings.error)) {
        $result.settings.Remove('error')
    }

    if ($Json) {
        $result | ConvertTo-Json -Depth 12
    }
    else {
        Write-Host "Scanned CodingAgent auth migration inputs under $($result.agentDirectory)."
        if ($result.dryRun) {
            Write-Host 'Dry-run only; pass -Apply to write auth.json and update legacy files.'
        }
        Write-Host "Providers to migrate: $($result.scan.providerCount)"
        Write-Host "OAuth providers: $($result.scan.oauthProviderCount)"
        Write-Host "API key providers: $($result.scan.apiKeyProviderCount)"
        Write-Host "Skipped provider entries: $($result.scan.skippedProviderCount)"
        Write-Host "Failed: $($result.scan.failed)"
        Write-Host "Auth: $($result.auth.action) $($result.auth.reason)"
        Write-Host "OAuth: $($result.oauth.action) $($result.oauth.reason)"
        Write-Host "Settings: $($result.settings.action) $($result.settings.reason)"
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
        authPath = $AuthPath
        oauthPath = $OAuthPath
        settingsPath = $SettingsPath
        error = $_.Exception.Message
    }

    if ($Json) {
        $result | ConvertTo-Json -Depth 6
    }
    else {
        Write-Host 'Tau CodingAgent auth migration failed'
        Write-Host $_.Exception.Message
    }

    exit 1
}
