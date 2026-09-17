# Subtitle Guard

**0.3.1: a small guard around Jellyfin 12's native subtitle extraction.**
Two production C# files. No replacement encoder, custom downloader, playback
queue, configuration page, cache database, proxy or library scan.

## What It Fixes

Jellyfin's affected native extractor deletes failed output for exit `-1`, but
can accept other nonzero exits when a subtitle file is nonempty. That can leave
a file with subtitles through minute 45 reused as though extraction completed.

This plugin patches four native methods in memory using Harmony:

1. Map any nonzero FFmpeg exit, or known truncation/error diagnostics, to the
   native failure path. Keep native extraction, conversions and cleanup.
2. Write a `.subtitleguard.pending` marker beside each output before extraction.
   Remove markers only after successful extraction and nonempty expected files.
3. Refuse marked files as fresh cache entries, including after a crash/restart.
4. Refuse to serve marked files even when the native outer method swallows an
   extraction exception. A later request can retry through Jellyfin normally.

No last-cue heuristic: sparse and forced subtitle tracks can legitimately end
early. Exit/diagnostic checks detect known extraction failures, not a provider
serving an already truncated but internally valid file.

## Scope

- No new source reads, scheduled work or automatic retry loops. Native subtitle
  requests still behave as native requests; they can require reading much of a
  film and are not guaranteed to stop merely because a client closes playback.
- No pre-play download requirement, post-play queue or playback interlock is
  added. This fixes incomplete-cache reuse, not progressive subtitle delivery or
  IPTV connection contention. A player that already loaded an old file may need
  its subtitle track reselected after repair.
- Subtitle Extract is not required. It is an optional scheduler around the same
  native encoder; keep its background schedules off or remove it separately.
  This plugin does not modify another plugin or its settings.
- Native cache placement, timeout and stream selection remain Jellyfin-owned.
  English/Dutch player preferences remain native preferences, not a new hard
  extraction filter. Existing good caches are not flushed or re-downloaded.
- No custom state-file capacity or mount checks are needed: only per-output
  markers use the native cache directory. Cache-write errors affect that
  extraction, not an unrelated cache database or all completed subtitles.

## Compatibility

Jellyfin exposes no supported callback with the native FFmpeg exit status.
Harmony is the sole added runtime dependency; it avoids copying the encoder.
The guard checks Jellyfin major version 12 and all four method signatures before
patching, and removes its patches if installation fails. It never modifies server
binaries. Version0.3.0 failed on Jellyfin12.1 because Harmony/MonoMod could not
generate its proxy in Jellyfin's collectible plugin load context. Version0.3.1
bootstraps the bundled Harmony and patch runtime in the non-collectible default
context while retaining normal plugin discovery. The runtime remains loaded until
server shutdown: installation, update and removal require a full server restart.
Private methods can change: do not assume future releases compatible
merely because the plugin catalog reports Active.

Local checks exercised actual Jellyfin encoder methods on .NET 10 with simulated
process results: bad exits, truncation, cancellation, restart markers, successful
retry and read refusal. The cold collectible-context check invokes the actual plugin
registrator before exercising the same guard behavior, with no Harmony preloaded
in the default context. Release CI checks Jellyfin12.0 and12.1 host assemblies.
No IPTV request or full-movie download is made by these checks. Version0.3.1 live
activation still requires the explicit startup-log verification below.

## Build And Package

Use .NET SDK 10 and a matching Jellyfin 12 release bin directory containing
MediaBrowser.MediaEncoding.dll. Host DLLs are references, not package contents.

```powershell
$JellyfinBin = '/path/to/jellyfin/bin'
dotnet run --project Tests/RuntimeChecks.csproj -c Release "-p:JellyfinBin=$JellyfinBin"
dotnet run --project Tests/RuntimeChecks.csproj -c Release "-p:JellyfinBin=$JellyfinBin" -- --collectible
./scripts/package.ps1 -JellyfinBin $JellyfinBin
```

The versioned ZIP contains the plugin DLL and `0Harmony.dll` only. The historical
plugin DLL name is retained for compatibility; use the ZIP contents, not a DLL
from an older prototype package. The helper prints version and SHA-256.

## Deferred Rollout

Add this repository in Jellyfin and install **SubtitleGuard 0.3.1**:

```text
https://jensdufour.github.io/PUB-Jellyfin-SubtitleGuard/manifest.json
```

The release workflow builds the native checks and plugin on GitHub, packages
Harmony with the plugin, publishes the ZIP and updates the hosted catalog.
Installing the repository package does not require an immediate restart.

1. Wait for the current scan/native writers to finish. Back up the previous
   plugin/configuration and any specifically identified bad native subtitle
   cache files. Do not clear all subtitles or change media paths.
2. Install version `0.3.1` from the repository, then leave its restart pending
   until approved. No settings, environment variables or sandbox marker are
   required. Old prototype private caches are not imported.
3. Start Jellyfin and confirm `Subtitle Guard 0.3.1.0: native extraction/cache
   guards installed` in the current startup log; verify the installed package
   hash. There is no native-encoder replacement to select in configuration.
4. Old unmarked caches cannot be retrospectively proven complete. For a cache
   already known to be truncated, quarantine only that source/track's file with
   Jellyfin stopped, preserving a backup, then let the next request regenerate
   it. Do not infer truncation from cue count or the last cue alone.
5. To roll back, stop Jellyfin and remove the new plugin directory or restore
   the recorded previous version. Native Jellyfin ignores pending markers, so
   quarantine any still-marked subtitle outputs with their markers before
   returning to unguarded extraction. Never delete just a marker to declare a
   partial file complete. Keep good unmarked native caches untouched.

The previous prototype download/policy/queue fixtures were removed. The release
workflow uses the current native guard checks and includes Harmony in the ZIP.
Published version `0.1.1` is retained for history, not recommended for this setup.