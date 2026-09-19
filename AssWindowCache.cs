using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SubtitleGuard;

internal sealed class AssWindowCache(string encoderPath, string root, CancellationToken stopping)
{
    private const long MaximumBytes = 8 * 1024 * 1024;
    private readonly Dictionary<string, SourceState> _sources = [];
    private readonly object _files = new();
    private readonly SemaphoreSlim _workers = new(2);

    public async Task<string> PrepareAsync(string source, int subtitleIndex, int videoIndex,
        IReadOnlyDictionary<string, string> headers, long startTicks, long endTicks, bool finalWindow, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        stopping.ThrowIfCancellationRequested();
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || subtitleIndex < 0 || videoIndex < 0 || startTicks < 0 || endTicks <= startTicks)
            throw new NotSupportedException("Unsupported subtitle window source.");
        var identity = source + "\n" + subtitleIndex.ToString(CultureInfo.InvariantCulture)
            + "\n" + string.Join("\n", headers.OrderBy(pair => pair.Key).Select(pair => pair.Key + ":" + pair.Value));
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        SourceState state;
        lock (_sources)
        {
            stopping.ThrowIfCancellationRequested();
            if (!_sources.TryGetValue(key, out state!))
            {
                if (_sources.Count >= 16)
                {
                    var expired = _sources.Where(pair => DateTime.UtcNow - pair.Value.LastUsed > TimeSpan.FromMinutes(10))
                        .OrderBy(pair => pair.Value.LastUsed).FirstOrDefault();
                    if (expired.Value is null || !expired.Value.Gate.Wait(0))
                        throw new IOException("Subtitle window source capacity reached.");
                    try
                    {
                        lock (_files) Directory.Delete(expired.Value.Directory, true);
                        _sources.Remove(expired.Key);
                    }
                    finally { expired.Value.Gate.Release(); }
                }
                var directory = Path.Combine(root, key);
                Directory.CreateDirectory(directory);
                state = new SourceState(directory);
                _sources.Add(key, state);
            }
            state.LastUsed = DateTime.UtcNow;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping);
        linked.CancelAfter(TimeSpan.FromSeconds(45));
        var token = linked.Token;
        await state.Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (endTicks - state.CoveredTicks > TimeSpan.FromMinutes(2).Ticks)
                throw new NotSupportedException("Cold seek exceeds the bounded preparation window.");
            if (state.CoveredTicks < endTicks)
            {
                await _workers.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    var pending = Path.Combine(state.Directory, "extract.pending.ass");
                    try
                    {
                        for (var attempt = 0; ; attempt++)
                        {
                            try
                            {
                                await ExtractAsync(source, subtitleIndex, videoIndex, headers, state.CoveredTicks, endTicks, finalWindow, pending, token).ConfigureAwait(false);
                                break;
                            }
                            catch (SourceReadException exception) when (exception.Retryable && attempt == 0 && !token.IsCancellationRequested)
                            {
                            }
                        }
                        var lines = await File.ReadAllLinesAsync(pending, token).ConfigureAwait(false);
                        var header = string.Join('\n', lines.Where(line => !line.StartsWith("Dialogue:", StringComparison.Ordinal))) + "\n";
                        if (!header.Contains("[Events]", StringComparison.Ordinal)
                            || !header.Contains("Format: Layer, Start, End,", StringComparison.Ordinal)
                            || (state.Header is not null && state.Header != header))
                            throw new IOException("Subtitle header changed or is unsupported.");
                        var events = lines.Where(line => line.StartsWith("Dialogue:", StringComparison.Ordinal)).ToArray();
                        foreach (var line in events) _ = ReadCue(line);
                        var merged = new List<string>(state.Events);
                        var counts = state.Events.GroupBy(line => line, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
                        foreach (var group in events.GroupBy(line => line, StringComparer.Ordinal))
                        {
                            var missing = group.Count() - counts.GetValueOrDefault(group.Key);
                            if (missing > 0) merged.AddRange(Enumerable.Repeat(group.Key, missing));
                        }
                        if (merged.Count > 100_000 || Encoding.UTF8.GetByteCount(header) + merged.Sum(line => (long)Encoding.UTF8.GetByteCount(line) + 1) + 2 > MaximumBytes)
                            throw new IOException("Subtitle window cache size limit reached.");
                        state.Header = header;
                        state.Events = merged;
                        state.CoveredTicks = endTicks;
                    }
                    finally { File.Delete(pending); }
                }
                finally { _workers.Release(); }
            }

            var path = Path.Combine(state.Directory, $"{startTicks}-{endTicks}.window.ass");
            lock (_files)
            {
                if (File.Exists(path))
                {
                    File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                    return path;
                }
            }
            if (new DriveInfo(root).AvailableFreeSpace < 256L * 1024 * 1024)
                throw new IOException("Insufficient free space for subtitle preparation.");
            var selected = state.Events.Where(line =>
            {
                var cue = ReadCue(line);
                return cue.Start < endTicks && cue.End > startTicks;
            });
            var temporary = path + ".pending";
            try
            {
                await File.WriteAllTextAsync(temporary, state.Header + string.Join('\n', selected) + "\n", new UTF8Encoding(false), token).ConfigureAwait(false);
                lock (_files)
                {
                    var files = Directory.GetFiles(root, "*.window.ass", SearchOption.AllDirectories);
                    foreach (var old in files.Where(file => DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromMinutes(10))) File.Delete(old);
                    if (Directory.GetFiles(root, "*.window.ass", SearchOption.AllDirectories).Length >= 64)
                        throw new IOException("Subtitle window file capacity reached.");
                    File.Move(temporary, path, true);
                }
            }
            finally { File.Delete(temporary); }
            return path;
        }
        finally { state.Gate.Release(); }
    }

    public async Task CleanupAsync(CancellationToken cancellationToken)
    {
        SourceState[] states;
        lock (_sources) states = _sources.Values.ToArray();
        var acquired = new List<SourceState>();
        try
        {
            foreach (var state in states)
            {
                await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                acquired.Add(state);
            }
            lock (_files)
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }
        finally { foreach (var state in acquired) state.Gate.Release(); }
    }

    private async Task ExtractAsync(string source, int subtitleIndex, int videoIndex, IReadOnlyDictionary<string, string> headers,
        long startTicks, long endTicks, bool finalWindow, string output, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(encoderPath)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var argument in new[] { "-hide_banner", "-loglevel", "warning", "-nostdin", "-y", "-copyts" }) start.ArgumentList.Add(argument);
        if (source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add("-tls_verify");
            start.ArgumentList.Add("1");
        }
        if (headers.Count > 0)
        {
            if (headers.Any(pair => pair.Key.IndexOfAny(['\r', '\n', ':']) >= 0 || pair.Value.IndexOfAny(['\r', '\n']) >= 0))
                throw new IOException("Invalid source HTTP headers.");
            start.ArgumentList.Add("-headers");
            start.ArgumentList.Add(string.Join("", headers.Select(pair => pair.Key + ": " + pair.Value + "\r\n")));
        }
        foreach (var argument in new[]
        {
            "-ss", Seconds(startTicks), "-t", Seconds(endTicks - startTicks + TimeSpan.TicksPerSecond), "-i", source,
            "-map", "0:" + subtitleIndex.ToString(CultureInfo.InvariantCulture), "-c:s", "copy", "-fs", MaximumBytes.ToString(CultureInfo.InvariantCulture),
            "-f", "ass", output, "-map", "0:" + videoIndex.ToString(CultureInfo.InvariantCulture), "-c:v", "copy", "-an", "-sn", "-f", "framecrc", "pipe:1"
        }) start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException("Cannot start native subtitle preparation.");
        using var registration = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });
        var timestamps = ReadCoverageAsync(process.StandardOutput, cancellationToken);
        var errors = ReadErrorsAsync(process.StandardError, cancellationToken);
        try
        {
            await Task.WhenAll(timestamps, errors, process.WaitForExitAsync(cancellationToken)).ConfigureAwait(false);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        var diagnostic = await errors.ConfigureAwait(false);
        if (process.ExitCode != 0 || diagnostic.Failed)
            throw new SourceReadException(diagnostic.Retryable);
        var requiredCoverage = finalWindow ? Math.Max(startTicks, endTicks - TimeSpan.TicksPerSecond) : endTicks;
        var coveredTicks = await timestamps.ConfigureAwait(false);
        if (coveredTicks <= startTicks || coveredTicks < requiredCoverage || !File.Exists(output)
            || new FileInfo(output).Length is 0 or >= MaximumBytes)
            throw new SourceReadException(false);
    }

    private static async Task<long> ReadCoverageAsync(StreamReader reader, CancellationToken token)
    {
        long numerator = 0, denominator = 0, covered = 0;
        var count = 0;
        while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
        {
            if (++count > 200_000 || line.Length > 4096) throw new IOException("Unexpected video coverage output.");
            if (line.StartsWith("#tb 0:", StringComparison.Ordinal))
            {
                var fraction = line[6..].Trim().Split('/');
                numerator = long.Parse(fraction[0], CultureInfo.InvariantCulture);
                denominator = long.Parse(fraction[1], CultureInfo.InvariantCulture);
                if (numerator <= 0 || denominator <= 0) throw new IOException("Invalid video time base.");
            }
            else if (!line.StartsWith('#') && !string.IsNullOrWhiteSpace(line))
            {
                var fields = line.Split(',');
                if (fields.Length < 6 || numerator == 0 || denominator == 0) throw new IOException("Missing video coverage evidence.");
                var end = long.Parse(fields[1], CultureInfo.InvariantCulture) + long.Parse(fields[3], CultureInfo.InvariantCulture);
                covered = Math.Max(covered, checked((long)((decimal)end * numerator * TimeSpan.TicksPerSecond / denominator)));
            }
        }
        return covered;
    }

    private static async Task<(bool Failed, bool Retryable)> ReadErrorsAsync(StreamReader reader, CancellationToken token)
    {
        var failed = false;
        var retryable = false;
        while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
        {
            var transient = new[] { "premature", "Connection reset", "Connection timed out", "HTTP error 429", "HTTP error 5" }
                .Any(value => line.Contains(value, StringComparison.OrdinalIgnoreCase));
            retryable |= transient;
            failed |= transient || new[] { "Input/output error", "Error during demuxing", "[error]", "[fatal]", "Invalid data" }
                .Any(value => line.Contains(value, StringComparison.OrdinalIgnoreCase));
        }
        return (failed, retryable);
    }

    private static (long Start, long End) ReadCue(string line)
    {
        var fields = line.Split(',', 10);
        if (fields.Length != 10) throw new IOException("Invalid ASS event.");
        return (TimeSpan.Parse(fields[1], CultureInfo.InvariantCulture).Ticks, TimeSpan.Parse(fields[2], CultureInfo.InvariantCulture).Ticks);
    }

    private static string Seconds(long ticks) => ((decimal)ticks / TimeSpan.TicksPerSecond).ToString("0.#######", CultureInfo.InvariantCulture);

    private sealed class SourceState(string directory)
    {
        public string Directory { get; } = directory;
        public SemaphoreSlim Gate { get; } = new(1);
        public DateTime LastUsed { get; set; } = DateTime.UtcNow;
        public long CoveredTicks { get; set; }
        public string? Header { get; set; }
        public List<string> Events { get; set; } = [];
    }

    private sealed class SourceReadException(bool retryable) : IOException("Native subtitle window extraction did not complete.")
    {
        public bool Retryable { get; } = retryable;
    }
}