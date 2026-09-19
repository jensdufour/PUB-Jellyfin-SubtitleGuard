using System.Reflection;
using System.Runtime.Loader;
using HarmonyLib;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Configuration;
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

if (args.Length == 2 && args[0] == "--window-contract")
{
    await CheckWindowContract(args[1]);
    return;
}

await using var registered = args.Contains("--collectible") ? await RegisterCollectible() : null;
await CheckNativeBehavior(registered is not null);

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
            if (process.ExitCode != 0 || (await errors).Length > 0) throw new IOException("Synthetic FFmpeg contract command failed.");
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
    public static bool Process(ref Task<(int, string)> __result)
    {
        foreach (var path in Paths) File.WriteAllText(path, "1\n00:00:01,000 --> 00:00:02,000\nPartial or complete fixture\n");
        __result = Result;
        return false;
    }
    public static bool Read(ref Task<SubtitleEncoder.SubtitleInfo> __result)
    {
        __result = Task.FromResult(new SubtitleEncoder.SubtitleInfo { Path = Paths[0] });
        return false;
    }
}