param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{32}$')][string]$Checksum,
    [Parameter(Mandatory)][string]$Timestamp
)

$ErrorActionPreference = 'Stop'
$path = Join-Path (Split-Path -Parent $PSScriptRoot) 'manifest.json'
$catalog = Get-Content -Raw $path | ConvertFrom-Json -NoEnumerate
if ($catalog.Count -ne 1 -or $catalog[0].guid -ne 'a7c9c612-4b77-46ed-9b92-b32b83d2f771') { throw 'Unexpected catalog identity' }
$existing = @($catalog[0].versions | Where-Object version -eq $Version)
if ($existing.Count) {
    if ($existing.Count -ne 1 -or $existing[0].checksum -ne $Checksum) { throw 'Published release cannot be replaced' }
    return
}
$entry = [pscustomobject]@{
    version = $Version
    changelog = "SubtitleGuard ${Version}: retries one transient native subtitle transport failure before rejecting incomplete output. Optional bounded remote ASS preparation and existing cache guards remain. Set SUBTITLEGUARD_WINDOWED_ASS=1 to enable windowed burn-in for all users. Requires Jellyfin 12 and a full restart."
    targetAbi = '12.0.0.0'
    sourceUrl = "https://github.com/jensdufour/PUB-Jellyfin-SubtitleGuard/releases/download/v$Version/subtitle-guard_$Version.zip"
    checksum = $Checksum
    timestamp = ([datetimeoffset]$Timestamp).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
}
$catalog[0].versions = @($entry) + @($catalog[0].versions)
ConvertTo-Json -InputObject $catalog -Depth 10 | Set-Content $path -Encoding utf8