param(
    [string]$SeedPath = "src/Tau.Ai/Registry/generated-models.seed.json",
    [string]$OutputPath = "src/Tau.Ai/Registry/GeneratedBuiltInModels.g.cs"
)

$ErrorActionPreference = "Stop"

function Escape-CSharpString {
    param([string]$Value)
    return $Value.Replace('\', '\\').Replace('"', '\"')
}

function Format-DecimalLiteral {
    param([decimal]$Value)
    return ($Value.ToString([System.Globalization.CultureInfo]::InvariantCulture) + "m")
}

function Format-BoolLiteral {
    param([bool]$Value)
    if ($Value) { return "true" }
    return "false"
}

function Format-StringArray {
    param([object[]]$Items)
    $escaped = $Items | ForEach-Object { '"' + (Escape-CSharpString ([string]$_)) + '"' }
    return "[" + ($escaped -join ", ") + "]"
}

function Test-JsonProperty {
    param([object]$Object, [string]$Name)
    return $null -ne $Object -and $null -ne $Object.PSObject.Properties[$Name]
}

function Format-StringDictionary {
    param([object]$Object)
    $entries = @()
    foreach ($property in $Object.PSObject.Properties) {
        $entries += '["' + (Escape-CSharpString $property.Name) + '"] = "' + (Escape-CSharpString ([string]$property.Value)) + '"'
    }

    return "new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { " + ($entries -join ", ") + " }"
}

function Format-ObjectValue {
    param([object]$Value)

    if ($null -eq $Value) {
        return "null"
    }

    if ($Value -is [bool]) {
        return Format-BoolLiteral $Value
    }

    if ($Value -is [byte] -or $Value -is [sbyte] -or
        $Value -is [short] -or $Value -is [ushort] -or
        $Value -is [int] -or $Value -is [uint] -or
        $Value -is [long] -or $Value -is [ulong] -or
        $Value -is [float] -or $Value -is [double] -or $Value -is [decimal]) {
        return ([string]::Format([System.Globalization.CultureInfo]::InvariantCulture, "{0}", $Value))
    }

    if ($Value -is [string]) {
        return '"' + (Escape-CSharpString $Value) + '"'
    }

    if ($Value -is [System.Array]) {
        return Format-StringArray @($Value)
    }

    if ($Value -is [System.Management.Automation.PSCustomObject]) {
        return Format-ObjectDictionary $Value
    }

    return '"' + (Escape-CSharpString ([string]$Value)) + '"'
}

function Format-ObjectDictionary {
    param([object]$Object)
    $entries = @()
    foreach ($property in $Object.PSObject.Properties) {
        $entries += '["' + (Escape-CSharpString $property.Name) + '"] = ' + (Format-ObjectValue $property.Value)
    }

    return "new Dictionary<string, object> { " + ($entries -join ", ") + " }"
}

function Format-VercelGatewayRouting {
    param([object]$Routing)
    $clauses = @()
    if (Test-JsonProperty $Routing "only") {
        $clauses += "Only = " + (Format-StringArray @($Routing.only))
    }
    if (Test-JsonProperty $Routing "order") {
        $clauses += "Order = " + (Format-StringArray @($Routing.order))
    }

    if ($clauses.Count -eq 0) {
        return $null
    }

    return "new VercelGatewayRouting { " + ($clauses -join ", ") + " }"
}

function Format-Compat {
    param([object]$Compat)

    if ($null -eq $Compat) {
        return $null
    }

    $clauses = @()
    $boolProperties = @(
        @("supportsStore", "SupportsStore"),
        @("supportsDeveloperRole", "SupportsDeveloperRole"),
        @("supportsReasoningEffort", "SupportsReasoningEffort"),
        @("supportsUsageInStreaming", "SupportsUsageInStreaming"),
        @("requiresThinkingAsText", "RequiresThinkingAsText"),
        @("zaiToolStream", "ZaiToolStream"),
        @("supportsStrictMode", "SupportsStrictMode")
    )
    foreach ($propertyPair in $boolProperties) {
        if (Test-JsonProperty $Compat $propertyPair[0]) {
            $clauses += $propertyPair[1] + " = " + (Format-BoolLiteral ([bool]$Compat.($propertyPair[0])))
        }
    }

    if (Test-JsonProperty $Compat "reasoningEffortMap") {
        $clauses += "ReasoningEffortMap = " + (Format-StringDictionary $Compat.reasoningEffortMap)
    }
    if (Test-JsonProperty $Compat "maxTokensField") {
        $clauses += 'MaxTokensField = "' + (Escape-CSharpString ([string]$Compat.maxTokensField)) + '"'
    }
    if (Test-JsonProperty $Compat "thinkingFormat") {
        $clauses += 'ThinkingFormat = "' + (Escape-CSharpString ([string]$Compat.thinkingFormat)) + '"'
    }
    if (Test-JsonProperty $Compat "openRouterRouting") {
        $clauses += "OpenRouterRouting = " + (Format-ObjectDictionary $Compat.openRouterRouting)
    }
    if (Test-JsonProperty $Compat "vercelGatewayRouting") {
        $routing = Format-VercelGatewayRouting $Compat.vercelGatewayRouting
        if ($null -ne $routing) {
            $clauses += "VercelGatewayRouting = " + $routing
        }
    }

    if ($clauses.Count -eq 0) {
        return $null
    }

    return "new ModelCompatibility { " + ($clauses -join ", ") + " }"
}

$seed = Get-Content -Raw -Path $SeedPath | ConvertFrom-Json
$providers = $seed.providers

$builder = [System.Text.StringBuilder]::new()

[void]$builder.AppendLine("// <auto-generated />")
[void]$builder.AppendLine("// Generated by scripts/generate-tau-ai-models.ps1 from generated-models.seed.json.")
[void]$builder.AppendLine("using Tau.Ai.Providers;")
[void]$builder.AppendLine()
[void]$builder.AppendLine("namespace Tau.Ai.Registry;")
[void]$builder.AppendLine()
[void]$builder.AppendLine("public static class GeneratedBuiltInModels")
[void]$builder.AppendLine("{")
[void]$builder.AppendLine("    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, Model>> Catalog =")
[void]$builder.AppendLine("        new Dictionary<string, IReadOnlyDictionary<string, Model>>(StringComparer.OrdinalIgnoreCase)")
[void]$builder.AppendLine("        {")

foreach ($providerEntry in $providers.PSObject.Properties) {
    $providerName = $providerEntry.Name
    $providerModels = $providerEntry.Value
    [void]$builder.AppendLine("            [`"$providerName`"] = new Dictionary<string, Model>(StringComparer.OrdinalIgnoreCase)")
    [void]$builder.AppendLine("            {")

    foreach ($model in $providerModels) {
        $id = [string]$model.id
        $name = [string]$model.name
        $api = [string]$model.api
        $provider = [string]$model.provider
        $baseUrl = [string]$model.baseUrl
        $reasoning = if ($model.reasoning) { "true" } else { "false" }
        $contextWindow = [int]$model.contextWindow
        $maxOutputTokens = [int]$model.maxOutputTokens
        $inputModalities = Format-StringArray @($model.inputModalities)
        $cost = $model.cost
        $inputCost = Format-DecimalLiteral ([decimal]$cost.input)
        $outputCost = Format-DecimalLiteral ([decimal]$cost.output)
        $cacheReadCost = Format-DecimalLiteral ([decimal]$cost.cacheRead)
        $cacheWriteCost = Format-DecimalLiteral ([decimal]$cost.cacheWrite)

        $create = "Create(`"$id`", `"$name`", `"$api`", `"$provider`", `"$baseUrl`", $reasoning, $contextWindow, $maxOutputTokens, $inputCost, $outputCost, $cacheReadCost, $cacheWriteCost)"
        $withClauses = @("InputModalities = $inputModalities")
        if ($provider -eq "github-copilot") {
            $withClauses += "Headers = GitHubCopilotHeaders.CreateStaticHeaders()"
        }
        if (Test-JsonProperty $model "compat") {
            $compat = Format-Compat $model.compat
            if ($null -ne $compat) {
                $withClauses += "Compat = $compat"
            }
        }

        [void]$builder.AppendLine("                [`"$id`"] = $create with")
        [void]$builder.AppendLine("                {")
        for ($i = 0; $i -lt $withClauses.Count; $i++) {
            $suffix = if ($i -lt $withClauses.Count - 1) { "," } else { "" }
            [void]$builder.AppendLine("                    $($withClauses[$i])$suffix")
        }
        [void]$builder.AppendLine("                },")
    }

    [void]$builder.AppendLine("            },")
}

[void]$builder.AppendLine("        };")
[void]$builder.AppendLine()
[void]$builder.AppendLine("    private static Model Create(")
[void]$builder.AppendLine("        string id,")
[void]$builder.AppendLine("        string name,")
[void]$builder.AppendLine("        string api,")
[void]$builder.AppendLine("        string provider,")
[void]$builder.AppendLine("        string baseUrl,")
[void]$builder.AppendLine("        bool reasoning,")
[void]$builder.AppendLine("        int contextWindow,")
[void]$builder.AppendLine("        int maxOutputTokens,")
[void]$builder.AppendLine("        decimal inputCost,")
[void]$builder.AppendLine("        decimal outputCost,")
[void]$builder.AppendLine("        decimal cacheReadCost,")
[void]$builder.AppendLine("        decimal cacheWriteCost) =>")
[void]$builder.AppendLine("        new()")
[void]$builder.AppendLine("        {")
[void]$builder.AppendLine("            Id = id,")
[void]$builder.AppendLine("            Name = name,")
[void]$builder.AppendLine("            Api = api,")
[void]$builder.AppendLine("            Provider = provider,")
[void]$builder.AppendLine("            BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? string.Empty : baseUrl,")
[void]$builder.AppendLine("            Reasoning = reasoning,")
[void]$builder.AppendLine("            ContextWindow = contextWindow,")
[void]$builder.AppendLine("            MaxOutputTokens = maxOutputTokens,")
[void]$builder.AppendLine("            Cost = new ModelCost(inputCost, outputCost, cacheReadCost, cacheWriteCost)")
[void]$builder.AppendLine("        };")
[void]$builder.AppendLine("}")

$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
$absoluteOutputPath = Join-Path (Resolve-Path ".").Path ($OutputPath.Replace('/', '\'))
[System.IO.File]::WriteAllText($absoluteOutputPath, $builder.ToString(), $utf8NoBom)
Write-Host "Generated $OutputPath"
