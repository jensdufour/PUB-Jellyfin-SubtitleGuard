param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{32}$')]
    [string]$Checksum,

    [Parameter(Mandatory = $true)]
    [string]$Timestamp
)

$ErrorActionPreference = 'Stop'
$null = [version]$Version
$null = [datetime]::ParseExact($Timestamp, 'yyyy-MM-ddTHH:mm:ssZ', [Globalization.CultureInfo]::InvariantCulture)
$Checksum = $Checksum.ToLowerInvariant()
$root = Split-Path -Parent $PSScriptRoot
$path = Join-Path $root 'manifest.json'
$manifest = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -NoEnumerate
if ($manifest -isnot [array] -or $manifest.Count -ne 1 -or
    $manifest[0].guid -ne 'a7c9c612-4b77-46ed-9b92-b32b83d2f771' -or $manifest[0].name -ne 'SubtitleGuard') {
    throw 'Expected a single SubtitleGuard catalog entry.'
}
$plugin = $manifest[0]
$sourceUrl = "https://github.com/jensdufour/PUB-Jellyfin-SubtitleGuard/releases/download/v$Version/subtitle-guard_$Version.zip"
$existing = @($plugin.versions | Where-Object { [version]$_.version -eq [version]$Version })
if ($existing.Count) {
    if ($existing.Count -ne 1 -or $existing[0].checksum -ne $Checksum -or
        $existing[0].sourceUrl -ne $sourceUrl -or $existing[0].targetAbi -ne '12.0.0.0') {
        throw 'An existing catalog release cannot be replaced with different bytes or metadata.'
    }
    return
}
$entry = [pscustomobject]@{
    version = $Version
    changelog = "SubtitleGuard ${Version}: guarded subtitle extraction for Jellyfin 12. Requires an explicit source policy and coordinated startup-gate updates where applicable."
    targetAbi = '12.0.0.0'
    sourceUrl = $sourceUrl
    checksum = $Checksum
    timestamp = $Timestamp
}
$plugin.versions = @(@($entry) + @($plugin.versions) | Sort-Object { [version]$_.version } -Descending)
$temporary = "$path.$([guid]::NewGuid().ToString('N')).tmp"
try {
    [IO.File]::WriteAllText($temporary, ((ConvertTo-Json -InputObject $manifest -Depth 10) + "`n"), [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($temporary, $path, $true)
}
finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }