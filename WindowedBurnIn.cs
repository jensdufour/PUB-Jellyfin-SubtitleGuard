using System.Globalization;
using System.Reflection;
using HarmonyLib;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Streaming;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SubtitleGuard;

internal sealed class WindowedBurnIn(IServiceProvider services, ILogger<WindowedBurnIn> logger) : IHostedService, IDisposable
{
    private const string PatchId = "jensdufour.subtitleguard.windowed-burn-in";
    private readonly CancellationTokenSource _stopping = new();
    private readonly Harmony _patches = new(PatchId);
    private readonly ILogger<WindowedBurnIn> _logger = logger;
    private static readonly AsyncLocal<PreparedCall?> Current = new();
    private static WindowedBurnIn? _active;
    private AssWindowCache? _cache;
    private IPathManager? _pathManager;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Environment.GetEnvironmentVariable("SUBTITLEGUARD_WINDOWED_ASS") != "1") return Task.CompletedTask;
        try
        {
            if (!OperatingSystem.IsLinux() || typeof(EncodingHelper).Assembly.GetName().Version?.Major != 12)
                throw new NotSupportedException("Windowed burn-in requires Jellyfin 12 on Linux.");
            var paths = services.GetRequiredService<IApplicationPaths>();
            var encoder = services.GetRequiredService<IMediaEncoder>();
            _pathManager = services.GetRequiredService<IPathManager>();
            var controller = Type.GetType("Jellyfin.Api.Controllers.DynamicHlsController, Jellyfin.Api", throwOnError: true)!;
            var command = controller.GetMethod("GetCommandLineArguments", BindingFlags.Instance | BindingFlags.NonPublic);
            var video = controller.GetMethod("GetVideoArguments", BindingFlags.Instance | BindingFlags.NonPublic);
            if (command?.ReturnType != typeof(string) || video?.ReturnType != typeof(string)
                || command.GetParameters().Length != 4 || video.GetParameters().Length != 4
                || !typeof(EncodingJobInfo).IsAssignableFrom(command.GetParameters()[1].ParameterType)
                || !typeof(EncodingJobInfo).IsAssignableFrom(video.GetParameters()[0].ParameterType))
                throw new NotSupportedException("Unsupported native HLS method signatures.");
            var root = Path.Combine(paths.CachePath, "subtitleguard-windows", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            _cache = new AssWindowCache(() => encoder.EncoderPath, root, _stopping.Token);
            _active = this;
            _patches.Patch(video, postfix: Hook(nameof(LimitOutput)));
            _patches.Patch(command, prefix: Hook(nameof(Prepare)), finalizer: Hook(nameof(Restore)));
            _logger.LogInformation("Subtitle Guard: windowed remote ASS burn-in enabled for all users; native HLS, bounded preparation and guarded fallback.");
        }
        catch (Exception exception)
        {
            _patches.UnpatchAll(PatchId);
            _active = null;
            _logger.LogError("Subtitle Guard: windowed burn-in unavailable ({Reason}); native extraction guards remain active.", exception.GetType().Name);
        }
        return Task.CompletedTask;
    }

    private static HarmonyMethod Hook(string name) => new(typeof(WindowedBurnIn).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!);

    private static void Prepare(object __instance, object[] __args, ref PreparedCall? __state)
    {
        var active = _active;
        if (active?._cache is null || __args[1] is not StreamState state || __args[2] is not false
            || state.SubtitleStream is not { IsExternal: false } subtitle || state.VideoStream is null
            || state.SubtitleDeliveryMethod != SubtitleDeliveryMethod.Encode
            || EncodingHelper.IsCopyCodec(state.OutputVideoCodec)
            || !(string.Equals(subtitle.Codec, "ass", StringComparison.OrdinalIgnoreCase) || string.Equals(subtitle.Codec, "ssa", StringComparison.OrdinalIgnoreCase))
            || !Uri.TryCreate(state.MediaPath, UriKind.Absolute, out var source) || source.Scheme is not ("http" or "https")
            || state.RunTimeTicks is not > 0)
            return;
        var startTicks = state.BaseRequest.StartTimeTicks ?? 0;
        var runtimeTicks = state.RunTimeTicks.Value;
        long? stride = state.Request.ActualSegmentLengthTicks;
        if (stride is not > 0 || stride > TimeSpan.FromSeconds(20).Ticks || startTicks < 0 || startTicks >= runtimeTicks) return;
        var endTicks = Math.Min(runtimeTicks, checked(startTicks + stride.Value * 5));
        var cancellation = __instance is ControllerBase controller ? controller.HttpContext.RequestAborted : active._stopping.Token;
        try
        {
            var nativePath = active._pathManager?.GetSubtitlePath(state.MediaSource.Id, subtitle.Index, "." + subtitle.Codec.ToLowerInvariant());
            if (nativePath is not null && File.Exists(nativePath) && new FileInfo(nativePath).Length > 0
                && !File.Exists(nativePath + NativeExtractionGuard.PendingSuffix)) return;
            var path = active._cache.PrepareAsync(state.MediaPath, subtitle.Index, state.VideoStream.Index,
                state.RemoteHttpHeaders, startTicks, endTicks, endTicks == runtimeTicks, cancellation).GetAwaiter().GetResult();
            var call = new PreparedCall(state, subtitle, endTicks, Current.Value);
            __state = call;
            Current.Value = call;
            state.SubtitleStream = new MediaStream
            {
                Type = MediaStreamType.Subtitle, Codec = subtitle.Codec, Index = subtitle.Index,
                Language = subtitle.Language, IsExternal = true, Path = path
            };
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested || active._stopping.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or OperationCanceledException or FormatException or OverflowException)
        {
            active._logger.LogWarning("Subtitle Guard: bounded ASS preparation unavailable ({Reason}); using guarded native extraction.", exception.GetType().Name);
        }
    }

    private static void LimitOutput(object[] __args, ref string __result)
    {
        if (Current.Value is { } call && ReferenceEquals(call.State, __args[0]))
        {
            __result += " -to " + ((decimal)call.EndTicks / TimeSpan.TicksPerSecond).ToString("0.#######", CultureInfo.InvariantCulture);
            call.OutputLimited = true;
        }
    }

    private static Exception? Restore(PreparedCall? __state, Exception? __exception)
    {
        if (__state is null) return __exception;
        __state.State.SubtitleStream = __state.OriginalSubtitle;
        Current.Value = __state.Previous;
        return __exception ?? (__state.OutputLimited ? null : new IOException("Refusing unbounded playback with a subtitle window."));
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _active = null;
        _stopping.Cancel();
        _patches.UnpatchAll(PatchId);
        try
        {
            if (_cache is not null) await _cache.CleanupAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            _logger.LogWarning("Subtitle Guard: temporary window cleanup deferred ({Reason}).", exception.GetType().Name);
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
        _patches.UnpatchAll(PatchId);
    }

    private sealed class PreparedCall(EncodingJobInfo state, MediaStream subtitle, long endTicks, PreparedCall? previous)
    {
        public EncodingJobInfo State { get; } = state;
        public MediaStream OriginalSubtitle { get; } = subtitle;
        public long EndTicks { get; } = endTicks;
        public PreparedCall? Previous { get; } = previous;
        public bool OutputLimited { get; set; }
    }
}