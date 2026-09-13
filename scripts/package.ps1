param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$JellyfinBin,

    [string]$DotnetPath = 'dotnet',

    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$JellyfinBin = (Resolve-Path -LiteralPath $JellyfinBin).Path
$hostAssembly = Join-Path $JellyfinBin 'MediaBrowser.MediaEncoding.dll'
if ([Reflection.AssemblyName]::GetAssemblyName($hostAssembly).Version.ToString() -ne '12.0.0.0') {
    throw 'JellyfinBin must contain the official Jellyfin 12.0.0 assemblies.'
}
$assemblyVersion = [version]"$Version.0"
$output = Join-Path $root 'dist'
$work = Join-Path $output ([guid]::NewGuid().ToString('N'))
$archiveName = "subtitle-guard_$Version.zip"
$archive = Join-Path $output $archiveName
$dllName = 'Jellyfin.Plugin.SubtitleGuard.Prototype.dll'
New-Item $work -ItemType Directory -Force | Out-Null
try {
    $publish = Join-Path $work 'publish'
    $buildArguments = @(
        'publish', (Join-Path $root 'SubtitleGuard.csproj'),
        '--configuration', 'Release', '--output', $publish,
        "-p:JellyfinBin=$JellyfinBin", "-p:Version=$Version",
        "-p:AssemblyVersion=$assemblyVersion", "-p:FileVersion=$assemblyVersion",
        '-p:ContinuousIntegrationBuild=true', '-p:Deterministic=true',
        "-p:PathMap=$root=/_/src", '-p:DebugType=None',
        '-p:IncludeSourceRevisionInInformationalVersion=false'
    )
    if ($NoRestore) { $buildArguments += '--no-restore' }
    Push-Location $root
    try {
        & $DotnetPath @buildArguments | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
    }
    finally { Pop-Location }
    $dll = Join-Path $publish $dllName
    $identity = [Reflection.AssemblyName]::GetAssemblyName($dll)
    if ($identity.Name -ne 'Jellyfin.Plugin.SubtitleGuard.Prototype' -or $identity.Version -ne $assemblyVersion) {
        throw 'Published assembly identity/version does not match the requested release.'
    }
    $candidate = Join-Path $work $archiveName
    $zip = [IO.Compression.ZipFile]::Open($candidate, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $entry = $zip.CreateEntry($dllName, [IO.Compression.CompressionLevel]::NoCompression)
        $entry.LastWriteTime = [DateTimeOffset]'2000-01-01T00:00:00Z'
        $entry.ExternalAttributes = 0
        $stream = $entry.Open()
        try { $stream.Write([IO.File]::ReadAllBytes($dll)) }
        finally { $stream.Dispose() }
    }
    finally { $zip.Dispose() }
    $sha256 = (Get-FileHash $candidate -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksum = (Get-FileHash $candidate -Algorithm MD5).Hash.ToLowerInvariant()
    $dllSha256 = (Get-FileHash $dll -Algorithm SHA256).Hash.ToLowerInvariant()
    if (Test-Path -LiteralPath $archive) {
        if ((Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $sha256) {
            throw "Release archive already exists with different bytes: $archiveName"
        }
    }
    else { [IO.File]::Move($candidate, $archive) }
    [IO.File]::WriteAllText("$archive.sha256", "$sha256  $archiveName`n", [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $output "subtitle-guard_$Version.dll.sha256"), "$dllSha256  $dllName`n", [Text.UTF8Encoding]::new($false))
    [pscustomobject]@{
        Archive = $archive
        Checksum = $checksum
        Sha256 = $sha256
        DllSha256 = $dllSha256
        Version = $Version
    } | ConvertTo-Json
}
finally { Remove-Item -LiteralPath $work -Recurse -Force }