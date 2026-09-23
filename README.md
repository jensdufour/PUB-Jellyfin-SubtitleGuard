# Subtitle Guard

**0.4.3 candidate: native subtitle-cache guards with one bounded transport retry
and optional windowed ASS burn-in.**
Four production C# files. Native authentication, playback permissions, HLS routes
and encoder selection are retained. No new playback service, proxy, library scan,
custom media downloader or replacement encoder is installed.

## Sequential Native Extraction Retry

Version0.4.3 retries Jellyfin's native FFmpeg extraction process exactly once when
stderr reports a premature stream end, input/output error or connection reset.
The retry is sequential, uses the same native arguments and cancellation token,
and keeps the pending marker in place until every expected output is nonempty.
Permanent decoder failures, generic nonzero exits, cancellation and a failed second
attempt still follow the existing rejection and cleanup path.

This closes the observed Web failure where the selector remained checked after a
transient remote read ended early and the first subtitle response failed. It adds
no background job, prefetch, loop or playback-policy change. A retry can still take
as long as another native source read. This candidate is not published or installed;
CT110 remains on0.4.2 until a separately approved idle deployment.

## Optional Native HLS Preparation

On Linux, set `SUBTITLEGUARD_WINDOWED_ASS=1` in the Jellyfin service environment
and perform a full service restart. For example, a systemd drop-in:

```ini
[Service]
Environment="SUBTITLEGUARD_WINDOWED_ASS=1"
```

This is one server-wide switch, applied equally to all users. It does not change
account policies or personal language preferences. With the switch absent, only
the existing four incomplete-cache guards run. Startup must explicitly report
`windowed remote ASS burn-in enabled for all users`; an unsupported hook signature
or missing prerequisite leaves windowed mode inactive and the original guards on.

Version0.4.2 resolves FFmpeg's path at preparation time, after native initialization.
In0.4.1 the hosted-service startup check ran too early and left windowed mode off;
the original guards remained active. The focused contract now covers lazy path
resolution. Version0.4.0 was not packaged because its CI FFmpeg prerequisite was
missing;0.4.1 corrected the build prerequisite, not this startup ordering.

For embedded ASS/SSA over HTTP(S), requested as burn-in through native VOD HLS:

An existing nonempty, unmarked native cache stays on the native fast path; it is
not re-downloaded or retrospectively reclassified by windowed mode.

1. Prepare five native HLS segments of subtitles, usually15-30seconds, retaining
   absolute cue times and overlapping cues. Native FFmpeg reads only that input
   interval plus one second of lookahead. A copied-video timestamp stream proves
   acquisition coverage; sparse windows need no artificial last-cue heuristic.
2. Validate exit status, truncation diagnostics, subtitle syntax/header and size.
   A transient source-read failure may retry once, within the same45-second budget.
   Only validated window files are published, outside the native full-source cache.
3. Let Jellyfin build its ordinary burn-in command using that external window.
   Cap the output at the window's **absolute end** with `-to`, not a relative `-t`:
   native HLS preserves timestamps. Refuse a command if the output-limit hook was
   not applied. Restore the original stream descriptor after command construction.
4. Jellyfin's existing HLS job handling starts another job for a later missing
   segment once the bounded job exits. Native permissions, QSV/software selection,
   audio choices, playlists and job tracking remain in charge.

Preparation has two concurrent workers, at most16 source states and64 window
files,8MiB/100,000-event subtitle limits, a256MiB free-space floor, and request/
shutdown cancellation. Inactive window files expire on later preparation calls;
the private runtime generation is removed during normal shutdown. An unclean
shutdown can leave an unused generation under the native cache's
`subtitleguard-windows/`; never treat it as a reusable full-source subtitle cache.

Unsupported requests, resource limits, header changes, failed preparation or a
cold seek more than two minutes beyond known coverage fall back to guarded native
extraction. Local/external subtitles, non-ASS formats, direct play and live HLS are
unchanged. Final-window video coverage allows up to one second of audio/container
tail difference, but still requires video packets, successful extraction and no
truncation diagnostics. This mode does **not** promise zero delay for every source.

The production adapter uses native bounded HLS jobs, **not** the prototype's
continuous RGB pipe. The prototype timing figures below are not measurements of
this adapter. The user waived further provider/device acceptance before release;
native job transitions, long-film behavior, real-provider contention and cold seeks
remain unverified. Basic builds, existing loader/cache checks and a small native
FFmpeg timestamp/duration contract remain required. That contract caught and fixed
the difference between relative duration and absolute end time during seeking.

To disable the feature, remove the environment switch (or set it to0), reload the
service configuration and restart only when playback is idle. This preserves the
existing incomplete-cache guard; no user policy or good native cache needs changing.

## Isolated Segmented Burn-In Prototype

**September19: the user-approved synthetic prototype passes, including Intel QSV.
It is not connected to Jellyfin playback and is not a production fix.** The native
filter experiment below remains valid: a growing ASS file does not stream. This
prototype instead gives libass a complete subtitle window for each completed
media segment while the rest of the source is still arriving.

[Tests/segmented-burn-in.py](Tests/segmented-burn-in.py) uses native FFmpeg to split
the source into timestamp-preserving media windows and extract their ASS events.
It carries still-active events into following windows, preserving original event
times, styles, movement and fades. A window is marked complete only after its
duration and decoded frame count pass; it is never placed in Jellyfin's native
full-source subtitle cache. The continuous HLS encoder receives burned-in RGB
frames from prepared windows. Audio uses a separate HTTP reader and one continuous
AAC encoder, avoiding a fresh audio encoder and its priming at every boundary.

Measured on CT110's bundled FFmpeg8.1.2 as the `jellyfin` user:

| Check | Result |
| --- | --- |
| Complete-window rendering | Four3s windows,120 colour frames, pixel-exact against continuous burn-in |
| ASS continuity | Boundary-spanning cue, layered moving/fading cue and comma-containing text retained |
| Sparse subtitles | Valid final window with no cues accepted |
| Seeks | Prepared windows replayed in order2,1,0,3, each pixel-exact |
| Cold HTTP startup | First prepared window0.066s before final250,787 of1,003,148 source bytes were released |
| Software HLS | First playable segment0.170s before source tail; four segments/120 decoded frames |
| QSV HLS | First playable segment0.271s before source tail; four segments/120 decoded frames |
| Audio/video continuity | Maximum adjacent packet timing error1microsecond audio,0video, for both encoders |
| Mid-source truncation | Three valid prefix windows retained; incomplete next window not published |
| Recovery/cancellation | Separate successful retry generation matched reference; cancellation stopped acquisition |

The HTTP server withholds the final quarter until a completed window or HLS
playlist entry is observed, with a bounded deadline. Both the audio and preparation
readers must pass that gate for HLS acceptance. Native HLS temporary-file publishing
is enabled. Every output frame is decoded; sampled lossy output has maximum mean
RGB error1.932/software and2.085/QSV against the uncompressed reference. This is
packet/frame validation, not listening or physical-device acceptance.

```bash
python3 Tests/segmented-burn-in.py
python3 Tests/segmented-burn-in.py --qsv
```

The sibling `streaming-burn-in.py` supplies the synthetic ASS header. Python3,
FFmpeg and its sibling FFprobe suffice; `--ffmpeg` overrides the binary location.
QSV mode additionally needs the existing Intel render device and driver. Generated
media, HTTP listeners and subprocesses are disposable and bounded; no provider,
Jellyfin API, production cache, plugin configuration or user policy is touched.

**Limits before integration:** this fixture is12s/320x180/10fps FFV1+PCM Matroska,
with keyframes exactly3s apart. Loopback timings are not IPTV or1080p benchmarks.
The prototype deliberately rejects non3s windows and retains all test windows and
decoded frames for comparison; it is not a bounded production buffer. Irregular
keyframes, short legitimate final segments, reordered packets/B-frames, font
attachments, other formats, long films and resource limits still need coverage.
Seeking only replays already-prepared windows: fast cold seeks and long overlapping
cues at an unprepared seek point remain unproven. The recovery check starts a
separate generation, not an automatic or seamless mid-playback retry. Two upstream
reads still need real-provider validation. No Android TV/iPhone playback, native
Jellyfin HLS integration, release, install or restart was performed. Existing0.3.1
guards and ASS burn-in remain unchanged.

## Streaming Burn-In Feasibility

The September19 experiment **does not establish a streaming burn-in fix**.
The installed `8.1.2-Jellyfin` FFmpeg was tested as the Jellyfin service user with
synthetic video and loopback-only ASS sources. Both the ordinary `subtitles`
filter and the `alpha=1:sub2video=1` overlay form showed the same limitation:

| Input | Observed result |
| --- | --- |
| Complete ASS control | 60 frames, early and late captions present; first frame about0.03s |
| HTTP tail withheld until first frame or3s deadline | Two source reads; first video only after both waits/EOF, about6.03s |
| HTTP response interrupted before late cue | Exit0 despite error diagnostics; early caption present, late caption absent |
| ASS file extended after first frame | Exit0 with no error; appended late caption never rendered |

The growing-file test uses real-time synthetic video, appending the late cue after
the first frame and before its4-second display time. Pixel checks distinguish
the early and late caption windows on black video. The overlay test uses software
composition to isolate subtitle ingestion; it is not a QSV or Android TV test.
Fixtures and a dynamically assigned loopback HTTP listener are disposed at exit;
each FFmpeg process has a20-second kill deadline. No real media, application API,
cache, user settings, plugin installation or server restart is involved.

Run on a Linux Jellyfin host with Python3 and its bundled FFmpeg:

```bash
python3 Tests/streaming-burn-in.py
python3 Tests/streaming-burn-in.py --sub2video
```

Use `--ffmpeg /path/to/ffmpeg` for another binary. Exit0 means the controlled
experiment completed, **not** that streaming passed: check `streaming_proven`
and `growing_file_proven`, both false on the tested binary. This probe remains
outside the production plugin and does not require a package-version change.

Do not expose a growing native cache, remove its pending marker, or suppress
truncation diagnostics to bypass preparation. Native ASS burn-in remains enabled;
validated complete caches still work. Bounded extraction retries could improve
recovery but cannot remove this renderer's initial full-input wait. A genuine
streaming implementation needs a different packet-fed rendering path or complete
subtitle windows with segment-by-segment transcoding, plus seek, synchronization,
interruption and client acceptance tests. The isolated segmented prototype above
explores the latter approach; no production architecture is implemented or proven
by these experiments. The optional0.4.2 adapter above is a separate implementation.

## What It Fixes

Jellyfin's affected native extractor deletes failed output for exit `-1`, but
can accept other nonzero exits when a subtitle file is nonempty. That can leave
a file with subtitles through minute 45 reused as though extraction completed.

The always-on cache guard patches four native methods in memory using Harmony;
the opt-in preparation adapter adds the two HLS command hooks described above:

1. Retry once when FFmpeg reports a known transient transport truncation.
2. Map any remaining nonzero FFmpeg exit, or known truncation/error diagnostics, to the
   native failure path. Keep native extraction, conversions and cleanup.
3. Write a `.subtitleguard.pending` marker beside each output before extraction.
   Remove markers only after successful extraction and nonempty expected files.
4. Refuse marked files as fresh cache entries, including after a crash/restart.
5. Refuse to serve marked files even when the native outer method swallows an
   extraction exception. A later request can retry through Jellyfin normally.

No last-cue heuristic: sparse and forced subtitle tracks can legitimately end
early. Exit/diagnostic checks detect known extraction failures, not a provider
serving an already truncated but internally valid file.

## Default Guard Scope

These guarantees describe the default mode with windowed preparation disabled.

- A known transport truncation can trigger one immediate sequential source re-read
   inside the same native request. There is no background extraction, schedule or
   retry loop. Native requests can still require reading much of a film and are not
   guaranteed to stop merely because a client closes playback.
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
process results: bad exits, truncation, cancellation, restart markers, a transient
first-failure/second-success retry and read refusal. The cold collectible-context
check invokes the actual plugin
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
dotnet run --project Tests/RuntimeChecks.csproj -c Release "-p:JellyfinBin=$JellyfinBin" -- --window-contract /path/to/ffmpeg
./scripts/package.ps1 -JellyfinBin $JellyfinBin
```

The versioned ZIP contains the plugin DLL and `0Harmony.dll` only. The historical
plugin DLL name is retained for compatibility; use the ZIP contents, not a DLL
from an older prototype package. The helper prints version and SHA-256.

## Repository Rollout

The published production version remains **SubtitleGuard0.4.2**. This source tree
is an unreleased0.4.3 candidate; do not install it until the release workflow has
published the matching immutable package and catalog entry.

Repository URL:

```text
https://jensdufour.github.io/PUB-Jellyfin-SubtitleGuard/manifest.json
```

The release workflow builds the native checks and plugin on GitHub, packages
Harmony with the plugin, publishes the ZIP and updates the hosted catalog.
Installing the repository package does not require an immediate restart.

1. Wait for the current scan/native writers to finish. Back up the previous
   plugin/configuration and any specifically identified bad native subtitle
   cache files. Do not clear all subtitles or change media paths.
2. Install the published version matching `SubtitleGuard.csproj`, then leave its
   restart pending until approved. The windowed-mode environment switch is optional;
   the original guards need no settings. Old prototype private caches are not imported.
3. Start Jellyfin and confirm the matching `Subtitle Guard <version>.0: native extraction/cache
   guards installed` in the current startup log; verify the installed package
   hash. If opted in, separately require the windowed burn-in enabled message.
   There is no native-encoder replacement to select in configuration.
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