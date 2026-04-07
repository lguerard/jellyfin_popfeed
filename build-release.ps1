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

dotnet publish $projectPath -c $Configuration -o $publishRoot

Copy-Item (Join-Path $publishRoot "Jellyfin.Plugin.Popfeed.dll") $packageRoot

if (Test-Path (Join-Path $publishRoot "Jellyfin.Plugin.Popfeed.pdb")) {
    Copy-Item (Join-Path $publishRoot "Jellyfin.Plugin.Popfeed.pdb") $packageRoot
}

Copy-Item (Join-Path $PSScriptRoot "README.md") (Join-Path $packageRoot "README.md")

Compress-Archive -Path (Join-Path $packageRoot "*") -DestinationPath $zipPath

if ($ManifestSourceUrl) {
    $checksum = (Get-FileHash -Path $zipPath -Algorithm MD5).Hash.ToLowerInvariant()
    & (Join-Path $PSScriptRoot "generate-manifest.ps1") -Version $Version -SourceUrl $ManifestSourceUrl -Checksum $checksum -OutputPath $ManifestOutputPath
}

Write-Host "Created release package: $zipPath"