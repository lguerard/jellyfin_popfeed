param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [Parameter(Mandatory = $true)]
    [string]$SourceUrl,
    [Parameter(Mandatory = $true)]
    [string]$Checksum,
    [string]$OutputPath = "",
    [datetime]$Timestamp = (Get-Date).ToUniversalTime()
)

$ErrorActionPreference = "Stop"

if (-not $OutputPath) {
    $OutputPath = Join-Path $PSScriptRoot "artifacts\manifest.json"
}

$buildYamlPath = Join-Path $PSScriptRoot "build.yaml"
$lines = Get-Content $buildYamlPath

function Get-ScalarValue {
    param([string]$Key)

    foreach ($line in $lines) {
        if ($line -match "^{0}:\s*(.+?)\s*$" -f [regex]::Escape($Key)) {
            $value = $Matches[1].Trim()
            if ($value -in @('|', '|-', '>', '>-')) {
                return $null
            }

            return $value.Trim('"')
        }
    }

    throw "Missing key '$Key' in build.yaml."
}

function Get-BlockValue {
    param([string]$Key)

    for ($index = 0; $index -lt $lines.Count; $index++) {
        if ($lines[$index] -match "^{0}:\s*(.+?)\s*$" -f [regex]::Escape($Key)) {
            $value = $Matches[1].Trim()
            if ($value -notin @('|', '|-', '>', '>-')) {
                return $value.Trim('"')
            }

            $blockLines = @()
            for ($innerIndex = $index + 1; $innerIndex -lt $lines.Count; $innerIndex++) {
                $candidate = $lines[$innerIndex]
                if ($candidate -match '^\s{2,}(.*)$') {
                    $blockLines += $Matches[1]
                    continue
                }

                if ([string]::IsNullOrWhiteSpace($candidate)) {
                    $blockLines += ""
                    continue
                }

                break
            }

            return ($blockLines -join [Environment]::NewLine).TrimEnd()
        }
    }

    throw "Missing block '$Key' in build.yaml."
}

function Normalize-Version {
    param([string]$RawVersion)

    $normalized = $RawVersion.Trim()
    if ($normalized.StartsWith('v', [StringComparison]::OrdinalIgnoreCase)) {
        $normalized = $normalized.Substring(1)
    }

    $parts = $normalized.Split('.')
    while ($parts.Count -lt 4) {
        $parts += '0'
    }

    return ($parts[0..3] -join '.')
}

$manifest = @(
    [ordered]@{
        guid = Get-ScalarValue -Key 'guid'
        name = Get-ScalarValue -Key 'name'
        description = Get-BlockValue -Key 'description'
        overview = Get-ScalarValue -Key 'overview'
        owner = Get-ScalarValue -Key 'owner'
        category = Get-ScalarValue -Key 'category'
        versions = @(
            [ordered]@{
                version = Normalize-Version -RawVersion $Version
                changelog = Get-BlockValue -Key 'changelog'
                targetAbi = Get-ScalarValue -Key 'targetAbi'
                sourceUrl = $SourceUrl
                checksum = $Checksum.ToLowerInvariant()
                timestamp = $Timestamp.ToUniversalTime().ToString('o')
            }
        )
    }
)

$manifestDirectory = Split-Path -Path $OutputPath -Parent
if ($manifestDirectory -and -not (Test-Path $manifestDirectory)) {
    New-Item -ItemType Directory -Path $manifestDirectory -Force | Out-Null
}

$manifest | ConvertTo-Json -Depth 6 | Set-Content -Path $OutputPath -Encoding UTF8
Write-Host "Created manifest: $OutputPath"