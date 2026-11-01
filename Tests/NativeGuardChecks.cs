using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using HarmonyLib;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Streaming;
using MediaBrowser.Model.Dlna;
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
        Check(Harmony.GetAllPatchedMethods().Count(method => Harmony.GetPatchInfo(method)?.Owners.Contains("jensdufour.subtitleguard.native-extraction") == true) == 4,
            "collectible plugin registration installs all four native guards");
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
        Console.WriteLine("Native Subtitle Guard checks passed; no media download or FFmpeg process was started.");

        Task Extract() => (Task)extract.Invoke(encoder, ["https://example.invalid/movie.mkv", "simulated", outputs, CancellationToken.None])!;
    }
    finally
    {
        simulated.UnpatchAll(simulated.Id);
        Directory.Delete(root, true);
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

public static class Simulation
{
    public static string[] Paths { get; set; } = [];
    public static Task<(int, string)> Result { get; set; } = Task.FromResult((0, string.Empty));
    public static Queue<Task<(int, string)>> Results { get; } = new();
    public static int ProcessCalls { get; set; }
    public static bool Process(ref Task<(int, string)> __result)
    {
        ProcessCalls++;
        foreach (var path in Paths) File.WriteAllText(path, "1\n00:00:01,000 --> 00:00:02,000\nPartial or complete fixture\n");
        __result = Results.Count > 0 ? Results.Dequeue() : Result;
        return false;
    }
    public static bool Read(ref Task<SubtitleEncoder.SubtitleInfo> __result)
    {
        __result = Task.FromResult(new SubtitleEncoder.SubtitleInfo { Path = Paths[0] });
        return false;
    }
}