[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $CurrentPath,

    [Parameter(Mandatory = $true)]
    [string] $BaselinePath
)

$ErrorActionPreference = "Stop"
$failures = [System.Collections.Generic.List[string]]::new()

function Get-JsonProperty($object, [string] $name) {
    if ($null -eq $object) {
        return $null
    }

    $property = $object.PSObject.Properties[$name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function Add-Failure([string] $message) {
    $failures.Add($message)
}

if (-not (Test-Path -LiteralPath $CurrentPath -PathType Leaf)) {
    throw "Current OpenAPI artifact was not found: $CurrentPath"
}
if (-not (Test-Path -LiteralPath $BaselinePath -PathType Leaf)) {
    throw "OpenAPI baseline was not found: $BaselinePath"
}

$current = Get-Content -Raw -LiteralPath $CurrentPath | ConvertFrom-Json
$baseline = Get-Content -Raw -LiteralPath $BaselinePath | ConvertFrom-Json

if ($current.openapi -ne $baseline.openapi) {
    Add-Failure "OpenAPI version changed from '$($baseline.openapi)' to '$($current.openapi)'."
}

foreach ($baselinePathProperty in @($baseline.paths.PSObject.Properties)) {
    $currentPathValue = Get-JsonProperty $current.paths $baselinePathProperty.Name
    if ($null -eq $currentPathValue) {
        Add-Failure "Removed path: $($baselinePathProperty.Name)"
        continue
    }

    foreach ($baselineMethod in @($baselinePathProperty.Value.PSObject.Properties)) {
        if ($baselineMethod.Name -notin @('get', 'post', 'put', 'patch', 'delete', 'head', 'options', 'trace')) {
            continue
        }

        $currentOperation = Get-JsonProperty $currentPathValue $baselineMethod.Name
        if ($null -eq $currentOperation) {
            Add-Failure "Removed operation: $($baselineMethod.Name.ToUpperInvariant()) $($baselinePathProperty.Name)"
            continue
        }

        if ($baselineMethod.Value.operationId -and
            $baselineMethod.Value.operationId -ne $currentOperation.operationId) {
            Add-Failure "Changed operationId for $($baselineMethod.Name.ToUpperInvariant()) $($baselinePathProperty.Name)."
        }

        foreach ($baselineResponse in $baselineMethod.Value.responses.PSObject.Properties) {
            $currentResponse = Get-JsonProperty $currentOperation.responses $baselineResponse.Name
            if ($null -eq $currentResponse) {
                Add-Failure "Removed response $($baselineResponse.Name) from $($baselineMethod.Name.ToUpperInvariant()) $($baselinePathProperty.Name)."
                continue
            }

            foreach ($baselineMediaType in @($baselineResponse.Value.content.PSObject.Properties)) {
                if ($null -eq $baselineMediaType) {
                    continue
                }

                $currentMediaType = Get-JsonProperty $currentResponse.content $baselineMediaType.Name
                if ($null -eq $currentMediaType) {
                    Add-Failure "Removed media type '$($baselineMediaType.Name)' from response $($baselineResponse.Name) on $($baselineMethod.Name.ToUpperInvariant()) $($baselinePathProperty.Name)."
                    continue
                }

                $baselineSchemaRef = Get-JsonProperty $baselineMediaType.Value.schema '$ref'
                $currentSchemaRef = Get-JsonProperty $currentMediaType.schema '$ref'
                if ($baselineSchemaRef -and $baselineSchemaRef -ne $currentSchemaRef) {
                    Add-Failure "Changed response schema for $($baselineMethod.Name.ToUpperInvariant()) $($baselinePathProperty.Name) status $($baselineResponse.Name)."
                }
            }
        }

        foreach ($baselineParameter in @($baselineMethod.Value.parameters)) {
            if ($null -eq $baselineParameter) {
                continue
            }

            $currentParameter = @($currentOperation.parameters) |
                Where-Object { $_.name -eq $baselineParameter.name -and $_.in -eq $baselineParameter.in } |
                Select-Object -First 1
            if ($null -eq $currentParameter) {
                Add-Failure "Removed parameter '$($baselineParameter.name)' from $($baselineMethod.Name.ToUpperInvariant()) $($baselinePathProperty.Name)."
                continue
            }

            if ($baselineParameter.required -and -not $currentParameter.required) {
                Add-Failure "Parameter '$($baselineParameter.name)' became optional on $($baselineMethod.Name.ToUpperInvariant()) $($baselinePathProperty.Name)."
            }
            if (-not $baselineParameter.required -and $currentParameter.required) {
                Add-Failure "Parameter '$($baselineParameter.name)' became required on $($baselineMethod.Name.ToUpperInvariant()) $($baselinePathProperty.Name)."
            }
        }
    }
}

foreach ($baselineSchema in $baseline.components.schemas.PSObject.Properties) {
    $currentSchema = Get-JsonProperty $current.components.schemas $baselineSchema.Name
    if ($null -eq $currentSchema) {
        Add-Failure "Removed schema: $($baselineSchema.Name)"
        continue
    }

    $baselineRequired = @($baselineSchema.Value.required)
    $currentRequired = @($currentSchema.required)
    foreach ($property in $baselineRequired) {
        if ($property -notin $currentRequired) {
            Add-Failure "Property '$property' is no longer required in schema '$($baselineSchema.Name)'."
        }
    }
    foreach ($property in $currentRequired) {
        if ($property -notin $baselineRequired) {
            Add-Failure "New required property '$property' was added to schema '$($baselineSchema.Name)'."
        }
    }

    foreach ($baselineProperty in $baselineSchema.Value.properties.PSObject.Properties) {
        $currentProperty = Get-JsonProperty $currentSchema.properties $baselineProperty.Name
        if ($null -eq $currentProperty) {
            Add-Failure "Removed property '$($baselineProperty.Name)' from schema '$($baselineSchema.Name)'."
            continue
        }

        if ($baselineProperty.Value.type -and $baselineProperty.Value.type -ne $currentProperty.type) {
            Add-Failure "Property '$($baselineProperty.Name)' in schema '$($baselineSchema.Name)' changed type."
        }
        if ($baselineProperty.Value.format -and $baselineProperty.Value.format -ne $currentProperty.format) {
            Add-Failure "Property '$($baselineProperty.Name)' in schema '$($baselineSchema.Name)' changed format."
        }

        foreach ($enumValue in @($baselineProperty.Value.enum)) {
            if ($enumValue -notin @($currentProperty.enum)) {
                Add-Failure "Enum value '$enumValue' was removed from '$($baselineSchema.Name).$($baselineProperty.Name)'."
            }
        }
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Host "OpenAPI contract comparison passed: no breaking changes detected."
