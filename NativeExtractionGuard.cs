using System.Reflection;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.MediaEncoding;
using HarmonyLib;
using MediaBrowser.MediaEncoding.Subtitles;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace SubtitleGuard;

public sealed class NativeExtractionGuard : IDisposable
{
    private const string PatchId = "jensdufour.subtitleguard.native-extraction";
    internal const string PendingSuffix = ".subtitleguard.pending";
    private const long MaximumCoverageBytes = 64L * 1024 * 1024;
    private static readonly AsyncLocal<CoverageContext?> SourceCoverage = new();
    private static readonly AsyncLocal<ReadScope?> CurrentRead = new();
    private static readonly AsyncLocal<bool> StrmSubtitleRequest = new();
    private static readonly AsyncLocal<bool> RetryingProcess = new();
    private readonly Harmony _patches = new(PatchId);

    public void Install()
    {
        var encoder = typeof(SubtitleEncoder);
        if (encoder.Assembly.GetName().Version?.Major != 12)
            throw new NotSupportedException("Subtitle Guard requires Jellyfin 12; no native methods were patched.");
        var process = RequireMethod("RunSubtitleExtractionProcess", typeof(Task<(int, string)>), typeof(string), typeof(CancellationToken));
        var extract = RequireMethod("ExtractSubtitlesForFile", typeof(Task), typeof(string), typeof(string), typeof(IReadOnlyList<string>), typeof(CancellationToken));
        var extractAll = RequireMethod("ExtractAllExtractableSubtitlesInternal", typeof(Task), typeof(MediaSourceInfo), typeof(List<MediaStream>), typeof(CancellationToken));
        var getSubtitlesParameters = new[] { typeof(BaseItem), typeof(string), typeof(int), typeof(string), typeof(long), typeof(long), typeof(bool), typeof(CancellationToken) };
        var getSubtitles = encoder.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .SingleOrDefault(method => method.Name.EndsWith("ISubtitleEncoder.GetSubtitles", StringComparison.Ordinal)
                && method.ReturnType == typeof(Task<Stream>)
                && method.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(getSubtitlesParameters));
        if (getSubtitles is null)
            throw new NotSupportedException("Unsupported Jellyfin subtitle request method. No native methods were patched.");
        var fresh = RequireMethod("IsCachedSubtitleFresh", typeof(bool), typeof(string), typeof(string));
        var read = RequireMethod("GetReadableFile", typeof(Task<SubtitleEncoder.SubtitleInfo>), typeof(MediaSourceInfo), typeof(MediaStream), typeof(CancellationToken));
        try
        {
            _patches.Patch(process, postfix: Hook(nameof(ProcessFinished)));
            _patches.Patch(extract, prefix: Hook(nameof(ExtractionStarting)), postfix: Hook(nameof(ExtractionFinished)));
            _patches.Patch(getSubtitles, prefix: Hook(nameof(BeginItemScope)), finalizer: Hook(nameof(EndItemScope)));
            _patches.Patch(extractAll, prefix: Hook(nameof(BeginSourceCoverage)), postfix: Hook(nameof(EndSourceCoverage)));
            _patches.Patch(fresh, prefix: Hook(nameof(CheckPending)));
            _patches.Patch(read, prefix: Hook(nameof(BeginReadScope)), postfix: Hook(nameof(CheckReadable)), finalizer: Hook(nameof(EndReadScope)));
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

    private sealed record CoverageScope(CoverageContext? Previous);
    private sealed record ReadScopeState(ReadScope? Previous);
    private sealed record ReadScope(string SourcePath, string SourceId, string? ETag, long RuntimeTicks);

    private sealed class CoverageContext(long expectedRuntimeTicks, int videoStreamIndex, string sourceId, string? eTag)
    {
        public long ExpectedRuntimeTicks { get; } = expectedRuntimeTicks;
        public int VideoStreamIndex { get; } = videoStreamIndex;
        public string SourceId { get; } = sourceId;
        public string? ETag { get; } = eTag;
        public string? Path { get; set; }
    }

    private static void BeginItemScope(BaseItem __0, ref bool __state)
    {
        __state = StrmSubtitleRequest.Value;
        StrmSubtitleRequest.Value = IsStrmShortcut(__0.IsShortcut, __0.Path);
    }

    private static Exception? EndItemScope(bool __state, Exception? __exception)
    {
        StrmSubtitleRequest.Value = __state;
        return __exception;
    }

    internal static bool IsStrmShortcut(bool isShortcut, string? path) =>
        isShortcut && path?.EndsWith(".strm", StringComparison.OrdinalIgnoreCase) == true;

    private static void BeginSourceCoverage(MediaSourceInfo __0, ref object? __state)
    {
        __state = new CoverageScope(SourceCoverage.Value);
        SourceCoverage.Value = null;
        if (StrmSubtitleRequest.Value && TryGetCoverageVideoStream(__0, out var videoStreamIndex))
            SourceCoverage.Value = new CoverageContext(__0.RunTimeTicks.GetValueOrDefault(), videoStreamIndex, __0.Id, __0.ETag);
    }

    private static void EndSourceCoverage(object? __state)
    {
        if (__state is CoverageScope scope) SourceCoverage.Value = scope.Previous;
    }

    private static void BeginReadScope(MediaSourceInfo __0, MediaStream __1, ref object? __state)
    {
        __state = new ReadScopeState(CurrentRead.Value);
        CurrentRead.Value = null;
        if (StrmSubtitleRequest.Value && !__1.IsExternal && TryGetCoverageVideoStream(__0, out _))
            CurrentRead.Value = new ReadScope(__0.Path, __0.Id, __0.ETag, __0.RunTimeTicks.GetValueOrDefault());
    }

    private static Exception? EndReadScope(object? __state, Exception? __exception)
    {
        if (__state is ReadScopeState scope) CurrentRead.Value = scope.Previous;
        return __exception;
    }

    private static bool TryGetCoverageVideoStream(MediaSourceInfo source, out int streamIndex)
    {
        streamIndex = -1;
        if (!source.IsRemote || source.RunTimeTicks is not > TimeSpan.TicksPerSecond
            || !Uri.TryCreate(source.Path, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return false;

        var video = source.MediaStreams.FirstOrDefault(stream => stream.Type == MediaStreamType.Video && !stream.IsExternal);
        if (video is null) return false;
        streamIndex = EncodingHelper.FindIndex(source.MediaStreams, video);
        return streamIndex >= 0;
    }

    private static string CoverageReceiptPath(string cachePath) => cachePath + ".subtitleguard.coverage";

    private static string ETagHash(string? eTag) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(eTag ?? string.Empty)));

    private static void WriteCoverageReceipt(string cachePath, CoverageContext coverage)
    {
        var cache = new FileInfo(cachePath);
        if (!cache.Exists || cache.Length == 0) throw new IOException("Cannot validate a missing subtitle cache.");
        var receiptPath = CoverageReceiptPath(cachePath);
        var temporary = receiptPath + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp";
        var receipt = string.Join('\n',
            "1",
            coverage.SourceId,
            ETagHash(coverage.ETag),
            coverage.ExpectedRuntimeTicks.ToString(CultureInfo.InvariantCulture),
            cache.Length.ToString(CultureInfo.InvariantCulture),
            cache.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options))
            {
                var bytes = Encoding.UTF8.GetBytes(receipt);
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temporary, receiptPath, true);
        }
        finally { File.Delete(temporary); }
    }

    private static bool HasValidCoverageReceipt(string cachePath, ReadScope source)
    {
        try
        {
            var cache = new FileInfo(cachePath);
            if (!cache.Exists || cache.Length == 0) return false;
            var values = File.ReadAllLines(CoverageReceiptPath(cachePath));
            return values.Length == 6
                && values[0] == "1"
                && values[1] == source.SourceId
                && values[2] == ETagHash(source.ETag)
                && long.TryParse(values[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var runtimeTicks)
                && runtimeTicks == source.RuntimeTicks
                && long.TryParse(values[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var length)
                && length == cache.Length
                && long.TryParse(values[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var modifiedTicks)
                && modifiedTicks == cache.LastWriteTimeUtc.Ticks;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static string AppendCoverageOutput(string arguments, string coveragePath, int videoStreamIndex) =>
        string.Concat("-reconnect 1 -reconnect_on_network_error 1 -reconnect_max_retries 2 -reconnect_delay_max 2 -reconnect_delay_total_max 4 ", arguments,
            " -map 0:", videoStreamIndex.ToString(CultureInfo.InvariantCulture),
            " -c:v copy -an -sn -fs ", MaximumCoverageBytes.ToString(CultureInfo.InvariantCulture),
            " -f framecrc \"", coveragePath, "\"");

    internal static bool HasCompleteCoverage(string path, long expectedRuntimeTicks)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length == 0 || file.Length >= MaximumCoverageBytes) return false;

            var numerator = 0L;
            var denominator = 0L;
            var firstStartTicks = long.MaxValue;
            var maximumEndTicks = long.MinValue;
            foreach (var line in File.ReadLines(path))
            {
                if (line.StartsWith("#tb 0:", StringComparison.Ordinal))
                {
                    var parts = line[6..].Trim().Split('/');
                    if (parts.Length != 2
                        || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out numerator)
                        || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out denominator)
                        || numerator <= 0 || denominator <= 0)
                        return false;
                    continue;
                }

                if (line.Length == 0 || line[0] == '#') continue;
                var fields = line.Split(',', 6);
                if (fields.Length < 4 || !int.TryParse(fields[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var streamIndex))
                    return false;
                if (streamIndex != 0) continue;
                if (numerator <= 0 || denominator <= 0
                    || !long.TryParse(fields[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pts)
                    || !long.TryParse(fields[3].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var duration))
                    return false;

                var packetEnd = checked(pts + Math.Max(0, duration));
                var startTicks = (long)((decimal)pts * numerator * TimeSpan.TicksPerSecond / denominator);
                var endTicks = (long)((decimal)packetEnd * numerator * TimeSpan.TicksPerSecond / denominator);
                firstStartTicks = Math.Min(firstStartTicks, startTicks);
                maximumEndTicks = Math.Max(maximumEndTicks, endTicks);
            }

            var coveredTicks = maximumEndTicks - firstStartTicks;
            var minimumCoverageTicks = Math.Max(0, expectedRuntimeTicks - TimeSpan.TicksPerSecond);
            return firstStartTicks != long.MaxValue && coveredTicks >= minimumCoverageTicks;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException or OverflowException)
        {
            return false;
        }
    }

    private static bool CoverageIncomplete(CoverageContext? coverage) =>
        coverage is not null && (coverage.Path is null || !HasCompleteCoverage(coverage.Path, coverage.ExpectedRuntimeTicks));

    private static bool DeleteCoverage(CoverageContext? coverage)
    {
        if (coverage?.Path is null) return true;
        try
        {
            File.Delete(coverage.Path);
            return !File.Exists(coverage.Path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string PendingPath(string path) => path + PendingSuffix;

    private static string CreateCoveragePath()
    {
        var path = Path.Combine(Path.GetTempPath(), "subtitleguard-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".framecrc");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.ReadWrite };
        if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (new FileStream(path, options)) { }
        return path;
    }

    private static bool CheckPending(string __0, string __1, ref bool __result)
    {
        if (File.Exists(PendingPath(__0)))
        {
            __result = false;
            return false;
        }

        var source = CurrentRead.Value;
        if (source is null || !string.Equals(__1, source.SourcePath, StringComparison.Ordinal)
            || string.Equals(Path.GetExtension(__0), ".mks", StringComparison.OrdinalIgnoreCase)
            || HasValidCoverageReceipt(__0, source))
            return true;

        __result = false;
        return false;
    }

    private static void ExtractionStarting(ref string __1, IReadOnlyList<string> __2)
    {
        var coverage = SourceCoverage.Value;
        if (coverage is not null && coverage.Path is null && __2.Count > 0)
        {
            coverage.Path = CreateCoveragePath();
            try { __1 = AppendCoverageOutput(__1, coverage.Path, coverage.VideoStreamIndex); }
            catch
            {
                if (!DeleteCoverage(coverage)) throw new IOException("Subtitle Guard could not clean up coverage evidence after command setup failed.");
                coverage.Path = null;
                throw;
            }
        }

        try
        {
            foreach (var path in __2)
            {
                using var marker = new FileStream(PendingPath(path), FileMode.Create, FileAccess.Write, FileShare.Read);
                marker.WriteByte(1);
                marker.Flush(true);
            }
        }
        catch
        {
            if (!DeleteCoverage(coverage)) throw new IOException("Subtitle Guard could not clean up coverage evidence after marker setup failed.");
            throw;
        }
    }

    private static void ExtractionFinished(IReadOnlyList<string> __2, ref Task __result) => __result = CompleteAsync(__result, __2, SourceCoverage.Value);

    private static async Task CompleteAsync(Task extraction, IReadOnlyList<string> paths, CoverageContext? coverage)
    {
        try
        {
            await extraction.ConfigureAwait(false);
            foreach (var path in paths)
            {
                if (!File.Exists(path) || new FileInfo(path).Length == 0)
                    throw new IOException("Subtitle extraction did not produce every expected file; outputs remain incomplete.");
            }
            if (coverage is not null)
            {
                foreach (var path in paths) WriteCoverageReceipt(path, coverage);
            }
            foreach (var path in paths) File.Delete(PendingPath(path));
        }
        finally
        {
            if (!DeleteCoverage(coverage))
                throw new IOException("Subtitle Guard could not remove temporary video coverage evidence.");
        }
    }

    private static void ProcessFinished(SubtitleEncoder __instance, string __0, CancellationToken __1, MethodBase __originalMethod,
        ref Task<(int ExitCode, string StandardError)> __result) =>
        __result = ValidateProcessAsync(__instance, __0, __1, __originalMethod, __result, !RetryingProcess.Value);

    private static async Task<(int ExitCode, string StandardError)> ValidateProcessAsync(SubtitleEncoder encoder, string arguments,
        CancellationToken cancellationToken, MethodBase processMethod, Task<(int ExitCode, string StandardError)> process, bool allowRetry)
    {
        var result = await process.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var coverage = SourceCoverage.Value;
        var coverageIncomplete = CoverageIncomplete(coverage);
        var failed = Failed(result) && !Recovered(result, coverage is not null && !coverageIncomplete);
        if (!failed && !coverageIncomplete) return result;
        var coverageRetryable = !failed && coverageIncomplete;
        if (!allowRetry || cancellationToken.IsCancellationRequested || (!Retryable(result.StandardError) && !coverageRetryable))
            return (-1, coverageIncomplete ? result.StandardError + Environment.NewLine + "[error] Subtitle Guard: remote source coverage was incomplete." : result.StandardError);
        if (!DeleteCoverage(coverage))
            return (-1, result.StandardError + Environment.NewLine + "[error] Subtitle Guard: coverage evidence could not be reset for retry.");

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

    private static bool Recovered((int ExitCode, string StandardError) result, bool completeCoverage)
    {
        if (result.ExitCode != 0 || !completeCoverage) return false;
        // FFmpeg separates progress updates with \r, so a diagnostic can follow one on the same physical line.
        var lines = result.StandardError.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim()).Where(line => !line.StartsWith("frame=", StringComparison.Ordinal)
                && !line.StartsWith("size=", StringComparison.Ordinal)).ToList();
        var remaining = new List<string>();
        var recovered = false;
        for (var index = 0; index < lines.Count; index++)
        {
            var eof = Regex.Match(lines[index], @"^\[http @ (?<connection>[0-9a-fx]+)\]\s+(?:\[(?:warning|error)\]\s+)?Stream ends prematurely at (?<offset>\d+), should be \d+$", RegexOptions.IgnoreCase);
            if (eof.Success && index + 1 < lines.Count && Regex.IsMatch(lines[index + 1],
                @"^\[http @ " + Regex.Escape(eof.Groups["connection"].Value) + @"\]\s+(?:\[(?:warning|error)\]\s+)?Will reconnect at " + eof.Groups["offset"].Value + @" in \d+ second\(s\), error=Input/output error\.$", RegexOptions.IgnoreCase))
            {
                recovered = true;
                index++;
                continue;
            }
            remaining.Add(lines[index]);
        }
        return recovered && !Failed((0, string.Join('\n', remaining)));
    }

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