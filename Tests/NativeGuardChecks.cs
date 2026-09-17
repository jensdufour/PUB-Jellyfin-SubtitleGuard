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

await using var registered = args.Contains("--collectible") ? await RegisterCollectible() : null;
await CheckNativeBehavior(registered is not null);

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