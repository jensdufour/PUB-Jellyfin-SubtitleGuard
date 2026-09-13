using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.MediaEncoding.Subtitles;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SubtitleGuard;

internal static class HttpFaultTests
{
    private const string SyntheticKey = "synthetic-http-api-key";

    public static async Task Run(string root, string ffmpeg, Func<string, Func<Task>, Task> test)
    {
        Check(OperatingSystem.IsLinux()
            && NetworkInterface.GetAllNetworkInterfaces().All(network => network.NetworkInterfaceType == NetworkInterfaceType.Loopback),
            "Run the entire console suite inside unshare --net with only loopback enabled.");
        Check(new[] { "http_proxy", "https_proxy", "all_proxy", "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY" }
            .All(name => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name))),
            "Clear inherited proxy variables before running synthetic HTTP checks.");

        var httpRoot = Path.Combine(root, "http");
        Check(!Path.Exists(httpRoot), "HTTP tests require their own new sandbox.");
        Directory.CreateDirectory(httpRoot);
        using (File.Open(Path.Combine(httpRoot, ".subtitle-guard-sandbox"), FileMode.CreateNew, FileAccess.Write)) { }
        var payload = await File.ReadAllBytesAsync(Path.Combine(root, "bilingual.mkv"));
        var episodePayload = await EpisodePayload(httpRoot, ffmpeg);
        var endpoints = new ConcurrentDictionary<string, Endpoint>(StringComparer.Ordinal);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = httpRoot });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var server = new ServerLifetime(builder.Build());
        var app = server.App;
        app.Run(context => Serve(context, endpoints, payload, app.Lifetime.ApplicationStopping));
        using var suiteDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        try
        {
            await app.StartAsync(suiteDeadline.Token);
            var origin = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            Check(origin.Scheme == "http" && origin.Host == "127.0.0.1" && origin.Port > 0, "Kestrel did not bind literal loopback.");
            var clock = new ManualTimeProvider();
            var sandbox = new Sandbox(httpRoot) { HttpTestOrigin = origin, Clock = clock };

            Task Case(string name, Func<CancellationToken, Task> action) => test("HTTP: " + name, async () =>
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(suiteDeadline.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                try { await action(deadline.Token); }
                finally
                {
                    _ = Generations(httpRoot);
                    AssertNoMediaStaging(httpRoot);
                }
            });

            Fixture Create(string name, Mode mode = Mode.Full, Sandbox? configuration = null, string extension = ".mkv")
            {
                var endpoint = new Endpoint(mode);
                Check(endpoints.TryAdd("/" + name + extension, endpoint), "Duplicate HTTP scenario.");
                var url = new Uri(origin, "/" + name + extension + "?api_key=" + SyntheticKey).AbsoluteUri;
                return new Fixture(configuration ?? sandbox, ffmpeg, url, endpoint);
            }

            string WriteShim(string name, string body)
            {
                if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("Test shims require Linux."); }
                var path = sandbox.ValidatePath(Path.Combine(httpRoot, "episode-fixture", name + "-ffmpeg.sh"));
                Check(!Path.Exists(path) && new[] { path, ffmpeg }.All(value => value.All(character =>
                    char.IsAsciiLetterOrDigit(character) || character is '/' or '-' or '_' or '.')),
                    "Test shim paths must be new and shell-safe.");
                File.WriteAllText(path, "#!/bin/sh\nset -eu\n" + body, new UTF8Encoding(false));
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                return path;
            }

            using var complete = Create("complete");
            await Case("eight cold requests share one full-EOF extraction and publish both tracks", async token =>
            {
                var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var requests = Enumerable.Range(0, 8).Select(async request =>
                {
                    await start.Task;
                    var track = complete.Tracks[request % 2];
                    var path = await complete.Encoder.GetSubtitleFilePath(track, complete.Source, token);
                    complete.AssertCues(await File.ReadAllTextAsync(path, token), "ass", track.Language);
                    await complete.AssertBoth(token);
                }).ToArray();
                start.SetResult();
                await Task.WhenAll(requests);
                Check(Generations(httpRoot).Length == 1, "Cold requests published more than one generation.");
                Check(complete.Endpoint.Requests == 1 && complete.Endpoint.BytesWritten == payload.Length
                    && complete.Endpoint.DeclaredLength == payload.Length, "Full EOF did not use one complete Content-Length response.");
                complete.VerifyProcesses(1);
            });

            await Case("warm all-four-entrypoint reads make zero HTTP requests and preserve cache", async token =>
            {
                var before = Snapshot(httpRoot);
                var requests = complete.Endpoint.Requests;
                await complete.Encoder.ExtractAllExtractableSubtitles(complete.Source, token);
                foreach (var track in complete.Tracks)
                {
                    _ = await complete.Encoder.GetSubtitleFilePath(track, complete.Source, token);
                    Check(await complete.Encoder.GetSubtitleFileCharacterSet(track, track.Language, complete.Source, token) == "UTF-8",
                        "HTTP track charset is not UTF-8.");
                    await complete.AssertConverted(track, "ass", token);
                }

                Check(requests == complete.Endpoint.Requests && before.SequenceEqual(Snapshot(httpRoot)), "Warm reads fetched HTTP or modified cache.");
                complete.VerifyProcesses(1);
            });

            await Case("real GetSubtitles parser/writer preserves all 12 EN/NL cues through 11.8 seconds", async token =>
            {
                var requests = complete.Endpoint.Requests;
                foreach (var track in complete.Tracks)
                {
                    foreach (var format in new[] { "srt", "vtt" })
                    {
                        await complete.AssertConverted(track, format, token);
                    }
                }

                Check(complete.Endpoint.Requests == requests, "Conversion unexpectedly fetched media again.");
                complete.Sources.Verify(manager => manager.GetPlaybackMediaSources(complete.Item, null!, false, false,
                    It.IsAny<CancellationToken>()), Times.Exactly(6));
                complete.Sources.VerifyNoOtherCalls();
                complete.VerifyProcesses(1);
            });

            using var episode = Create("whole-episode", Mode.PauseAfterHalf);
            episode.Source.RunTimeTicks = TimeSpan.FromMinutes(45).Ticks;
            episode.Endpoint.Payload = episodePayload;
            episode.BeforeProcess = () => AssertNoMediaStaging(httpRoot);
            await Case("45-minute body over 32 MiB streams to pending tracks before EOF, then publishes both atomically", async token =>
            {
                Check(episodePayload.Length > 32L * 1024 * 1024 && episodePayload.Length < RemoteSources.MaximumBodyBytes
                    && RemoteSources.MaximumBodyBytes == 256L * 1024 * 1024,
                    "Whole-episode fixture must exceed the old cap and fit within the 256 MiB streaming budget.");
                var before = GoodSnapshot(httpRoot);
                var generations = Generations(httpRoot);
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                var extraction = episode.Encoder.GetSubtitleFilePath(episode.Tracks[0], episode.Source, cancellation.Token);
                try
                {
                    await episode.Endpoint.Started.Task.WaitAsync(token);
                    await episode.ProcessRequested.Task.WaitAsync(token);
                    var cache = Path.Combine(httpRoot, "guard-cache");
                    var pending = Directory.GetDirectories(cache, ".pending-*").Single();
                    while (!episode.Tracks.All(track => File.Exists(Path.Combine(pending, $"{track.Index}.ass"))
                        && new FileInfo(Path.Combine(pending, $"{track.Index}.ass")).Length > 0))
                    {
                        Check(!extraction.IsCompleted, "Extraction completed before both pending tracks were written.");
                        await Task.Delay(TimeSpan.FromMilliseconds(10), token);
                    }

                    Check(!extraction.IsCompleted && episode.Endpoint.BytesWritten == episodePayload.Length / 2
                        && Directory.GetDirectories(cache).Where(path => path != pending).Order(StringComparer.Ordinal).SequenceEqual(generations),
                        "A generation became visible before the complete HTTP body arrived.");
                    AssertNoMediaStaging(httpRoot);
                    episode.Endpoint.Resume.TrySetResult();
                    var path = await extraction;
                    Check(Path.IsPathFullyQualified(path) && File.Exists(path)
                        && !path.Contains(".pending-", StringComparison.Ordinal), "Cold request returned an unpublished subtitle path.");
                    await episode.AssertBoth(token);
                    foreach (var track in episode.Tracks)
                    {
                        await episode.AssertConverted(track, "vtt", token);
                    }

                    Check(episode.Endpoint.Requests == 1 && episode.Endpoint.BytesWritten == episodePayload.Length
                        && episode.Endpoint.DeclaredLength == episodePayload.Length
                        && Generations(httpRoot).Length == generations.Length + 1 && before.All(Snapshot(httpRoot).Contains),
                        "Whole-episode extraction did not consume exactly one full body and publish one complete generation.");
                    episode.VerifyProcesses(1);
                }
                finally
                {
                    episode.Endpoint.Resume.TrySetResult();
                    cancellation.Cancel();
                    try { await extraction.WaitAsync(TimeSpan.FromSeconds(2)); }
                    catch (Exception) when (extraction.IsCompleted) { }
                }
            });

            await Case("whole-episode warm reads across all four entrypoints use zero additional HTTP or FFmpeg", async token =>
            {
                var before = GoodSnapshot(httpRoot);
                await episode.Encoder.ExtractAllExtractableSubtitles(episode.Source, token);
                await episode.AssertBoth(token);
                foreach (var track in episode.Tracks)
                {
                    Check(await episode.Encoder.GetSubtitleFileCharacterSet(track, track.Language, episode.Source, token) == "UTF-8",
                        "Whole-episode charset is not UTF-8.");
                    await episode.AssertConverted(track, "vtt", token);
                }

                Check(episode.Endpoint.Requests == 1 && episode.Endpoint.BytesWritten == episodePayload.Length
                    && before.SequenceEqual(Snapshot(httpRoot)), "Warm whole-episode access fetched media or rewrote cache.");
                episode.VerifyProcesses(1);
            });

            await Case("whole-episode half-transfer refresh publishes nothing and preserves old hashes during cooldown", async token =>
            {
                var before = GoodSnapshot(httpRoot);
                episode.Endpoint.Mode = Mode.Disconnect;
                clock.Advance(TimeSpan.FromMinutes(5));
                await Fails<IOException>(() => episode.Encoder.GetSubtitleFilePath(episode.Tracks[0], episode.Source, token));
                foreach (var track in episode.Tracks)
                {
                    await Fails<IOException>(() => episode.Encoder.GetSubtitleFilePath(track, episode.Source, token));
                }

                Check(!token.IsCancellationRequested && episode.Endpoint.Requests == 2
                    && episode.Endpoint.BytesWritten == episodePayload.Length + (long)episodePayload.Length / 2
                    && before.SequenceEqual(Snapshot(httpRoot)), "Large disconnect retried, published partial tracks or changed old hashes.");
                episode.VerifyProcesses(2);
            });

            await Case("whole-episode explicit full retry discards identical pending output and restores warm reuse", async token =>
            {
                var before = GoodSnapshot(httpRoot);
                episode.Endpoint.Mode = Mode.Full;
                clock.Advance(TimeSpan.FromSeconds(30));
                await episode.AssertBoth(token);
                foreach (var track in episode.Tracks)
                {
                    await episode.AssertConverted(track, "vtt", token);
                }

                await episode.Encoder.ExtractAllExtractableSubtitles(episode.Source, token);
                Check(!token.IsCancellationRequested && episode.Endpoint.Requests == 3
                    && episode.Endpoint.BytesWritten == 2L * episodePayload.Length + episodePayload.Length / 2
                    && before.SequenceEqual(Snapshot(httpRoot)), "Full retry failed to reuse identical files or subsequent warm calls refetched media.");
                episode.VerifyProcesses(3);
            });

            await Case("empty and over-budget declared bodies fail before FFmpeg without publication", async token =>
            {
                foreach (var declared in new[] { 0L, RemoteSources.MaximumBodyBytes + 1 })
                {
                    using var fixture = Create("declared-body-" + declared);
                    fixture.Endpoint.LengthOverride = declared;
                    var before = GoodSnapshot(httpRoot);
                    await Fails<IOException>(() => fixture.Encoder.ExtractAllExtractableSubtitles(fixture.Source, token));
                    await Fails<IOException>(() => fixture.Encoder.ExtractAllExtractableSubtitles(fixture.Source, token));
                    Check(fixture.Endpoint.Requests == 1 && fixture.Endpoint.DeclaredLength == declared
                        && fixture.Endpoint.BytesWritten == 0 && before.SequenceEqual(Snapshot(httpRoot)),
                        "Invalid declared body reached extraction, retried during cooldown or changed cache.");
                    fixture.VerifyProcesses(0);
                }
            });

            await Case("HLS body disguised as Matroska fails without publication or parent-fixture writes", async token =>
            {
                using var fixture = Create("disguised-hls");
                var outsideMedia = Path.Combine(root, "bilingual.mkv");
                Check(File.Exists(outsideMedia) && Path.GetDirectoryName(outsideMedia) == root
                    && !outsideMedia.StartsWith(httpRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal),
                    "Referenced media must be synthetic and outside the HTTP sandbox but inside its parent fixture.");
                fixture.Endpoint.Payload = Encoding.UTF8.GetBytes(
                    $"#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:12\n#EXT-X-MEDIA-SEQUENCE:0\n#EXTINF:12.0,\n{new Uri(outsideMedia).AbsoluteUri}\n#EXT-X-ENDLIST\n");
                string[] OutsideSnapshot() => Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
                    .Where(path => path != httpRoot && !path.StartsWith(httpRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    .Order(StringComparer.Ordinal).Select(path => Directory.Exists(path) ? path
                        : $"{path}|{File.GetLastWriteTimeUtc(path).Ticks}|{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}").ToArray();
                var outsideBefore = OutsideSnapshot();
                var before = GoodSnapshot(httpRoot);
                await Fails<IOException>(() => fixture.Encoder.ExtractAllExtractableSubtitles(fixture.Source, token));
                Check(!token.IsCancellationRequested, "Expected real FFmpeg rejection, not a deadline.");
                Check(fixture.Endpoint.Requests == 1 && fixture.Endpoint.BytesWritten == fixture.Endpoint.Payload.Length
                    && fixture.Endpoint.DeclaredLength == fixture.Endpoint.Payload.Length,
                    "Disguised HLS body was not downloaded completely exactly once.");
                Check(before.SequenceEqual(Snapshot(httpRoot)) && outsideBefore.SequenceEqual(OutsideSnapshot()),
                    "Disguised HLS published a generation or changed files outside the HTTP sandbox.");
                fixture.VerifyProcesses(1);
            });

            await Case("early pipe-consumer exit cleans pending output and releases the same encoder gate for retry", async token =>
            {
                using var fixture = Create("early-consumer");
                var before = GoodSnapshot(httpRoot);
                var generations = Generations(httpRoot).Length;
                Check(File.Exists("/bin/false"), "Early-consumer injection requires the native false executable.");
                fixture.EncoderPathOverride = "/bin/false";
                fixture.BeforeProcess = () =>
                {
                    Check(Directory.GetDirectories(Path.Combine(httpRoot, "guard-cache"), ".pending-*").Length == 1,
                        "Early consumer did not have a pending output directory.");
                    AssertNoMediaStaging(httpRoot);
                };
                try
                {
                    await Fails<IOException>(() => fixture.Encoder.ExtractAllExtractableSubtitles(fixture.Source, token));
                    Check(!token.IsCancellationRequested && fixture.Endpoint.Requests == 1
                        && before.SequenceEqual(Snapshot(httpRoot)),
                        "Early-consumer failure timed out, retried HTTP or changed a good generation.");
                    fixture.VerifyProcesses(1);
                    await Fails<IOException>(() => fixture.Encoder.ExtractAllExtractableSubtitles(fixture.Source, token));
                    Check(fixture.Endpoint.Requests == 1, "Early-consumer failure did not retain the URL cooldown.");
                    fixture.VerifyProcesses(1);
                }
                finally
                {
                    fixture.BeforeProcess = null;
                    fixture.EncoderPathOverride = null;
                }

                clock.Advance(TimeSpan.FromSeconds(30));
                var transferred = fixture.Endpoint.BytesWritten;
                await fixture.Encoder.ExtractAllExtractableSubtitles(fixture.Source, token);
                await fixture.AssertBoth(token);
                Check(!token.IsCancellationRequested && fixture.Endpoint.Requests == 2
                    && fixture.Endpoint.BytesWritten >= transferred + payload.Length
                    && Generations(httpRoot).Length == generations + 1 && before.All(Snapshot(httpRoot).Contains),
                    "Same-encoder retry failed to acquire the gate, fetch once and publish one complete generation while preserving old cache.");
                fixture.VerifyProcesses(2);
            });

            await Case("missing FFmpeg after HTTP headers is sanitized, cooled down and retryable on the same encoder", async token =>
            {
                using var fixture = Create("missing-ffmpeg");
                var before = GoodSnapshot(httpRoot);
                var generations = Generations(httpRoot).Length;
                fixture.EncoderPathOverride = sandbox.ValidatePath(Path.Combine(httpRoot, "episode-fixture", "missing-ffmpeg", "executable"));
                Check(!Path.Exists(fixture.EncoderPathOverride), "Startup failure requires a nonexistent executable.");
                fixture.BeforeProcess = () => Check(fixture.Endpoint.Requests == 1 && fixture.Endpoint.DeclaredLength == payload.Length,
                    "Missing-executable startup was not reached after successful HTTP headers.");
                try
                {
                    await Fails<IOException>(() => fixture.Encoder.ExtractAllExtractableSubtitles(fixture.Source, token));
                    await Fails<IOException>(() => fixture.Encoder.GetSubtitleFilePath(fixture.Tracks[1], fixture.Source, token));
                    Check(!token.IsCancellationRequested && fixture.Endpoint.Requests == 1 && before.SequenceEqual(Snapshot(httpRoot)),
                        "Startup failure timed out, bypassed cooldown or changed good cache.");
                    fixture.VerifyProcesses(1);
                }
                finally
                {
                    fixture.BeforeProcess = null;
                    fixture.EncoderPathOverride = null;
                }

                clock.Advance(TimeSpan.FromSeconds(30));
                await fixture.AssertBoth(token);
                Check(fixture.Endpoint.Requests == 2 && Generations(httpRoot).Length == generations + 1
                    && before.All(Snapshot(httpRoot).Contains), "Fixed executable did not release the gate and publish both tracks on retry.");
                fixture.VerifyProcesses(2);
            });

            await Case("256 KiB stdout and stderr retain a diagnostic across the 4 KiB edge despite valid FFmpeg output", async token =>
            {
                using var fixture = Create("large-diagnostics");
                var before = GoodSnapshot(httpRoot);
                var generations = Generations(httpRoot).Length;
                fixture.EncoderPathOverride = WriteShim("large-diagnostics",
                    "head -c 262144 /dev/zero\nprintf '%4094s' '' >&2\nprintf '%s' '[error]' >&2\n"
                    + "head -c 258043 /dev/zero >&2\n"
                    + $"exec '{ffmpeg}' \"$@\"\n");
                try
                {
                    await Fails<IOException>(() => fixture.Encoder.ExtractAllExtractableSubtitles(fixture.Source, token));
                    await fixture.Endpoint.Finished.Task.WaitAsync(token);
                    Check(!token.IsCancellationRequested && fixture.Endpoint.Requests == 1
                        && fixture.Endpoint.BytesWritten == payload.Length && fixture.Endpoint.DeclaredLength == payload.Length
                        && before.SequenceEqual(Snapshot(httpRoot)), "Diagnostic extraction stalled, skipped full EOF or published output.");
                    await Fails<IOException>(() => fixture.Encoder.ExtractAllExtractableSubtitles(fixture.Source, token));
                    Check(fixture.Endpoint.Requests == 1, "Diagnostic rejection bypassed cooldown.");
                    fixture.VerifyProcesses(1);
                }
                finally
                {
                    fixture.EncoderPathOverride = null;
                }

                clock.Advance(TimeSpan.FromSeconds(30));
                await fixture.AssertBoth(token);
                Check(fixture.Endpoint.Requests == 2 && fixture.Endpoint.BytesWritten == 2L * payload.Length
                    && Generations(httpRoot).Length == generations + 1 && before.All(Snapshot(httpRoot).Contains),
                    "Diagnostic failure stranded the encoder gate or damaged permanent cache.");
                fixture.VerifyProcesses(2);
            });

            await Case("exited wrapper with inherited child pipes times out during EOF drains and leaves no orphan", async token =>
            {
                using var fixture = Create("inherited-drains", configuration:
                    new Sandbox(httpRoot) { HttpTestOrigin = origin, Timeout = TimeSpan.FromSeconds(1), Clock = clock });
                var before = GoodSnapshot(httpRoot);
                var childMarker = sandbox.ValidatePath(Path.Combine(httpRoot, "episode-fixture", "inherited-drains-child.pid"));
                var parentMarker = sandbox.ValidatePath(Path.Combine(httpRoot, "episode-fixture", "inherited-drains-parent.pid"));
                fixture.EncoderPathOverride = WriteShim("inherited-drains",
                    "cat > /dev/null\nsleep 10 &\n"
                    + $"printf '%s\\n' \"$!\" > '{childMarker}'\nprintf '%s\\n' \"$$\" > '{parentMarker}'\nexit 0\n");
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                var elapsed = Stopwatch.StartNew();
                var extraction = fixture.Encoder.ExtractAllExtractableSubtitles(fixture.Source, cancellation.Token);
                try
                {
                    await Fails<OperationCanceledException>(() => extraction.WaitAsync(TimeSpan.FromSeconds(5)));
                    var parentId = int.Parse(await File.ReadAllTextAsync(parentMarker, token));
                    var childId = int.Parse(await File.ReadAllTextAsync(childMarker, token));
                    Check(!token.IsCancellationRequested && !cancellation.IsCancellationRequested
                        && elapsed.Elapsed >= TimeSpan.FromMilliseconds(750) && elapsed.Elapsed < TimeSpan.FromSeconds(5)
                        && parentId > 0 && childId > 0 && !Directory.Exists($"/proc/{parentId}") && Directory.Exists($"/proc/{childId}"),
                        "Expected sandbox timeout after wrapper exit while the inherited-pipe child was still alive.");
                    Check(fixture.Endpoint.Requests == 1 && fixture.Endpoint.BytesWritten == payload.Length
                        && before.SequenceEqual(Snapshot(httpRoot)), "Drain timeout failed full-body consumption or changed cache.");
                    await Fails<IOException>(() => fixture.Encoder.ExtractAllExtractableSubtitles(fixture.Source, token));
                    Check(fixture.Endpoint.Requests == 1, "Drain timeout bypassed cooldown.");
                    fixture.VerifyProcesses(1);
                }
                finally
                {
                    cancellation.Cancel();
                    fixture.EncoderPathOverride = null;
                    if (File.Exists(childMarker))
                    {
                        var childId = int.Parse(await File.ReadAllTextAsync(childMarker));
                        Check(childId > 0 && childId != Environment.ProcessId, "Invalid fixture child PID.");
                        try
                        {
                            using var child = Process.GetProcessById(childId);
                            if (!child.HasExited) { child.Kill(true); }
                            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                        }
                        catch (ArgumentException) when (!Directory.Exists($"/proc/{childId}")) { }
                        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        while (Directory.Exists($"/proc/{childId}"))
                        {
                            await Task.Delay(TimeSpan.FromMilliseconds(20), cleanup.Token);
                        }
                    }

                    try { await extraction.WaitAsync(TimeSpan.FromSeconds(2)); }
                    catch (Exception) when (extraction.IsCompleted) { }
                }

                clock.Advance(TimeSpan.FromSeconds(30));
                await fixture.AssertBoth(token);
                Check(fixture.Endpoint.Requests == 2 && fixture.Endpoint.BytesWritten == 2L * payload.Length
                    && before.All(Snapshot(httpRoot).Contains), "Drain timeout stranded the gate or changed an older generation.");
                fixture.VerifyProcesses(2);
            });

            using var disconnected = Create("disconnect-retry", Mode.Disconnect);
            await Case("abort at 50 percent with full Content-Length publishes neither track and preserves good cache", async token =>
            {
                var before = GoodSnapshot(httpRoot);
                await Fails<IOException>(() => disconnected.Encoder.ExtractAllExtractableSubtitles(disconnected.Source, token));
                Check(before.SequenceEqual(Snapshot(httpRoot)), "Disconnect changed cache or left partial output.");
                Check(disconnected.Endpoint.Requests == 1
                    && disconnected.Endpoint.DeclaredLength == payload.Length
                    && disconnected.Endpoint.BytesWritten == (long)(payload.Length / 2) * disconnected.Endpoint.Requests,
                    "Disconnect did not abort halfway through a declared full body, or retried excessively.");
                disconnected.VerifyProcesses(1);
            });

            await Case("explicit later retry of the same URL succeeds after server mode flips", async token =>
            {
                var before = GoodSnapshot(httpRoot);
                var generations = Generations(httpRoot).Length;
                var requests = disconnected.Endpoint.Requests;
                disconnected.Endpoint.Mode = Mode.Full;
                clock.Advance(TimeSpan.FromSeconds(30));
                await disconnected.Encoder.ExtractAllExtractableSubtitles(disconnected.Source, token);
                await disconnected.AssertBoth(token);
                Check(disconnected.Endpoint.Requests == requests + 1 && Generations(httpRoot).Length == generations + 1,
                    "Explicit retry did not fetch once and publish exactly one generation.");
                Check(before.All(Snapshot(httpRoot).Contains), "Successful retry changed a prior generation.");
                disconnected.VerifyProcesses(2);
            });

            await Case("persistent disconnect starts one process and cooldown suppresses subsequent HTTP and FFmpeg", async token =>
            {
                using var fixture = Create("persistent-disconnect", Mode.Disconnect);
                var before = GoodSnapshot(httpRoot);
                await Fails<IOException>(() => fixture.Encoder.GetSubtitleFilePath(fixture.Tracks[1], fixture.Source, token));
                await Fails<IOException>(() => fixture.Encoder.GetSubtitleFilePath(fixture.Tracks[0], fixture.Source, token));
                Check(fixture.Endpoint.Requests == 1, "Persistent disconnect triggered a retry during cooldown.");
                Check(before.SequenceEqual(Snapshot(httpRoot)), "Persistent disconnect published partial subtitles.");
                fixture.VerifyProcesses(1);
            });

            foreach (var status in new[] { 403, 404, 429, 500 })
            {
                await Case($"status {status} fails without publication or blind retry", async token =>
                {
                    using var fixture = Create("status-" + status, Mode.Status);
                    fixture.Endpoint.Status = status;
                    var before = GoodSnapshot(httpRoot);
                    await Fails<IOException>(() => fixture.Encoder.ExtractAllExtractableSubtitles(fixture.Source, token));
                    Check(fixture.Endpoint.Requests == 1 && before.SequenceEqual(Snapshot(httpRoot)), "HTTP error retried or altered cache.");
                    Check(status != 429 || fixture.Endpoint.RetryAfter == "60", "429 did not carry Retry-After: 60.");
                    fixture.VerifyProcesses(0);
                });
            }

            await Case("actual stalled transfer times out after two seconds and cleans both outputs", async token =>
            {
                using var fixture = Create("stall-timeout", Mode.Stall,
                    new Sandbox(httpRoot) { HttpTestOrigin = origin, Timeout = TimeSpan.FromSeconds(2), Clock = clock });
                var before = GoodSnapshot(httpRoot);
                var elapsed = Stopwatch.StartNew();
                await Fails<OperationCanceledException>(() => fixture.Encoder.ExtractAllExtractableSubtitles(fixture.Source, token));
                Check(fixture.Endpoint.Started.Task.IsCompletedSuccessfully && !token.IsCancellationRequested
                    && elapsed.Elapsed >= TimeSpan.FromSeconds(1.5) && elapsed.Elapsed < TimeSpan.FromSeconds(5),
                    "Expected sandbox timeout during a real transfer, not caller timeout or pre-transfer failure.");
                await fixture.Endpoint.Finished.Task.WaitAsync(TimeSpan.FromSeconds(2), token);
                Check(fixture.Endpoint.Requests == 1 && before.SequenceEqual(Snapshot(httpRoot)), "Timeout retried or left output behind.");
                fixture.VerifyProcesses(1);
            });

            await Case("caller cancellation after server-start signal ends transfer and leaves cache unchanged", async token =>
            {
                using var fixture = Create("stall-cancel", Mode.Stall);
                var before = GoodSnapshot(httpRoot);
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                var extraction = fixture.Encoder.GetSubtitleFilePath(fixture.Tracks[0], fixture.Source, cancellation.Token);
                try
                {
                    await fixture.Endpoint.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), token);
                    await fixture.ProcessRequested.Task.WaitAsync(TimeSpan.FromSeconds(2), token);
                    Check(!extraction.IsCompleted, "Transfer ended before caller cancellation.");
                    cancellation.Cancel();
                    await Fails<OperationCanceledException>(() => extraction.WaitAsync(TimeSpan.FromSeconds(2)));
                    await fixture.Endpoint.Finished.Task.WaitAsync(TimeSpan.FromSeconds(2), token);
                    Check(!token.IsCancellationRequested && fixture.Endpoint.Requests == 1
                        && before.SequenceEqual(Snapshot(httpRoot)), "Cancellation timed out, retried or changed cache.");
                    fixture.VerifyProcesses(1);
                }
                finally
                {
                    cancellation.Cancel();
                    try { await extraction.WaitAsync(TimeSpan.FromSeconds(2)); }
                    catch (Exception) when (extraction.IsCompleted) { }
                }
            });

            await Case("range server honors explicit offsets and supports extraction; no FFmpeg-seek claim", async token =>
            {
                using var fixture = Create("ranges", Mode.Range);
                using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
                using var request = new HttpRequestMessage(HttpMethod.Get, fixture.Source.Path);
                request.Headers.Range = new RangeHeaderValue(17, 63);
                using var response = await client.SendAsync(request, token);
                Check(response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentLength == 47
                    && response.Content.Headers.ContentRange?.From == 17 && response.Content.Headers.ContentRange?.To == 63
                    && response.Content.Headers.ContentRange?.Length == payload.Length, "Range response headers are incorrect.");
                Check((await response.Content.ReadAsByteArrayAsync(token)).SequenceEqual(payload[17..64]), "Range body did not match requested offsets.");
                Check(fixture.Endpoint.Ranges.Contains((17L, 63L)), "Server did not record the explicit range offsets.");
                var requests = fixture.Endpoint.Requests;
                await fixture.Encoder.ExtractAllExtractableSubtitles(fixture.Source, token);
                await fixture.AssertBoth(token);
                Check(fixture.Endpoint.Requests > requests && fixture.Endpoint.Requests <= requests + 4, "Range extraction had no fetch or excessive fetches.");
                fixture.VerifyProcesses(1);
            });

            await Case("cross-origin redirect rejects a reachable second loopback listener before any target request", async token =>
            {
                var targetHits = 0;
                var targetBuilder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = httpRoot });
                targetBuilder.Logging.ClearProviders();
                targetBuilder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
                await using var targetServer = new ServerLifetime(targetBuilder.Build());
                var targetApp = targetServer.App;
                targetApp.Run(context =>
                {
                    Interlocked.Increment(ref targetHits);
                    context.Response.ContentLength = payload.Length;
                    return context.Response.Body.WriteAsync(payload, context.RequestAborted).AsTask();
                });
                await targetApp.StartAsync(token);
                using var fixture = Create("redirect", Mode.Redirect);
                try
                {
                    var targetOrigin = new Uri(targetApp.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
                    Check(targetOrigin.Host == "127.0.0.1" && targetOrigin.Port != origin.Port, "Second listener is not a distinct loopback origin.");
                    fixture.Endpoint.RedirectTo = new Uri(targetOrigin, "/synthetic.mkv?api_key=" + SyntheticKey).AbsoluteUri;
                    var before = GoodSnapshot(httpRoot);
                    await Fails<IOException>(() => fixture.Encoder.ExtractAllExtractableSubtitles(fixture.Source, token));
                    Check(fixture.Endpoint.Requests == 1 && Volatile.Read(ref targetHits) == 0
                        && before.SequenceEqual(Snapshot(httpRoot)), "Cross-origin redirect reached its target, retried or published output.");
                    fixture.VerifyProcesses(0);
                }
                finally
                {
                    using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await targetApp.StopAsync(shutdown.Token);
                }
            });

            await Case("different-origin input is rejected before spawning FFmpeg", async token =>
            {
                using var fixture = Create("different-origin");
                fixture.Source.Path = new UriBuilder(fixture.Source.Path) { Port = origin.Port == 1 ? 2 : 1 }.Uri.AbsoluteUri;
                var before = GoodSnapshot(httpRoot);
                await Fails<NotSupportedException>(() => fixture.Encoder.ExtractAllExtractableSubtitles(fixture.Source, token));
                Check(fixture.Endpoint.Requests == 0 && before.SequenceEqual(Snapshot(httpRoot)), "Different origin reached HTTP or cache.");
                fixture.VerifyProcesses(0);
            });

            await Case("absent HTTP opt-in rejects even loopback before spawning FFmpeg", async token =>
            {
                using var fixture = Create("no-origin", configuration: new Sandbox(httpRoot) { Clock = clock });
                var before = GoodSnapshot(httpRoot);
                await Fails<NotSupportedException>(() => fixture.Encoder.GetSubtitleFilePath(fixture.Tracks[0], fixture.Source, token));
                Check(fixture.Endpoint.Requests == 0 && before.SequenceEqual(Snapshot(httpRoot)), "Absent opt-in reached HTTP or cache.");
                fixture.VerifyProcesses(0);
            });

            await Case("strong ETag revalidates once at five-minute expiry with 304 and no body or FFmpeg", async token =>
            {
                using var fixture = Create("etag-304");
                fixture.Endpoint.ETag = "\"revision-one\"";
                fixture.Endpoint.Modified = clock.GetUtcNow().AddDays(-1);
                await fixture.AssertBoth(token);
                var before = GoodSnapshot(httpRoot);
                clock.Advance(TimeSpan.FromSeconds(299));
                await fixture.AssertBoth(token);
                Check(fixture.Endpoint.Requests == 1, "Warm TTL ended before five minutes.");
                clock.Advance(TimeSpan.FromSeconds(1));
                await fixture.AssertBoth(token);
                fixture.Endpoint.AssertHeaders(("", ""), ("\"revision-one\"", ""));
                Check(fixture.Endpoint.NotModified == 1 && fixture.Endpoint.BytesWritten == payload.Length
                    && before.SequenceEqual(Snapshot(httpRoot)), "304 transferred a body or changed the published generation.");
                clock.Advance(TimeSpan.FromSeconds(299));
                await fixture.AssertBoth(token);
                Check(fixture.Endpoint.Requests == 2, "304 failed to refresh the five-minute warm TTL.");
                fixture.VerifyProcesses(1);
            });

            await Case("changed ETag and same-length payload on the same URL publish new EN/NL text, not stale cues", async token =>
            {
                using var fixture = Create("etag-changed");
                fixture.Endpoint.ETag = "\"old\"";
                await fixture.AssertBoth(token);
                var before = GoodSnapshot(httpRoot);
                var generations = Generations(httpRoot).Length;
                var originalUrl = fixture.Source.Path;
                var originalId = fixture.Source.Id;
                var oldPaths = await Task.WhenAll(fixture.Tracks.Select(track => fixture.Encoder.GetSubtitleFilePath(track, fixture.Source, token)));
                fixture.Endpoint.Payload = ChangedPayload(payload);
                fixture.Endpoint.ETag = "\"new\"";
                fixture.EnglishText = "XX";
                fixture.DutchText = "YY";
                clock.Advance(TimeSpan.FromMinutes(5));
                await fixture.AssertBoth(token);
                foreach (var track in fixture.Tracks)
                {
                    var path = await fixture.Encoder.GetSubtitleFilePath(track, fixture.Source, token);
                    var oldPath = oldPaths.Single(previous => Path.GetFileName(previous) == Path.GetFileName(path));
                    var text = await File.ReadAllTextAsync(path, token);
                    Check(path != oldPath && text != await File.ReadAllTextAsync(oldPath, token)
                        && !text.Contains(track.Language == "eng" ? "EN cue " : "NL cue ", StringComparison.Ordinal),
                        "Same-URL replacement returned a stale file or stale dialogue.");
                }

                fixture.Endpoint.AssertHeaders(("", ""), ("\"old\"", ""));
                Check(fixture.Source.Path == originalUrl && fixture.Source.Id == originalId
                    && fixture.Endpoint.BytesWritten == 2L * payload.Length && fixture.Endpoint.NotModified == 0
                    && Generations(httpRoot).Length == generations + 1 && before.All(Snapshot(httpRoot).Contains),
                    "Changed content did not publish one new generation while preserving prior files and URL identity.");
                fixture.VerifyProcesses(2);
            });

            await Case("missing validators force full GET after TTL but unchanged digest reuses files", async token =>
            {
                using var fixture = Create("no-validators");
                await fixture.AssertBoth(token);
                var before = GoodSnapshot(httpRoot);
                clock.Advance(TimeSpan.FromMinutes(5));
                await fixture.AssertBoth(token);
                fixture.Endpoint.AssertHeaders(("", ""), ("", ""));
                Check(fixture.Endpoint.BytesWritten == 2L * payload.Length && fixture.Endpoint.NotModified == 0
                    && before.SequenceEqual(Snapshot(httpRoot)), "Unvalidated source skipped a full fetch or rewrote unchanged content.");
                fixture.VerifyProcesses(2);
            });

            await Case("Last-Modified alone supplies If-Modified-Since and accepts a bodyless 304", async token =>
            {
                using var fixture = Create("modified-304");
                var modified = clock.GetUtcNow().AddDays(-1);
                fixture.Endpoint.Modified = modified;
                await fixture.AssertBoth(token);
                var before = GoodSnapshot(httpRoot);
                clock.Advance(TimeSpan.FromMinutes(5));
                await fixture.AssertBoth(token);
                fixture.Endpoint.AssertHeaders(("", ""), ("", modified.ToString("R")));
                Check(fixture.Endpoint.NotModified == 1 && fixture.Endpoint.BytesWritten == payload.Length
                    && before.SequenceEqual(Snapshot(httpRoot)), "Last-Modified revalidation downloaded or republished unchanged content.");
                fixture.VerifyProcesses(1);
            });

            await Case("weak ETag without Last-Modified is never sent as a conditional validator", async token =>
            {
                using var fixture = Create("weak-etag");
                fixture.Endpoint.ETag = "W/\"weak\"";
                await fixture.AssertBoth(token);
                var before = GoodSnapshot(httpRoot);
                clock.Advance(TimeSpan.FromMinutes(5));
                await fixture.AssertBoth(token);
                fixture.Endpoint.AssertHeaders(("", ""), ("", ""));
                Check(fixture.Endpoint.NotModified == 0 && fixture.Endpoint.BytesWritten == 2L * payload.Length
                    && before.SequenceEqual(Snapshot(httpRoot)), "Weak ETag incorrectly avoided a full-body check.");
                fixture.VerifyProcesses(2);
            });

            await Case("unsolicited 304 is rejected and a later retry still requires a full body", async token =>
            {
                using var fixture = Create("unexpected-304", Mode.Status);
                fixture.Endpoint.Status = 304;
                var before = GoodSnapshot(httpRoot);
                await Fails<IOException>(() => fixture.Encoder.ExtractAllExtractableSubtitles(fixture.Source, token));
                fixture.VerifyProcesses(0);
                Check(before.SequenceEqual(Snapshot(httpRoot)) && fixture.Endpoint.BytesWritten == 0, "Unsolicited 304 published a generation.");
                clock.Advance(TimeSpan.FromSeconds(30));
                fixture.Endpoint.Mode = Mode.Full;
                await fixture.AssertBoth(token);
                fixture.Endpoint.AssertHeaders(("", ""), ("", ""));
                Check(fixture.Endpoint.BytesWritten == payload.Length, "Recovery from unsolicited 304 did not download a body.");
                fixture.VerifyProcesses(1);
            });

            await Case("expired revalidation failure preserves good files but never serves them during cooldown", async token =>
            {
                using var fixture = Create("revalidation-failure");
                fixture.Endpoint.ETag = "\"good\"";
                await fixture.AssertBoth(token);
                var before = GoodSnapshot(httpRoot);
                clock.Advance(TimeSpan.FromMinutes(5));
                fixture.Endpoint.Mode = Mode.Status;
                fixture.Endpoint.Status = 500;
                await Fails<IOException>(() => fixture.Encoder.ExtractAllExtractableSubtitles(fixture.Source, token));
                foreach (var track in fixture.Tracks)
                {
                    await Fails<IOException>(() => fixture.Encoder.GetSubtitleFilePath(track, fixture.Source, token));
                    await Fails<IOException>(() => fixture.Encoder.GetSubtitleFileCharacterSet(track, track.Language, fixture.Source, token));
                    await Fails<IOException>(() => fixture.AssertConverted(track, "srt", token));
                }

                Check(fixture.Endpoint.Requests == 2 && before.SequenceEqual(Snapshot(httpRoot)), "Failed revalidation served stale content or changed files.");
                fixture.VerifyProcesses(1);
                fixture.Endpoint.Mode = Mode.Full;
                clock.Advance(TimeSpan.FromSeconds(30));
                await fixture.AssertBoth(token);
                fixture.Endpoint.AssertHeaders(("", ""), ("\"good\"", ""), ("\"good\"", ""));
                Check(fixture.Endpoint.NotModified == 1 && fixture.Endpoint.BytesWritten == payload.Length
                    && before.SequenceEqual(Snapshot(httpRoot)), "Failed revalidation replaced the last successful validator snapshot.");
                fixture.VerifyProcesses(1);
            });

            await Case("Retry-After delta 60 extends URL cooldown beyond 30 seconds without blocking another URL", async token =>
            {
                using var fixture = Create("retry-delta", Mode.Status);
                using var other = Create("retry-delta-other");
                fixture.Endpoint.Status = 500;
                fixture.Endpoint.RetryAfter = "60";
                var before = GoodSnapshot(httpRoot);
                await Fails<IOException>(() => fixture.Encoder.ExtractAllExtractableSubtitles(fixture.Source, token));
                clock.Advance(TimeSpan.FromSeconds(30));
                await Fails<IOException>(() => fixture.Encoder.ExtractAllExtractableSubtitles(fixture.Source, token));
                clock.Advance(TimeSpan.FromSeconds(29));
                await Fails<IOException>(() => fixture.Encoder.ExtractAllExtractableSubtitles(fixture.Source, token));
                Check(fixture.Endpoint.Requests == 1 && before.SequenceEqual(Snapshot(httpRoot)), "Delta cooldown retried before 60 seconds.");
                fixture.VerifyProcesses(0);
                await fixture.Encoder.ExtractAllExtractableSubtitles(other.Source, token);
                Check(other.Endpoint.Requests == 1, "A URL-only 500 cooldown incorrectly blocked another URL on the same origin.");
                fixture.Endpoint.Mode = Mode.Full;
                clock.Advance(TimeSpan.FromSeconds(1));
                await fixture.AssertBoth(token);
                Check(fixture.Endpoint.Requests == 2, "URL did not become retryable at the exact delta boundary.");
                fixture.VerifyProcesses(2);
                other.VerifyProcesses(0);
            });

            await Case("Retry-After HTTP-date follows the injected second-aligned clock", async token =>
            {
                using var fixture = Create("retry-date", Mode.Status);
                fixture.Endpoint.Status = 403;
                fixture.Endpoint.Date = clock.GetUtcNow();
                fixture.Endpoint.RetryAfter = clock.GetUtcNow().AddSeconds(90).ToString("R");
                var before = GoodSnapshot(httpRoot);
                await Fails<IOException>(() => fixture.Encoder.ExtractAllExtractableSubtitles(fixture.Source, token));
                clock.Advance(TimeSpan.FromSeconds(30));
                await Fails<IOException>(() => fixture.Encoder.ExtractAllExtractableSubtitles(fixture.Source, token));
                clock.Advance(TimeSpan.FromSeconds(59));
                await Fails<IOException>(() => fixture.Encoder.ExtractAllExtractableSubtitles(fixture.Source, token));
                Check(fixture.Endpoint.Requests == 1 && before.SequenceEqual(Snapshot(httpRoot)), "HTTP-date cooldown ended early.");
                fixture.VerifyProcesses(0);
                fixture.Endpoint.Mode = Mode.Full;
                clock.Advance(TimeSpan.FromSeconds(1));
                await fixture.AssertBoth(token);
                Check(fixture.Endpoint.Requests == 2, "HTTP-date cooldown did not expire at its absolute boundary.");
                fixture.VerifyProcesses(1);
            });

            await Case("eight concurrent failed callers share one HTTP request and retry once after cooldown expiry", async token =>
            {
                using var fixture = Create("concurrent-failure", Mode.Status);
                fixture.Endpoint.Status = 500;
                var before = GoodSnapshot(httpRoot);
                async Task Burst()
                {
                    var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var requests = Enumerable.Range(0, 8).Select(async request =>
                    {
                        await start.Task;
                        await Fails<IOException>(() => fixture.Encoder.GetSubtitleFilePath(fixture.Tracks[request % 2], fixture.Source, token));
                    }).ToArray();
                    start.SetResult();
                    await Task.WhenAll(requests);
                }

                await Burst();
                Check(fixture.Endpoint.Requests == 1, "Concurrent failed callers each fetched the same URL.");
                clock.Advance(TimeSpan.FromSeconds(29));
                await Burst();
                Check(fixture.Endpoint.Requests == 1, "Concurrent callers bypassed the active cooldown.");
                clock.Advance(TimeSpan.FromSeconds(1));
                await Burst();
                Check(fixture.Endpoint.Requests == 2 && before.SequenceEqual(Snapshot(httpRoot)), "Expiry caused a retry stampede or published failed content.");
                fixture.VerifyProcesses(0);
            });

            foreach (var status in new[] { 429, 503 })
            {
                await Case($"status {status} blocks another URL on the same encoder until origin cooldown expires", async token =>
                {
                    using var fixture = Create("origin-" + status, Mode.Status);
                    using var other = Create("origin-other-" + status);
                    fixture.Endpoint.Status = status;
                    fixture.Endpoint.RetryAfter = status == 429 ? "60" : null;
                    var cooldownSeconds = status == 429 ? 60 : 30;
                    var originalUrl = fixture.Source.Path;
                    var originalId = fixture.Source.Id;
                    var before = GoodSnapshot(httpRoot);
                    await Fails<IOException>(() => fixture.Encoder.ExtractAllExtractableSubtitles(fixture.Source, token));
                    fixture.Source.Path = other.Source.Path;
                    fixture.Source.Id = other.Source.Id;
                    await Fails<IOException>(() => fixture.Encoder.ExtractAllExtractableSubtitles(fixture.Source, token));
                    clock.Advance(TimeSpan.FromSeconds(cooldownSeconds - 1));
                    await Fails<IOException>(() => fixture.Encoder.ExtractAllExtractableSubtitles(fixture.Source, token));
                    Check(fixture.Endpoint.Requests == 1 && other.Endpoint.Requests == 0
                        && before.SequenceEqual(Snapshot(httpRoot)), "Origin cooldown did not stop the second URL before HTTP.");
                    fixture.VerifyProcesses(0);
                    clock.Advance(TimeSpan.FromSeconds(1));
                    await fixture.AssertBoth(token);
                    Check(other.Endpoint.Requests == 1, "Second URL remained blocked after origin cooldown expired.");
                    fixture.Source.Path = originalUrl;
                    fixture.Source.Id = originalId;
                    fixture.Endpoint.Mode = Mode.Full;
                    await fixture.AssertBoth(token);
                    Check(fixture.Endpoint.Requests == 2, "Original URL remained blocked after its cooldown expired.");
                    fixture.VerifyProcesses(2);
                    other.VerifyProcesses(0);
                });
            }

            await Case("missing generation after conditional 304 forces an unconditional GET and restores both files", async token =>
            {
                using var fixture = Create("missing-generation");
                fixture.Endpoint.ETag = "\"restore\"";
                await fixture.AssertBoth(token);
                var paths = await Task.WhenAll(fixture.Tracks.Select(track => fixture.Encoder.GetSubtitleFilePath(track, fixture.Source, token)));
                var hashes = paths.Select(path => SHA256.HashData(File.ReadAllBytes(path))).ToArray();
                var generation = Path.GetDirectoryName(paths[0])!;
                Directory.Delete(generation, true);
                var remaining = GoodSnapshot(httpRoot);
                clock.Advance(TimeSpan.FromMinutes(5));
                await fixture.AssertBoth(token);
                fixture.Endpoint.AssertHeaders(("", ""), ("\"restore\"", ""), ("", ""));
                Check(fixture.Endpoint.NotModified == 1 && fixture.Endpoint.BytesWritten == 2L * payload.Length
                    && paths.Select((path, position) => File.Exists(path) && SHA256.HashData(File.ReadAllBytes(path)).SequenceEqual(hashes[position])).All(equal => equal)
                    && remaining.All(Snapshot(httpRoot).Contains), "Missing-generation recovery failed to restore identical tracks or damaged other generations.");
                fixture.VerifyProcesses(2);
            });

            await Case("three same-origin relative redirects succeed within the four-request ceiling", async token =>
            {
                using var fixture = Create("relative-redirect", Mode.Redirect);
                using var middle = Create("relative-middle", Mode.Redirect);
                using var last = Create("relative-last", Mode.Redirect);
                using var target = Create("relative-target");
                fixture.Endpoint.RedirectTo = "relative-middle.mkv?api_key=" + SyntheticKey;
                middle.Endpoint.RedirectTo = "./relative-last.mkv?api_key=" + SyntheticKey;
                last.Endpoint.RedirectTo = "/relative-target.mkv?api_key=" + SyntheticKey;
                await fixture.AssertBoth(token);
                Check(new[] { fixture.Endpoint, middle.Endpoint, last.Endpoint, target.Endpoint }.All(endpoint => endpoint.Requests == 1)
                    && fixture.Endpoint.BytesWritten + middle.Endpoint.BytesWritten + last.Endpoint.BytesWritten == 0
                    && target.Endpoint.BytesWritten == payload.Length, "Relative redirect chain did not download only the final body.");
                fixture.VerifyProcesses(1);
                middle.VerifyProcesses(0);
                last.VerifyProcesses(0);
                target.VerifyProcesses(0);
            });

            await Case("redirect loop stops after four requests and subsequent calls stay in cooldown", async token =>
            {
                using var fixture = Create("redirect-loop", Mode.Redirect);
                fixture.Endpoint.RedirectTo = new Uri(fixture.Source.Path).PathAndQuery;
                var before = GoodSnapshot(httpRoot);
                await Fails<IOException>(() => fixture.Encoder.ExtractAllExtractableSubtitles(fixture.Source, token));
                Check(fixture.Endpoint.Requests == 4, "Redirect hop ceiling did not bound the loop to four requests.");
                await Fails<IOException>(() => fixture.Encoder.ExtractAllExtractableSubtitles(fixture.Source, token));
                Check(fixture.Endpoint.Requests == 4 && before.SequenceEqual(Snapshot(httpRoot)), "Redirect loop bypassed cooldown or published content.");
                fixture.VerifyProcesses(0);
            });

            foreach (var credentialBearing in new[] { true, false })
            {
                await Case(credentialBearing
                    ? "credential-bearing input and redirect target are rejected before the target request"
                    : "non-HTTP input and redirect target are rejected before the target request", async token =>
                {
                    using var fixture = Create(credentialBearing ? "credential-target" : "scheme-target");
                    using var redirect = Create(credentialBearing ? "credential-redirect" : "scheme-redirect", Mode.Redirect);
                    fixture.Source.Path = credentialBearing
                        ? new UriBuilder(fixture.Source.Path) { UserName = "synthetic", Password = SyntheticKey }.Uri.AbsoluteUri
                        : new UriBuilder(fixture.Source.Path) { Scheme = "ftp" }.Uri.AbsoluteUri;
                    redirect.Endpoint.RedirectTo = fixture.Source.Path;
                    var before = GoodSnapshot(httpRoot);
                    await Fails<NotSupportedException>(() => fixture.Encoder.ExtractAllExtractableSubtitles(fixture.Source, token));
                    await Fails<IOException>(() => redirect.Encoder.ExtractAllExtractableSubtitles(redirect.Source, token));
                    Check(fixture.Endpoint.Requests == 0 && redirect.Endpoint.Requests == 1
                        && before.SequenceEqual(Snapshot(httpRoot)), "Rejected URL reached its target or modified the cache.");
                    fixture.VerifyProcesses(0);
                    redirect.VerifyProcesses(0);
                });
            }

            await Case("redirect target change never forwards the previous target's conditional ETag", async token =>
            {
                using var fixture = Create("redirect-revalidate", Mode.Redirect);
                using var previous = Create("redirect-previous");
                using var target = Create("redirect-new");
                fixture.Endpoint.RedirectTo = new Uri(previous.Source.Path).PathAndQuery;
                previous.Endpoint.ETag = "\"previous-target\"";
                await fixture.AssertBoth(token);
                var before = GoodSnapshot(httpRoot);
                previous.Endpoint.Mode = Mode.Redirect;
                previous.Endpoint.RedirectTo = new Uri(target.Source.Path).PathAndQuery;
                target.Endpoint.ETag = "\"new-target\"";
                target.Endpoint.Payload = ChangedPayload(payload);
                fixture.EnglishText = "XX";
                fixture.DutchText = "YY";
                clock.Advance(TimeSpan.FromMinutes(5));
                await fixture.AssertBoth(token);
                fixture.Endpoint.AssertHeaders(("", ""), ("", ""));
                previous.Endpoint.AssertHeaders(("", ""), ("\"previous-target\"", ""));
                target.Endpoint.AssertHeaders(("", ""));
                Check(target.Endpoint.BytesWritten == payload.Length && before.All(Snapshot(httpRoot).Contains), "Target change did not fetch new content or preserved files were altered.");
                var refreshed = GoodSnapshot(httpRoot);
                clock.Advance(TimeSpan.FromMinutes(5));
                await fixture.AssertBoth(token);
                fixture.Endpoint.AssertHeaders(("", ""), ("", ""), ("", ""));
                previous.Endpoint.AssertHeaders(("", ""), ("\"previous-target\"", ""), ("", ""));
                target.Endpoint.AssertHeaders(("", ""), ("\"new-target\"", ""));
                Check(target.Endpoint.NotModified == 1 && target.Endpoint.BytesWritten == payload.Length
                    && refreshed.SequenceEqual(Snapshot(httpRoot)), "New final URL did not become the sole conditional-validation target.");
                fixture.VerifyProcesses(2);
                previous.VerifyProcesses(0);
                target.VerifyProcesses(0);
            });

            var mp4End = await File.ReadAllBytesAsync(Path.Combine(root, "media-formats", "moov-end.mp4"), suiteDeadline.Token);
            var mp4Fast = await File.ReadAllBytesAsync(Path.Combine(root, "media-formats", "faststart.mp4"), suiteDeadline.Token);
            void MovText(Fixture fixture, byte[] body, string container = "mp4")
            {
                fixture.Endpoint.Payload = body;
                fixture.Source.Container = container;
                foreach (var track in fixture.Tracks) { track.Codec = "mov_text"; }
            }

            foreach (var variant in new[]
            {
                (Name: "mp4-end", Extension: ".MP4", Container: "mkv", Body: mp4End),
                (Name: "mp4-faststart", Extension: ".m4v", Container: "mkv", Body: mp4Fast),
                (Name: "mov-path", Extension: ".MOV", Container: "mkv", Body: mp4End),
                (Name: "mov-container", Extension: ".bin", Container: "mov,mp4,m4a,3gp,3g2,mj2", Body: mp4End),
                (Name: "mov-autodetect", Extension: ".bin", Container: "", Body: mp4End)
            })
            {
                await Case($"{variant.Name} uses one private seekable body, complete SRT and warm zero HTTP", async token =>
                {
                    if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException(); }
                    using var fixture = Create(variant.Name, extension: variant.Extension);
                    MovText(fixture, variant.Body, variant.Container);
                    var before = GoodSnapshot(httpRoot);
                    fixture.BeforeProcess = () =>
                    {
                        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException(); }
                        var directory = Path.Combine(httpRoot, "input-staging");
                        var staged = Directory.GetFiles(directory).Single();
                        Check(File.GetUnixFileMode(directory) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute)
                            && File.GetUnixFileMode(staged) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "Seekable input permissions are not private.");
                        Check(File.ReadAllBytes(staged).SequenceEqual(variant.Body), "FFmpeg started before one complete unchanged input was staged.");
                    };
                    await fixture.Encoder.ExtractAllExtractableSubtitles(fixture.Source, token);
                    AssertNoMediaStaging(httpRoot);
                    foreach (var track in fixture.Tracks)
                    {
                        var path = await fixture.Encoder.GetSubtitleFilePath(track, fixture.Source, token);
                        Check(Path.GetExtension(path) == ".srt", "mov_text cache is not SRT.");
                        fixture.AssertCues(await File.ReadAllTextAsync(path, token), "srt", track.Language);
                        await using var raw = await fixture.Encoder.GetSubtitles(fixture.Item, fixture.Source.Id, track.Index, "srt", 0, 0, true, token);
                        using var delivered = new MemoryStream();
                        await raw.CopyToAsync(delivered, token);
                        var expected = await File.ReadAllBytesAsync(path, token);
                        Check(delivered.ToArray().SequenceEqual(expected), "Same-format mov_text SRT bytes changed.");
                    }
                    var warm = Snapshot(httpRoot);
                    await fixture.Encoder.ExtractAllExtractableSubtitles(fixture.Source, token);
                    foreach (var track in fixture.Tracks)
                    {
                        await fixture.AssertConverted(track, "srt", token);
                        await fixture.AssertConverted(track, "vtt", token);
                        Check(await fixture.Encoder.GetSubtitleFileCharacterSet(track, track.Language, fixture.Source, token) == "UTF-8", "Wrong SRT charset.");
                    }
                    Check(fixture.Endpoint.Requests == 1 && fixture.Endpoint.BytesWritten == variant.Body.Length
                        && fixture.Endpoint.DeclaredLength == variant.Body.Length && warm.SequenceEqual(Snapshot(httpRoot))
                        && before.All(Snapshot(httpRoot).Contains), "Seekable extraction downloaded twice, changed a good cache or failed warm reuse.");
                    fixture.VerifyProcesses(1);
                });
            }

            await Case("explicit Matroska with unknown URL suffix still pipes without a media spool", async token =>
            {
                using var fixture = Create("matroska-bin", extension: ".bin");
                fixture.BeforeProcess = () => AssertNoMediaStaging(httpRoot);
                await fixture.AssertBoth(token);
                Check(fixture.Endpoint.Requests == 1 && fixture.Endpoint.BytesWritten == payload.Length, "Matroska did not use one complete body.");
                fixture.VerifyProcesses(1);
            });

            await Case("MP4 disconnect deletes staged input before FFmpeg and retains cooldown", async token =>
            {
                using var fixture = Create("mp4-disconnect", Mode.Disconnect, extension: ".mp4");
                MovText(fixture, mp4End);
                var before = GoodSnapshot(httpRoot);
                await Fails<IOException>(() => fixture.Encoder.ExtractAllExtractableSubtitles(fixture.Source, token));
                await Fails<IOException>(() => fixture.Encoder.ExtractAllExtractableSubtitles(fixture.Source, token));
                Check(!token.IsCancellationRequested && fixture.Endpoint.Requests == 1 && before.SequenceEqual(Snapshot(httpRoot)),
                    "Disconnected MP4 retried, published output or changed a good cache.");
                fixture.VerifyProcesses(0);
            });

            foreach (var timeout in new[] { false, true })
            {
                await Case(timeout ? "MP4 transfer timeout deletes staged input" : "MP4 in-flight cancellation deletes staged input", async token =>
                {
                    using var fixture = Create(timeout ? "mp4-timeout" : "mp4-cancel", Mode.Stall,
                        new Sandbox(httpRoot) { HttpTestOrigin = origin, Clock = clock, Timeout = TimeSpan.FromSeconds(timeout ? 1 : 30) }, ".mp4");
                    MovText(fixture, mp4End);
                    var before = GoodSnapshot(httpRoot);
                    using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                    var extraction = fixture.Encoder.ExtractAllExtractableSubtitles(fixture.Source, cancellation.Token);
                    try
                    {
                        await fixture.Endpoint.Started.Task.WaitAsync(token);
                        var inputRoot = Path.Combine(httpRoot, "input-staging");
                        while (!Directory.Exists(inputRoot) || Directory.GetFiles(inputRoot).Length != 1)
                        {
                            Check(!extraction.IsCompleted, "MP4 extraction ended before the staging check.");
                            await Task.Delay(10, token);
                        }
                        Check(before.SequenceEqual(Snapshot(httpRoot)), "MP4 transfer published before EOF.");
                        if (!timeout) { cancellation.Cancel(); }
                        await Fails<OperationCanceledException>(() => extraction);
                        Check(!token.IsCancellationRequested && fixture.Endpoint.Requests == 1 && before.SequenceEqual(Snapshot(httpRoot)),
                            "Interrupted MP4 exceeded case deadline, retried or changed good output.");
                        fixture.VerifyProcesses(0);
                    }
                    finally
                    {
                        cancellation.Cancel();
                        try { await extraction.WaitAsync(TimeSpan.FromSeconds(2)); }
                        catch (Exception) when (extraction.IsCompleted) { }
                    }
                });
            }

            await Case("MP4 rejected headers create no input file or process", async token =>
            {
                foreach (var length in new[] { 0L, sandbox.TransferLimitBytes + 1 })
                {
                    using var fixture = Create("mp4-length-" + length, extension: ".mp4");
                    MovText(fixture, mp4End);
                    fixture.Endpoint.LengthOverride = length;
                    var before = GoodSnapshot(httpRoot);
                    await Fails<IOException>(() => fixture.Encoder.ExtractAllExtractableSubtitles(fixture.Source, token));
                    Check(fixture.Endpoint.Requests == 1 && fixture.Endpoint.BytesWritten == 0 && before.SequenceEqual(Snapshot(httpRoot)), "MP4 header rejection changed state.");
                    fixture.VerifyProcesses(0);
                    AssertNoMediaStaging(httpRoot);
                }
            });

            await Case("pre-cancelled MP4 extraction starts no HTTP, spool or process", async token =>
            {
                using var fixture = Create("mp4-pre-cancel", extension: ".mp4");
                MovText(fixture, mp4End);
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                cancellation.Cancel();
                await Fails<OperationCanceledException>(() => fixture.Encoder.ExtractAllExtractableSubtitles(fixture.Source, cancellation.Token));
                Check(fixture.Endpoint.Requests == 0, "Pre-cancelled MP4 made an HTTP request.");
                fixture.VerifyProcesses(0);
            });

            foreach (var extension in new[] { ".MP4", ".AVI", ".bin" })
            {
                await Case($"HLS disguised as {extension} fails explicit demuxer or format whitelist and deletes input", async token =>
                {
                    using var fixture = Create("seekable-hls-" + extension[1..], extension: extension);
                    fixture.Source.Container = string.Empty;
                    fixture.Endpoint.Payload = Encoding.UTF8.GetBytes(
                        $"#EXTM3U\n#EXT-X-TARGETDURATION:12\n#EXTINF:12.0,\n{complete.Source.Path}\n#EXT-X-ENDLIST\n");
                    var before = GoodSnapshot(httpRoot);
                    var targetRequests = complete.Endpoint.Requests;
                    await Fails<IOException>(() => fixture.Encoder.ExtractAllExtractableSubtitles(fixture.Source, token));
                    Check(!token.IsCancellationRequested && fixture.Endpoint.Requests == 1 && fixture.Endpoint.BytesWritten == fixture.Endpoint.Payload.Length
                        && complete.Endpoint.Requests == targetRequests && before.SequenceEqual(Snapshot(httpRoot)), "Disguised HLS followed media references or published output.");
                    fixture.VerifyProcesses(1);
                });
            }

            await Case("MP4 input deletion failure blocks publication and releases the same encoder gate", async token =>
            {
                using var fixture = Create("mp4-delete-failure", extension: ".mp4");
                MovText(fixture, mp4End);
                var before = GoodSnapshot(httpRoot);
                var staged = string.Empty;
                fixture.BeforeProcess = () => staged = Directory.GetFiles(Path.Combine(httpRoot, "input-staging")).Single();
                fixture.EncoderPathOverride = WriteShim("input-delete-failure",
                    $"previous=''\ninput=''\nfor argument in \"$@\"; do\nif [ \"$previous\" = '-i' ]; then input=\"$argument\"; fi\nprevious=\"$argument\"\ndone\n'{ffmpeg}' \"$@\"\nrm -- \"$input\"\nmkdir -- \"$input\"\n");
                try
                {
                    await Fails<IOException>(() => fixture.Encoder.ExtractAllExtractableSubtitles(fixture.Source, token));
                    Check(staged.Length > 0 && Directory.Exists(staged) && before.SequenceEqual(Snapshot(httpRoot)), "Input deletion failure published a generation.");
                    await Fails<IOException>(() => fixture.Encoder.ExtractAllExtractableSubtitles(fixture.Source, token));
                    Check(fixture.Endpoint.Requests == 1, "Input deletion failure bypassed cooldown.");
                    fixture.VerifyProcesses(1);
                }
                finally
                {
                    if (Directory.Exists(staged)) { Directory.Delete(staged); }
                    fixture.BeforeProcess = null;
                    fixture.EncoderPathOverride = null;
                }
                clock.Advance(TimeSpan.FromSeconds(30));
                await fixture.Encoder.ExtractAllExtractableSubtitles(fixture.Source, token);
                foreach (var track in fixture.Tracks) { await fixture.AssertConverted(track, "srt", token); }
                Check(fixture.Endpoint.Requests == 2 && before.All(Snapshot(httpRoot).Contains), "Same encoder could not retry after input cleanup was repaired.");
                fixture.VerifyProcesses(2);
            });
        }
        finally
        {
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await app.StopAsync(shutdown.Token);
        }
    }

    private static async Task Serve(HttpContext context, ConcurrentDictionary<string, Endpoint> endpoints, byte[] payload, CancellationToken stopping)
    {
        if (!endpoints.TryGetValue(context.Request.Path.Value ?? string.Empty, out var endpoint))
        {
            context.Response.StatusCode = 404;
            return;
        }

        Interlocked.Increment(ref endpoint.Requests);
        endpoint.Headers.Enqueue((context.Request.Headers.IfNoneMatch.ToString(), context.Request.Headers.IfModifiedSince.ToString()));
        payload = endpoint.Payload ?? payload;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopping);
        try
        {
            var mode = endpoint.Mode;
            if (endpoint.Date.HasValue)
            {
                context.Response.Headers.Date = endpoint.Date.Value.ToString("R");
            }

            if (mode == Mode.Status)
            {
                context.Response.StatusCode = endpoint.Status;
                endpoint.RetryAfter ??= endpoint.Status == 429 ? "60" : null;
                if (endpoint.RetryAfter is not null)
                {
                    context.Response.Headers.RetryAfter = endpoint.RetryAfter;
                }

                context.Response.ContentLength = 0;
                return;
            }

            if (mode == Mode.Redirect)
            {
                context.Response.StatusCode = 302;
                context.Response.Headers.Location = endpoint.RedirectTo;
                context.Response.ContentLength = 0;
                return;
            }

            if (endpoint.ETag is not null)
            {
                context.Response.Headers.ETag = endpoint.ETag;
            }

            if (endpoint.Modified.HasValue)
            {
                context.Response.Headers.LastModified = endpoint.Modified.Value.ToString("R");
            }

            if ((endpoint.ETag is not null && context.Request.Headers.IfNoneMatch == endpoint.ETag)
                || (endpoint.Modified.HasValue && context.Request.Headers.IfNoneMatch.Count == 0
                    && context.Request.Headers.IfModifiedSince == endpoint.Modified.Value.ToString("R")))
            {
                Interlocked.Increment(ref endpoint.NotModified);
                context.Response.StatusCode = 304;
                return;
            }

            long from = 0;
            long to = payload.Length - 1;
            if (mode == Mode.Range)
            {
                context.Response.Headers.AcceptRanges = "bytes";
                if (context.Request.Headers.ContainsKey("Range"))
                {
                    if (!RangeHeaderValue.TryParse(context.Request.Headers.Range.ToString(), out var range)
                        || range.Unit != "bytes" || range.Ranges.Count != 1)
                    {
                        context.Response.StatusCode = 416;
                        context.Response.Headers.ContentRange = $"bytes */{payload.Length}";
                        context.Response.ContentLength = 0;
                        return;
                    }

                    var requested = range.Ranges.Single();
                    from = requested.From ?? Math.Max(0, payload.Length - requested.To!.Value);
                    to = requested.From.HasValue ? Math.Min(requested.To ?? to, to) : to;
                    if (from >= payload.Length || to < from)
                    {
                        context.Response.StatusCode = 416;
                        context.Response.Headers.ContentRange = $"bytes */{payload.Length}";
                        context.Response.ContentLength = 0;
                        return;
                    }

                    context.Response.StatusCode = 206;
                    context.Response.Headers.ContentRange = $"bytes {from}-{to}/{payload.Length}";
                    endpoint.Ranges.Enqueue((from, to));
                }
            }

            var length = checked((int)(to - from + 1));
            context.Response.ContentType = "video/x-matroska";
            if (endpoint.LengthOverride is { } declared)
            {
                context.Response.ContentLength = declared;
                Interlocked.Exchange(ref endpoint.DeclaredLength, declared);
                await context.Response.StartAsync(cancellation.Token);
                await context.Response.Body.FlushAsync(cancellation.Token);
                if (declared > 0)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);
                }

                return;
            }

            context.Response.ContentLength = length;
            Interlocked.Exchange(ref endpoint.DeclaredLength, length);
            var sent = mode is Mode.Disconnect or Mode.Stall or Mode.PauseAfterHalf ? payload.Length / 2 : length;
            await context.Response.Body.WriteAsync(payload.AsMemory((int)from, sent), cancellation.Token);
            await context.Response.Body.FlushAsync(cancellation.Token);
            Interlocked.Add(ref endpoint.BytesWritten, sent);
            endpoint.Started.TrySetResult();
            if (mode == Mode.Disconnect)
            {
                context.Abort();
            }
            else if (mode == Mode.Stall)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);
            }
            else if (mode == Mode.PauseAfterHalf)
            {
                await endpoint.Resume.Task.WaitAsync(cancellation.Token);
                await context.Response.Body.WriteAsync(payload.AsMemory(sent), cancellation.Token);
                await context.Response.Body.FlushAsync(cancellation.Token);
                Interlocked.Add(ref endpoint.BytesWritten, payload.Length - sent);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (IOException) when (context.RequestAborted.IsCancellationRequested) { }
        finally
        {
            endpoint.Finished.TrySetResult();
        }
    }

    private sealed class ServerLifetime(WebApplication app) : IAsyncDisposable
    {
        public WebApplication App { get; } = app;

        public async ValueTask DisposeAsync() => await App.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _ticks = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()).Ticks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public void Advance(TimeSpan interval) => Interlocked.Add(ref _ticks, interval.Ticks);
    }

    private enum Mode { Full, Disconnect, Status, Stall, Range, Redirect, PauseAfterHalf }

    private sealed class Endpoint(Mode mode)
    {
        private int _mode = (int)mode;
        public Mode Mode { get => (Mode)Volatile.Read(ref _mode); set => Volatile.Write(ref _mode, (int)value); }
        public int Requests;
        public long BytesWritten;
        public long DeclaredLength;
        public int NotModified;
        public int Status { get; set; }
        public string? RetryAfter { get; set; }
        public string? RedirectTo { get; set; }
        public string? ETag { get; set; }
        public DateTimeOffset? Modified { get; set; }
        public DateTimeOffset? Date { get; set; }
        public byte[]? Payload { get; set; }
        public long? LengthOverride { get; set; }
        public ConcurrentQueue<(string ETag, string Modified)> Headers { get; } = new();
        public ConcurrentQueue<(long From, long To)> Ranges { get; } = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void AssertHeaders(params (string ETag, string Modified)[] expected) =>
            Check(Requests == expected.Length && Headers.SequenceEqual(expected), "Unexpected request count or conditional validator headers.");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly Mock<IMediaEncoder> _mediaEncoder = new(MockBehavior.Strict);
        private readonly ISubtitleParser _parser = new SubtitleEditParser(NullLogger<SubtitleEditParser>.Instance);
        public Mock<IMediaSourceManager> Sources { get; } = new(MockBehavior.Strict);
        public Endpoint Endpoint { get; }
        public MediaSourceInfo Source { get; }
        public Video Item { get; }
        public GuardedEncoder Encoder { get; }
        public string EnglishText { get; set; } = "EN";
        public string DutchText { get; set; } = "NL";
        public Action? BeforeProcess { get; set; }
        public string? EncoderPathOverride { get; set; }
        public TaskCompletionSource ProcessRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public MediaStream[] Tracks { get; } =
        [
            new() { Index = 2, Type = MediaStreamType.Subtitle, Codec = "ass", Language = "eng", Path = string.Empty },
            new() { Index = 3, Type = MediaStreamType.Subtitle, Codec = "ass", Language = "nld", Path = string.Empty }
        ];

        public Fixture(Sandbox sandbox, string ffmpeg, string url, Endpoint endpoint)
        {
            Endpoint = endpoint;
            Source = new MediaSourceInfo
            {
                Id = "synthetic-http-" + new Uri(url).AbsolutePath,
                Path = url,
                Protocol = MediaProtocol.Http,
                Container = "mkv",
                RunTimeTicks = 12 * TimeSpan.TicksPerSecond,
                MediaStreams = new List<MediaStream>
                {
                    new() { Index = 0, Type = MediaStreamType.Video, Codec = "ffv1" },
                    new() { Index = 1, Type = MediaStreamType.Audio, Codec = "pcm_s16le" }
                }.Concat(Tracks).ToList()
            };
            Item = new Video { Id = Guid.NewGuid(), Path = url, Name = "Synthetic HTTP fixture" };
            _mediaEncoder.SetupGet(encoder => encoder.EncoderPath).Returns(() =>
            {
                BeforeProcess?.Invoke();
                ProcessRequested.TrySetResult();
                return EncoderPathOverride ?? ffmpeg;
            });
            Sources.Setup(manager => manager.GetPlaybackMediaSources(Item, null!, false, false, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<MediaSourceInfo> { Source });
            Encoder = new GuardedEncoder(sandbox, _mediaEncoder.Object, Sources.Object, _parser);
        }

        public async Task AssertBoth(CancellationToken token)
        {
            var paths = new List<string>();
            foreach (var track in Tracks)
            {
                var path = await Encoder.GetSubtitleFilePath(track, Source, token);
                Check(File.Exists(path), "A published HTTP track is missing.");
                AssertCues(await File.ReadAllTextAsync(path, token), "ass", track.Language);
                paths.Add(path);
            }

            Check(paths.Distinct().Count() == 2 && Path.GetDirectoryName(paths[0]) == Path.GetDirectoryName(paths[1])
                && Directory.GetFiles(Path.GetDirectoryName(paths[0])!).Length == 2, "Both tracks were not published as one complete generation.");
        }

        public async Task AssertConverted(MediaStream track, string format, CancellationToken token)
        {
            await using var stream = await Encoder.GetSubtitles(Item, Source.Id, track.Index, format, 0, 0, true, token);
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
            var text = await reader.ReadToEndAsync(token);
            Check(format != "vtt" || text.StartsWith("WEBVTT", StringComparison.Ordinal), "WebVTT header is missing.");
            AssertCues(text, format, track.Language);
        }

        public void AssertCues(string text, string format, string language)
        {
            using var stream = new MemoryStream(new UTF8Encoding(false, true).GetBytes(text));
            var subtitle = _parser.Parse(stream, format);
            var wholeEpisode = Source.RunTimeTicks == TimeSpan.FromMinutes(45).Ticks;
            var count = wholeEpisode ? 45 : 12;
            Check(subtitle.Paragraphs.Count == count, $"HTTP extraction lost cues before full EOF: expected {count}.");
            for (var position = 0; position < count; position++)
            {
                var cue = subtitle.Paragraphs[position];
                Check(cue.Text.Contains($"{(language == "eng" ? EnglishText : DutchText)} cue {position + 1:D2}", StringComparison.Ordinal)
                    && cue.Text.Contains("caf\u00e9", StringComparison.Ordinal), "HTTP cue text, track order or UTF-8 changed.");
                var start = wholeEpisode ? position * TimeSpan.TicksPerMinute
                    : position * TimeSpan.TicksPerSecond + TimeSpan.TicksPerSecond / 5;
                var end = wholeEpisode ? position * TimeSpan.TicksPerMinute + 58 * TimeSpan.TicksPerSecond
                    : position * TimeSpan.TicksPerSecond + 4 * TimeSpan.TicksPerSecond / 5;
                Check(cue.StartTime.TimeSpan.Ticks == start && cue.EndTime.TimeSpan.Ticks == end,
                    "HTTP cue timestamps changed or the final cue is missing.");
            }
        }

        public void VerifyProcesses(int count)
        {
            _mediaEncoder.VerifyGet(encoder => encoder.EncoderPath, Times.Exactly(count));
            _mediaEncoder.VerifyNoOtherCalls();
        }

        public void Dispose() => Encoder.Dispose();
    }

    private static async Task<byte[]> EpisodePayload(string root, string ffmpeg)
    {
        var fixture = Directory.CreateDirectory(Path.Combine(root, "episode-fixture")).FullName;
        var english = Path.Combine(fixture, "english.ass");
        var dutch = Path.Combine(fixture, "dutch.ass");
        foreach (var (path, language) in new[] { (english, "EN"), (dutch, "NL") })
        {
            var text = new StringBuilder("""
                [Script Info]
                ScriptType: v4.00+
                PlayResX: 64
                PlayResY: 36
                [V4+ Styles]
                Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
                Style: Default,DejaVu Sans,8,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,1,0,2,1,1,1,1
                [Events]
                Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
                """);
            text.Append('\n');
            for (var minute = 0; minute < 45; minute++)
            {
                text.AppendLine($"Dialogue: 0,0:{minute:D2}:00.00,0:{minute:D2}:58.00,Default,,0,0,0,,{language} cue {minute + 1:D2} caf\u00e9");
            }

            await File.WriteAllTextAsync(path, text.ToString(), new UTF8Encoding(false));
        }

        var media = Path.Combine(fixture, "whole-episode.mkv");
        await Program.RunFfmpeg(ffmpeg, Path.Combine(fixture, "fixture.stderr.txt"),
            "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-protocol_whitelist", "file,pipe",
            "-f", "lavfi", "-i", "color=c=black:s=64x36:r=1:d=2700",
            "-f", "lavfi", "-i", "anullsrc=r=8000:cl=mono", "-i", english, "-i", dutch,
            "-map", "0:v:0", "-map", "1:a:0", "-map", "2:0", "-map", "3:0",
            "-t", "2700", "-c:v", "ffv1", "-threads", "1", "-c:a", "pcm_s16le", "-c:s", "copy",
            "-metadata:s:s:0", "language=eng", "-metadata:s:s:1", "language=nld",
            "-cluster_time_limit", "1000", "-cluster_size_limit", "0", media);
        return await File.ReadAllBytesAsync(media);
    }

    private static void AssertNoMediaStaging(string root)
    {
        var staging = Path.Combine(root, "http-staging");
        Check(!Directory.Exists(staging) || !Directory.EnumerateFileSystemEntries(staging).Any(),
            "Streaming HTTP created a media staging file.");
        var inputStaging = Path.Combine(root, "input-staging");
        Check(!Directory.Exists(inputStaging) || !Directory.EnumerateFileSystemEntries(inputStaging).Any(),
            "Seekable HTTP input survived a completed operation.");
        var cache = Path.Combine(root, "guard-cache");
        Check(!Directory.Exists(cache) || Directory.EnumerateFiles(cache, "*", SearchOption.AllDirectories)
            .All(path => Path.GetExtension(path) is ".ass" or ".srt"), "Streaming cache contains something other than subtitle output.");
        var fixture = Path.Combine(root, "episode-fixture") + Path.DirectorySeparatorChar;
        Check(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).All(path =>
            path == Path.Combine(root, ".subtitle-guard-sandbox") || path.StartsWith(fixture, StringComparison.Ordinal)
            || (path.StartsWith(cache + Path.DirectorySeparatorChar, StringComparison.Ordinal) && Path.GetExtension(path) is ".ass" or ".srt")),
            "HTTP extraction left a persistent non-subtitle file outside the server fixture directory.");
    }

    private static string[] Generations(string root)
    {
        var cache = Path.Combine(root, "guard-cache");
        var directories = Directory.Exists(cache) ? Directory.GetDirectories(cache) : [];
        Check(directories.All(path => !Path.GetFileName(path).StartsWith(".pending-", StringComparison.Ordinal)), "HTTP staging survived a completed operation.");
        return directories.Order(StringComparer.Ordinal).ToArray();
    }

    private static string[] Snapshot(string root) => Generations(root).SelectMany(directory =>
        new[] { directory }.Concat(Directory.GetFiles(directory).Order(StringComparer.Ordinal).Select(path =>
            $"{path}|{File.GetLastWriteTimeUtc(path).Ticks}|{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}"))).ToArray();

    private static string[] GoodSnapshot(string root)
    {
        var snapshot = Snapshot(root);
        Check(snapshot.Length >= 3, "Fault assertions require an existing good two-track generation.");
        return snapshot;
    }

    private static byte[] ChangedPayload(byte[] payload)
    {
        var text = Encoding.Latin1.GetString(payload);
        Check(text.Split("EN cue ", StringSplitOptions.None).Length == 13
            && text.Split("NL cue ", StringSplitOptions.None).Length == 13, "Fixture must contain 12 uncompressed ASS cues per language.");
        var changed = Encoding.Latin1.GetBytes(text.Replace("EN cue ", "XX cue ", StringComparison.Ordinal)
            .Replace("NL cue ", "YY cue ", StringComparison.Ordinal));
        Check(changed.Length == payload.Length && !changed.SequenceEqual(payload), "In-place fixture replacement changed container size or left dialogue unchanged.");
        return changed;
    }

    private static async Task Fails<TException>(Func<Task> action) where TException : Exception
    {
        try { await action(); }
        catch (TException exception)
        {
            var detail = exception.ToString();
            Check(!detail.Contains(SyntheticKey, StringComparison.Ordinal)
                && !detail.Contains("http://", StringComparison.OrdinalIgnoreCase)
                && !detail.Contains("https://", StringComparison.OrdinalIgnoreCase), "Failure exposed a URI or the synthetic API key.");
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name} for synthetic HTTP failure.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}