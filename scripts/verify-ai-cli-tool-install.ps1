param(
    [switch]$SkipRestore,
    [switch]$Json
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
Set-Location $repoRoot

$env:DOTNET_CLI_HOME = if ($env:DOTNET_CLI_HOME) { $env:DOTNET_CLI_HOME } else { Join-Path $repoRoot '.dotnet' }
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = if ($env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE) { $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE } else { '1' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = if ($env:DOTNET_CLI_TELEMETRY_OPTOUT) { $env:DOTNET_CLI_TELEMETRY_OPTOUT } else { '1' }
$env:DOTNET_NOLOGO = if ($env:DOTNET_NOLOGO) { $env:DOTNET_NOLOGO } else { '1' }

New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME | Out-Null

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

function Invoke-ProcessText {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [string[]]$Arguments = @()
    )

    $output = & $FilePath @Arguments 2>&1
    return [ordered]@{
        exitCode = $LASTEXITCODE
        output = ($output -join [Environment]::NewLine)
        command = "$FilePath $($Arguments -join ' ')".Trim()
    }
}

function Invoke-CheckedProcess {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [string[]]$Arguments = @()
    )

    $result = Invoke-ProcessText -FilePath $FilePath -Arguments $Arguments
    $script:results[$Name] = $result
    Add-Assertion -Name "$Name exit code" -Passed ($result.exitCode -eq 0) -Detail "Expected exit code 0, actual $($result.exitCode). Output: $($result.output)"
    return $result
}

function Get-Version {
    $propsPath = Join-Path $repoRoot 'Directory.Build.props'
    [xml]$propsXml = Get-Content -LiteralPath $propsPath -Raw
    foreach ($propertyName in @('PackageVersion', 'Version', 'VersionPrefix')) {
        $node = @($propsXml.SelectNodes("//*[local-name()='$propertyName']")) | Select-Object -First 1
        if ($node -and -not [string]::IsNullOrWhiteSpace($node.InnerText)) {
            return $node.InnerText.Trim()
        }
    }

    throw 'Directory.Build.props does not define PackageVersion, Version or VersionPrefix.'
}

function New-ToolPackage {
    param(
        [Parameter(Mandatory = $true)]
        [string]$CommandName,
        [Parameter(Mandatory = $true)]
        [string]$PackageId,
        [Parameter(Mandatory = $true)]
        [string]$PackageSource,
        [Parameter(Mandatory = $true)]
        [string]$Version
    )

    $packArgs = @(
        'pack',
        'src/Tau.Ai.Cli/Tau.Ai.Cli.csproj',
        '--configuration',
        'Release',
        '--output',
        $PackageSource,
        "-p:PackAsTool=true",
        "-p:ToolCommandName=$CommandName",
        "-p:PackageId=$PackageId",
        "-p:PackageVersion=$Version"
    )
    if ($SkipRestore) {
        $packArgs += '--no-restore'
    }

    return Invoke-CheckedProcess -Name "pack-$CommandName" -FilePath 'dotnet' -Arguments $packArgs
}

function Install-ToolPackage {
    param(
        [Parameter(Mandatory = $true)]
        [string]$CommandName,
        [Parameter(Mandatory = $true)]
        [string]$PackageId,
        [Parameter(Mandatory = $true)]
        [string]$PackageSource,
        [Parameter(Mandatory = $true)]
        [string]$ToolPath,
        [Parameter(Mandatory = $true)]
        [string]$Version
    )

    $installArgs = @(
        'tool',
        'install',
        '--tool-path',
        $ToolPath,
        '--add-source',
        $PackageSource,
        $PackageId,
        '--version',
        $Version,
        '--ignore-failed-sources'
    )

    $result = Invoke-CheckedProcess -Name "install-$CommandName" -FilePath 'dotnet' -Arguments $installArgs
    Add-Assertion -Name "$CommandName install advertises command" -Passed ($result.output -match [regex]::Escape($CommandName)) -Detail "Install output should mention command $CommandName. Output: $($result.output)"
}

function Invoke-Tool {
    param(
        [Parameter(Mandatory = $true)]
        [string]$CommandName,
        [Parameter(Mandatory = $true)]
        [string]$ToolPath,
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    $candidatePaths = @(
        (Join-Path $ToolPath "$CommandName.exe"),
        (Join-Path $ToolPath $CommandName)
    )
    $commandPath = @($candidatePaths | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf }) | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($commandPath)) {
        $commandPath = $candidatePaths[0]
    }

    Add-Assertion -Name "$CommandName shim exists" -Passed (Test-Path -LiteralPath $commandPath -PathType Leaf) -Detail "Expected installed shim at $commandPath."
    return Invoke-CheckedProcess -Name "$CommandName-$($Arguments -join '-')" -FilePath $commandPath -Arguments $Arguments
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("tau-ai-cli-tool-install-" + [Guid]::NewGuid().ToString('N'))
$packageSource = Join-Path $tempRoot 'packages'
$piToolPath = Join-Path $tempRoot 'pi-ai-tool'
$tauToolPath = Join-Path $tempRoot 'tau-ai-tool'

try {
    New-Item -ItemType Directory -Force -Path $packageSource, $piToolPath, $tauToolPath | Out-Null

    $version = Get-Version
    $piPackageId = 'Tau.Ai.Cli.PiAiTool'
    $tauPackageId = 'Tau.Ai.Cli.TauAiTool'

    New-ToolPackage -CommandName 'pi-ai' -PackageId $piPackageId -PackageSource $packageSource -Version $version | Out-Null
    New-ToolPackage -CommandName 'tau-ai' -PackageId $tauPackageId -PackageSource $packageSource -Version $version | Out-Null

    $packages = @(Get-ChildItem -LiteralPath $packageSource -Filter '*.nupkg' -File | Sort-Object Name)
    Add-Assertion -Name 'tool package count' -Passed ($packages.Count -eq 2) -Detail "Expected two tool packages, actual $($packages.Count): $(@($packages | ForEach-Object { $_.Name }) -join ', ')"

    Install-ToolPackage -CommandName 'pi-ai' -PackageId $piPackageId -PackageSource $packageSource -ToolPath $piToolPath -Version $version
    Install-ToolPackage -CommandName 'tau-ai' -PackageId $tauPackageId -PackageSource $packageSource -ToolPath $tauToolPath -Version $version

    $piHelp = Invoke-Tool -CommandName 'pi-ai' -ToolPath $piToolPath -Arguments @('--help')
    Add-Assertion -Name 'pi-ai help command name' -Passed ($piHelp.output -match 'Usage: pi-ai <command> \[provider\] \[options\]') -Detail "pi-ai help should render pi-ai usage. Output: $($piHelp.output)"
    Add-Assertion -Name 'pi-ai help examples' -Passed ($piHelp.output -match 'pi-ai login' -and $piHelp.output -match 'pi-ai list') -Detail "pi-ai help should render pi-ai examples. Output: $($piHelp.output)"

    $piList = Invoke-Tool -CommandName 'pi-ai' -ToolPath $piToolPath -Arguments @('list')
    Add-Assertion -Name 'pi-ai list providers' -Passed ($piList.output -match 'Available OAuth providers:' -and $piList.output -match 'anthropic' -and $piList.output -match 'openai-codex') -Detail "pi-ai list should include OAuth providers. Output: $($piList.output)"

    $tauHelp = Invoke-Tool -CommandName 'tau-ai' -ToolPath $tauToolPath -Arguments @('--help')
    Add-Assertion -Name 'tau-ai help command name' -Passed ($tauHelp.output -match 'Usage: tau-ai <command> \[provider\] \[options\]') -Detail "tau-ai help should render tau-ai usage. Output: $($tauHelp.output)"

    $finalResult = [ordered]@{
        schemaVersion = 1
        succeeded = $true
        version = $version
        packageSource = $packageSource
        packages = @($packages | ForEach-Object { $_.Name })
        results = $script:results
        assertions = $script:assertions
        remainingGaps = @(
            'This is a local dotnet tool package install rehearsal against a temp package source, not a real NuGet registry publish.',
            'Real package registry execution, signing/provenance and package source credential boundaries remain open.'
        )
    }

    if ($Json) {
        $finalResult | ConvertTo-Json -Depth 10
    }
    else {
        Write-Host 'Tau AI CLI tool install smoke passed'
        Write-Host "  version: $version"
        Write-Host "  packages: $(@($packages | ForEach-Object { $_.Name }) -join ', ')"
        Write-Host "  assertions: $($script:assertions.Count)"
    }
}
catch {
    $finalResult = [ordered]@{
        schemaVersion = 1
        succeeded = $false
        results = $script:results
        assertions = $script:assertions
        error = $_.Exception.Message
    }

    if ($Json) {
        $finalResult | ConvertTo-Json -Depth 10
    }
    else {
        Write-Host 'Tau AI CLI tool install smoke failed'
        Write-Host $_.Exception.Message
    }

    exit 1
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
