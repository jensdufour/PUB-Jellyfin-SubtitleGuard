using System.Net;
using System.Reflection;
using System.Security.Claims;
using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.IO;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SubtitleGuard;

internal sealed class PlaybackFirst(IPathManager paths, ILogger<PlaybackFirst> logger) : IHostedService
{
    private const string PatchId = "jensdufour.subtitleguard.playback-first";
    private readonly Harmony _patches = new(PatchId);
    private static PlaybackFirst? _active;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var helper = Type.GetType("Jellyfin.Api.Helpers.MediaInfoHelper, Jellyfin.Api", throwOnError: true)!;
            var method = helper.GetMethod("SetDeviceSpecificData", BindingFlags.Instance | BindingFlags.Public,
                [typeof(BaseItem), typeof(MediaSourceInfo), typeof(DeviceProfile), typeof(ClaimsPrincipal),
                    typeof(int?), typeof(long), typeof(string), typeof(int?), typeof(int?), typeof(int?),
                    typeof(string), typeof(Guid), typeof(bool), typeof(bool), typeof(bool), typeof(bool),
                    typeof(bool), typeof(bool), typeof(IPAddress)]);
            if (typeof(BaseItem).Assembly.GetName().Version?.Major != 12 || method?.ReturnType != typeof(void))
                throw new NotSupportedException("Unsupported native playback negotiation signature.");
            _active = this;
            _patches.Patch(method, prefix: new HarmonyMethod(typeof(PlaybackFirst).GetMethod(nameof(Negotiate), BindingFlags.Static | BindingFlags.NonPublic)!));
            logger.LogInformation("Subtitle Guard: playback-first negotiation enabled for uncached remote embedded ASS/SSA subtitles.");
        }
        catch (Exception exception)
        {
            _patches.UnpatchAll(PatchId);
            _active = null;
            logger.LogError("Subtitle Guard: playback-first negotiation unavailable ({Reason}); native playback unchanged.", exception.GetType().Name);
        }
        return Task.CompletedTask;
    }

    private static void Negotiate(BaseItem __0, MediaSourceInfo __1, ref int? __8)
    {
        var active = _active;
        if (active is null || !NativeExtractionGuard.IsStrmShortcut(__0.IsShortcut, __0.Path)
            || !__1.IsRemote || !Uri.TryCreate(__1.Path, UriKind.Absolute, out var source)
            || source.Scheme is not ("http" or "https")) return;
        var selected = __8 ?? __1.DefaultSubtitleStreamIndex;
        var subtitle = __1.MediaStreams.FirstOrDefault(stream => stream.Type == MediaStreamType.Subtitle && stream.Index == selected);
        if (subtitle is null || subtitle.IsExternal
            || !(string.Equals(subtitle.Codec, "ass", StringComparison.OrdinalIgnoreCase)
                || string.Equals(subtitle.Codec, "ssa", StringComparison.OrdinalIgnoreCase))) return;
        try
        {
            var path = active.Paths.GetSubtitlePath(__1.Id, subtitle.Index, "." + subtitle.Codec.ToLowerInvariant());
            if (path is not null && NativeExtractionGuard.IsReadyForPlayback(path, __1)) return;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            active.Logger.LogWarning("Subtitle Guard: subtitle readiness unavailable ({Reason}); continuing without subtitles.", exception.GetType().Name);
        }
        __8 = -1;
        __1.DefaultSubtitleStreamIndex = -1;
        active.Logger.LogInformation("Subtitle Guard: skipping unready embedded subtitles for playback item {ItemId}.", __0.Id);
    }

    private IPathManager Paths => paths;
    private ILogger<PlaybackFirst> Logger => logger;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _active = null;
        _patches.UnpatchAll(PatchId);
        return Task.CompletedTask;
    }
}