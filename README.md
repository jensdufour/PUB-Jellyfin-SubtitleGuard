# Subtitle Guard for Jellyfin

Subtitle Guard prevents failed subtitle extraction from being accepted as a
nonempty, valid cache. It reads provider media, extracts subtitle tracks into a
private generation, and publishes the generation only after complete input and
successful extraction. It does not change video playback or fetch subtitles from
external subtitle providers.

## Requirements

- Jellyfin 12.0, .NET 10 and Jellyfin FFmpeg.
- Linux for production registration and private-file permission checks.
- Explicit source-origin configuration and a dedicated plugin cache directory.

This release preserves all supplied subtitle languages. Source subtitles that are
already missing dialogue cannot be repaired by extraction validation. First-use
extraction may read the entire media file and delay subtitle availability.

## Custom Repository

The published catalog will be available at:

```text
https://jensdufour.github.io/PUB-Jellyfin-SubtitleGuard/manifest.json
```

Catalog installation does not configure the required production policy. Complete
the setup below before restarting Jellyfin. Automatic updates should stay disabled
when an external startup gate pins the installed DLL hash; update that gate to
the reviewed release hash before restarting after an upgrade.

## Production Policy

Create `/var/lib/jellyfin/data/subtitle-guard` owned by the Jellyfin service user
with mode `0700`. Create `.subtitle-guard-sandbox` and `production-policy.json`
inside it with mode `0600`, owned by the same user. The marker filename is retained
for compatibility; production mode is selected by the environment variable below.

```json
{
  "CacheRoot": "/var/lib/jellyfin/data/subtitle-guard",
  "InitialOrigins": ["https://provider.example"],
  "RedirectOrigins": ["http://media.example"],
  "MaxBytes": 4294967296,
  "TimeoutSeconds": 900
}
```

Set `SUBTITLE_GUARD_PRODUCTION_POLICY` to the absolute policy-file path in the
Jellyfin service environment. Do not combine it with sandbox or pilot variables.
Origins include scheme, host and optional port, with no path, query or credentials.
HTTPS-to-HTTP redirects are allowed only when the destination is approved.
HTTPS certificate validation remains enabled. The production path above is the
currently supported Linux layout; custom data-directory layouts require review.

After startup, check `registration.json` in the cache root against the current
Jellyfin PID/start time and installed assembly hash. A catalog entry marked Active
alone is not proof that the guarded encoder is in use. This repository does not
install a systemd startup gate automatically. Never clear old subtitle caches
until active guarded registration has been independently verified.

## Limits

Matroska extraction streams into FFmpeg; seek-dependent supported containers use
a bounded temporary input file that is removed afterward. Text, mov_text-to-SRT
and PGS paths have integration coverage. DVD output mapping and AVI support have
less runtime coverage. Bitmap-to-text conversion is not supported.

Extraction is serialized. Defaults are a 4 GiB input limit, 15-minute operation
deadline, 24-hour freshness and five-minute failure cooldown. Persistent state
is bounded to 1 MiB and 4,096 entries per category; invalid/excessive state fails
closed. No background cache-retention job or library-wide prefetch is installed.
Atomic publication is not a guarantee against every filesystem/power failure.

## Build And Test

Use .NET SDK 10 and matching Jellyfin 12 release assemblies. Supply `JellyfinBin`
pointing to a directory containing `MediaBrowser.MediaEncoding.dll` and
`Jellyfin.Api.dll`. Host assemblies are references, not plugin payloads.

```sh
dotnet build SubtitleGuard.csproj -c Release -p:JellyfinBin=/path/to/jellyfin/bin
dotnet build Tests/Integration.csproj -c Release -p:JellyfinBin=/path/to/jellyfin/bin
unshare --net -- sh -c 'ip link set lo up && dotnet Tests/bin/Release/net10.0/Integration.dll /tmp/subtitle-guard-new-test /usr/lib/jellyfin-ffmpeg/ffmpeg'
```

Tests generate synthetic media. The test directory must be a new direct `/tmp`
child with the indicated prefix. Do not run integration fixtures against a live
library or use production directories for test output.

## License

GPL-3.0. Dependencies retain their respective licenses.