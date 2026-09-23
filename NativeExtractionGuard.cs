using System.Reflection;
using HarmonyLib;
using MediaBrowser.MediaEncoding.Subtitles;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace SubtitleGuard;

public sealed class NativeExtractionGuard : IDisposable
{
    private const string PatchId = "jensdufour.subtitleguard.native-extraction";
    internal const string PendingSuffix = ".subtitleguard.pending";
    private static readonly AsyncLocal<bool> RetryingProcess = new();
    private readonly Harmony _patches = new(PatchId);

    public void Install()
    {
        var encoder = typeof(SubtitleEncoder);
        if (encoder.Assembly.GetName().Version?.Major != 12)
            throw new NotSupportedException("Subtitle Guard requires Jellyfin 12; no native methods were patched.");
        var process = RequireMethod("RunSubtitleExtractionProcess", typeof(Task<(int, string)>), typeof(string), typeof(CancellationToken));
        var extract = RequireMethod("ExtractSubtitlesForFile", typeof(Task), typeof(string), typeof(string), typeof(IReadOnlyList<string>), typeof(CancellationToken));
        var fresh = RequireMethod("IsCachedSubtitleFresh", typeof(bool), typeof(string), typeof(string));
        var read = RequireMethod("GetReadableFile", typeof(Task<SubtitleEncoder.SubtitleInfo>), typeof(MediaSourceInfo), typeof(MediaStream), typeof(CancellationToken));
        try
        {
            _patches.Patch(process, postfix: Hook(nameof(ProcessFinished)));
            _patches.Patch(extract, prefix: Hook(nameof(ExtractionStarting)), postfix: Hook(nameof(ExtractionFinished)));
            _patches.Patch(fresh, prefix: Hook(nameof(CheckPending)));
            _patches.Patch(read, postfix: Hook(nameof(CheckReadable)));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private static MethodInfo RequireMethod(string name, Type result, params Type[] parameters)
    {
        var method = typeof(SubtitleEncoder).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic, parameters);
        if (method?.ReturnType != result)
            throw new NotSupportedException($"Unsupported Jellyfin subtitle method: {name}. No native methods were patched.");
        return method;
    }

    private static HarmonyMethod Hook(string name) => new(typeof(NativeExtractionGuard).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!);

    private static string PendingPath(string path) => path + PendingSuffix;

    private static bool CheckPending(string __0, ref bool __result)
    {
        if (!File.Exists(PendingPath(__0))) return true;
        __result = false;
        return false;
    }

    private static void ExtractionStarting(IReadOnlyList<string> __2)
    {
        foreach (var path in __2)
        {
            using var marker = new FileStream(PendingPath(path), FileMode.Create, FileAccess.Write, FileShare.Read);
            marker.WriteByte(1);
            marker.Flush(true);
        }
    }

    private static void ExtractionFinished(IReadOnlyList<string> __2, ref Task __result) => __result = CompleteAsync(__result, __2);

    private static async Task CompleteAsync(Task extraction, IReadOnlyList<string> paths)
    {
        await extraction.ConfigureAwait(false);
        foreach (var path in paths)
        {
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
                throw new IOException("Subtitle extraction did not produce every expected file; outputs remain incomplete.");
        }
        foreach (var path in paths) File.Delete(PendingPath(path));
    }

    private static void ProcessFinished(SubtitleEncoder __instance, string __0, CancellationToken __1, MethodBase __originalMethod,
        ref Task<(int ExitCode, string StandardError)> __result) =>
        __result = ValidateProcessAsync(__instance, __0, __1, __originalMethod, __result, !RetryingProcess.Value);

    private static async Task<(int ExitCode, string StandardError)> ValidateProcessAsync(SubtitleEncoder encoder, string arguments,
        CancellationToken cancellationToken, MethodBase processMethod, Task<(int ExitCode, string StandardError)> process, bool allowRetry)
    {
        var result = await process.ConfigureAwait(false);
        if (!Failed(result)) return result;
        if (!allowRetry || cancellationToken.IsCancellationRequested || !Retryable(result.StandardError))
            return (-1, result.StandardError);

        RetryingProcess.Value = true;
        try
        {
            var retry = (Task<(int ExitCode, string StandardError)>)processMethod.Invoke(encoder, [arguments, cancellationToken])!;
            return await retry.ConfigureAwait(false);
        }
        finally
        {
            RetryingProcess.Value = false;
        }
    }

    private static bool Failed((int ExitCode, string StandardError) result) => result.ExitCode != 0 || new[]
        {
            "File ended prematurely", "Stream ends prematurely", "Error during demuxing",
            "Input/output error", "Connection reset by peer", "[error]", "[fatal]"
        }.Any(message => result.StandardError.Contains(message, StringComparison.OrdinalIgnoreCase));

    private static bool Retryable(string standardError) => new[]
        {
            "File ended prematurely", "Stream ends prematurely", "Input/output error", "Connection reset by peer"
        }.Any(message => standardError.Contains(message, StringComparison.OrdinalIgnoreCase));

    private static void CheckReadable(ref Task<SubtitleEncoder.SubtitleInfo> __result) => __result = ReadCompletedAsync(__result);

    private static async Task<SubtitleEncoder.SubtitleInfo> ReadCompletedAsync(Task<SubtitleEncoder.SubtitleInfo> read)
    {
        var result = await read.ConfigureAwait(false);
        if (File.Exists(PendingPath(result.Path)))
            throw new IOException("Subtitle extraction is incomplete; retry the subtitle request after the source is available.");
        return result;
    }

    public void Dispose() => _patches.UnpatchAll(PatchId);
}