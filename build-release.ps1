param(
    [string]$Configuration = "Release",
    [string]$Version = "dev",
    [string]$ManifestSourceUrl = "",
    [string]$ManifestOutputPath = ""
)

$ErrorActionPreference = "Stop"

$projectPath = Join-Path $PSScriptRoot "Jellyfin.Plugin.Popfeed\Jellyfin.Plugin.Popfeed.csproj"
$artifactsRoot = Join-Path $PSScriptRoot "artifacts"
$publishRoot = Join-Path $artifactsRoot "publish"
$packageRoot = Join-Path $artifactsRoot "package\Popfeed"
$zipPath = Join-Path $artifactsRoot ("Jellyfin.Plugin.Popfeed-{0}.zip" -f $Version)
$buildYamlPath = Join-Path $PSScriptRoot "build.yaml"
$buildYamlLines = Get-Content $buildYamlPath

function Get-NormalizedAssemblyVersion {
    param([string]$RawVersion)

    $trimmedVersion = $RawVersion.Trim()
    if ($trimmedVersion.StartsWith('v', [StringComparison]::OrdinalIgnoreCase)) {
        $trimmedVersion = $trimmedVersion.Substring(1)
    }

    $numericPrefix = [regex]::Match($trimmedVersion, '^\d+(?:\.\d+){0,3}').Value
    if ([string]::IsNullOrWhiteSpace($numericPrefix)) {
        throw "Unable to derive assembly version from '$RawVersion'."
    }

    $parts = $numericPrefix.Split('.')
    while ($parts.Count -lt 4) {
        $parts += '0'
    }

    return ($parts[0..3] -join '.')
}

function Find-BuildScalarValue {
    param([string]$Key)

    foreach ($line in $buildYamlLines) {
        if ($line -match "^{0}:\s*(.+?)\s*$" -f [regex]::Escape($Key)) {
            $value = $Matches[1].Trim()
            if ($value -in @('|', '|-', '>', '>-')) {
                return $null
            }

            return $value.Trim('"')
        }
    }

    return $null
}

function Get-BuildScalarValue {
    param([string]$Key)

    $value = Find-BuildScalarValue -Key $Key
    if ($null -ne $value) {
        return $value
    }

    throw "Missing key '$Key' in build.yaml."
}

function Get-BuildBlockValue {
    param([string]$Key)

    for ($index = 0; $index -lt $buildYamlLines.Count; $index++) {
        if ($buildYamlLines[$index] -match "^{0}:\s*(.+?)\s*$" -f [regex]::Escape($Key)) {
            $value = $Matches[1].Trim()
            if ($value -notin @('|', '|-', '>', '>-')) {
                return $value.Trim('"')
            }

            $blockLines = @()
            for ($innerIndex = $index + 1; $innerIndex -lt $buildYamlLines.Count; $innerIndex++) {
                $candidate = $buildYamlLines[$innerIndex]
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

function Get-BuildArtifacts {
    $artifacts = @()
    $inArtifacts = $false

    foreach ($line in $buildYamlLines) {
        if ($line -match '^artifacts:\s*$') {
            $inArtifacts = $true
            continue
        }

        if (-not $inArtifacts) {
            continue
        }

        if ($line -match '^\s*-\s*"?([^"\r\n]+)"?\s*$') {
            $artifacts += $Matches[1]
            continue
        }

        if (-not [string]::IsNullOrWhiteSpace($line)) {
            break
        }
    }

    if ($artifacts.Count -eq 0) {
        throw 'No artifacts were defined in build.yaml.'
    }

    return $artifacts
}

$normalizedAssemblyVersion = Get-NormalizedAssemblyVersion -RawVersion $Version
$informationalVersion = $Version.Trim()
if ($informationalVersion.StartsWith('v', [StringComparison]::OrdinalIgnoreCase)) {
    $informationalVersion = $informationalVersion.Substring(1)
}

if (-not $ManifestOutputPath) {
    $ManifestOutputPath = Join-Path $artifactsRoot "manifest.json"
}

if (Test-Path $publishRoot) {
    Remove-Item $publishRoot -Recurse -Force
}

if (Test-Path $packageRoot) {
    Remove-Item $packageRoot -Recurse -Force
}

if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}

New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null
New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null

dotnet publish $projectPath -c $Configuration -o $publishRoot -p:Version=$normalizedAssemblyVersion -p:AssemblyVersion=$normalizedAssemblyVersion -p:FileVersion=$normalizedAssemblyVersion -p:InformationalVersion=$informationalVersion

$packageFiles = Get-BuildArtifacts
foreach ($packageFile in $packageFiles) {
    Copy-Item (Join-Path $publishRoot $packageFile) $packageRoot
}

$pluginMeta = [ordered]@{
    category = Get-BuildScalarValue -Key 'category'
    changelog = Get-BuildBlockValue -Key 'changelog'
    description = Get-BuildBlockValue -Key 'description'
    guid = Get-BuildScalarValue -Key 'guid'
    name = Get-BuildScalarValue -Key 'name'
    overview = Get-BuildScalarValue -Key 'overview'
    owner = Get-BuildScalarValue -Key 'owner'
    targetAbi = Get-BuildScalarValue -Key 'targetAbi'
    timestamp = (Get-Date).ToUniversalTime().ToString('o')
    version = $normalizedAssemblyVersion
}

$imageUrl = Find-BuildScalarValue -Key 'imageUrl'
if (-not [string]::IsNullOrWhiteSpace($imageUrl)) {
    $pluginMeta.imageUrl = $imageUrl
}

ConvertTo-Json -InputObject $pluginMeta -Depth 4 | Set-Content -Path (Join-Path $packageRoot 'meta.json') -Encoding UTF8

Compress-Archive -Path (Join-Path $packageRoot "*") -DestinationPath $zipPath

if ($ManifestSourceUrl) {
    $checksum = (Get-FileHash -Path $zipPath -Algorithm MD5).Hash.ToLowerInvariant()
    & (Join-Path $PSScriptRoot "generate-manifest.ps1") -Version $Version -SourceUrl $ManifestSourceUrl -Checksum $checksum -OutputPath $ManifestOutputPath
}

Write-Host "Created release package: $zipPath"