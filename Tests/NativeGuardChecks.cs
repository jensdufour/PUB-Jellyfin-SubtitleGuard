using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using HarmonyLib;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Streaming;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.MediaEncoding.Subtitles;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SubtitleGuard;

if (args.Length == 2 && args[0] == "--hls-hooks")
{
    await CheckHlsHooks(Path.GetFullPath(args[1]));
    return;
}

if (args.Length == 2 && args[0] == "--window-contract")
{
    await CheckWindowContract(args[1]);
    return;
}

if (args.Length == 2 && args[0] == "--coverage-contract")
{
    await CheckCoverageContract(Path.GetFullPath(args[1]));
    return;
}

if (args.Length == 1 && args[0] == "--coverage-parser")
{
    CheckCoverageParser();
    return;
}

await using var registered = args.Contains("--collectible") ? await RegisterCollectible() : null;
await CheckNativeBehavior(registered is not null);

static async Task CheckHlsHooks(string host)
{
    AssemblyLoadContext.Default.Resolving += (_, name) =>
        File.Exists(Path.Combine(host, name.Name + ".dll"))
            ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(host, name.Name + ".dll")) : null;
    AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(host, "Jellyfin.Api.dll"));
    var controller = Type.GetType("Jellyfin.Api.Controllers.DynamicHlsController, Jellyfin.Api", throwOnError: true)!;
    var flags = BindingFlags.Instance | BindingFlags.NonPublic;
    var command = controller.GetMethod("GetCommandLineArguments", flags, [typeof(string), typeof(MediaBrowser.Controller.Streaming.StreamState), typeof(bool), typeof(int)]);
    var video = controller.GetMethod("GetVideoArguments", flags, [typeof(MediaBrowser.Controller.Streaming.StreamState), typeof(int), typeof(bool), typeof(string)]);
    Check(command?.ReturnType == typeof(string) && video?.ReturnType == typeof(string), "native HLS command and video hooks have exact supported signatures");
    var playlist = controller.GetMethod("GetVariantPlaylistInternal", flags)!;
    var move = playlist.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType.GetMethod("MoveNext", flags);
    Check(move is not null, "native variant playlist exposes its awaited async state machine");
    var window = typeof(NativeExtractionGuard).Assembly.GetType("SubtitleGuard.WindowedBurnIn", throwOnError: true)!;
    var clock = window.GetMethod("PlaylistClock", BindingFlags.Static | BindingFlags.NonPublic)!;
    var getter = typeof(EncodingJobInfo).GetProperty(nameof(EncodingJobInfo.TargetFramerate))!.GetMethod!;
    var replacement = window.GetMethod("PlaylistFramerate", BindingFlags.Static | BindingFlags.NonPublic)!;
    var original = PatchProcessor.GetOriginalInstructions(move!).ToList();
    Check(original.Count(instruction => instruction.Calls(getter)) == 1, "native variant playlist has exactly one target-framerate getter");
    var patched = ((IEnumerable<CodeInstruction>)clock.Invoke(null, [original.Select(instruction => new CodeInstruction(instruction)).ToList()])!).ToList();
    Check(patched.Count(instruction => instruction.Calls(replacement)) == 1 && !patched.Any(instruction => instruction.Calls(getter)), "playlist transpiler replaces exactly the native getter");
    foreach (var invalid in new[] { new List<CodeInstruction> { new(OpCodes.Nop) }, new List<CodeInstruction> { new(OpCodes.Callvirt, getter), new(OpCodes.Callvirt, getter) } })
    {
        try { _ = ((IEnumerable<CodeInstruction>)clock.Invoke(null, [invalid])!).ToList(); }
        catch (TargetInvocationException exception) when (exception.InnerException is NotSupportedException)
        {
            Check(invalid.All(instruction => !instruction.Calls(replacement)), "unsupported getter count fails closed without partial replacement");
            continue;
        }
        throw new InvalidOperationException("Unsupported playlist getter count was accepted.");
    }
    await CheckPlaylistEligibility(window, move!, replacement);
    Console.WriteLine("CONTRACT native playlist calls: " + string.Join("; ", original.Select(instruction => instruction.operand).OfType<MethodInfo>().Where(value => value.Name.Contains("Segment", StringComparison.OrdinalIgnoreCase) || value.Name.Contains("Playlist", StringComparison.OrdinalIgnoreCase)).Distinct().Select(value => value.ToString())));
    Console.WriteLine("CONTRACT native encoder calls: " + string.Join("; ", PatchProcessor.GetOriginalInstructions(video!).Select(instruction => instruction.operand).OfType<MethodInfo>().Where(value => value.DeclaringType == typeof(EncodingHelper)).Distinct().Select(value => value.ToString())));
}

static async Task CheckPlaylistEligibility(Type window, MethodInfo move, MethodInfo replacement)
{
    var flags = BindingFlags.Static | BindingFlags.NonPublic;
    var active = window.GetField("_active", flags)!;
    var canPrepare = window.GetMethod("CanPrepare", flags)!;
    var root = Path.Combine(Path.GetTempPath(), "subtitleguard-hls-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var savedSwitch = Environment.GetEnvironmentVariable("SUBTITLEGUARD_WINDOWED_ASS");
    var previousActive = active.GetValue(null);
    var path = Path.Combine(root, "native.ass");
    var paths = new Mock<IApplicationPaths>();
    paths.SetupGet(value => value.CachePath).Returns(root);
    var manager = new Mock<IPathManager>();
    manager.Setup(value => value.GetSubtitlePath(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>())).Returns(path);
    var services = new ServiceCollection().AddSingleton(paths.Object).AddSingleton(manager.Object)
        .AddSingleton(Mock.Of<IMediaEncoder>()).BuildServiceProvider();
    var logger = Activator.CreateInstance(typeof(NullLogger<>).MakeGenericType(window))!;
    var instance = Activator.CreateInstance(window, [services, logger])!;
    try
    {
        Environment.SetEnvironmentVariable("SUBTITLEGUARD_WINDOWED_ASS", "1");
        await ((IHostedService)instance).StartAsync(CancellationToken.None);
        if (OperatingSystem.IsLinux())
        {
            Check(ReferenceEquals(active.GetValue(null), instance), "Linux hosted-service startup activates windowed burn-in");
            Check(Harmony.GetPatchInfo(move)?.Owners.Contains("jensdufour.subtitleguard.windowed-burn-in") == true,
                "Linux startup applies the native async playlist transpiler");
            var current = PatchProcessor.GetCurrentInstructions(move).ToList();
            Check(current.Count(instruction => instruction.Calls(replacement)) == 1, "installed native playlist contains exactly one replacement clock");
        }
        else
        {
            Check(active.GetValue(null) is null && Harmony.GetPatchInfo(move)?.Owners.Contains("jensdufour.subtitleguard.windowed-burn-in") != true,
                "non-Linux hosted-service startup rejects windowed hooks");
            var cacheType = window.Assembly.GetType("SubtitleGuard.AssWindowCache", throwOnError: true)!;
            window.GetField("_cache", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance,
                Activator.CreateInstance(cacheType, [(Func<string>)(() => string.Empty), root, CancellationToken.None]));
            window.GetField("_pathManager", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, manager.Object);
            active.SetValue(null, instance);
            Console.WriteLine("LIMIT Windows: reflection-scoped helper fixtures only; Linux activation remains unverified here.");
        }

        using var state = new StreamState(Mock.Of<IMediaSourceManager>(), default, Mock.Of<ITranscodeManager>());
        state.BaseRequest = new StreamingRequestDto { SegmentLength = 6 };
        state.MediaSource = new MediaSourceInfo { Id = "fixture", Path = "https://example.invalid/fixture.mkv", RunTimeTicks = TimeSpan.FromMinutes(5).Ticks };
        state.VideoStream = new MediaStream { Type = MediaStreamType.Video, Index = 0, RealFrameRate = 30, AverageFrameRate = 23.333f };
        state.SubtitleStream = new MediaStream { Type = MediaStreamType.Subtitle, Index = 1, Codec = "ass" };
        state.SubtitleDeliveryMethod = SubtitleDeliveryMethod.Encode;
        state.OutputVideoCodec = "h264";
        Set("MediaPath", state.MediaSource.Path);
        Set("RunTimeTicks", state.MediaSource.RunTimeTicks);
        Check(state.SegmentLength == 6, "native fixture selects six-second HLS segments");
        var originalSubtitle = state.SubtitleStream;
        Check(state.VideoStream.ReferenceFrameRate == 23.333f, "native metadata reference rate is 23.333 while real rate is 30");
        state.BaseRequest.MaxFramerate = 23.333f;
        Check(state.TargetFramerate == 23.333f && Eligible() && Rate() == 30,
            "integer source clock replaces mismatched native target without genuine maximum-FPS conversion");
        var frameMethod = typeof(EncodingHelper).GetMethod("GetFramerateParam", BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        var frameHelper = frameMethod.IsStatic ? null : RuntimeHelpers.GetUninitializedObject(typeof(EncodingHelper));
        Check(!PatchProcessor.GetOriginalInstructions(frameMethod).Any(instruction => instruction.opcode == OpCodes.Ldfld
            && instruction.operand is FieldInfo field && field.DeclaringType == typeof(EncodingHelper)),
            "native framerate helper reads request state without instance dependencies");
        Check(frameMethod.Invoke(frameHelper, [state]) is null, "native encoder applies no FPS conversion when reference equals maximum");
        foreach (var rate in new[] { 23.976f, 29.97f })
        {
            state.VideoStream.RealFrameRate = rate;
            Check(!Eligible() && Rate() == state.TargetFramerate, "fractional source keeps native clock: " + rate);
        }
        state.VideoStream.RealFrameRate = 30;
        Reject(() => state.MediaPath = "/fixture.mkv", () => state.MediaPath = state.MediaSource.Path, "local source");
        Reject(() => originalSubtitle.IsExternal = true, () => originalSubtitle.IsExternal = false, "external subtitle");
        Reject(() => originalSubtitle.Codec = "srt", () => originalSubtitle.Codec = "ass", "non-ASS subtitle");
        Reject(() => state.SubtitleDeliveryMethod = SubtitleDeliveryMethod.External, () => state.SubtitleDeliveryMethod = SubtitleDeliveryMethod.Encode, "direct subtitle delivery");
        Reject(() => state.OutputVideoCodec = "copy", () => state.OutputVideoCodec = "h264", "copied video");
        Reject(() => state.RunTimeTicks = null, () => state.RunTimeTicks = TimeSpan.FromMinutes(5).Ticks, "live source without runtime");
        foreach (var length in new[] { 0, 21 }) Reject(() => state.Request.SegmentLength = length, () => state.Request.SegmentLength = 6, "unsupported segment duration " + length);
        Reject(() => state.BaseRequest.Framerate = 24, () => state.BaseRequest.Framerate = null, "fixed FPS conversion");
        Reject(() => state.BaseRequest.MaxFramerate = 20, () => state.BaseRequest.MaxFramerate = 23.333f, "maximum FPS conversion");
        File.WriteAllText(path, "complete native fixture");
        Check(!Eligible() && Rate() == state.TargetFramerate, "good native cache preserves original clock and fast path");
        File.WriteAllText(path + ".subtitleguard.pending", "");
        Check(Eligible() && Rate() == 30, "marked native cache remains eligible for bounded preparation");
        File.Delete(path + ".subtitleguard.pending");
        File.Delete(path);
        Console.WriteLine("CONTRACT state hydration passed against " + typeof(StreamState).Assembly.GetName().Version);

        bool Eligible() => (bool)canPrepare.Invoke(null, [state])!;
        float? Rate() => (float?)replacement.Invoke(null, [state]);
        void Reject(Action change, Action restore, string name)
        {
            change();
            try { Check(!Eligible() && Rate() == state.TargetFramerate, name + " retains native fallback clock"); }
            finally { restore(); }
        }
        void Set(string name, object? value)
        {
            var property = typeof(StreamState).GetProperty(name)!;
            Check(property.SetMethod is not null, "native fixture exposes typed " + name + " hydration");
            property.SetValue(state, value);
        }
    }
    finally
    {
        await ((IHostedService)instance).StopAsync(CancellationToken.None);
        ((IDisposable)instance).Dispose();
        active.SetValue(null, previousActive);
        Environment.SetEnvironmentVariable("SUBTITLEGUARD_WINDOWED_ASS", savedSwitch);
        services.Dispose();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}

static async Task CheckWindowContract(string ffmpeg)
{
    var root = Path.Combine(Path.GetTempPath(), "subtitleguard-contract-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var source = Path.Combine(root, "source.mkv");
        var subtitles = Path.Combine(root, "source.ass");
        await File.WriteAllTextAsync(subtitles, """
            [Script Info]
            ScriptType: v4.00+
            PlayResX: 160
            PlayResY: 90
            [V4+ Styles]
            Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
            Style: Default,DejaVu Sans,16,&H00FFFFFF,&H00FFFFFF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,1,0,2,10,10,10,1
            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: 0,0:00:02.50,0:00:04.50,Default,,0,0,0,,BOUNDARY
            Dialogue: 0,0:00:05.00,0:00:07.00,Default,,0,0,0,,LATER
            """ + "\n");
        await Run(["-f", "lavfi", "-i", "color=c=black:s=160x90:r=10:d=12", "-i", subtitles,
            "-map", "0:v:0", "-map", "1:s:0", "-c:v", "ffv1", "-g", "30", "-c:s", "ass", "-t", "12", source]);
        var type = typeof(NativeExtractionGuard).Assembly.GetType("SubtitleGuard.AssWindowCache", throwOnError: true)!;
        var resolvedEncoder = string.Empty;
        var pathReads = 0;
        Func<string> encoderPath = () => { pathReads++; return resolvedEncoder; };
        var cache = Activator.CreateInstance(type, [encoderPath, root, CancellationToken.None])!;
        Check(pathReads == 0, "cache construction does not read the encoder path before native startup");
        resolvedEncoder = ffmpeg;
        var extract = type.GetMethod("ExtractAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var first = Path.Combine(root, "first.ass");
        await (Task)extract.Invoke(cache, [source, 1, 0, new Dictionary<string, string>(), 0L, TimeSpan.FromSeconds(3).Ticks, false, first, CancellationToken.None])!;
        Check(pathReads == 1, "preparation resolves the initialized native encoder path on demand");
        var firstText = await File.ReadAllTextAsync(first);
        Check(firstText.Contains("0:00:02.50,0:00:04.50", StringComparison.Ordinal), "bounded extraction preserves crossing-cue timestamps");
        Check(!firstText.Contains("LATER", StringComparison.Ordinal), "input duration does not read future subtitle windows");
        var second = Path.Combine(root, "second.ass");
        await (Task)extract.Invoke(cache, [source, 1, 0, new Dictionary<string, string>(), TimeSpan.FromSeconds(3).Ticks, TimeSpan.FromSeconds(6).Ticks, false, second, CancellationToken.None])!;
        Check((await File.ReadAllTextAsync(second)).Contains("0:00:05.00,0:00:07.00", StringComparison.Ordinal), "seeked extraction keeps absolute ASS times and proves video coverage");
        var video = await Run(["-ss", "3", "-copyts", "-i", source, "-map", "0:v:0", "-c:v", "rawvideo",
            "-start_at_zero", "-to", "6", "-f", "framecrc", "pipe:1"]);
        var frames = video.Split('\n').Count(line => line.StartsWith("0,", StringComparison.Ordinal));
        Check(frames == 30, $"absolute output end caps a seeked burn-in job at three seconds ({frames} frames)");
        Console.WriteLine("Window preparation FFmpeg contract passed; synthetic local media only.");

        async Task<string> Run(string[] arguments)
        {
            var info = new System.Diagnostics.ProcessStartInfo(ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-y" }.Concat(arguments)) info.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(info)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            try { await Task.WhenAll(output, errors, process.WaitForExitAsync(timeout.Token)); }
            finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
            if (process.ExitCode != 0 || (await errors).Length > 0) throw new IOException("Synthetic FFmpeg contract command failed: " + await errors);
            return await output;
        }
    }
    finally { Directory.Delete(root, true); }
}

static async Task<ServiceProvider> RegisterCollectible()
{
    Check(!AssemblyLoadContext.Default.Assemblies.Any(assembly => assembly.GetName().Name == "0Harmony"), "Harmony is not preloaded in the default context");
    var context = new AssemblyLoadContext("jellyfin-plugin-check", isCollectible: true);
    context.LoadFromAssemblyPath(Path.Combine(AppContext.BaseDirectory, "0Harmony.dll"));
    var plugin = context.LoadFromAssemblyPath(Path.Combine(AppContext.BaseDirectory, "Jellyfin.Plugin.SubtitleGuard.Prototype.dll"));
    Check(plugin.IsCollectible, "plugin is loaded in Jellyfin's collectible context");
    var services = new ServiceCollection();
    services.AddLogging();
    var registrator = (IPluginServiceRegistrator)Activator.CreateInstance(plugin.GetType("SubtitleGuard.Registrator")!)!;
    registrator.RegisterServices(services, null!);
    var provider = services.BuildServiceProvider();
    foreach (var service in provider.GetServices<IHostedService>()) await service.StartAsync(CancellationToken.None);
    return provider;
}

static async Task CheckNativeBehavior(bool registered)
{
    if (registered)
    {
        Check(Harmony.GetAllPatchedMethods().Count(method => Harmony.GetPatchInfo(method)?.Owners.Contains("jensdufour.subtitleguard.native-extraction") == true) == 6,
            "collectible plugin registration installs all six native extraction guards");
        Check(!typeof(Harmony).Assembly.IsCollectible && !typeof(NativeExtractionGuard).Assembly.IsCollectible, "patch runtime is non-collectible");
    }

    var root = Path.Combine(Path.GetTempPath(), "subtitle-guard-native-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    using var guard = new NativeExtractionGuard();
    var simulated = new Harmony("subtitleguard.local-checks");
    try
    {
        if (!registered) guard.Install();
        var encoderType = typeof(SubtitleEncoder);
        var methods = encoderType.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic);
        var process = methods.Single(method => method.Name == "RunSubtitleExtractionProcess");
        var extract = methods.Single(method => method.Name == "ExtractSubtitlesForFile");
        var fresh = methods.Single(method => method.Name == "IsCachedSubtitleFresh");
        var read = methods.Single(method => method.Name == "GetReadableFile");
        simulated.Patch(process, prefix: new HarmonyMethod(typeof(Simulation).GetMethod(nameof(Simulation.Process))!));
        simulated.Patch(read, prefix: new HarmonyMethod(typeof(Simulation).GetMethod(nameof(Simulation.Read))!));
        var fileSystem = new Mock<IFileSystem>();
        fileSystem.Setup(value => value.DeleteFile(It.IsAny<string>())).Callback<string>(File.Delete);
        fileSystem.Setup(value => value.GetFileInfo(It.IsAny<string>())).Returns((string path) => new FileSystemMetadata
        {
            Exists = File.Exists(path),
            Length = File.Exists(path) ? new FileInfo(path).Length : 0
        });
        var encoder = new SubtitleEncoder(NullLogger<SubtitleEncoder>.Instance, fileSystem.Object,
            Mock.Of<IMediaEncoder>(), Mock.Of<IHttpClientFactory>(), Mock.Of<IMediaSourceManager>(),
            Mock.Of<ISubtitleParser>(), Mock.Of<IPathManager>(), Mock.Of<IServerConfigurationManager>());
        var outputs = new[] { Path.Combine(root, "english.srt"), Path.Combine(root, "dutch.srt") };
        Simulation.Paths = outputs;
        Simulation.Result = Task.FromResult((0, string.Empty));
        await Extract();
        Check(Simulation.LastArguments == "simulated", "extraction without source coverage keeps native arguments unchanged");
        Check(outputs.All(File.Exists) && outputs.All(path => !File.Exists(path + ".subtitleguard.pending")), "successful native extraction publishes all tracks");
        Check((bool)fresh.Invoke(encoder, [outputs[0], "https://example.invalid/movie.mkv"])!, "completed native cache remains reusable");

        var callsBeforeRetry = Simulation.ProcessCalls;
        Simulation.Results.Enqueue(Task.FromResult((0, "Stream ends prematurely")));
        Simulation.Results.Enqueue(Task.FromResult((0, string.Empty)));
        await Extract();
        Check(Simulation.ProcessCalls == callsBeforeRetry + 2, "transient native extraction retries exactly once");
        Check(outputs.All(path => !File.Exists(path + ".subtitleguard.pending")), "successful retry publishes completed native tracks");

        foreach (var result in new[] { (1, "decoder failure"), (255, "interrupted"), (0, "File ended prematurely"), (0, "Stream ends prematurely"), (0, "Connection reset by peer") })
        {
            Simulation.Result = Task.FromResult(result);
            await Fails(Extract, "nonempty partial output is rejected: " + result.Item1 + " / " + result.Item2);
            Check(outputs.All(path => !File.Exists(path)) && outputs.All(path => File.Exists(path + ".subtitleguard.pending")), "native failure cleanup retains incomplete markers");
        }

        Simulation.Result = Task.FromResult((0, "ordinary codec warning"));
        await Extract();
        Check(outputs.All(path => !File.Exists(path + ".subtitleguard.pending")), "later successful request repairs failed extraction");
        var completion = new TaskCompletionSource<(int, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        Simulation.Result = completion.Task;
        var inFlight = Extract();
        Check(outputs.All(path => File.Exists(path + ".subtitleguard.pending")), "in-progress extraction records durable markers before output");
        Check(!(bool)fresh.Invoke(encoder, [outputs[0], "https://example.invalid/movie.mkv"])!, "nonempty in-progress cache is not fresh");
        await Fails(async () => await (Task<SubtitleEncoder.SubtitleInfo>)read.Invoke(encoder, [new MediaSourceInfo(), new MediaStream(), CancellationToken.None])!, "read path refuses marked output even if native code returns it");
        completion.SetCanceled();
        await Fails(() => inFlight, "cancelled extraction is not published");

        guard.Dispose();
        guard.Install();
        Check(!(bool)fresh.Invoke(encoder, [outputs[0], "https://example.invalid/movie.mkv"])!, "interrupted markers survive guard restart");
        Simulation.Result = Task.FromResult((0, string.Empty));
        await Extract();
        Check((await (Task<SubtitleEncoder.SubtitleInfo>)read.Invoke(encoder, [new MediaSourceInfo(), new MediaStream(), CancellationToken.None])!).Path == outputs[0], "completed subtitle read succeeds after repair");

        var source = new MediaSourceInfo
        {
            Id = "remote-coverage-fixture",
            Path = "https://example.invalid/fixture.mkv",
            IsRemote = true,
            Protocol = MediaProtocol.Http,
            RunTimeTicks = TimeSpan.FromMinutes(1).Ticks,
            MediaStreams =
            [
                new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = "h264" },
                new MediaStream { Type = MediaStreamType.Subtitle, Index = 1, Codec = "srt", SupportsExternalStream = true },
                new MediaStream { Type = MediaStreamType.Subtitle, Index = 2, Codec = "srt", SupportsExternalStream = true }
            ]
        };
        Check(source.MediaStreams.Count(stream => stream.Type == MediaStreamType.Subtitle && stream.IsExtractableSubtitleStream && stream.SupportsExternalStream) == 2,
            "native hook fixture exposes two extractable subtitle streams");
        var strmRequestField = typeof(NativeExtractionGuard).GetField("StrmSubtitleRequest", BindingFlags.NonPublic | BindingFlags.Static)!;
        var strmRequest = (AsyncLocal<bool>)strmRequestField.GetValue(null)!;
        var beginCoverage = typeof(NativeExtractionGuard).GetMethod("BeginSourceCoverage", BindingFlags.NonPublic | BindingFlags.Static)!;
        var endCoverage = typeof(NativeExtractionGuard).GetMethod("EndSourceCoverage", BindingFlags.NonPublic | BindingFlags.Static)!;
        async Task ExtractWithCoverage()
        {
            var previousRequest = strmRequest.Value;
            strmRequest.Value = true;
            object?[] scope = [source, null];
            beginCoverage.Invoke(null, scope);
            try { await Extract(); }
            finally
            {
                endCoverage.Invoke(null, [scope[1]]);
                strmRequest.Value = previousRequest;
            }
        }

        Simulation.Result = Task.FromResult((0, string.Empty));
        Simulation.CoverageTicks.Clear();
        Simulation.CoverageStartTicks.Clear();
        Simulation.CoveragePaths.Clear();
        Simulation.CoverageStartTicks.Enqueue(TimeSpan.FromSeconds(9).Ticks);
        Simulation.CoverageTicks.Enqueue(TimeSpan.FromSeconds(30).Ticks);
        Simulation.CoverageStartTicks.Enqueue(TimeSpan.FromSeconds(9).Ticks);
        Simulation.CoverageTicks.Enqueue(TimeSpan.FromSeconds(30).Ticks);
        var beforeCoverageRetry = Simulation.ProcessCalls;
        await Fails(ExtractWithCoverage, "clean nonempty partial extraction is rejected after one coverage retry");
        Check(Simulation.ProcessCalls == beforeCoverageRetry + 2, "clean partial coverage receives exactly one source retry");
        Check(outputs.All(path => !File.Exists(path)) && outputs.All(path => File.Exists(path + ".subtitleguard.pending")),
            "clean partial outputs stay unpublished after retry exhaustion");
        Check(Simulation.CoveragePaths.All(path => !File.Exists(path)), "coverage evidence is removed after failed extraction");

        Simulation.CoverageStartTicks.Enqueue(TimeSpan.FromSeconds(12).Ticks);
        Simulation.CoverageTicks.Enqueue(TimeSpan.FromSeconds(30).Ticks);
        Simulation.CoverageStartTicks.Enqueue(TimeSpan.FromSeconds(12).Ticks);
        Simulation.CoverageTicks.Enqueue(TimeSpan.FromSeconds(59).Ticks);
        await ExtractWithCoverage();
        Check(outputs.All(path => File.Exists(path) && !File.Exists(path + ".subtitleguard.pending")),
            "complete coverage on one retry publishes the subtitle outputs");
        Check(Simulation.CoveragePaths.All(path => !File.Exists(path)), "coverage evidence is removed after successful extraction");

        const string recoveredTransport = "[http @ 0x123abc] Stream ends prematurely at 1234, should be 5678\n[http @ 0x123abc] Will reconnect at 1234 in 0 second(s), error=Input/output error.\n";
        Simulation.Result = Task.FromResult((0, recoveredTransport));
        var beforeRecovered = Simulation.ProcessCalls;
        await ExtractWithCoverage();
        Check(Simulation.ProcessCalls == beforeRecovered + 1 && outputs.All(path => File.Exists(path) && !File.Exists(path + ".subtitleguard.pending")),
            "fully covered native reconnect publishes tracks without restarting extraction");
        Check(Simulation.LastArguments.StartsWith("-reconnect 1 ", StringComparison.Ordinal), "covered native extraction receives reconnect input options");
        Simulation.Result = Task.FromResult((0, recoveredTransport + "[error] decoder failure"));
        await Fails(ExtractWithCoverage, "permanent decoder error remains rejected after a transport reconnect");
        Check(outputs.All(path => !File.Exists(path) && File.Exists(path + ".subtitleguard.pending")),
            "permanent failure after reconnect keeps every output unpublished");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            var validate = typeof(NativeExtractionGuard).GetMethod("ValidateProcessAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
            var beforeCancelled = Simulation.ProcessCalls;
            await Fails(() => (Task)validate.Invoke(null,
                [encoder, "simulated", cancelled.Token, process, Task.FromResult((0, recoveredTransport)), true])!,
                "cancelled successful process cannot publish recovered extraction");
            Check(Simulation.ProcessCalls == beforeCancelled, "cancelled recovery does not restart a native process");
        }
        Simulation.Result = Task.FromResult((0, string.Empty));

        var beginItem = typeof(NativeExtractionGuard).GetMethod("BeginItemScope", BindingFlags.NonPublic | BindingFlags.Static)!;
        var endItem = typeof(NativeExtractionGuard).GetMethod("EndItemScope", BindingFlags.NonPublic | BindingFlags.Static)!;
        var nativeOutputs = new List<string>();
        var nativePathManager = new Mock<IPathManager>();
        nativePathManager.Setup(value => value.GetSubtitlePath(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()))
            .Returns((string _, int index, string extension) =>
            {
                var path = Path.Combine(root, $"native-{index}{extension}");
                nativeOutputs.Add(path);
                Simulation.Paths = nativeOutputs.ToArray();
                return path;
            });
        var nativeEncoderDependency = new Mock<IMediaEncoder>();
        nativeEncoderDependency.Setup(value => value.GetInputPathArgument(source.Path, source)).Returns(source.Path);
        var coverageEncoder = new SubtitleEncoder(NullLogger<SubtitleEncoder>.Instance, fileSystem.Object,
            nativeEncoderDependency.Object, Mock.Of<IHttpClientFactory>(), Mock.Of<IMediaSourceManager>(),
            Mock.Of<ISubtitleParser>(), nativePathManager.Object, Mock.Of<IServerConfigurationManager>());
        var nativeExtractAll = encoderType.GetMethod("ExtractAllExtractableSubtitlesInternal", BindingFlags.Instance | BindingFlags.NonPublic,
            [typeof(MediaSourceInfo), typeof(List<MediaStream>), typeof(CancellationToken)])!;
        var extractableSubtitles = source.MediaStreams
            .Where(stream => stream.Type == MediaStreamType.Subtitle && stream.IsExtractableSubtitleStream && stream.SupportsExternalStream)
            .ToList();
        Check(extractableSubtitles.Count == 2, "native extraction receives only extractable subtitle streams");
        var shortcut = new Movie { Path = "/library/remote.strm", IsShortcut = true };
        object?[] shortcutState = [shortcut, null];
        var coverageFilesBefore = Simulation.CoveragePaths.Count;
        Simulation.Paths = [];
        Simulation.CoverageStartTicks.Clear();
        Simulation.CoverageTicks.Clear();
        Simulation.CoverageStartTicks.Enqueue(0);
        Simulation.CoverageTicks.Enqueue(TimeSpan.FromMinutes(1).Ticks);
        beginItem.Invoke(null, shortcutState);
        try
        {
            await (Task)nativeExtractAll.Invoke(coverageEncoder, [source, extractableSubtitles, CancellationToken.None])!;
            Check(Simulation.CoveragePaths.Count == coverageFilesBefore + 1,
                "installed source-coverage hook injects framecrc for native .strm extraction");
            Check(nativeOutputs.Count == 2 && nativeOutputs.All(path => File.Exists(path) && !File.Exists(path + ".subtitleguard.pending")),
                "native .strm extraction publishes subtitle outputs only after full video coverage");
        }
        finally
        {
            endItem.Invoke(null, [shortcutState[1], null]);
            coverageEncoder.Dispose();
            Simulation.Paths = outputs;
        }
        Check(Simulation.CoveragePaths.All(path => !File.Exists(path)), "native .strm extraction cleans its framecrc side output");
        Console.WriteLine("Native Subtitle Guard checks passed; no media download or FFmpeg process was started.");

        Task Extract() => (Task)extract.Invoke(encoder, ["https://example.invalid/movie.mkv", "simulated", outputs, CancellationToken.None])!;
    }
    finally
    {
        simulated.UnpatchAll(simulated.Id);
        Directory.Delete(root, true);
    }
}

static async Task CheckCoverageContract(string ffmpeg)
{
    var root = Path.Combine(Path.GetTempPath(), "subtitleguard-coverage-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    using var portProbe = new TcpListener(IPAddress.Loopback, 0);
    portProbe.Start();
    var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
    portProbe.Stop();
    using var server = new HttpListener();
    server.Prefixes.Add($"http://127.0.0.1:{port}/");
    var fixtures = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    var requests = new Dictionary<string, List<long>>(StringComparer.Ordinal);
    try
    {
        var subtitles = Path.Combine(root, "source.srt");
        await File.WriteAllTextAsync(subtitles,
            "1\n00:00:05,000 --> 00:00:07,000\nEARLY\n\n2\n00:00:55,000 --> 00:00:57,000\nLATE\n");
        foreach (var (name, duration) in new[] { ("full", 60), ("short", 30) })
        {
            var source = Path.Combine(root, name + ".mkv");
            await Run(["-f", "lavfi", "-i", $"color=c=black:s=160x90:r=10:d={duration}", "-i", subtitles,
                "-map", "0:v:0", "-map", "1:s:0", "-c:v", "ffv1", "-g", "10", "-c:s", "srt", "-t", duration.ToString(CultureInfo.InvariantCulture), source]);
            fixtures[name] = await File.ReadAllBytesAsync(source);
        }
        fixtures["bytecut"] = fixtures["full"][..(fixtures["full"].Length * 55 / 100)];
        foreach (var name in new[] { "interrupted", "resumed", "exhausted" }) fixtures[name] = fixtures["full"];

        server.Start();
        var serving = ServeAsync();
        try
        {
            var appendCoverage = typeof(NativeExtractionGuard).GetMethod("AppendCoverageOutput", BindingFlags.NonPublic | BindingFlags.Static)!;
            var hasCoverage = typeof(NativeExtractionGuard).GetMethod("HasCompleteCoverage", BindingFlags.NonPublic | BindingFlags.Static)!;
            var failed = typeof(NativeExtractionGuard).GetMethod("Failed", BindingFlags.NonPublic | BindingFlags.Static)!;
            var recovered = typeof(NativeExtractionGuard).GetMethod("Recovered", BindingFlags.NonPublic | BindingFlags.Static)!;
            var referenceSubtitles = string.Empty;
            string[] referencePackets = [];
            foreach (var name in new[] { "full", "short", "bytecut", "interrupted", "resumed", "exhausted" })
            {
                var subtitleOutput = Path.Combine(root, name + ".srt");
                var coverageOutput = Path.Combine(root, name + ".framecrc");
                var url = $"http://127.0.0.1:{port}/{name}";
                var escapedSubtitleOutput = "\"" + subtitleOutput.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
                var arguments = $"-y -i {url} -map 0:1 -an -vn -c:s copy {escapedSubtitleOutput}";
                arguments = (string)appendCoverage.Invoke(null, [arguments, coverageOutput, 0])!;
                if (name == "interrupted") arguments = arguments.Replace("-reconnect 1 ", "-reconnect 0 ", StringComparison.Ordinal);
                var output = await RunArguments(arguments);
                var subtitleText = File.Exists(subtitleOutput) ? File.ReadAllText(subtitleOutput) : string.Empty;
                var complete = (bool)hasCoverage.Invoke(null, [coverageOutput, TimeSpan.FromSeconds(60).Ticks])!;
                if (name == "full")
                {
                    Check(output.ExitCode == 0 && subtitleText.Contains("LATE", StringComparison.Ordinal), "full remote fixture extracts late subtitle cue");
                    Check(complete, "same-read video packet coverage accepts the complete source");
                    referenceSubtitles = subtitleText;
                    referencePackets = File.ReadAllLines(coverageOutput).Where(line => !line.StartsWith('#')).ToArray();
                }
                else if (name == "short")
                {
                    Check(output.ExitCode == 0 && subtitleText.Contains("EARLY", StringComparison.Ordinal)
                        && !subtitleText.Contains("LATE", StringComparison.Ordinal), "short remote fixture reproduces clean nonempty partial subtitles");
                    Check(!complete, "same-read video packet coverage rejects the short source against known runtime");
                }
                else if (name == "resumed")
                {
                    Check(requests[name].Count > 1 && requests[name].Skip(1).Any(offset => offset > 0),
                        "native reconnect resumes a deliberately interrupted HTTP response using a byte range");
                    Check(output.ExitCode == 0 && complete && subtitleText == referenceSubtitles,
                        "resumed extraction retains exact early and late subtitle cues and complete coverage");
                    Check(File.ReadAllLines(coverageOutput).Where(line => !line.StartsWith('#')).SequenceEqual(referencePackets),
                        "resumed extraction preserves every reference video packet without gaps or duplicates");
                    Check(output.StandardError.Contains("Stream ends prematurely", StringComparison.OrdinalIgnoreCase)
                        && (bool)failed.Invoke(null, [output])! && (bool)recovered.Invoke(null, [output, complete])!,
                        "guard distinguishes recovered EOF diagnostics only after exact fixture parity and coverage pass");
                }
                else if (name == "exhausted")
                {
                    Check(requests[name].Count > 1 && requests[name].Skip(1).All(offset => offset == requests[name][1] && offset > 0),
                        "unrecoverable HTTP resume retries the failed byte range within the process deadline");
                    Check(!complete && (bool)failed.Invoke(null, [output])! && !(bool)recovered.Invoke(null, [output, complete])!,
                        "unrecoverable interruption remains rejected despite nonempty subtitle output");
                }
                else
                {
                    var nonempty = subtitleText.Length > 0;
                    Check(!complete && (!nonempty || (subtitleText.Contains("EARLY", StringComparison.Ordinal)
                        && !subtitleText.Contains("LATE", StringComparison.Ordinal))),
                        "same-read video packet coverage rejects the byte-truncated Matroska response");
                }
            }
        }
        finally
        {
            server.Stop();
            await serving;
        }
        Console.WriteLine("Subtitle coverage contract passed; synthetic loopback media only.");

        async Task<(int ExitCode, string StandardError)> RunArguments(string arguments)
        {
            var info = new System.Diagnostics.ProcessStartInfo(ffmpeg)
            {
                Arguments = "-hide_banner -loglevel warning -nostdin " + arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var process = System.Diagnostics.Process.Start(info)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try { await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(timeout.Token)); }
            finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
            return (process.ExitCode, await stderr);
        }

        async Task ServeAsync()
        {
            try
            {
                while (server.IsListening)
                {
                    var context = await server.GetContextAsync();
                    var name = context.Request.Url?.AbsolutePath is { } requestPath ? Path.GetFileName(requestPath) : string.Empty;
                    if (!fixtures.TryGetValue(name, out var bytes))
                    {
                        context.Response.StatusCode = 404;
                        context.Response.Close();
                        continue;
                    }
                    var offset = 0L;
                    if (context.Request.Headers["Range"] is { } range)
                    {
                        var match = Regex.Match(range, "^bytes=(\\d+)-$");
                        if (!match.Success || !long.TryParse(match.Groups[1].Value, out offset) || offset >= bytes.Length)
                        {
                            context.Response.StatusCode = 416;
                            context.Response.Close();
                            continue;
                        }
                    }
                    if (!requests.TryGetValue(name, out var offsets)) requests[name] = offsets = [];
                    offsets.Add(offset);
                    if (name == "exhausted" && offsets.Count > 1)
                    {
                        context.Response.StatusCode = 503;
                        context.Response.Close();
                        continue;
                    }
                    context.Response.StatusCode = offset == 0 ? 200 : 206;
                    context.Response.Headers["Accept-Ranges"] = "bytes";
                    if (offset > 0) context.Response.Headers["Content-Range"] = $"bytes {offset}-{bytes.Length - 1}/{bytes.Length}";
                    context.Response.ContentType = "video/x-matroska";
                    context.Response.ContentLength64 = bytes.Length - offset;
                    if (name is "interrupted" or "resumed" or "exhausted" && offsets.Count == 1)
                    {
                        await context.Response.OutputStream.WriteAsync(bytes.AsMemory((int)offset, (bytes.Length - (int)offset) * 55 / 100));
                        context.Response.Abort();
                    }
                    else
                    {
                        await context.Response.OutputStream.WriteAsync(bytes.AsMemory((int)offset));
                        context.Response.Close();
                    }
                }
            }
            catch (HttpListenerException) { }
            catch (ObjectDisposedException) { }
        }
    }
    finally { Directory.Delete(root, true); }

    async Task<string> Run(string[] arguments)
    {
        var info = new System.Diagnostics.ProcessStartInfo(ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-y" }.Concat(arguments)) info.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(info)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await Task.WhenAll(output, errors, process.WaitForExitAsync(timeout.Token)); }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
        if (process.ExitCode != 0 || (await errors).Length > 0) throw new IOException("Synthetic FFmpeg fixture command failed: " + await errors);
        return await output;
    }
}

static void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    Console.WriteLine("PASS " + name);
}

static async Task Fails(Func<Task> action, string name)
{
    try { await action(); }
    catch (Exception exception) when (exception is IOException or OperationCanceledException or MediaBrowser.Common.FfmpegException)
    {
        Console.WriteLine("PASS " + name);
        return;
    }
    throw new InvalidOperationException("Expected failure: " + name);
}

static void CheckCoverageParser()
{
    var root = Path.Combine(Path.GetTempPath(), "subtitleguard-coverage-parser-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var method = typeof(NativeExtractionGuard).GetMethod("HasCompleteCoverage", BindingFlags.NonPublic | BindingFlags.Static)!;
    var append = typeof(NativeExtractionGuard).GetMethod("AppendCoverageOutput", BindingFlags.NonPublic | BindingFlags.Static)!;
    try
    {
        var strmShortcut = typeof(NativeExtractionGuard).GetMethod("IsStrmShortcut", BindingFlags.NonPublic | BindingFlags.Static)!;
        Check((bool)strmShortcut.Invoke(null, [true, "/library/episode.strm"])!, "coverage eligibility recognizes a local .strm shortcut");
        Check(!(bool)strmShortcut.Invoke(null, [true, "/library/episode.mkv"])!, "coverage eligibility leaves ordinary media paths unchanged");
        Check(!(bool)strmShortcut.Invoke(null, [false, "/library/episode.strm"])!, "coverage eligibility requires Jellyfin shortcut metadata");
        var beginItem = typeof(NativeExtractionGuard).GetMethod("BeginItemScope", BindingFlags.NonPublic | BindingFlags.Static)!;
        var endItem = typeof(NativeExtractionGuard).GetMethod("EndItemScope", BindingFlags.NonPublic | BindingFlags.Static)!;
        var requestScope = (AsyncLocal<bool>)typeof(NativeExtractionGuard).GetField("StrmSubtitleRequest", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var shortcutItem = (Movie)RuntimeHelpers.GetUninitializedObject(typeof(Movie));
        shortcutItem.Path = "/library/episode.strm";
        shortcutItem.IsShortcut = true;
        var itemState = new object?[] { shortcutItem, null };
        var priorScope = requestScope.Value;
        beginItem.Invoke(null, itemState);
        var isScoped = requestScope.Value;
        endItem.Invoke(null, [itemState[1], null]);
        Check(isScoped && requestScope.Value == priorScope, "Harmony item scope enables coverage only during .strm subtitle requests");

        var beginRead = typeof(NativeExtractionGuard).GetMethod("BeginReadScope", BindingFlags.NonPublic | BindingFlags.Static)!;
        var endRead = typeof(NativeExtractionGuard).GetMethod("EndReadScope", BindingFlags.NonPublic | BindingFlags.Static)!;
        var currentRead = typeof(NativeExtractionGuard).GetField("CurrentRead", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var currentReadValue = currentRead.GetType().GetProperty("Value")!;
        var priorReadRequest = requestScope.Value;
        requestScope.Value = true;
        var scopedSource = new MediaSourceInfo
        {
            Id = "scoped-source", Path = "http://example.invalid/video.mkv", IsRemote = true,
            RunTimeTicks = TimeSpan.FromMinutes(1).Ticks,
            MediaStreams = [new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = "h264" }]
        };
        object?[] readState = [scopedSource, new MediaStream { Type = MediaStreamType.Subtitle, Codec = "srt", IsExternal = false }, null];
        beginRead.Invoke(null, readState);
        var sourceWasScoped = currentReadValue.GetValue(currentRead) is not null;
        endRead.Invoke(null, [readState[2], null]);
        var noVideoSource = new MediaSourceInfo
        {
            Id = "no-video-source", Path = "http://example.invalid/audio-only.mkv", IsRemote = true,
            RunTimeTicks = TimeSpan.FromMinutes(1).Ticks,
            MediaStreams = [new MediaStream { Type = MediaStreamType.Subtitle, Index = 0, Codec = "srt", IsExternal = false }]
        };
        readState = [noVideoSource, new MediaStream { Type = MediaStreamType.Subtitle, Codec = "srt", IsExternal = false }, null];
        beginRead.Invoke(null, readState);
        var noVideoWasScoped = currentReadValue.GetValue(currentRead) is not null;
        endRead.Invoke(null, [readState[2], null]);
        requestScope.Value = priorReadRequest;
        Check(sourceWasScoped && !noVideoWasScoped, "legacy cache migration is limited to sources with a measurable video stream");

        var coveragePath = Path.Combine(root, "coverage evidence.framecrc");
        var arguments = (string)append.Invoke(null, ["-y -i https://example.invalid/media -map 0:1 -c:s copy out.srt", coveragePath, 2])!;
        Check(arguments.StartsWith("-reconnect 1 -reconnect_on_network_error 1 -reconnect_max_retries 2 -reconnect_delay_max 2 -reconnect_delay_total_max 4 -y -i ", StringComparison.Ordinal),
            "covered extraction enables bounded native HTTP reconnect before the input");
        Check(arguments.Contains("-map 0:2 -c:v copy -an -sn -fs 67108864 -f framecrc \"" + coveragePath + "\"", StringComparison.Ordinal),
            "coverage output uses the selected video map, fixed byte bound, and quoted temp path");

        var recovered = typeof(NativeExtractionGuard).GetMethod("Recovered", BindingFlags.NonPublic | BindingFlags.Static)!;
        const string resumedError = "[http @ 0x123abc] Stream ends prematurely at 1234, should be 5678\n[http @ 0x123abc] Will reconnect at 1234 in 0 second(s), error=Input/output error.\n";
        Check((bool)recovered.Invoke(null, [(0, resumedError), true])!, "matching recovered EOF is accepted only with complete coverage");
        Check(!(bool)recovered.Invoke(null, [(0, resumedError), false])!, "reconnect text cannot validate incomplete coverage");
        Check(!(bool)recovered.Invoke(null, [(1, resumedError), true])!, "nonzero extraction exit cannot be recovered");
        foreach (var error in new[] { "[error] decoder failure", "[fatal] encoding failed", "Error during demuxing: Input/output error", "Connection reset by peer", "Stream ends prematurely" })
            Check(!(bool)recovered.Invoke(null, [(0, resumedError + error), true])!, "remaining failure still rejects recovered transport: " + error);
        Check(!(bool)recovered.Invoke(null, [(0, resumedError.Replace("at 1234 in", "at 1235 in", StringComparison.Ordinal)), true])!,
            "reconnect must resume the interrupted byte offset");
        Check(!(bool)recovered.Invoke(null, [(0, resumedError.Replace("0x123abc] Will", "0x456abc] Will", StringComparison.Ordinal)), true])!,
            "reconnect must match the interrupted HTTP connection");

        var cachePath = Path.Combine(root, "legacy.srt");
        File.WriteAllText(cachePath, "nonempty legacy cache");
        var readScopeType = typeof(NativeExtractionGuard).GetNestedType("ReadScope", BindingFlags.NonPublic)!;
        var readScope = Activator.CreateInstance(readScopeType, ["https://example.invalid/media", "source-id", "etag-v1", TimeSpan.FromMinutes(1).Ticks])!;
        var previousReadScope = currentReadValue.GetValue(currentRead);
        currentReadValue.SetValue(currentRead, readScope);
        var pendingCheck = typeof(NativeExtractionGuard).GetMethod("CheckPending", BindingFlags.NonPublic | BindingFlags.Static)!;
        bool IsFreshFor(string sourceId, string eTag, long runtimeTicks)
        {
            var scope = Activator.CreateInstance(readScopeType, ["https://example.invalid/media", sourceId, eTag, runtimeTicks])!;
            currentReadValue.SetValue(currentRead, scope);
            object?[] values = [cachePath, "https://example.invalid/media", true];
            return (bool)pendingCheck.Invoke(null, values)! && Equals(values[2], true);
        }
        try
        {
            object?[] freshness = [cachePath, "https://example.invalid/media", true];
            Check(!(bool)pendingCheck.Invoke(null, freshness)! && Equals(freshness[2], false),
                "unmarked legacy remote .strm cache is stale only when its subtitle is requested");

            var coverageType = typeof(NativeExtractionGuard).GetNestedType("CoverageContext", BindingFlags.NonPublic)!;
            var coverageContext = Activator.CreateInstance(coverageType, [TimeSpan.FromMinutes(1).Ticks, 0, "source-id", "etag-v1"])!;
            var writeReceipt = typeof(NativeExtractionGuard).GetMethod("WriteCoverageReceipt", BindingFlags.NonPublic | BindingFlags.Static)!;
            writeReceipt.Invoke(null, [cachePath, coverageContext]);
            freshness = [cachePath, "https://example.invalid/media", true];
            Check((bool)pendingCheck.Invoke(null, freshness)! && Equals(freshness[2], true),
                "cache with matching source-bound coverage receipt retains its fast path");
            Check(!IsFreshFor("different-source", "etag-v1", TimeSpan.FromMinutes(1).Ticks), "coverage receipt is bound to the media-source ID");
            Check(!IsFreshFor("source-id", "etag-v2", TimeSpan.FromMinutes(1).Ticks), "coverage receipt is bound to the source ETag");
            Check(!IsFreshFor("source-id", "etag-v1", TimeSpan.FromMinutes(2).Ticks), "coverage receipt is bound to the source runtime");

            File.AppendAllText(cachePath, " changed");
            Check(!IsFreshFor("source-id", "etag-v1", TimeSpan.FromMinutes(1).Ticks), "coverage receipt is bound to the subtitle cache size");
            File.WriteAllText(cachePath, "nonempty legacy cache");
            writeReceipt.Invoke(null, [cachePath, coverageContext]);
            File.SetLastWriteTimeUtc(cachePath, DateTime.UtcNow.AddSeconds(-10));
            Check(!IsFreshFor("source-id", "etag-v1", TimeSpan.FromMinutes(1).Ticks), "coverage receipt is bound to the subtitle cache modification time");

            currentReadValue.SetValue(currentRead, readScope);
            freshness = [cachePath, "https://example.invalid/other", true];
            Check((bool)pendingCheck.Invoke(null, freshness)! && Equals(freshness[2], true),
                "unrelated source paths keep the native cache decision");
            freshness = [cachePath + ".mks", "https://example.invalid/media", true];
            Check((bool)pendingCheck.Invoke(null, freshness)! && Equals(freshness[2], true),
                "MKS sidecar caches are outside the video-coverage migration");
        }
        finally { currentReadValue.SetValue(currentRead, previousReadScope); }

        bool CheckCoverage(string name, long startTicks, long durationTicks)
        {
            var path = Path.Combine(root, name + ".framecrc");
            File.WriteAllText(path, $"#tb 0: 1/{TimeSpan.TicksPerSecond}\n#media_type 0: video\n#codec_id 0: h264\n#dimensions 0: 160x90\n#sar 0: 1/1\n0, {startTicks}, {startTicks}, {durationTicks}, 1, 0x00\n");
            return (bool)method.Invoke(null, [path, TimeSpan.FromMinutes(1).Ticks])!;
        }

        Check(CheckCoverage("complete-offset", TimeSpan.FromSeconds(30).Ticks, TimeSpan.FromSeconds(59).Ticks),
            "coverage accepts a nonzero-start source ending within the one-second container tolerance");
        Check(!CheckCoverage("short-offset", TimeSpan.FromSeconds(50).Ticks, TimeSpan.FromSeconds(20).Ticks),
            "coverage measures elapsed span instead of accepting a high absolute PTS");

        var malformed = Path.Combine(root, "malformed.framecrc");
        File.WriteAllText(malformed, "#tb invalid\n0, bad, record\n");
        Check(!(bool)method.Invoke(null, [malformed, TimeSpan.FromMinutes(1).Ticks])!, "malformed packet evidence fails closed");
        Console.WriteLine("Subtitle coverage parser checks passed; no FFmpeg process or media source used.");
    }
    finally { Directory.Delete(root, true); }
}

public static class Simulation
{
    public static string[] Paths { get; set; } = [];
    public static Task<(int, string)> Result { get; set; } = Task.FromResult((0, string.Empty));
    public static Queue<Task<(int, string)>> Results { get; } = new();
    public static Queue<long> CoverageTicks { get; } = new();
    public static Queue<long> CoverageStartTicks { get; } = new();
    public static List<string> CoveragePaths { get; } = [];
    public static int ProcessCalls { get; set; }
    public static string LastArguments { get; set; } = string.Empty;
    public static bool Process(string __0, ref Task<(int, string)> __result)
    {
        ProcessCalls++;
        LastArguments = __0;
        foreach (var path in Paths) File.WriteAllText(path, "1\n00:00:01,000 --> 00:00:02,000\nPartial or complete fixture\n");
        var match = Regex.Match(__0, "-f framecrc (?:\"([^\"]+)\"|(\\S+))");
        if (match.Success)
        {
            var path = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            var startTicks = CoverageStartTicks.Count > 0 ? CoverageStartTicks.Dequeue() : 0;
            var ticks = CoverageTicks.Count > 0 ? CoverageTicks.Dequeue() : TimeSpan.FromMinutes(1).Ticks;
            File.WriteAllText(path, $"#tb 0: 1/{TimeSpan.TicksPerSecond}\n#media_type 0: video\n#codec_id 0: h264\n#dimensions 0: 160x90\n#sar 0: 1/1\n0, {startTicks}, {startTicks}, {ticks}, 1, 0x00\n");
            CoveragePaths.Add(path);
        }
        __result = Results.Count > 0 ? Results.Dequeue() : Result;
        return false;
    }

    public static bool Read(ref Task<SubtitleEncoder.SubtitleInfo> __result)
    {
        __result = Task.FromResult(new SubtitleEncoder.SubtitleInfo { Path = Paths[0] });
        return false;
    }
}
