param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [Parameter(Mandatory = $true)]
    [string]$SourceUrl,
    [Parameter(Mandatory = $true)]
    [string]$Checksum,
    [string]$OutputPath = "",
    [string]$Changelog = "",
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

    $value = Find-ScalarValue -Key $Key
    if ($null -ne $value) {
        return $value
    }

    throw "Missing key '$Key' in build.yaml."
}

function Find-ScalarValue {
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

    return $null
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

function Resolve-ReleaseGitReference {
    param([string]$RawVersion)

    $trimmedVersion = $RawVersion.Trim()
    $candidateTags = @($trimmedVersion)
    if (-not $trimmedVersion.StartsWith('v', [StringComparison]::OrdinalIgnoreCase)) {
        $candidateTags += "v$trimmedVersion"
    }

    foreach ($candidateTag in ($candidateTags | Select-Object -Unique)) {
        & git rev-parse --verify --quiet "refs/tags/$candidateTag" *> $null
        if ($LASTEXITCODE -eq 0) {
            return $candidateTag
        }
    }

    return 'HEAD'
}

function Try-GetPreviousReleaseTag {
    param([string]$Reference)

    $describeTarget = if ($Reference -eq 'HEAD') { 'HEAD' } else { "$Reference^" }
    $previousTag = & git describe --tags --abbrev=0 $describeTarget 2>$null
    if ($LASTEXITCODE -ne 0 -or $null -eq $previousTag) {
        return $null
    }

    return (@($previousTag) | Select-Object -First 1).Trim()
}

function Get-ReleaseCommitSubjects {
    param(
        [string]$Reference,
        [string]$PreviousTag,
        [switch]$IncludeMergeCommits
    )

    $arguments = @('log', '--format=%s', '--reverse')
    if (-not $IncludeMergeCommits) {
        $arguments += '--no-merges'
    }

    if ([string]::IsNullOrWhiteSpace($PreviousTag)) {
        $arguments += $Reference
    } else {
        $arguments += "$PreviousTag..$Reference"
    }

    $subjects = & git @arguments 2>$null
    if ($LASTEXITCODE -ne 0 -or $null -eq $subjects) {
        return @()
    }

    return @($subjects | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}

function Get-ReleaseChangelog {
    param([string]$RawVersion, [string]$FallbackChangelog)

    if ($null -eq (Get-Command git -ErrorAction SilentlyContinue)) {
        return $FallbackChangelog
    }

    $reference = Resolve-ReleaseGitReference -RawVersion $RawVersion
    $previousTag = Try-GetPreviousReleaseTag -Reference $reference

    $subjects = Get-ReleaseCommitSubjects -Reference $reference -PreviousTag $previousTag
    if ($subjects.Count -eq 0) {
        $subjects = Get-ReleaseCommitSubjects -Reference $reference -PreviousTag $previousTag -IncludeMergeCommits
    }

    if ($subjects.Count -eq 0) {
        return $FallbackChangelog
    }

    return ($subjects | ForEach-Object { "- $_" }) -join [Environment]::NewLine
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

$fallbackChangelog = Get-BlockValue -Key 'changelog'
if ([string]::IsNullOrWhiteSpace($Changelog)) {
    $Changelog = Get-ReleaseChangelog -RawVersion $Version -FallbackChangelog $fallbackChangelog
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
                changelog = $Changelog
                targetAbi = Get-ScalarValue -Key 'targetAbi'
                sourceUrl = $SourceUrl
                checksum = $Checksum.ToLowerInvariant()
                timestamp = $Timestamp.ToUniversalTime().ToString('o')
            }
        )
    }
)

$imageUrl = Find-ScalarValue -Key 'imageUrl'
if (-not [string]::IsNullOrWhiteSpace($imageUrl)) {
    $manifest[0]['imageUrl'] = $imageUrl
}

$manifestDirectory = Split-Path -Path $OutputPath -Parent
if ($manifestDirectory -and -not (Test-Path $manifestDirectory)) {
    New-Item -ItemType Directory -Path $manifestDirectory -Force | Out-Null
}

ConvertTo-Json -InputObject $manifest -Depth 6 | Set-Content -Path $OutputPath -Encoding UTF8
Write-Host "Created manifest: $OutputPath"