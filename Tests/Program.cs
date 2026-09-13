using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Net.NetworkInformation;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Api.Controllers;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.MediaEncoding.Subtitles;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nikse.SubtitleEdit.Core.Common;
using SubtitleGuard;

internal static class Program
{
    private const long StartTicks = 3 * TimeSpan.TicksPerSecond;
    private const long EndTicks = 59 * TimeSpan.TicksPerSecond / 10;
    private static int _passed;
    private static int _failed;

    public static async Task<int> Main(string[] arguments)
    {
        if (arguments.Length > 0 && arguments[0] == "--bitmap-check")
        {
            if (arguments.Length != 4)
            {
                Console.Error.WriteLine("Usage: dotnet Integration.dll --bitmap-check /tmp/subtitle-guard-bitmap-<new-unique-name> /absolute/path/to/ffmpeg /tmp/subtitle-guard-bitmap-<new-unique-name>/sample.sup");
                return 2;
            }

            return await RunBitmap(arguments[1], arguments[2], arguments[3]);
        }

        if (arguments.Length > 0 && arguments[0] == "--policy-network-refusal")
        {
            if (arguments.Length != 1)
            {
                Console.Error.WriteLine("Usage: dotnet Integration.dll --policy-network-refusal (Linux, outside unshare; creates its own temporary root)");
                return 2;
            }

            try
            {
                return await Task.Run(PolicyNetworkRefusal).WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException)
            {
                Console.Error.WriteLine("Policy network-refusal check exceeded ten seconds.");
                return 2;
            }
        }

        if (arguments.Length != 2)
        {
            Console.Error.WriteLine("Usage: dotnet Integration.dll /tmp/subtitle-guard-<new-unique-name> /absolute/path/to/ffmpeg");
            return 2;
        }

        var previousRoot = Environment.GetEnvironmentVariable("SUBTITLE_GUARD_SANDBOX");
        var previousOrigin = Environment.GetEnvironmentVariable("SUBTITLE_GUARD_TEST_ORIGIN");
        var previousPolicy = Environment.GetEnvironmentVariable("SUBTITLE_GUARD_SOURCE_POLICY");
        var previousNativePilot = Environment.GetEnvironmentVariable("SUBTITLE_GUARD_NATIVE_PILOT");
        var previousProductionPolicy = Environment.GetEnvironmentVariable("SUBTITLE_GUARD_PRODUCTION_POLICY");
        try
        {
            Environment.SetEnvironmentVariable("SUBTITLE_GUARD_SANDBOX", null);
            Environment.SetEnvironmentVariable("SUBTITLE_GUARD_TEST_ORIGIN", null);
            Environment.SetEnvironmentVariable("SUBTITLE_GUARD_SOURCE_POLICY", null);
            Environment.SetEnvironmentVariable("SUBTITLE_GUARD_NATIVE_PILOT", null);
            Environment.SetEnvironmentVariable("SUBTITLE_GUARD_PRODUCTION_POLICY", null);
            if (!OperatingSystem.IsLinux())
            {
                throw new PlatformNotSupportedException("Run media integration checks only in the isolated Linux console context.");
            }

            var root = arguments[0];
            var ffmpeg = arguments[1];
            Check(Path.IsPathFullyQualified(root) && root == Path.GetFullPath(root), "Sandbox must be a canonical absolute path.");
            Check(Path.GetDirectoryName(root) == "/tmp" && Path.GetFileName(root).StartsWith("subtitle-guard-", StringComparison.Ordinal),
                "Sandbox must be a new direct /tmp/subtitle-guard-* child.");
            Check(!Path.Exists(root), "Refusing to reuse an existing sandbox.");
            Check(new DirectoryInfo("/tmp").LinkTarget is null, "Refusing a symlinked temporary parent.");
            Check(Path.IsPathFullyQualified(ffmpeg) && File.Exists(ffmpeg), "FFmpeg must be an existing absolute local executable.");
            Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using (File.Open(Path.Combine(root, ".subtitle-guard-sandbox"), FileMode.CreateNew, FileAccess.Write)) { }
            Console.WriteLine($"Synthetic fixtures and evidence: {root}");

            await SourcePolicyTests(root);
            var production = await ProductionTests.Run(root, ffmpeg);
            _passed += production.Passed;
            _failed += production.Failed;

            await Test("Native failure messages retain safe stage/status without URL leakage", () =>
            {
                foreach (var stage in new[] { "request", "response", "redirect", "transfer" })
                {
                    var failure = new RemoteSourceFailure(stage, 302);
                    Check(failure.Message.Contains($"Stage={stage}; HTTP=302.", StringComparison.Ordinal),
                        "Native failure message lost stage/status.");
                    Check(failure.Stage == stage && failure.StatusCode == 302 && failure.InnerException is null,
                        "Failure properties changed or retained an inner exception.");
                }
                foreach (var status in new int?[] { null, 99, 600 })
                {
                    var failure = new RemoteSourceFailure("https://example.invalid/private-token", status);
                    Check(failure.Message.EndsWith("Stage=unknown; HTTP=unavailable.", StringComparison.Ordinal)
                        && !failure.ToString().Contains("private-token", StringComparison.Ordinal),
                        "Native failure message leaked untrusted data.");
                }
                return Task.CompletedTask;
            });

            var original = new Mock<ISubtitleEncoder>(MockBehavior.Strict);
            var host = new Mock<IServerApplicationHost>(MockBehavior.Strict);
            var mediaEncoder = new Mock<IMediaEncoder>(MockBehavior.Strict);
            mediaEncoder.SetupGet(encoder => encoder.EncoderPath).Returns(ffmpeg);
            var sources = new Mock<IMediaSourceManager>(MockBehavior.Strict);
            ISubtitleParser parser = new SubtitleEditParser(NullLogger<SubtitleEditParser>.Instance);
            var services = new ServiceCollection();
            services.AddSingleton(original.Object);
            services.AddSingleton(mediaEncoder.Object);
            services.AddSingleton(sources.Object);
            services.AddSingleton(parser);
            var registrator = new Registrator();

            await Test("Absent opt-in preserves original encoder", async () =>
            {
                var descriptors = services.ToArray();
                Environment.SetEnvironmentVariable("SUBTITLE_GUARD_SANDBOX", null);
                await Throws<InvalidOperationException>(() =>
                {
                    registrator.RegisterServices(services, host.Object);
                    return Task.CompletedTask;
                });
                Check(descriptors.SequenceEqual(services), "Failed registration mutated service descriptors.");
                using var rejectedProvider = services.BuildServiceProvider();
                Check(ReferenceEquals(original.Object, rejectedProvider.GetRequiredService<ISubtitleEncoder>()), "Original encoder was removed.");
            });

            await Test("Missing marker preserves original encoder", async () =>
            {
                var unmarked = Directory.CreateDirectory(Path.Combine(root, "unmarked")).FullName;
                var descriptors = services.ToArray();
                Environment.SetEnvironmentVariable("SUBTITLE_GUARD_SANDBOX", unmarked);
                await Throws<InvalidOperationException>(() =>
                {
                    registrator.RegisterServices(services, host.Object);
                    return Task.CompletedTask;
                });
                Check(descriptors.SequenceEqual(services), "Missing-marker rejection mutated services.");
            });

            Environment.SetEnvironmentVariable("SUBTITLE_GUARD_SANDBOX", root);
            registrator.RegisterServices(services, host.Object);
            using var provider = services.BuildServiceProvider();
            var encoder = provider.GetRequiredService<ISubtitleEncoder>();
            await Test("Opt-in resolves only the guarded singleton", () =>
            {
                Check(encoder is GuardedEncoder, "Registered encoder is not GuardedEncoder.");
                Check(ReferenceEquals(encoder, provider.GetRequiredService<ISubtitleEncoder>()), "Encoder is not singleton.");
                Check(provider.GetServices<ISubtitleEncoder>().Count() == 1, "Original registration remains selectable.");
                original.VerifyNoOtherCalls();
                host.VerifyNoOtherCalls();
                return Task.CompletedTask;
            });

            var moviePath = Path.Combine(root, "bilingual.mkv");
            var englishPath = Path.Combine(root, "english.ass");
            var dutchPath = Path.Combine(root, "dutch.ass");
            await File.WriteAllTextAsync(englishPath, Ass("EN"), new UTF8Encoding(false));
            await File.WriteAllTextAsync(dutchPath, Ass("NL"), new UTF8Encoding(false));
            await RunFfmpeg(ffmpeg, Path.Combine(root, "fixture.stderr.txt"),
                "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "color=c=black:s=160x90:r=10:d=12",
                "-f", "lavfi", "-i", "anullsrc=r=48000:cl=mono",
                "-i", englishPath, "-i", dutchPath,
                "-map", "0:v:0", "-map", "1:a:0", "-map", "2:0", "-map", "3:0",
                "-t", "12", "-c:v", "ffv1", "-threads", "1", "-c:a", "pcm_s16le", "-c:s", "copy",
                "-metadata:s:s:0", "language=eng", "-metadata:s:s:1", "language=nld",
                "-cluster_time_limit", "1000", "-cluster_size_limit", "0", moviePath);

            var english = Track(2, "eng");
            var dutch = Track(3, "nld");
            var source = Source(moviePath, "synthetic-bilingual", english, dutch);
            var item = new Video { Id = Guid.NewGuid(), Path = moviePath, Name = "Synthetic bilingual fixture" };
            sources.Setup(manager => manager.GetPlaybackMediaSources(item, null!, false, false, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<MediaSourceInfo> { source });

            await Test("Eight concurrent cold requests publish both tracks once", async () =>
            {
                var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var requests = Enumerable.Range(0, 8).Select(async request =>
                {
                    await start.Task;
                    var track = request % 2 == 0 ? english : dutch;
                    var result = await ReadText(encoder.GetSubtitles(item, source.Id, track.Index, "ass", 0, 0, true, CancellationToken.None));
                    AssertCues(Parse(parser, result, "ass"), request % 2 == 0 ? "EN" : "NL", false, true);
                    var generation = Generations(root).Single();
                    foreach (var expected in new[] { (Index: 2, Language: "EN"), (Index: 3, Language: "NL") })
                    {
                        var path = Path.Combine(generation, $"{expected.Index}.ass");
                        Check(File.Exists(path), "A request completed before both tracks were visible.");
                        AssertCues(Parse(parser, await File.ReadAllTextAsync(path), "ass"), expected.Language, false, true);
                    }
                }).ToArray();
                start.SetResult();
                await Task.WhenAll(requests);
                Check(Generations(root).Length == 1, "Concurrent requests published multiple generations.");
                mediaEncoder.VerifyGet(mock => mock.EncoderPath, Times.Once());
            });

            await Test("Cache reuse preserves generation, bytes and timestamps", async () =>
            {
                var before = Snapshot(root);
                await encoder.ExtractAllExtractableSubtitles(source, CancellationToken.None);
                foreach (var track in new[] { english, dutch })
                {
                    _ = await encoder.GetSubtitleFilePath(track, source, CancellationToken.None);
                }

                Check(before.SequenceEqual(Snapshot(root)), "Cache reuse changed published files.");
                mediaEncoder.VerifyGet(mock => mock.EncoderPath, Times.Once());
            });

            foreach (var format in new[] { "srt", "vtt", "json" })
            {
                await Test($"Real parser/writer ASS to {format.ToUpperInvariant()}, bounds and offset", async () =>
                {
                    foreach (var variant in new[] { (Bounded: false, Preserve: true), (Bounded: true, Preserve: false), (Bounded: true, Preserve: true) })
                    {
                        var result = await ReadText(encoder.GetSubtitles(item, source.Id, 2, format,
                            variant.Bounded ? StartTicks : 0, variant.Bounded ? EndTicks : 0, variant.Preserve, CancellationToken.None));
                        Check(result.Contains("caf\u00e9", StringComparison.Ordinal) || result.Contains("caf\\u00E9", StringComparison.OrdinalIgnoreCase),
                            "UTF-8 fixture text was lost.");
                        if (format == "json")
                        {
                            AssertJson(result, variant.Bounded, variant.Preserve);
                        }
                        else
                        {
                            if (format == "vtt")
                            {
                                Check(result.StartsWith("WEBVTT", StringComparison.Ordinal), "Missing WebVTT header.");
                            }

                            AssertCues(Parse(parser, result, format), "EN", variant.Bounded, variant.Preserve);
                        }
                    }
                });
            }

            await Test("Same-format ASS preserves style and full original bytes", async () =>
            {
                var path = await encoder.GetSubtitleFilePath(english, source, CancellationToken.None);
                var originalText = await File.ReadAllTextAsync(path);
                var result = await ReadText(encoder.GetSubtitles(item, source.Id, 2, "ass", StartTicks, EndTicks, false, CancellationToken.None));
                Check(result == originalText, "Same-format output was rewritten or clipped.");
                Check(result.Contains("[V4+ Styles]", StringComparison.Ordinal) && result.Contains("Style: Accent,", StringComparison.Ordinal)
                    && result.Contains("{\\i1}", StringComparison.Ordinal), "ASS styles or inline overrides were lost.");
                AssertCues(Parse(parser, result, "ass"), "EN", false, true);
            });

            await Test("Burn-in file paths are real complete per-track files", async () =>
            {
                var paths = new List<string>();
                foreach (var expected in new[] { (Stream: english, Language: "EN"), (Stream: dutch, Language: "NL") })
                {
                    var path = await encoder.GetSubtitleFilePath(expected.Stream, source, CancellationToken.None);
                    Check(Path.IsPathFullyQualified(path) && File.Exists(path), "Burn-in returned a nonexistent or relative path.");
                    provider.GetRequiredService<Sandbox>().ValidatePath(path);
                    AssertCues(Parse(parser, await File.ReadAllTextAsync(path), "ass"), expected.Language, false, true);
                    paths.Add(path);
                }

                Check(paths.Distinct().Count() == 2 && Path.GetDirectoryName(paths[0]) == Path.GetDirectoryName(paths[1]),
                    "Burn-in paths are not distinct tracks in the same generation.");
            });

            await Test("Charset API validates both UTF-8 tracks", async () =>
            {
                foreach (var track in new[] { english, dutch })
                {
                    var charset = await encoder.GetSubtitleFileCharacterSet(track, track.Language, source, CancellationToken.None);
                    Check(charset == "UTF-8", "Unexpected subtitle charset.");
                    var path = await encoder.GetSubtitleFilePath(track, source, CancellationToken.None);
                    Check(new UTF8Encoding(false, true).GetString(await File.ReadAllBytesAsync(path)).Contains("caf\u00e9", StringComparison.Ordinal),
                        "Non-ASCII fixture text did not survive extraction.");
                }
            });

            await Test("Core SubtitleController.GetSubtitle calls the registered encoder", async () =>
            {
                var userId = Guid.NewGuid();
                var library = new Mock<ILibraryManager>(MockBehavior.Strict);
                library.Setup(manager => manager.GetItemById(item.Id)).Returns(item);
                library.Setup(manager => manager.GetItemById<BaseItem>(item.Id)).Returns(item);
                library.Setup(manager => manager.GetItemById<BaseItem>(item.Id, userId)).Returns(item);
                library.Setup(manager => manager.GetItemById<Video>(item.Id, userId)).Returns(item);
                var serverConfiguration = new Mock<IServerConfigurationManager>(MockBehavior.Strict);
                var subtitleManager = new Mock<ISubtitleManager>(MockBehavior.Strict);
                var providerManager = new Mock<IProviderManager>(MockBehavior.Strict);
                var fileSystem = new Mock<IFileSystem>(MockBehavior.Strict);
                var controller = new SubtitleController(serverConfiguration.Object, library.Object, subtitleManager.Object,
                    provider.GetRequiredService<ISubtitleEncoder>(), sources.Object, providerManager.Object, fileSystem.Object,
                    NullLogger<SubtitleController>.Instance)
                {
                    ControllerContext = new ControllerContext
                    {
                        HttpContext = new DefaultHttpContext
                        {
                            RequestServices = provider,
                            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                            {
                                new Claim("Jellyfin-UserId", userId.ToString()),
                                new Claim(ClaimTypes.NameIdentifier, userId.ToString())
                            }, "Synthetic"))
                        }
                    }
                };
                var before = sources.Invocations.Count;
                var response = await controller.GetSubtitle(item.Id, source.Id, 2, "srt", null, null, null, null,
                    EndTicks, false, false, StartTicks);
                var result = response switch
                {
                    FileStreamResult streamed => await ReadText(Task.FromResult(streamed.FileStream)),
                    FileContentResult buffered => new UTF8Encoding(false, true).GetString(buffered.FileContents),
                    _ => throw new InvalidOperationException($"Unexpected controller response: {response.GetType().Name}")
                };
                Check(sources.Invocations.Count == before + 1, "Controller did not traverse GuardedEncoder.GetSubtitles exactly once.");
                AssertCues(Parse(parser, result, "srt"), "EN", true, false);
                serverConfiguration.VerifyNoOtherCalls();
                subtitleManager.VerifyNoOtherCalls();
                providerManager.VerifyNoOtherCalls();
                fileSystem.VerifyNoOtherCalls();
            });

            await Test("External MKS maps text track order to noncontiguous snapshot indices", async () =>
            {
                var mks = Path.Combine(root, "external.mks");
                await RunFfmpeg(ffmpeg, Path.Combine(root, "mks.stderr.txt"),
                    "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-protocol_whitelist", "file,pipe",
                    "-i", moviePath, "-map", "0:2", "-map", "0:3", "-c:s", "copy", "-f", "matroska", mks);
                var externalEnglish = Track(5, "eng", mks);
                var externalDutch = Track(8, "nld", mks);
                var externalSource = Source(moviePath, "synthetic-mks", externalEnglish, externalDutch);
                var processCount = mediaEncoder.Invocations.Count;
                var generationCount = Generations(root).Length;
                foreach (var expected in new[] { (Stream: externalEnglish, Language: "EN"), (Stream: externalDutch, Language: "NL") })
                {
                    var path = await encoder.GetSubtitleFilePath(expected.Stream, externalSource, CancellationToken.None);
                    AssertCues(Parse(parser, await File.ReadAllTextAsync(path), "ass"), expected.Language, false, true);
                }

                Check(mediaEncoder.Invocations.Count == processCount + 1, "MKS did not extract all text tracks in one process.");
                Check(Generations(root).Length == generationCount + 1, "MKS did not publish exactly one generation.");
            });

            await Test("Outside-sandbox paths are rejected before spawning FFmpeg", async () =>
            {
                var before = Snapshot(root);
                var processCount = mediaEncoder.Invocations.Count;
                var outside = Path.Combine(root, "..", "subtitle-guard-outside.mkv");
                await Throws<NotSupportedException>(() => encoder.ExtractAllExtractableSubtitles(Source(outside, "outside", Track(2, "eng")), CancellationToken.None));
                var external = Track(2, "eng", Path.Combine(root, "..", "subtitle-guard-outside.ass"));
                await Throws<NotSupportedException>(() => encoder.GetSubtitleFilePath(external, source, CancellationToken.None));
                Check(processCount == mediaEncoder.Invocations.Count && before.SequenceEqual(Snapshot(root)), "Rejected paths touched the cache or encoder.");
            });

            await Test("Network media source is rejected without network access", async () =>
            {
                var processCount = mediaEncoder.Invocations.Count;
                await Throws<NotSupportedException>(() => encoder.ExtractAllExtractableSubtitles(
                    Source("https://example.invalid/synthetic.mkv", "network-rejection", Track(2, "eng")), CancellationToken.None));
                Check(processCount == mediaEncoder.Invocations.Count, "A network source reached FFmpeg.");
            });

            await Test("Mixed unknown codec fails closed without partial text publication", async () =>
            {
                var unsupported = Track(3, "nld");
                unsupported.Codec = "unrecognized";
                var before = Snapshot(root);
                var processCount = mediaEncoder.Invocations.Count;
                await Throws<NotSupportedException>(() => encoder.ExtractAllExtractableSubtitles(
                    Source(moviePath, "synthetic-unknown-metadata", Track(2, "eng"), unsupported), CancellationToken.None));
                Check(processCount == mediaEncoder.Invocations.Count && before.SequenceEqual(Snapshot(root)), "Unknown codec rejection left partial output.");
            });

            await Test("Truncated local Matroska cannot publish or damage a good generation", async () =>
            {
                var before = Snapshot(root);
                var complete = await File.ReadAllBytesAsync(moviePath);
                var modified = File.GetLastWriteTimeUtc(moviePath);
                try
                {
                    await File.WriteAllBytesAsync(moviePath, complete[..(complete.Length / 2)]);
                    await Throws<IOException>(() => encoder.ExtractAllExtractableSubtitles(source, CancellationToken.None));
                    Check(before.SequenceEqual(Snapshot(root)), "Failed extraction published a generation or changed a good one.");
                }
                finally
                {
                    await File.WriteAllBytesAsync(moviePath, complete);
                    File.SetLastWriteTimeUtc(moviePath, modified);
                }
            });

            await Test("Pre-cancelled extraction leaves cache and original generation unchanged", async () =>
            {
                var before = Snapshot(root);
                var processCount = mediaEncoder.Invocations.Count;
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();
                await Throws<OperationCanceledException>(() => encoder.ExtractAllExtractableSubtitles(
                    Source(moviePath, "synthetic-cancelled-cold", Track(2, "eng"), Track(3, "nld")), cancellation.Token));
                Check(processCount == mediaEncoder.Invocations.Count && before.SequenceEqual(Snapshot(root)), "Pre-cancellation spawned or published work.");
            });

            foreach (var useTimeout in new[] { false, true })
            {
                await Test(useTimeout
                    ? "In-flight FFmpeg timeout reaps process and preserves good generations"
                    : "In-flight FFmpeg cancellation reaps process and preserves good generations", async () =>
                {
                    if (!OperatingSystem.IsLinux())
                    {
                        throw new PlatformNotSupportedException("In-flight checks require Linux process evidence.");
                    }

                    var goodGenerations = Generations(root);
                    var goodSnapshot = Snapshot(root);
                    Check(goodSnapshot.Length > 0, "In-flight checks require a previously published good generation.");
                    var scenario = useTimeout ? "timeout" : "inflight-cancellation";
                    var sandbox = new Sandbox(root)
                    {
                        Timeout = TimeSpan.FromSeconds(useTimeout ? 1 : 30)
                    };
                    var shim = sandbox.ValidatePath(Path.Combine(root, scenario + "-ffmpeg.sh"));
                    var marker = sandbox.ValidatePath(shim + ".pid");
                    Check(sandbox.ValidatePath(ffmpeg, false) == ffmpeg
                        && new[] { ffmpeg, marker }.All(path => path.All(character =>
                            char.IsAsciiLetterOrDigit(character) || character is '/' or '-' or '_' or '.')),
                        "Test shim paths must be canonical, symlink-free and contain only shell-safe characters.");
                    File.WriteAllText(shim,
                        $"#!/bin/sh\nset -eu\nprintf '%s\\n' \"$$\" > '{marker}'\nexec '{ffmpeg}' -readrate 0.1 \"$@\"\n",
                        new UTF8Encoding(false));
                    File.SetUnixFileMode(shim, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    var slowMediaEncoder = new Mock<IMediaEncoder>(MockBehavior.Strict);
                    slowMediaEncoder.SetupGet(mock => mock.EncoderPath).Returns(shim);
                    using var slowEncoder = new GuardedEncoder(sandbox, slowMediaEncoder.Object, sources.Object, parser);
                    using var cancellation = new CancellationTokenSource();
                    var elapsed = Stopwatch.StartNew();
                    var extraction = slowEncoder.GetSubtitleFilePath(english,
                        Source(moviePath, "synthetic-" + scenario, english, dutch), cancellation.Token);
                    var processId = 0;
                    try
                    {
                        while (true)
                        {
                            Check(!extraction.IsCompleted, "Extraction finished before a running FFmpeg could be verified.");
                            Check(elapsed.Elapsed < TimeSpan.FromSeconds(5), "FFmpeg did not start within five seconds.");
                            if (File.Exists(marker)
                                && int.TryParse(await File.ReadAllTextAsync(marker), out processId)
                                && processId > 0
                                && new FileInfo($"/proc/{processId}/exe").LinkTarget == ffmpeg)
                            {
                                break;
                            }

                            await Task.Delay(TimeSpan.FromMilliseconds(20));
                        }

                        Check(Directory.GetDirectories(Path.Combine(root, "guard-cache"), ".pending-*").Length == 1,
                            "Running extraction did not have exactly one staging directory.");
                        if (!useTimeout)
                        {
                            cancellation.CancelAfter(TimeSpan.FromMilliseconds(500));
                        }

                        await Throws<OperationCanceledException>(() => extraction.WaitAsync(TimeSpan.FromSeconds(10)));
                        Check(elapsed.Elapsed < TimeSpan.FromSeconds(10), "Interrupted extraction exceeded ten seconds.");
                        Check(cancellation.IsCancellationRequested == !useTimeout,
                            "Extraction did not use the expected caller-cancellation or sandbox-timeout path.");
                        Check(!Directory.Exists($"/proc/{processId}"), "FFmpeg survived the completed extraction task.");
                        Check(goodGenerations.SequenceEqual(Generations(root)) && goodSnapshot.SequenceEqual(Snapshot(root)),
                            "Interrupted extraction left staging, published a new generation or changed a good one.");
                        slowMediaEncoder.VerifyGet(mock => mock.EncoderPath, Times.Once());
                        slowMediaEncoder.VerifyNoOtherCalls();
                    }
                    finally
                    {
                        cancellation.Cancel();
                        if (processId > 0 && new FileInfo($"/proc/{processId}/exe").LinkTarget == ffmpeg)
                        {
                            using var remaining = Process.GetProcessById(processId);
                            if (!remaining.HasExited)
                            {
                                remaining.Kill(true);
                            }

                            await remaining.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                        }

                        try
                        {
                            await extraction.WaitAsync(TimeSpan.FromSeconds(5));
                        }
                        catch (Exception) when (extraction.IsCompleted)
                        {
                        }
                    }
                });
            }

            await MediaFormatTests(root, ffmpeg);
            await HttpFaultTests.Run(root, ffmpeg, Test);

            await Test("No native subtitle delegation or media probing occurred", () =>
            {
                original.VerifyNoOtherCalls();
                host.VerifyNoOtherCalls();
                mediaEncoder.VerifyGet(mock => mock.EncoderPath, Times.AtLeastOnce());
                mediaEncoder.VerifyNoOtherCalls();
                sources.Verify(manager => manager.GetPlaybackMediaSources(item, null!, false, false, It.IsAny<CancellationToken>()), Times.AtLeastOnce());
                sources.VerifyNoOtherCalls();
                _ = Generations(root);
                return Task.CompletedTask;
            });

            await PersistentStateTests.Run(root, Test);
            Console.WriteLine($"RESULT: {_passed} passed, {_failed} failed");
            return _failed == 0 ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"SETUP FAILED: {exception}");
            return 2;
        }
        finally
        {
            Environment.SetEnvironmentVariable("SUBTITLE_GUARD_SANDBOX", previousRoot);
            Environment.SetEnvironmentVariable("SUBTITLE_GUARD_TEST_ORIGIN", previousOrigin);
            Environment.SetEnvironmentVariable("SUBTITLE_GUARD_SOURCE_POLICY", previousPolicy);
            Environment.SetEnvironmentVariable("SUBTITLE_GUARD_NATIVE_PILOT", previousNativePilot);
            Environment.SetEnvironmentVariable("SUBTITLE_GUARD_PRODUCTION_POLICY", previousProductionPolicy);
        }
    }

    private static async Task MediaFormatTests(string parent, string ffmpeg)
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException(); }
        var root = Directory.CreateDirectory(Path.Combine(parent, "media-formats")).FullName;
        using (File.Open(Path.Combine(root, ".subtitle-guard-sandbox"), FileMode.CreateNew, FileAccess.Write)) { }
        var sandbox = new Sandbox(root);
        var mediaEncoder = new Mock<IMediaEncoder>(MockBehavior.Strict);
        mediaEncoder.SetupGet(mock => mock.EncoderPath).Returns(ffmpeg);
        ISubtitleParser parser = new SubtitleEditParser(NullLogger<SubtitleEditParser>.Instance);
        var sources = new Mock<IMediaSourceManager>(MockBehavior.Strict);
        using var encoder = new GuardedEncoder(sandbox, mediaEncoder.Object, sources.Object, parser);

        foreach (var faststart in new[] { false, true })
        {
            await Test($"MP4 mov_text: {(faststart ? "faststart" : "moov at end")} normalizes to complete SRT", async () =>
            {
                var path = Path.Combine(root, faststart ? "faststart.mp4" : "moov-end.mp4");
                var arguments = new List<string>
                {
                    "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-protocol_whitelist", "file,pipe",
                    "-i", Path.Combine(parent, "bilingual.mkv"), "-map", "0", "-t", "12", "-c:v", "mpeg4",
                    "-threads", "1", "-c:a", "aac", "-c:s", "mov_text"
                };
                if (faststart) { arguments.AddRange(["-movflags", "+faststart"]); }
                arguments.Add(path);
                await RunFfmpeg(ffmpeg, path + ".stderr.txt", arguments.ToArray());
                var bytes = await File.ReadAllBytesAsync(path);
                var boxes = new List<string>();
                var offset = 0;
                while (offset < bytes.Length)
                {
                    Check(bytes.Length - offset >= 8, "Truncated MP4 box header.");
                    var size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
                    Check(size >= 8 && size <= bytes.Length - offset, "Unexpected MP4 box length.");
                    boxes.Add(Encoding.ASCII.GetString(bytes, offset + 4, 4));
                    offset += checked((int)size);
                }
                Check(boxes.Contains("moov") && boxes.Contains("mdat")
                    && (boxes.IndexOf("moov") < boxes.IndexOf("mdat")) == faststart, "MP4 fixture has the wrong moov layout.");
                var english = Track(2, "eng");
                var dutch = Track(3, "nld");
                english.Codec = dutch.Codec = "mov_text";
                var source = Source(path, Path.GetFileName(path), english, dutch);
                source.Container = "mov,mp4,m4a,3gp,3g2,mj2";
                var item = new Video { Id = Guid.NewGuid(), Path = path };
                sources.Setup(manager => manager.GetPlaybackMediaSources(item, null!, false, false, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new List<MediaSourceInfo> { source });
                foreach (var (track, language) in new[] { (english, "EN"), (dutch, "NL") })
                {
                    var extracted = await encoder.GetSubtitleFilePath(track, source, CancellationToken.None);
                    Check(Path.GetExtension(extracted) == ".srt", "mov_text did not get an SRT cache path.");
                    AssertCues(Parse(parser, await File.ReadAllTextAsync(extracted), "srt"), language, false, true);
                    var raw = await ReadText(encoder.GetSubtitles(item, source.Id, track.Index, "srt", 0, 0, true, CancellationToken.None));
                    Check(raw == await File.ReadAllTextAsync(extracted), "Same-format SRT delivery changed bytes.");
                    var converted = await ReadText(encoder.GetSubtitles(item, source.Id, track.Index, "vtt", 0, 0, true, CancellationToken.None));
                    AssertCues(Parse(parser, converted, "vtt"), language, false, true);
                    Check(await encoder.GetSubtitleFileCharacterSet(track, track.Language, source, CancellationToken.None) == "UTF-8",
                        "Converted mov_text has the wrong charset.");
                }
                var before = Snapshot(root);
                var processCount = mediaEncoder.Invocations.Count;
                await encoder.ExtractAllExtractableSubtitles(source, CancellationToken.None);
                Check(before.SequenceEqual(Snapshot(root)) && mediaEncoder.Invocations.Count == processCount, "Warm MP4 started FFmpeg or changed output.");
            });
        }

        foreach (var (codec, extension) in new[]
        {
            ("hdmv_pgs_subtitle", "sup"), ("pgssub", "sup"), ("dvd_subtitle", "mks"), ("dvdsub", "mks")
        })
        {
            await Test($"Bitmap mapping only: {codec} raw .{extension}, mixed text copy, no parser or charset", async () =>
            {
                if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException(); }
                var fixtureRoot = Directory.CreateDirectory(Path.Combine(root, codec)).FullName;
                using (File.Open(Path.Combine(fixtureRoot, ".subtitle-guard-sandbox"), FileMode.CreateNew, FileAccess.Write)) { }
                var textPath = Path.Combine(fixtureRoot, "text.ass");
                var bitmapPath = Path.Combine(fixtureRoot, "mapping-only.bytes");
                var log = Path.Combine(fixtureRoot, "arguments.txt");
                var shim = Path.Combine(fixtureRoot, "mapping-only.sh");
                Check(fixtureRoot.All(character => char.IsAsciiLetterOrDigit(character) || character is '/' or '-' or '_' or '.'),
                    "Mapping shim paths must be shell-safe.");
                File.Copy(Path.Combine(parent, "english.ass"), textPath);
                await File.WriteAllBytesAsync(bitmapPath, extension == "sup" ? [0x50, 0x47, 0xff, 0x00] : [0x1a, 0x45, 0xdf, 0xa3]);
                await File.WriteAllTextAsync(shim,
                    $"#!/bin/sh\nset -eu\nprintf '%s\\n' \"$@\" > '{log}'\nfor argument in \"$@\"; do\ncase \"$argument\" in\n*/.pending-*/2.ass) cp '{textPath}' \"$argument\";;\n*/.pending-*/3.{extension}) cp '{bitmapPath}' \"$argument\";;\nesac\ndone\n", new UTF8Encoding(false));
                File.SetUnixFileMode(shim, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                var fakeMediaEncoder = new Mock<IMediaEncoder>(MockBehavior.Strict);
                fakeMediaEncoder.SetupGet(mock => mock.EncoderPath).Returns(shim);
                var noParser = new Mock<ISubtitleParser>(MockBehavior.Strict);
                var fixtureSources = new Mock<IMediaSourceManager>(MockBehavior.Strict);
                using var fixtureEncoder = new GuardedEncoder(new Sandbox(fixtureRoot), fakeMediaEncoder.Object, fixtureSources.Object, noParser.Object);
                var bitmap = Track(3, "nld");
                bitmap.Codec = codec;
                var source = Source(bitmapPath, "mapping-only-" + codec, Track(2, "eng"), bitmap);
                var item = new Video { Id = Guid.NewGuid(), Path = bitmapPath };
                fixtureSources.Setup(manager => manager.GetPlaybackMediaSources(item, null!, false, false, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new List<MediaSourceInfo> { source });
                var extracted = await fixtureEncoder.GetSubtitleFilePath(bitmap, source, CancellationToken.None);
                Check(Path.GetExtension(extracted) == "." + extension, "Wrong bitmap cache extension.");
                var command = await File.ReadAllLinesAsync(log);
                var output = Array.FindIndex(command, argument => argument.EndsWith("/3." + extension, StringComparison.Ordinal));
                Check(command.Count(argument => argument == "copy") == 2 && command.Contains("0:2") && command.Contains("0:3")
                    && (extension == "sup" ? command[output - 2] == "-flush_packets" : command[output - 2] == "-f" && command[output - 1] == "matroska"),
                    "Bitmap or mixed-text mapping did not use the required raw-copy muxer.");
                var expectedText = await File.ReadAllBytesAsync(textPath);
                var expectedBitmap = await File.ReadAllBytesAsync(bitmapPath);
                Check((await File.ReadAllBytesAsync(Path.Combine(Path.GetDirectoryName(extracted)!, "2.ass")))
                    .SequenceEqual(expectedText), "Mixed ASS was rewritten.");
                await using var raw = await fixtureEncoder.GetSubtitles(item, source.Id, bitmap.Index, extension, 0, 0, true, CancellationToken.None);
                using var delivered = new MemoryStream();
                await raw.CopyToAsync(delivered);
                Check(delivered.ToArray().SequenceEqual(expectedBitmap), "Raw bitmap delivery changed bytes.");
                await Throws<NotSupportedException>(() => fixtureEncoder.GetSubtitles(item, source.Id, bitmap.Index, "srt", 0, 0, true, CancellationToken.None));
                await Throws<NotSupportedException>(() => fixtureEncoder.GetSubtitleFileCharacterSet(bitmap, bitmap.Language, source, CancellationToken.None));
                await fixtureEncoder.ExtractAllExtractableSubtitles(source, CancellationToken.None);
                fakeMediaEncoder.VerifyGet(mock => mock.EncoderPath, Times.Once());
                noParser.VerifyNoOtherCalls();
            });
        }

        await Test("Actual ASS mislabeled as PGS fails FFmpeg checks without mixed partial publication", async () =>
        {
            var input = Path.Combine(root, "mislabeled.mkv");
            File.Copy(Path.Combine(parent, "bilingual.mkv"), input);
            var bitmap = Track(3, "nld");
            bitmap.Codec = "hdmv_pgs_subtitle";
            var before = Snapshot(root);
            var processCount = mediaEncoder.Invocations.Count;
            await Throws<IOException>(() => encoder.ExtractAllExtractableSubtitles(
                Source(input, "mislabeled-bitmap", Track(2, "eng"), bitmap), CancellationToken.None));
            Check(mediaEncoder.Invocations.Count == processCount + 1 && before.SequenceEqual(Snapshot(root)),
                "Mismatched codec skipped FFmpeg validation or published partial output.");
        });
    }

    private const string PolicyToken = "policy-sentinel-token";
    private const string PolicyUrl = "https://source.invalid:8443/policy-sentinel-path/episode.mkv?token=" + PolicyToken;
    private const string PolicyRedirectOrigin = "https://redirect.invalid:9443";

    private static async Task SourcePolicyTests(string parent)
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException(); }
        Check(NetworkInterface.GetAllNetworkInterfaces().All(network => network.NetworkInterfaceType == NetworkInterfaceType.Loopback),
            "Source policy checks require a loopback-only Linux network namespace.");
        using var network = new PolicyNetworkEvents();
        var names = new[] { "SUBTITLE_GUARD_SANDBOX", "SUBTITLE_GUARD_TEST_ORIGIN", "SUBTITLE_GUARD_SOURCE_POLICY", "SUBTITLE_GUARD_NATIVE_PILOT" };
        var previous = names.Select(Environment.GetEnvironmentVariable).ToArray();
        var root = Path.Combine(parent, "policy-sentinel-path-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "policy.json");
        Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            using (File.Open(Path.Combine(root, ".subtitle-guard-sandbox"), FileMode.CreateNew, FileAccess.Write)) { }
            Environment.SetEnvironmentVariable(names[0], root);
            Environment.SetEnvironmentVariable(names[1], null);
            Environment.SetEnvironmentVariable(names[2], path);
            Environment.SetEnvironmentVariable(names[3], null);
            foreach (var limits in new[] { (Bytes: 1L, Seconds: 1), (Bytes: 67108865L, Seconds: 47), (Bytes: 4L * 1024 * 1024 * 1024, Seconds: 900) })
            {
                await Test($"Source policy: registered singleton loads {limits.Bytes} bytes/{limits.Seconds} seconds", () =>
                {
                    if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException(); }
                    var json = PolicyJson(limits.Bytes, limits.Seconds);
                    if (limits.Seconds == 900) { json = json.PadRight(16384); }
                    WritePolicy(path, json);
                    Check(new FileInfo(path).Length == (limits.Seconds == 900 ? 16384 : Encoding.UTF8.GetByteCount(json)),
                        "Policy fixture has an unexpected byte length.");
                    var original = new Mock<ISubtitleEncoder>(MockBehavior.Strict);
                    var host = new Mock<IServerApplicationHost>(MockBehavior.Strict);
                    var mediaEncoder = new Mock<IMediaEncoder>(MockBehavior.Strict);
                    var sources = new Mock<IMediaSourceManager>(MockBehavior.Strict);
                    var parser = new Mock<ISubtitleParser>(MockBehavior.Strict);
                    var services = new ServiceCollection();
                    services.AddSingleton(original.Object);
                    services.AddSingleton(mediaEncoder.Object);
                    services.AddSingleton(sources.Object);
                    services.AddSingleton(parser.Object);
                    new Registrator().RegisterServices(services, host.Object);
                    using var provider = services.BuildServiceProvider();
                    var sandbox = provider.GetRequiredService<Sandbox>();
                    var encoder = provider.GetRequiredService<ISubtitleEncoder>();
                    Check(encoder is GuardedEncoder && ReferenceEquals(encoder, provider.GetRequiredService<ISubtitleEncoder>())
                        && provider.GetServices<ISubtitleEncoder>().Count() == 1, "Policy did not register only the guarded singleton.");
                    Check(ReferenceEquals(sandbox, provider.GetRequiredService<Sandbox>()) && provider.GetServices<Sandbox>().Count() == 1,
                        "Policy sandbox is not a unique singleton.");
                    Check(sandbox.Root == root && sandbox.HttpTestOrigin is null && sandbox.PilotUrls.SequenceEqual(new[] { PolicyUrl })
                        && sandbox.PilotRedirectOrigins.SequenceEqual(new[] { PolicyRedirectOrigin }), "Registered policy values differ from the file.");
                    Check(sandbox.TransferLimitBytes == limits.Bytes && sandbox.Timeout == TimeSpan.FromSeconds(limits.Seconds)
                        && sandbox.PersistRemoteState && sandbox.RevalidationInterval == TimeSpan.FromHours(24)
                        && sandbox.FailureCooldown == TimeSpan.FromMinutes(5), "Policy budgets or fixed persistence windows were not loaded.");
                    Check(typeof(Sandbox).GetProperty("RequireLoopbackNetwork", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                        ?.GetValue(sandbox) is true, "Registered policy did not enable per-URI network isolation.");
                    Check(sandbox.ValidateInput(PolicyUrl) == PolicyUrl, "Exact initial URL was rejected.");
                    original.VerifyNoOtherCalls();
                    host.VerifyNoOtherCalls();
                    mediaEncoder.VerifyNoOtherCalls();
                    sources.VerifyNoOtherCalls();
                    parser.VerifyNoOtherCalls();
                    return Task.CompletedTask;
                });
            }

            string Changed(string field, JsonNode? value, bool remove = false)
            {
                var json = JsonNode.Parse(PolicyJson())!.AsObject();
                if (remove) { json.Remove(field); } else { json[field] = value; }
                return json.ToJsonString();
            }

            var invalidJson = new List<(string Name, string Json)>
            {
                ("empty document", ""),
                ("malformed document", "{\"AllowedUrls\":[\"" + PolicyUrl + "\"],"),
                ("null document", "null"),
                ("array document", "[]"),
                ("empty object", "{}"),
                ("trailing document", PolicyJson() + "{}"),
                ("comments", "/*" + PolicyToken + "*/" + PolicyJson()),
                ("trailing comma", PolicyJson()[..^1] + ",}"),
                ("over 16 KiB", PolicyJson().PadRight(16385)),
                ("unknown member", Changed("Unexpected", PolicyToken)),
                ("wrong member case", Changed("allowedUrls", new JsonArray(PolicyUrl))),
                ("no initial URLs", Changed("AllowedUrls", new JsonArray())),
                ("multiple initial URLs", Changed("AllowedUrls", new JsonArray(PolicyUrl, PolicyUrl))),
                ("null initial URL", Changed("AllowedUrls", new JsonArray((JsonNode?)null))),
                ("initial URLs not array", Changed("AllowedUrls", PolicyUrl)),
                ("redirect origins not array", Changed("AllowedRedirectOrigins", PolicyRedirectOrigin)),
                ("null redirect origin", Changed("AllowedRedirectOrigins", new JsonArray((JsonNode?)null))),
                ("too many redirect origins", Changed("AllowedRedirectOrigins", new JsonArray(Enumerable.Range(0, 5)
                    .Select(index => (JsonNode?)JsonValue.Create($"https://redirect-{index}.invalid")).ToArray()))),
                ("bytes zero", Changed("MaxBytes", 0)),
                ("bytes negative", Changed("MaxBytes", -1)),
                ("bytes above ceiling", Changed("MaxBytes", 4L * 1024 * 1024 * 1024 + 1)),
                ("bytes fractional", Changed("MaxBytes", 1.5)),
                ("bytes string", Changed("MaxBytes", "1")),
                ("bytes overflow", Changed("MaxBytes", decimal.MaxValue)),
                ("timeout zero", Changed("TimeoutSeconds", 0)),
                ("timeout negative", Changed("TimeoutSeconds", -1)),
                ("timeout above ceiling", Changed("TimeoutSeconds", 901)),
                ("timeout fractional", Changed("TimeoutSeconds", 1.5)),
                ("timeout string", Changed("TimeoutSeconds", "1")),
                ("timeout overflow", Changed("TimeoutSeconds", long.MaxValue))
            };
            foreach (var field in new[] { "AllowedUrls", "AllowedRedirectOrigins", "MaxBytes", "TimeoutSeconds" })
            {
                invalidJson.Add(("missing " + field, Changed(field, null, true)));
                invalidJson.Add(("null " + field, Changed(field, null)));
                invalidJson.Add(("duplicate " + field, PolicyJson()[..^1] + "," + JsonSerializer.Serialize(field)
                    + ":" + JsonNode.Parse(PolicyJson())![field]!.ToJsonString() + "}"));
            }
            foreach (var entry in new[]
            {
                (Name: "empty", Value: ""),
                (Name: "relative", Value: "policy-sentinel-path/episode.mkv"),
                (Name: "invalid authority", Value: "https://[invalid/" + PolicyToken),
                (Name: "non-HTTP", Value: "ftp://source.invalid/" + PolicyToken),
                (Name: "credentials", Value: "https://" + PolicyToken + "@source.invalid/episode.mkv"),
                (Name: "fragment", Value: PolicyUrl + "#" + PolicyToken),
                (Name: "noncanonical host", Value: PolicyUrl.Replace("source.invalid", "SOURCE.invalid", StringComparison.Ordinal)),
                (Name: "file URI", Value: new Uri(path).AbsoluteUri),
                (Name: "local path", Value: path)
            })
            {
                invalidJson.Add(("initial URL " + entry.Name, Changed("AllowedUrls", new JsonArray(entry.Value))));
            }
            foreach (var entry in new[]
            {
                (Name: "empty", Value: ""),
                (Name: "relative", Value: "policy-sentinel-path"),
                (Name: "non-HTTP", Value: "ftp://redirect.invalid/"),
                (Name: "path", Value: PolicyRedirectOrigin + "/policy-sentinel-path"),
                (Name: "query", Value: PolicyRedirectOrigin + "?token=" + PolicyToken),
                (Name: "fragment", Value: PolicyRedirectOrigin + "#" + PolicyToken),
                (Name: "credentials", Value: "https://" + PolicyToken + "@redirect.invalid:9443"),
                (Name: "invalid authority", Value: "https://[invalid/" + PolicyToken)
            })
            {
                invalidJson.Add(("redirect origin " + entry.Name, Changed("AllowedRedirectOrigins", new JsonArray(entry.Value))));
            }
            foreach (var entry in invalidJson)
            {
                await Test("Source policy rejects " + entry.Name, () =>
                {
                    WritePolicy(path, entry.Json);
                    AssertPolicyRegistrationRejected(root, path);
                    return Task.CompletedTask;
                });
            }

            var outside = Path.Combine(parent, "policy-sentinel-path-outside-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                WritePolicy(outside, PolicyJson());
                foreach (var scenario in new[] { "missing file", "relative file", "outside root", "symlink file", "symlink parent", "symlink root", "conflicting modes" })
                {
                    await Test("Source policy rejects " + scenario, () =>
                    {
                        WritePolicy(path, PolicyJson());
                        var link = Path.Combine(parent, "policy-sentinel-path-link-" + Guid.NewGuid().ToString("N"));
                        var fileLink = scenario == "symlink file";
                        try
                        {
                            switch (scenario)
                            {
                                case "missing file": Environment.SetEnvironmentVariable(names[2], path + ".missing"); break;
                                case "relative file": Environment.SetEnvironmentVariable(names[2], "policy-sentinel-path.json"); break;
                                case "outside root": Environment.SetEnvironmentVariable(names[2], outside); break;
                                case "symlink file":
                                    link = Path.Combine(root, "policy-sentinel-path-link.json");
                                    File.CreateSymbolicLink(link, path);
                                    Environment.SetEnvironmentVariable(names[2], link);
                                    break;
                                case "symlink parent":
                                    link = Path.Combine(root, "policy-sentinel-path-link");
                                    Directory.CreateSymbolicLink(link, root);
                                    Environment.SetEnvironmentVariable(names[2], Path.Combine(link, "policy.json"));
                                    break;
                                case "symlink root":
                                    Directory.CreateSymbolicLink(link, root);
                                    Environment.SetEnvironmentVariable(names[0], link);
                                    Environment.SetEnvironmentVariable(names[2], Path.Combine(link, "policy.json"));
                                    break;
                                case "conflicting modes": Environment.SetEnvironmentVariable(names[1], "http://127.0.0.1:18443"); break;
                            }
                            AssertPolicyRegistrationRejected(root, path, outside, link);
                        }
                        finally
                        {
                            Environment.SetEnvironmentVariable(names[0], root);
                            Environment.SetEnvironmentVariable(names[1], null);
                            Environment.SetEnvironmentVariable(names[2], path);
                            if (fileLink && File.Exists(link)) { File.Delete(link); }
                            else if (Directory.Exists(link)) { Directory.Delete(link); }
                        }
                        return Task.CompletedTask;
                    });
                }
            }
            finally { File.Delete(outside); }

            foreach (var target in new[] { "file", "root" })
            {
                var required = UnixFileMode.UserRead | UnixFileMode.UserWrite
                    | (target == "root" ? UnixFileMode.UserExecute : 0);
                foreach (var extra in new[] { UnixFileMode.GroupRead, UnixFileMode.GroupWrite, UnixFileMode.OtherRead, UnixFileMode.OtherWrite, UnixFileMode.StickyBit })
                {
                    await Test($"Source policy rejects {target} permission {extra}", () =>
                    {
                        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException(); }
                        WritePolicy(path, PolicyJson());
                        var targetPath = target == "root" ? root : path;
                        try
                        {
                            File.SetUnixFileMode(targetPath, required | extra);
                            Check(File.GetUnixFileMode(targetPath) == (required | extra), "Unsafe permission fixture was not applied.");
                            AssertPolicyRegistrationRejected(root, path);
                        }
                        finally { File.SetUnixFileMode(targetPath, required); }
                        return Task.CompletedTask;
                    });
                }
            }

            WritePolicy(path, PolicyJson());
            var validationServices = new ServiceCollection();
            validationServices.AddSingleton(new Mock<ISubtitleEncoder>(MockBehavior.Strict).Object);
            new Registrator().RegisterServices(validationServices, new Mock<IServerApplicationHost>(MockBehavior.Strict).Object);
            var configured = (Sandbox)validationServices.Single(descriptor => descriptor.ServiceType == typeof(Sandbox)).ImplementationInstance!;
            foreach (var candidate in new[]
            {
                PolicyUrl.Replace("source.invalid", "other.invalid", StringComparison.Ordinal),
                PolicyUrl.Replace("source.invalid", "source.invalid.attacker.invalid", StringComparison.Ordinal),
                PolicyUrl.Replace(":8443", ":8444", StringComparison.Ordinal),
                PolicyUrl.Replace("https://", "http://", StringComparison.Ordinal),
                PolicyUrl.Replace("episode.mkv", "episode.mkv/extra", StringComparison.Ordinal),
                PolicyUrl.Replace("episode.mkv", "Episode.mkv", StringComparison.Ordinal),
                PolicyUrl.Replace("episode.mkv", "other.mkv", StringComparison.Ordinal),
                PolicyUrl[..PolicyUrl.IndexOf('?')],
                PolicyUrl + "-changed",
                PolicyUrl + "&extra=1",
                PolicyUrl + "#fragment",
                PolicyRedirectOrigin + "/policy-sentinel-path?token=" + PolicyToken
            }.Select((value, index) => (Value: value, Index: index)))
            {
                await Test($"Source policy rejects initial URL boundary {candidate.Index + 1}", () =>
                {
                    AssertPolicyRejected<NotSupportedException>(validationServices, () => configured.ValidateInput(candidate.Value), root, path, candidate.Value);
                    return Task.CompletedTask;
                });
            }

            await Test("Source policy permits only granted redirects, including subsequent hops", () =>
            {
                foreach (var target in new[] { PolicyRedirectOrigin + "/one?token=" + PolicyToken, PolicyRedirectOrigin + "/two/other.mkv?different=1" })
                {
                    Check(configured.ValidateRedirect(PolicyUrl, target) == target
                        && configured.ValidateRedirect(PolicyRedirectOrigin + "/prior", target) == target,
                        "Approved redirect origin did not permit arbitrary paths and queries.");
                }
                Check(configured.ValidateRedirect(PolicyUrl, PolicyUrl) == PolicyUrl, "Exact initial URL was rejected as a redirect.");
                return Task.CompletedTask;
            });
            foreach (var candidate in new[]
            {
                PolicyUrl.Replace("episode.mkv", "other.mkv", StringComparison.Ordinal),
                PolicyRedirectOrigin.Replace("redirect.invalid", "other.invalid", StringComparison.Ordinal) + "/file",
                PolicyRedirectOrigin.Replace("redirect.invalid", "redirect.invalid.attacker.invalid", StringComparison.Ordinal) + "/file",
                PolicyRedirectOrigin.Replace(":9443", ":9444", StringComparison.Ordinal) + "/file",
                PolicyRedirectOrigin.Replace("https://", "http://", StringComparison.Ordinal) + "/file",
                "https://" + PolicyToken + "@redirect.invalid:9443/file",
                PolicyRedirectOrigin + "/file#" + PolicyToken,
                "ftp://redirect.invalid:9443/file"
            }.Select((value, index) => (Value: value, Index: index)))
            {
                await Test($"Source policy rejects redirect boundary {candidate.Index + 1}", () =>
                {
                    AssertPolicyRejected<NotSupportedException>(validationServices, () => configured.ValidateRedirect(PolicyUrl, candidate.Value), root, path, candidate.Value);
                    return Task.CompletedTask;
                });
            }
            await Test("Source policy rejects an unapproved redirect source", () =>
            {
                AssertPolicyRejected<NotSupportedException>(validationServices,
                    () => configured.ValidateRedirect("https://other.invalid/" + PolicyToken, PolicyRedirectOrigin + "/file"), root, path);
                return Task.CompletedTask;
            });
            await Test("Source policy still validates local paths", () =>
            {
                Check(configured.ValidateInput(path) == path, "Valid local sandbox path was rejected.");
                foreach (var candidate in new[] { "policy-sentinel-path.mkv", outside, Path.Combine(root, "..", "policy-sentinel-path-outside.mkv"), root + "-sibling/file.mkv" })
                {
                    AssertPolicyRejected<NotSupportedException>(validationServices, () => configured.ValidateInput(candidate), root, path, candidate);
                }
                var link = Path.Combine(root, "policy-sentinel-path-input-link");
                File.CreateSymbolicLink(link, path);
                try { AssertPolicyRejected<NotSupportedException>(validationServices, () => configured.ValidateInput(link), root, path, link); }
                finally { File.Delete(link); }
                return Task.CompletedTask;
            });
            await Test("Source policy accepts an empty redirect list without granting redirects", () =>
            {
                WritePolicy(path, Changed("AllowedRedirectOrigins", new JsonArray()));
                var services = new ServiceCollection();
                new Registrator().RegisterServices(services, new Mock<IServerApplicationHost>(MockBehavior.Strict).Object);
                var sandbox = (Sandbox)services.Single(descriptor => descriptor.ServiceType == typeof(Sandbox)).ImplementationInstance!;
                Check(sandbox.PilotRedirectOrigins.Length == 0 && sandbox.ValidateInput(PolicyUrl) == PolicyUrl, "Empty redirect list was not preserved.");
                AssertPolicyRejected<NotSupportedException>(services, () => sandbox.ValidateRedirect(PolicyUrl, PolicyRedirectOrigin + "/file"), root, path);
                return Task.CompletedTask;
            });
            await Test("Source policy permits HTTPS downgrade only to an approved HTTP origin", () =>
            {
                var httpOrigin = "http://redirect.invalid:9443";
                WritePolicy(path, Changed("AllowedRedirectOrigins", new JsonArray(httpOrigin)));
                var services = new ServiceCollection();
                new Registrator().RegisterServices(services, new Mock<IServerApplicationHost>(MockBehavior.Strict).Object);
                var sandbox = (Sandbox)services.Single(descriptor => descriptor.ServiceType == typeof(Sandbox)).ImplementationInstance!;
                Check(sandbox.ValidateRedirect(PolicyUrl, httpOrigin + "/file") == httpOrigin + "/file",
                    "Registered policy rejected an approved HTTP redirect.");
                AssertPolicyRejected<NotSupportedException>(services, () => sandbox.ValidateRedirect(PolicyUrl, "http://other.invalid:9443/file"), root, path);
                AssertPolicyRejected<NotSupportedException>(services, () => sandbox.ValidateRedirect(PolicyUrl, "http://redirect.invalid:9444/file"), root, path);
                return Task.CompletedTask;
            });
            var nativeRoot = Path.Combine("/tmp", "subtitle-guard-native-policy-sentinel-path-" + Guid.NewGuid().ToString("N"));
            Check(!Path.Exists(nativeRoot), "Refusing to reuse a native pilot root.");
            Directory.CreateDirectory(nativeRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            try
            {
                var marker = Path.Combine(nativeRoot, ".subtitle-guard-sandbox");
                using (File.Open(marker, FileMode.CreateNew, FileAccess.Write)) { }
                foreach (var directory in new[] { "data", "config", "cache", "logs" }) { Directory.CreateDirectory(Path.Combine(nativeRoot, directory)); }
                var nativePolicy = Path.Combine(nativeRoot, "policy.json");
                WritePolicy(nativePolicy, PolicyJson());
                var networkPath = Path.Combine(nativeRoot, "config", "network.xml");
                const string networkXml = """
                    <NetworkConfiguration>
                      <InternalHttpPort>18097</InternalHttpPort>
                      <EnableIPv4>true</EnableIPv4>
                      <EnableIPv6>false</EnableIPv6>
                      <EnableRemoteAccess>false</EnableRemoteAccess>
                      <EnableAutoDiscovery>false</EnableAutoDiscovery>
                      <LocalNetworkAddresses><string>127.0.0.1</string></LocalNetworkAddresses>
                    </NetworkConfiguration>
                    """;
                WritePolicy(networkPath, networkXml);
                string[] nativeArguments = ["/usr/lib/jellyfin/bin/jellyfin", "--datadir", Path.Combine(nativeRoot, "data"),
                    "--configdir", Path.Combine(nativeRoot, "config"), "--cachedir", Path.Combine(nativeRoot, "cache"),
                    "--logdir", Path.Combine(nativeRoot, "logs"), "--nowebclient"];
                var nativeGuard = typeof(Registrator).GetMethod("ValidateNativePilot", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                Check(nativeGuard is not null, "Native host guard is absent.");
                void ValidateNative(string candidateRoot, string[] candidateArguments, string cwd)
                {
                    try { nativeGuard!.Invoke(null, [candidateRoot, candidateArguments, cwd]); }
                    catch (System.Reflection.TargetInvocationException exception) { throw exception.InnerException!; }
                }
                void RejectNative(string candidateRoot, string[] candidateArguments, string cwd) =>
                    AssertPolicyRejected<InvalidOperationException>(validationServices,
                        () => ValidateNative(candidateRoot, candidateArguments, cwd), nativeRoot, candidateRoot, networkPath);

                await Test("Native pilot helper accepts only the explicit private host layout (not a process-loader proof)", () =>
                {
                    ValidateNative(nativeRoot, nativeArguments, nativeRoot);
                    WritePolicy(networkPath, networkXml.Replace("<NetworkConfiguration>",
                        "<NetworkConfiguration xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">", StringComparison.Ordinal));
                    ValidateNative(nativeRoot, nativeArguments, nativeRoot);
                    WritePolicy(networkPath, networkXml);
                    return Task.CompletedTask;
                });

                foreach (var scenario in new[] { "blank opt-in", "wrong opt-in", "missing policy", "conflicting origin", "missing sandbox", "actual process lacks native arguments" })
                {
                    await Test("Native pilot registration rejects " + scenario + " before DI mutation", () =>
                    {
                        Environment.SetEnvironmentVariable(names[0], scenario == "missing sandbox" ? null : nativeRoot);
                        Environment.SetEnvironmentVariable(names[1], scenario == "conflicting origin" ? "http://127.0.0.1:18443" : null);
                        Environment.SetEnvironmentVariable(names[2], scenario == "missing policy" ? null : nativePolicy);
                        Environment.SetEnvironmentVariable(names[3], scenario == "blank opt-in" ? " " : scenario == "wrong opt-in" ? nativeRoot + "-wrong" : nativeRoot);
                        var previousDirectory = Environment.CurrentDirectory;
                        try
                        {
                            Environment.CurrentDirectory = nativeRoot;
                            AssertPolicyRegistrationRejected(nativeRoot, nativePolicy);
                        }
                        finally
                        {
                            Environment.CurrentDirectory = previousDirectory;
                            Environment.SetEnvironmentVariable(names[0], root);
                            Environment.SetEnvironmentVariable(names[1], null);
                            Environment.SetEnvironmentVariable(names[2], path);
                            Environment.SetEnvironmentVariable(names[3], null);
                        }
                        return Task.CompletedTask;
                    });
                }

                await Test("Native pilot rejects noncanonical, nested and ordinary sandbox roots or wrong cwd", () =>
                {
                    foreach (var candidate in new[] { nativeRoot + "/../" + Path.GetFileName(nativeRoot), nativeRoot + "/nested", root, "/tmp/subtitle-guard-native-", "relative" })
                    {
                        RejectNative(candidate, nativeArguments, candidate);
                    }
                    RejectNative(nativeRoot, nativeArguments, root);
                    return Task.CompletedTask;
                });
                foreach (var flag in new[] { "--datadir", "--configdir", "--cachedir", "--logdir", "--nowebclient" })
                {
                    await Test("Native pilot rejects missing, duplicate or ambiguous " + flag, () =>
                    {
                        var position = Array.IndexOf(nativeArguments, flag);
                        var count = flag == "--nowebclient" ? 1 : 2;
                        RejectNative(nativeRoot, nativeArguments.Take(position).Concat(nativeArguments.Skip(position + count)).ToArray(), nativeRoot);
                        RejectNative(nativeRoot, nativeArguments.Concat(nativeArguments.Skip(position).Take(count)).ToArray(), nativeRoot);
                        RejectNative(nativeRoot, [.. nativeArguments, flag + "=false"], nativeRoot);
                        var changed = nativeArguments.ToArray();
                        changed[position] = flag.ToUpperInvariant();
                        RejectNative(nativeRoot, changed, nativeRoot);
                        if (count == 2)
                        {
                            changed = nativeArguments.ToArray();
                            changed[position + 1] = path;
                            RejectNative(nativeRoot, changed, nativeRoot);
                            RejectNative(nativeRoot, [.. nativeArguments, "--other", nativeArguments[position + 1]], nativeRoot);
                        }
                        else { RejectNative(nativeRoot, [.. nativeArguments, "false"], nativeRoot); }
                        return Task.CompletedTask;
                    });
                }
                await Test("Native pilot rejects short option overrides and argument terminators", () =>
                {
                    RejectNative(nativeRoot, [.. nativeArguments, "-d", root], nativeRoot);
                    RejectNative(nativeRoot, [nativeArguments[0], "--", .. nativeArguments[1..]], nativeRoot);
                    return Task.CompletedTask;
                });

                foreach (var (name, before, after) in new[]
                {
                    ("wrong port", "18097", "8096"),
                    ("disabled IPv4", "<EnableIPv4>true", "<EnableIPv4>false"),
                    ("enabled IPv6", "<EnableIPv6>false", "<EnableIPv6>true"),
                    ("remote access", "<EnableRemoteAccess>false", "<EnableRemoteAccess>true"),
                    ("discovery", "<EnableAutoDiscovery>false", "<EnableAutoDiscovery>true"),
                    ("public bind", "127.0.0.1", "0.0.0.0"),
                    ("additional bind", "</string>", "</string><string>192.0.2.1</string>"),
                    ("missing setting", "<EnableIPv4>true</EnableIPv4>", ""),
                    ("duplicate setting", "</InternalHttpPort>", "</InternalHttpPort><InternalHttpPort>8096</InternalHttpPort>"),
                    ("nil configuration", "<NetworkConfiguration>", "<NetworkConfiguration xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:nil=\"true\">"),
                    ("DTD", "<NetworkConfiguration>", "<!DOCTYPE NetworkConfiguration [<!ENTITY private SYSTEM 'file:///policy-sentinel-path'>]><NetworkConfiguration>"),
                    ("malformed XML", "</NetworkConfiguration>", "")
                })
                {
                    await Test("Native pilot rejects network XML " + name, () =>
                    {
                        try
                        {
                            WritePolicy(networkPath, networkXml.Replace(before, after, StringComparison.Ordinal));
                            RejectNative(nativeRoot, nativeArguments, nativeRoot);
                        }
                        finally { WritePolicy(networkPath, networkXml); }
                        return Task.CompletedTask;
                    });
                }
                await Test("Native pilot rejects missing, linked or nonprivate host files", () =>
                {
                    if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException(); }
                    foreach (var target in new[] { marker, networkPath, Path.Combine(nativeRoot, "config"), Path.Combine(nativeRoot, "data"), nativeRoot })
                    {
                        var saved = target + ".saved";
                        var directory = Directory.Exists(target);
                        if (directory) { Directory.Move(target, saved); } else { File.Move(target, saved); }
                        try
                        {
                            RejectNative(nativeRoot, nativeArguments, nativeRoot);
                            if (directory) { Directory.CreateSymbolicLink(target, saved); } else { File.CreateSymbolicLink(target, saved); }
                            RejectNative(nativeRoot, nativeArguments, nativeRoot);
                        }
                        finally
                        {
                            if (directory)
                            {
                                if (Directory.Exists(target)) { Directory.Delete(target); }
                                Directory.Move(saved, target);
                            }
                            else
                            {
                                File.Delete(target);
                                File.Move(saved, target);
                            }
                        }
                    }
                    foreach (var target in new[] { nativeRoot, networkPath })
                    {
                        var mode = File.GetUnixFileMode(target);
                        try
                        {
                            File.SetUnixFileMode(target, mode | UnixFileMode.OtherRead);
                            RejectNative(nativeRoot, nativeArguments, nativeRoot);
                        }
                        finally { File.SetUnixFileMode(target, mode); }
                    }
                    return Task.CompletedTask;
                });
            }
            finally { Directory.Delete(nativeRoot, true); }

            await Test("Source policy registration and validation make no HTTP, DNS or socket connections", () =>
            {
                Check(network.Starts == 0, "Policy checks started network I/O.");
                return Task.CompletedTask;
            });
        }
        finally
        {
            for (var index = 0; index < names.Length; index++) { Environment.SetEnvironmentVariable(names[index], previous[index]); }
            Directory.Delete(root, true);
        }
    }

    private static string PolicyJson(long bytes = 67108865, int seconds = 47) => JsonSerializer.Serialize(new
    {
        AllowedUrls = new[] { PolicyUrl }, AllowedRedirectOrigins = new[] { PolicyRedirectOrigin },
        MaxBytes = bytes, TimeoutSeconds = seconds
    });

    private static void WritePolicy(string path, string json)
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException(); }
        File.WriteAllText(path, json, new UTF8Encoding(false));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static void AssertPolicyRegistrationRejected(params string[] privateValues)
    {
        var original = new Mock<ISubtitleEncoder>(MockBehavior.Strict);
        var host = new Mock<IServerApplicationHost>(MockBehavior.Strict);
        var services = new ServiceCollection();
        services.AddSingleton(original.Object);
        services.AddSingleton(TimeProvider.System);
        AssertPolicyRejected<InvalidOperationException>(services, () => new Registrator().RegisterServices(services, host.Object), privateValues);
        using var provider = services.BuildServiceProvider();
        Check(ReferenceEquals(provider.GetRequiredService<ISubtitleEncoder>(), original.Object), "Rejected policy replaced the original encoder.");
        original.VerifyNoOtherCalls();
        host.VerifyNoOtherCalls();
    }

    private static void AssertPolicyRejected<TException>(IServiceCollection services, Action action, params string[] privateValues) where TException : Exception
    {
        var before = services.ToArray();
        Exception? rejection = null;
        try { action(); }
        catch (Exception exception) { rejection = exception; }
        Check(before.SequenceEqual(services), "Policy rejection mutated the original service descriptors.");
        Check(rejection is TException, "Policy did not throw the expected rejection type.");
        Check(rejection!.InnerException is null, "Policy rejection retained an inner exception.");
        Check(new[] { PolicyUrl, PolicyToken, "policy-sentinel-path" }.Concat(privateValues)
            .Where(value => !string.IsNullOrEmpty(value)).All(value => !rejection.ToString().Contains(value, StringComparison.OrdinalIgnoreCase)),
            "Policy rejection leaked a sentinel URL, token or private path.");
    }

    private static async Task<int> PolicyNetworkRefusal()
    {
        if (!OperatingSystem.IsLinux() || !NetworkInterface.GetAllNetworkInterfaces()
            .Any(network => network.NetworkInterfaceType != NetworkInterfaceType.Loopback))
        {
            Console.Error.WriteLine("Network-refusal proof requires Linux with a non-loopback interface; loopback-only is not a passing test.");
            return 2;
        }
        var names = new[] { "SUBTITLE_GUARD_SANDBOX", "SUBTITLE_GUARD_TEST_ORIGIN", "SUBTITLE_GUARD_SOURCE_POLICY", "SUBTITLE_GUARD_NATIVE_PILOT" };
        var previous = names.Select(Environment.GetEnvironmentVariable).ToArray();
        var root = Path.Combine("/tmp", "subtitle-guard-policy-sentinel-path-" + Guid.NewGuid().ToString("N"));
        var created = false;
        try
        {
            Check(new DirectoryInfo("/tmp").LinkTarget is null && !Path.Exists(root), "Refusing an unsafe temporary root.");
            Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            created = true;
            using (File.Open(Path.Combine(root, ".subtitle-guard-sandbox"), FileMode.CreateNew, FileAccess.Write)) { }
            var path = Path.Combine(root, "policy.json");
            WritePolicy(path, PolicyJson());
            Environment.SetEnvironmentVariable(names[0], root);
            Environment.SetEnvironmentVariable(names[1], null);
            Environment.SetEnvironmentVariable(names[2], path);
            Environment.SetEnvironmentVariable(names[3], null);
            using var network = new PolicyNetworkEvents();
            await Test("Source policy refuses non-loopback registration before DI mutation", () =>
            {
                AssertPolicyRegistrationRejected(root, path);
                return Task.CompletedTask;
            });
            var sandbox = new Sandbox(root) { PilotUrls = [PolicyUrl], PilotRedirectOrigins = [PolicyRedirectOrigin] };
            var guard = typeof(Sandbox).GetProperty("RequireLoopbackNetwork", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Check(guard is not null, "Per-URI network guard is absent.");
            guard!.SetValue(sandbox, true);
            var services = new ServiceCollection();
            services.AddSingleton(new Mock<ISubtitleEncoder>(MockBehavior.Strict).Object);
            foreach (var redirect in new[] { false, true })
            {
                await Test(redirect ? "Source policy rechecks network isolation on redirects" : "Source policy rechecks network isolation on initial URLs", () =>
                {
                    AssertPolicyRejected<NotSupportedException>(services,
                        () => { if (redirect) { sandbox.ValidateRedirect(PolicyUrl, PolicyRedirectOrigin + "/file"); } else { sandbox.ValidateInput(PolicyUrl); } }, root, path);
                    return Task.CompletedTask;
                });
            }
            await Test("Network-refusal proof makes no HTTP, DNS or socket connections", () =>
            {
                Check(network.Starts == 0, "Network-refusal proof started network I/O.");
                return Task.CompletedTask;
            });
            Console.WriteLine($"RESULT: {_passed} passed, {_failed} failed");
            return _failed == 0 ? 0 : 1;
        }
        catch (Exception)
        {
            Console.Error.WriteLine("Network-refusal proof setup failed.");
            return 2;
        }
        finally
        {
            for (var index = 0; index < names.Length; index++) { Environment.SetEnvironmentVariable(names[index], previous[index]); }
            if (created) { Directory.Delete(root, true); }
        }
    }

    private sealed class PolicyNetworkEvents : EventListener
    {
        private int _starts;
        public int Starts => Volatile.Read(ref _starts);

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name is "System.Net.Http" or "System.Net.NameResolution" or "System.Net.Sockets")
            {
                EnableEvents(source, EventLevel.Informational);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs data)
        {
            if (data.EventName is "RequestStart" or "ResolutionStart" or "ConnectStart") { Interlocked.Increment(ref _starts); }
        }
    }

    private static async Task<int> RunBitmap(string root, string ffmpeg, string sup)
    {
        try
        {
            if (!OperatingSystem.IsLinux())
            {
                throw new PlatformNotSupportedException("Bitmap checks require Linux.");
            }
            Check(NetworkInterface.GetAllNetworkInterfaces().All(network => network.NetworkInterfaceType == NetworkInterfaceType.Loopback),
                "Bitmap checks require an isolated Linux network namespace.");
            Check(Path.IsPathFullyQualified(root) && root == Path.GetFullPath(root)
                && Path.GetDirectoryName(root) == "/tmp" && Path.GetFileName(root).StartsWith("subtitle-guard-bitmap-", StringComparison.Ordinal),
                "Expected a canonical direct /tmp/subtitle-guard-bitmap-* root.");
            Check(File.GetUnixFileMode(root) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute),
                "Bitmap root must be private mode 0700.");
            var sandbox = new Sandbox(root);
            Check(sandbox.ValidatePath(sup) == sup && Path.GetDirectoryName(sup) == root
                && Path.GetExtension(sup) == ".sup" && new FileInfo(sup).Length is > 0 and <= 16 * 1024 * 1024,
                "Expected a nonempty private SUP copy, at most 16 MiB, directly inside the marked root.");
            Check(Path.IsPathFullyQualified(ffmpeg) && File.Exists(ffmpeg), "Expected an absolute local FFmpeg executable.");
            var ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe");
            Check(File.Exists(ffprobe), "FFprobe must accompany FFmpeg.");
            Check(Directory.GetDirectories(root).Length == 0
                && Directory.GetFiles(root).Select(Path.GetFileName).Order().SequenceEqual(new[] { ".subtitle-guard-sandbox", Path.GetFileName(sup) }.Order()),
                "Refusing a reused root: only the marker and private SUP copy may exist.");
            var sourceHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(sup)));
            var sourceModified = File.GetLastWriteTimeUtc(sup);
            var moviePath = Path.Combine(root, "mixed.mkv");
            var textPath = Path.Combine(root, "english.ass");
            await File.WriteAllTextAsync(textPath, Ass("EN"), new UTF8Encoding(false));
            await RunFfmpeg(ffmpeg, Path.Combine(root, "fixture.stderr.txt"),
                "-nostdin", "-hide_banner", "-loglevel", "error", "-n", "-protocol_whitelist", "file,pipe",
                "-f", "lavfi", "-i", "color=c=black:s=160x90:r=10:d=12",
                "-f", "lavfi", "-i", "anullsrc=r=48000:cl=mono:d=12",
                "-i", textPath, "-i", sup,
                "-map", "0:v:0", "-map", "1:a:0", "-map", "2:s:0", "-map", "3:s:0",
                "-c:v", "ffv1", "-threads", "1", "-c:a", "pcm_s16le", "-c:s", "copy",
                "-metadata:s:s:0", "language=eng", "-metadata:s:s:1", "language=nld", moviePath);

            async Task<JsonElement> Probe(string path, string name)
            {
                var output = Path.Combine(root, name + ".json");
                await RunFfmpeg(ffprobe, Path.Combine(root, name + ".stderr.txt"),
                    "-v", "error", "-protocol_whitelist", "file,pipe", "-count_packets", "-show_streams", "-show_packets",
                    "-show_data_hash", "sha256", "-show_entries",
                    "stream=index,codec_name,codec_type,width,height,nb_read_packets:stream_tags=language:packet=stream_index,size,data_hash",
                    "-of", "json", "-o", output, path);
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(output));
                return document.RootElement.Clone();
            }

            static string[] Packets(JsonElement probe, int index) => probe.GetProperty("packets").EnumerateArray()
                .Where(packet => packet.GetProperty("stream_index").GetInt32() == index)
                .Select(packet => packet.GetProperty("size").GetString() + "|" + packet.GetProperty("data_hash").GetString()).ToArray();

            var original = await Probe(sup, "source-probe");
            var mixed = await Probe(moviePath, "mixed-probe");
            var originalStream = original.GetProperty("streams").EnumerateArray().Single();
            var originalPackets = Packets(original, originalStream.GetProperty("index").GetInt32());
            var mixedPackets = mixed.GetProperty("packets").EnumerateArray()
                .Where(packet => packet.GetProperty("stream_index").GetInt32() == 3).ToArray();
            await Test("Real PGS fixture retains payload byte count beside video, audio and English ASS", () =>
            {
                Check(originalStream.GetProperty("codec_name").GetString() == "hdmv_pgs_subtitle" && originalPackets.Length > 0,
                    "Private SUP is not a nonempty PGS packet stream.");
                var streams = mixed.GetProperty("streams").EnumerateArray().ToArray();
                Check(streams.Select(stream => stream.GetProperty("index").GetInt32()).SequenceEqual(new[] { 0, 1, 2, 3 })
                    && streams.Select(stream => stream.GetProperty("codec_name").GetString()).SequenceEqual(new[] { "ffv1", "pcm_s16le", "ass", "hdmv_pgs_subtitle" })
                    && streams[2].GetProperty("tags").GetProperty("language").GetString() == "eng", "Mixed stream maps or language changed.");
                Check(mixedPackets.Length > 0
                    && mixedPackets.Sum(packet => long.Parse(packet.GetProperty("size").GetString()!))
                        == original.GetProperty("packets").EnumerateArray().Sum(packet => long.Parse(packet.GetProperty("size").GetString()!)),
                    "SUP-to-MKV copy changed total PGS payload bytes.");
                return Task.CompletedTask;
            });

            var mediaEncoder = new Mock<IMediaEncoder>(MockBehavior.Strict);
            mediaEncoder.SetupGet(encoder => encoder.EncoderPath).Returns(ffmpeg);
            var sources = new Mock<IMediaSourceManager>(MockBehavior.Strict);
            ISubtitleParser parser = new SubtitleEditParser(NullLogger<SubtitleEditParser>.Instance);
            using var encoder = new GuardedEncoder(sandbox, mediaEncoder.Object, sources.Object, parser);
            var english = Track(2, "eng");
            var bitmap = Track(3, "nld");
            bitmap.Codec = "hdmv_pgs_subtitle";
            var source = Source(moviePath, "private-real-pgs", english, bitmap);
            var item = new Video { Id = Guid.NewGuid(), Path = moviePath, Name = "Private bitmap fixture" };
            sources.Setup(manager => manager.GetPlaybackMediaSources(item, null!, false, false, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<MediaSourceInfo> { source });
            var bitmapPath = string.Empty;
            var extractedPacketCount = 0;
            await Test("Actual GuardedEncoder extracts mixed ASS and PGS in one cold process", async () =>
            {
                await encoder.ExtractAllExtractableSubtitles(source, CancellationToken.None);
                bitmapPath = await encoder.GetSubtitleFilePath(bitmap, source, CancellationToken.None);
                var englishPath = await encoder.GetSubtitleFilePath(english, source, CancellationToken.None);
                var generation = Generations(root).Single();
                Check(bitmapPath == Path.Combine(generation, "3.sup") && englishPath == Path.Combine(generation, "2.ass")
                    && Directory.GetFiles(generation).Order().SequenceEqual(new[] { englishPath, bitmapPath }.Order())
                    && new FileInfo(sandbox.ValidatePath(bitmapPath)).Length > 0, "Wrong, empty or missing per-track generation outputs.");
                AssertCues(Parse(parser, await File.ReadAllTextAsync(englishPath), "ass"), "EN", false, true);
                mediaEncoder.VerifyGet(mock => mock.EncoderPath, Times.Once());
            });

            await Test("Guarded PGS SUP has matching FFprobe headers and every original packet payload", async () =>
            {
                var extracted = await Probe(bitmapPath, "extracted-probe");
                var stream = extracted.GetProperty("streams").EnumerateArray().Single();
                extractedPacketCount = Packets(extracted, stream.GetProperty("index").GetInt32()).Length;
                foreach (var property in new[] { "codec_name", "codec_type", "width", "height", "nb_read_packets" })
                {
                    Check(stream.GetProperty(property).ToString() == originalStream.GetProperty(property).ToString(), "PGS metadata differs: " + property);
                }
                Check(originalPackets.SequenceEqual(Packets(extracted, stream.GetProperty("index").GetInt32())),
                    "Guarded SUP lost or changed PGS packet payloads/count/order (container timestamps excluded).");
            });

            await Test("GetSubtitles serves exact raw PGS bytes and complete ASS plus SRT/VTT conversion", async () =>
            {
                await using var raw = await encoder.GetSubtitles(item, source.Id, bitmap.Index, "sup", 0, 0, true, CancellationToken.None);
                using var delivered = new MemoryStream();
                await raw.CopyToAsync(delivered);
                var expected = await File.ReadAllBytesAsync(bitmapPath);
                Check(delivered.ToArray().SequenceEqual(expected), "Raw PGS delivery rewrote extracted bytes.");
                foreach (var format in new[] { "ass", "srt", "vtt" })
                {
                    var text = await ReadText(encoder.GetSubtitles(item, source.Id, english.Index, format, 0, 0, true, CancellationToken.None));
                    AssertCues(Parse(parser, text, format), "EN", false, true);
                }
                await Throws<NotSupportedException>(() => encoder.GetSubtitles(item, source.Id, bitmap.Index, "srt", 0, 0, true, CancellationToken.None));
                await Throws<NotSupportedException>(() => encoder.GetSubtitleFileCharacterSet(bitmap, bitmap.Language, source, CancellationToken.None));
            });

            await Test("Warm mixed-track requests keep hashes and timestamps with only one extraction process", async () =>
            {
                var before = Snapshot(root);
                await encoder.ExtractAllExtractableSubtitles(source, CancellationToken.None);
                foreach (var track in new[] { english, bitmap })
                {
                    _ = await encoder.GetSubtitleFilePath(track, source, CancellationToken.None);
                    await using var warm = await encoder.GetSubtitles(item, source.Id, track.Index, track == bitmap ? "sup" : "ass", 0, 0, true, CancellationToken.None);
                    await warm.CopyToAsync(Stream.Null);
                }
                Check(before.SequenceEqual(Snapshot(root)), "Warm requests changed output hashes or timestamps.");
                Check(sourceHash == Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(sup)))
                    && sourceModified == File.GetLastWriteTimeUtc(sup), "Private SUP input was modified.");
                mediaEncoder.VerifyGet(mock => mock.EncoderPath, Times.Once());
                mediaEncoder.VerifyNoOtherCalls();
            });
            await File.WriteAllTextAsync(Path.Combine(root, "bitmap-result.json"), JsonSerializer.Serialize(new
            {
                Root = root, SourceSha256 = sourceHash, SourceBytes = new FileInfo(sup).Length,
                PgsPackets = originalPackets.Length, MixedPgsPackets = mixedPackets.Length,
                ExtractedPgsPackets = extractedPacketCount, EnglishCues = 12, BitmapPath = bitmapPath,
                ExtractionProcesses = mediaEncoder.Invocations.Count, Passed = _passed, Failed = _failed,
                PluginSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(typeof(GuardedEncoder).Assembly.Location))),
                DvdRuntimeCovered = false
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Bitmap checks: {_passed} passed, {_failed} failed; PGS packets={originalPackets.Length}; evidence={root}");
            return _failed == 0 ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL bitmap setup: {exception}");
            return 1;
        }
    }

    private static MediaStream Track(int index, string language, string? external = null) => new()
    {
        Index = index,
        Type = MediaStreamType.Subtitle,
        Codec = "ass",
        Language = language,
        IsExternal = external is not null,
        Path = external ?? string.Empty
    };

    private static MediaSourceInfo Source(string path, string id, params MediaStream[] subtitles) => new()
    {
        Id = id,
        Path = path,
        Container = "mkv",
        Protocol = MediaProtocol.File,
        RunTimeTicks = 12 * TimeSpan.TicksPerSecond,
        MediaStreams = new List<MediaStream>
        {
            new() { Index = 0, Type = MediaStreamType.Video, Codec = "ffv1" },
            new() { Index = 1, Type = MediaStreamType.Audio, Codec = "pcm_s16le" }
        }.Concat(subtitles).ToList()
    };

    private static string Ass(string language)
    {
        var text = new StringBuilder("""
            [Script Info]
            ScriptType: v4.00+
            PlayResX: 160
            PlayResY: 90

            [V4+ Styles]
            Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
            Style: Accent,DejaVu Sans,22,&H0000FFFF,&H000000FF,&H00000000,&H00000000,-1,0,0,0,100,100,0,0,1,2,1,2,10,10,10,1

            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            """);
        text.Append('\n');
        for (var cue = 0; cue < 12; cue++)
        {
            text.AppendLine($"Dialogue: 0,0:00:{cue:D2}.20,0:00:{cue:D2}.80,Accent,,0,0,0,,{{\\i1}}{language} cue {cue + 1:D2}{{\\i0}} caf\u00e9");
        }

        return text.ToString();
    }

    private static Subtitle Parse(ISubtitleParser parser, string text, string format)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        return parser.Parse(stream, format);
    }

    private static void AssertCues(Subtitle subtitle, string language, bool bounded, bool preserve)
    {
        var first = bounded ? 3 : 0;
        var count = bounded ? 3 : 12;
        Check(subtitle.Paragraphs.Count == count, $"Expected {count} cues, got {subtitle.Paragraphs.Count}.");
        for (var position = 0; position < count; position++)
        {
            var paragraph = subtitle.Paragraphs[position];
            var cue = first + position;
            var shift = bounded && !preserve ? StartTicks : 0;
            Check(paragraph.Text.Contains($"{language} cue {cue + 1:D2}", StringComparison.Ordinal), "Wrong track, cue order or missing dialogue.");
            Check(paragraph.StartTime.TimeSpan.Ticks == cue * TimeSpan.TicksPerSecond + TimeSpan.TicksPerSecond / 5 - shift, "Incorrect cue start/offset.");
            Check(paragraph.EndTime.TimeSpan.Ticks == cue * TimeSpan.TicksPerSecond + 4 * TimeSpan.TicksPerSecond / 5 - shift, "Incorrect cue end/offset.");
        }
    }

    private static void AssertJson(string text, bool bounded, bool preserve)
    {
        using var json = JsonDocument.Parse(text);
        var events = Property(json.RootElement, "TrackEvents");
        var first = bounded ? 3 : 0;
        var count = bounded ? 3 : 12;
        Check(events.GetArrayLength() == count, "Incorrect JSON cue count.");
        for (var position = 0; position < count; position++)
        {
            var cue = first + position;
            var shift = bounded && !preserve ? StartTicks : 0;
            Check(Property(events[position], "Text").GetString()!.Contains($"EN cue {cue + 1:D2}", StringComparison.Ordinal), "Incorrect JSON cue text.");
            Check(Property(events[position], "StartPositionTicks").GetInt64() == cue * TimeSpan.TicksPerSecond + TimeSpan.TicksPerSecond / 5 - shift,
                "Incorrect JSON start/offset.");
            Check(Property(events[position], "EndPositionTicks").GetInt64() == cue * TimeSpan.TicksPerSecond + 4 * TimeSpan.TicksPerSecond / 5 - shift,
                "Incorrect JSON end/offset.");
        }
    }

    private static JsonElement Property(JsonElement element, string name) => element.EnumerateObject()
        .Single(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static async Task<string> ReadText(Task<Stream> pending)
    {
        await using var stream = await pending;
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        return await reader.ReadToEndAsync();
    }

    private static string[] Generations(string root)
    {
        var cache = Path.Combine(root, "guard-cache");
        var directories = Directory.Exists(cache) ? Directory.GetDirectories(cache) : Array.Empty<string>();
        Check(!directories.Any(path => Path.GetFileName(path).StartsWith(".pending-", StringComparison.Ordinal)), "Staging directory survived a completed request.");
        return directories.Order(StringComparer.Ordinal).ToArray();
    }

    private static string[] Snapshot(string root) => Generations(root)
        .SelectMany(directory => Directory.GetFiles(directory))
        .Order(StringComparer.Ordinal)
        .Select(path => $"{path}|{File.GetLastWriteTimeUtc(path).Ticks}|{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}")
        .ToArray();

    internal static async Task RunFfmpeg(string executable, string log, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }

            await process.WaitForExitAsync();
            throw;
        }
        finally
        {
            await Task.WhenAll(stdout, stderr);
            await File.WriteAllTextAsync(log, await stderr);
        }

        Check(process.ExitCode == 0, $"Synthetic FFmpeg fixture failed: exit {process.ExitCode}; see {log}.");
    }

    private static async Task Test(string name, Func<Task> action)
    {
        try
        {
            await action();
            _passed++;
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception exception)
        {
            _failed++;
            Console.Error.WriteLine($"FAIL {name}: {exception}");
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static async Task Throws<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
}