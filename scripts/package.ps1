param([Parameter(Mandatory)][string]$JellyfinBin)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'SubtitleGuard.csproj'
$version = ([xml](Get-Content -Raw $project)).Project.PropertyGroup.Version
$output = Join-Path $root "dist/$version"
$publish = Join-Path $output 'publish'
$hostBin = (Resolve-Path $JellyfinBin).Path

dotnet publish $project -c Release "-p:JellyfinBin=$hostBin" -o $publish | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Subtitle Guard build failed' }
$plugin = Join-Path $publish 'Jellyfin.Plugin.SubtitleGuard.Prototype.dll'
$harmony = Join-Path $publish '0Harmony.dll'
if ([System.Diagnostics.FileVersionInfo]::GetVersionInfo($plugin).FileVersion -ne "$version.0") {
    throw 'Plugin assembly version does not match the package'
}
if (-not (Test-Path $harmony)) { throw 'Harmony runtime dependency is missing' }
$archive = Join-Path $output "subtitle-guard_$version.zip"
Compress-Archive -Path $plugin, $harmony -DestinationPath $archive -Force
[pscustomobject]@{
    Version = $version
    Archive = $archive
    SHA256 = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
} | ConvertTo-Json