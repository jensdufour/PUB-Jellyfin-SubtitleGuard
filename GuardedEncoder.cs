using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.MediaEncoding.Subtitles;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Nikse.SubtitleEdit.Core.Common;
using Nikse.SubtitleEdit.Core.SubtitleFormats;

namespace SubtitleGuard;

public sealed class Sandbox
{
    public Sandbox(string root)
    {
        Root = Path.GetFullPath(root);
        if (!File.Exists(Path.Combine(Root, ".subtitle-guard-sandbox")))
        {
            throw new InvalidOperationException("Missing sandbox marker.");
        }

        ValidatePath(Root, false);
    }

    public string Root { get; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    public Uri? HttpTestOrigin { get; init; }
    public TimeProvider Clock { get; init; } = TimeProvider.System;
    public TimeSpan RevalidationInterval { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan FailureCooldown { get; init; } = TimeSpan.FromSeconds(30);
    public string[] PilotUrls { get; init; } = [];
    public string[] InitialOrigins { get; init; } = [];
    public string[] PilotRedirectOrigins { get; init; } = [];
    public long TransferLimitBytes { get; init; } = RemoteSources.MaximumBodyBytes;
    public bool PersistRemoteState { get; init; }
    internal bool RequireLoopbackNetwork { get; init; }

    public string ValidateInput(string path)
    {
        if (!Uri.TryCreate(path, UriKind.Absolute, out var uri) || uri.IsFile)
        {
            return ValidatePath(path);
        }

        if (PilotUrls.Length > 0 || InitialOrigins.Length > 0)
        {
            return ValidatePilotUri(uri, false);
        }

        if (HttpTestOrigin is null || uri.Scheme != "http" || uri.Host != "127.0.0.1"
            || uri.GetLeftPart(UriPartial.Authority) != HttpTestOrigin.GetLeftPart(UriPartial.Authority)
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0
            || NetworkInterface.GetAllNetworkInterfaces().Any(network => network.NetworkInterfaceType != NetworkInterfaceType.Loopback))
        {
            throw new NotSupportedException("HTTP fixtures require the designated loopback origin in an isolated network namespace.");
        }

        return uri.AbsoluteUri;
    }

    public string ValidateRedirect(string source, string destination)
    {
        _ = PilotUrls.Length > 0 || InitialOrigins.Length > 0 ? ValidatePilotUri(new Uri(source, UriKind.Absolute), true) : ValidateInput(source);
        return PilotUrls.Length > 0 || InitialOrigins.Length > 0 ? ValidatePilotUri(new Uri(destination, UriKind.Absolute), true) : ValidateInput(destination);
    }

    private string ValidatePilotUri(Uri uri, bool redirect)
    {
        if (RequireLoopbackNetwork && NetworkInterface.GetAllNetworkInterfaces().Any(network => network.NetworkInterfaceType != NetworkInterfaceType.Loopback))
        {
            throw new NotSupportedException("Private host source policy requires loopback-only networking.");
        }

        if (PilotRedirectOrigins.Length > (InitialOrigins.Length > 0 ? 8 : 4))
        {
            throw new NotSupportedException("Too many pilot redirect origins.");
        }
        var origins = PilotRedirectOrigins.Select(value =>
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var origin) || origin.Scheme is not ("http" or "https")
                || origin.UserInfo.Length != 0 || origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0)
            {
                throw new NotSupportedException("Pilot redirect grants must be HTTP(S) origins without paths, query or credentials.");
            }
            return origin.GetLeftPart(UriPartial.Authority);
        }).ToArray();
        var allowed = PilotUrls.Contains(uri.AbsoluteUri, StringComparer.Ordinal)
            || InitialOrigins.Contains(uri.GetLeftPart(UriPartial.Authority), StringComparer.Ordinal)
            || (redirect && origins.Contains(uri.GetLeftPart(UriPartial.Authority), StringComparer.Ordinal));
        if (uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || !allowed
            || TransferLimitBytes <= 0 || TransferLimitBytes > 8L * 1024 * 1024 * 1024
            || Timeout <= TimeSpan.Zero || Timeout > TimeSpan.FromMinutes(15))
        {
            throw new NotSupportedException("Request is outside the pilot URL/origin policy or its resource limits.");
        }
        return uri.AbsoluteUri;
    }

    public string ValidatePath(string path, bool requireWithin = true)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            throw new NotSupportedException("Only absolute sandbox paths are supported.");
        }

        var full = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(Root, full);
        if (requireWithin && (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
        {
            throw new NotSupportedException("Path is outside the sandbox.");
        }

        for (var current = full; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new NotSupportedException("Symlinks and junctions are not accepted in the sandbox path.");
            }
        }

        return full;
    }
}

public sealed class GuardedEncoder(
    Sandbox sandbox,
    IMediaEncoder mediaEncoder,
    IMediaSourceManager mediaSources,
    ISubtitleParser parser) : ISubtitleEncoder, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly RemoteSources _remote = new(sandbox);

    public async Task<Stream> GetSubtitles(BaseItem item, string mediaSourceId, int subtitleStreamIndex,
        string outputFormat, long startTimeTicks, long endTimeTicks, bool preserveOriginalTimestamps,
        CancellationToken cancellationToken)
    {
        var candidates = await mediaSources.GetPlaybackMediaSources(item, null!, false, false, cancellationToken).ConfigureAwait(false);
        var source = candidates.Single(candidate => candidate.Id == mediaSourceId);
        var stream = source.MediaStreams.Single(candidate => candidate.Type == MediaStreamType.Subtitle && candidate.Index == subtitleStreamIndex);
        var path = await GetSubtitleFilePath(stream, source, cancellationToken).ConfigureAwait(false);
        var inputFormat = Normalize(Path.GetExtension(path).TrimStart('.'));
        var format = Normalize(outputFormat.ToLowerInvariant());
        if (inputFormat == format || (inputFormat == "ssa" && format == "ass"))
        {
            return File.OpenRead(path);
        }

        if (inputFormat is "sup" or "mks")
        {
            throw new NotSupportedException("Bitmap subtitles can only be returned in their extracted format.");
        }

        var subtitle = parser.Parse(File.OpenRead(path), inputFormat);
        subtitle.Paragraphs.RemoveAll(paragraph =>
            (paragraph.StartTime.TimeSpan.Ticks < startTimeTicks && paragraph.EndTime.TimeSpan.Ticks < startTimeTicks)
            || (endTimeTicks > 0 && paragraph.StartTime.TimeSpan.Ticks > endTimeTicks));
        if (!preserveOriginalTimestamps)
        {
            foreach (var paragraph in subtitle.Paragraphs)
            {
                paragraph.StartTime = new TimeCode(TimeSpan.FromTicks(Math.Max(0, paragraph.StartTime.TimeSpan.Ticks - startTimeTicks)));
                paragraph.EndTime = new TimeCode(TimeSpan.FromTicks(Math.Max(0, paragraph.EndTime.TimeSpan.Ticks - startTimeTicks)));
            }
        }

        SubtitleFormat writer = format switch
        {
            "srt" => new SubRip(),
            "vtt" => new WebVTT(),
            "ass" => new AdvancedSubStationAlpha(),
            "ssa" => new SubStationAlpha(),
            "ttml" => new TimedText10(),
            "json" => new JsonWriter(),
            _ => throw new NotSupportedException("Unsupported subtitle output format.")
        };
        return new MemoryStream(Encoding.UTF8.GetBytes(writer.ToText(subtitle, "subtitle")), false);
    }

    public async Task<string> GetSubtitleFileCharacterSet(MediaStream subtitleStream, string language,
        MediaSourceInfo mediaSource, CancellationToken cancellationToken)
    {
        if (Normalize(subtitleStream.Codec) is "sup" or "mks")
        {
            throw new NotSupportedException("Bitmap subtitles do not have a character set.");
        }

        var path = await GetSubtitleFilePath(subtitleStream, mediaSource, cancellationToken).ConfigureAwait(false);
        _ = new UTF8Encoding(false, true).GetString(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
        return "UTF-8";
    }

    public async Task<string> GetSubtitleFilePath(MediaStream subtitleStream, MediaSourceInfo mediaSource,
        CancellationToken cancellationToken)
    {
        if (subtitleStream.Type != MediaStreamType.Subtitle)
        {
            throw new ArgumentException("Expected a subtitle stream.", nameof(subtitleStream));
        }

        if (subtitleStream.IsExternal && !subtitleStream.Path.EndsWith(".mks", StringComparison.OrdinalIgnoreCase))
        {
            var external = sandbox.ValidatePath(subtitleStream.Path);
            var extension = Normalize(Path.GetExtension(external).TrimStart('.').ToLowerInvariant());
            if (extension != "sup" && !parser.SupportsFileExtension(extension))
            {
                throw new NotSupportedException("Unsupported external subtitle format.");
            }

            return external;
        }

        var paths = await Extract(mediaSource, cancellationToken).ConfigureAwait(false);
        return paths.TryGetValue(subtitleStream.Index, out var path)
            ? path : throw new NotSupportedException("Requested track is outside the supported subtitle formats.");
    }

    public async Task ExtractAllExtractableSubtitles(MediaSourceInfo mediaSource, CancellationToken cancellationToken)
    {
        _ = await Extract(mediaSource, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Dictionary<int, string>> Extract(MediaSourceInfo mediaSource, CancellationToken cancellationToken)
    {
        var tracks = mediaSource.MediaStreams.Where(stream => stream.Type == MediaStreamType.Subtitle
            && (!stream.IsExternal || stream.Path.EndsWith(".mks", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(stream => stream.Index).ToArray();
        if (tracks.Length == 0)
        {
            return [];
        }

        if (tracks.Select(track => track.Index).Distinct().Count() != tracks.Length || tracks.Any(track => track.Index < 0))
        {
            throw new NotSupportedException("Duplicate or invalid subtitle indices.");
        }

        foreach (var track in tracks)
        {
            if (Normalize(track.Codec) is not ("ass" or "ssa" or "srt" or "vtt" or "sup" or "mks"))
            {
                throw new NotSupportedException("Unsupported subtitle codec.");
            }
        }

        var inputs = tracks.Select(track => sandbox.ValidateInput(track.IsExternal ? track.Path : mediaSource.Path)).ToArray();
        var downloads = new Dictionary<string, RemoteSources.Download>(StringComparer.Ordinal);
        string Revision() => JsonSerializer.Serialize(tracks.Select((track, position) =>
        {
            if (IsRemote(inputs[position]))
            {
                return new { mediaSource.Id, track.Index, track.Codec, track.IsExternal, Path = inputs[position] + "#" + downloads[inputs[position]].Digest, Length = 0L, LastWriteTimeUtc = DateTime.MinValue };
            }

            var file = new FileInfo(inputs[position]);
            if (!file.Exists)
            {
                throw new FileNotFoundException("Sandbox media is missing.");
            }

            return new { mediaSource.Id, track.Index, track.Codec, track.IsExternal, Path = file.FullName, file.Length, file.LastWriteTimeUtc };
        }));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? staging = null;
        try
        {
            var cacheRoot = sandbox.ValidatePath(Path.Combine(sandbox.Root, "guard-cache"));
            Directory.CreateDirectory(cacheRoot);
            staging = Path.Combine(cacheRoot, ".pending-" + Guid.NewGuid().ToString("N"));
            string Filename(MediaStream track) => $"{track.Index}.{Normalize(track.Codec)}";

            async Task ExtractInput(string input, Func<Stream, CancellationToken, Task>? writeBody, CancellationToken token)
            {
                var demuxer = RemoteDemuxer(input, mediaSource);
                string? stagedInput = null;
                try
                {
                    if (writeBody is not null && demuxer != "matroska")
                    {
                        token.ThrowIfCancellationRequested();
                        var inputRoot = sandbox.ValidatePath(Path.Combine(sandbox.Root, "input-staging"));
                        if (OperatingSystem.IsLinux())
                        {
                            Directory.CreateDirectory(inputRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                        }
                        else
                        {
                            Directory.CreateDirectory(inputRoot);
                        }
                        var inputPath = sandbox.ValidatePath(Path.Combine(inputRoot, Guid.NewGuid().ToString("N") + ".media"));
                        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
                        if (OperatingSystem.IsLinux())
                        {
                            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                        }
                        await using (var destination = new FileStream(inputPath, options))
                        {
                            stagedInput = inputPath;
                            await writeBody(destination, token).ConfigureAwait(false);
                        }
                    }

                    token.ThrowIfCancellationRequested();
                    Directory.CreateDirectory(staging);
                    var arguments = new List<string> { "-nostdin", "-y", "-xerror", "-loglevel", "level+warning", "-protocol_whitelist", "file,pipe" };
                    if (writeBody is not null)
                    {
                        arguments.AddRange(["-format_whitelist", "matroska,webm,mov,avi,mpegts,mpeg"]);
                        if (demuxer is not null)
                        {
                            arguments.AddRange(["-f", demuxer]);
                        }
                    }
                    arguments.AddRange(["-i", stagedInput ?? (writeBody is null ? input : "pipe:0")]);
                    foreach (var track in tracks.Where((track, index) => inputs[index] == input))
                    {
                        var streamIndex = track.IsExternal
                            ? mediaSource.MediaStreams.Where(candidate => candidate.Type == MediaStreamType.Subtitle && candidate.IsExternal && candidate.Path == track.Path)
                                .OrderBy(candidate => candidate.Index).ToList().IndexOf(track)
                            : mediaSource.MediaStreams.Where(candidate => !candidate.IsExternal).OrderBy(candidate => candidate.Index).ToList().IndexOf(track);
                        arguments.AddRange(["-map", $"0:{streamIndex}", "-an", "-vn", "-c:s", track.Codec.Equals("mov_text", StringComparison.OrdinalIgnoreCase) ? "srt" : "copy", "-flush_packets", "1"]);
                        if (Normalize(track.Codec) == "mks")
                        {
                            arguments.AddRange(["-f", "matroska"]);
                        }
                        arguments.Add(Path.Combine(staging, Filename(track)));
                    }

                    await RunProcess(arguments, token, stagedInput is null ? writeBody : null).ConfigureAwait(false);
                }
                finally
                {
                    if (stagedInput is not null)
                    {
                        try { File.Delete(stagedInput); }
                        catch (UnauthorizedAccessException)
                        {
                            throw new IOException("Cannot remove the temporary subtitle input; no generation published.");
                        }
                    }
                }
            }

            foreach (var input in inputs.Distinct().Where(IsRemote))
            {
                downloads.Add(input, await _remote.Get(input, (writeBody, token) => ExtractInput(input, writeBody, token), cancellationToken).ConfigureAwait(false));
            }

            var revision = Revision();
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(revision))).ToLowerInvariant();
            var final = sandbox.ValidatePath(Path.Combine(cacheRoot, key));
            var files = tracks.ToDictionary(track => track.Index, track => Path.Combine(final, Filename(track)));
            if (Directory.Exists(final))
            {
                var expected = tracks.Select(Filename).Order().ToArray();
                var actual = Directory.GetFiles(final).Select(Path.GetFileName).Order().ToArray();
                if (!expected.SequenceEqual(actual) || files.Values.Any(path => new FileInfo(sandbox.ValidatePath(path)).Length == 0))
                {
                    throw new IOException("Committed sandbox generation is invalid; explicit recovery is required.");
                }

                foreach (var download in downloads.Values)
                {
                    download.Commit();
                }

                return files;
            }

            foreach (var input in downloads.Keys.ToArray())
            {
                if (!downloads[input].Extracted)
                {
                    var previous = downloads[input].Digest;
                    downloads[input] = await _remote.Get(input, (writeBody, token) => ExtractInput(input, writeBody, token), cancellationToken, forceBody: true).ConfigureAwait(false);
                    if (downloads[input].Digest != previous)
                    {
                        throw new IOException("Remote source changed while restoring a missing generation; retry later.");
                    }
                }
            }

            foreach (var input in inputs.Distinct().Where(input => !downloads.ContainsKey(input)))
            {
                await ExtractInput(input, null, cancellationToken).ConfigureAwait(false);
            }

            foreach (var track in tracks)
            {
                var path = Path.Combine(staging, Filename(track));
                if (!File.Exists(path) || new FileInfo(path).Length == 0)
                {
                    throw new IOException("Extraction did not produce every required output.");
                }
            }

            if (revision != Revision())
            {
                throw new IOException("Source changed during extraction.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, final);
            foreach (var download in downloads.Values)
            {
                download.Commit();
            }
            return files;
        }
        catch
        {
            foreach (var input in downloads.Keys)
            {
                _remote.Failed(input);
            }

            throw;
        }
        finally
        {
            try
            {
                if (staging is not null && Directory.Exists(staging))
                {
                    Directory.Delete(staging, true);
                }
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    private async Task RunProcess(IReadOnlyList<string> arguments, CancellationToken cancellationToken,
        Func<Stream, CancellationToken, Task>? writeBody = null)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(mediaEncoder.EncoderPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        using var input = process.StandardInput;
        using var errorReader = process.StandardError;
        using var outputReader = process.StandardOutput;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(sandbox.Timeout);
        var stderr = HasFailureDiagnostics(errorReader, timeout.Token);
        var stdout = outputReader.BaseStream.CopyToAsync(Stream.Null, 65536, timeout.Token);
        try
        {
            if (writeBody is not null)
            {
                await writeBody(input.BaseStream, timeout.Token).ConfigureAwait(false);
            }
            input.Close();
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await Task.WhenAll(stderr, stdout).WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            timeout.Cancel();
            if (!process.HasExited)
            {
                process.Kill(true);
            }

            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            try
            {
                await Task.WhenAll(stderr, stdout).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or TimeoutException)
            {
            }
            throw;
        }

        if (process.ExitCode != 0 || await stderr.ConfigureAwait(false))
        {
            throw new IOException($"Subtitle extraction failed its exit/diagnostic checks (exit {process.ExitCode}); no generation published.");
        }
    }

    private static async Task<bool> HasFailureDiagnostics(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var tail = string.Empty;
        var failed = false;
        int count;
        while ((count = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            var text = tail + new string(buffer, 0, count);
            failed |= text.Contains("[error]", StringComparison.OrdinalIgnoreCase)
                || text.Contains("[fatal]", StringComparison.OrdinalIgnoreCase)
                || text.Contains("File ended prematurely", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Stream ends prematurely", StringComparison.OrdinalIgnoreCase);
            tail = text.Length <= 32 ? text : text[^32..];
        }

        return failed;
    }

    private static string? RemoteDemuxer(string input, MediaSourceInfo mediaSource)
    {
        var extension = Path.GetExtension(IsRemote(input) ? new Uri(input).AbsolutePath : input).ToLowerInvariant();
        if (extension is ".mp4" or ".m4v" or ".mov") { return "mov"; }
        if (extension == ".avi") { return "avi"; }
        if (input != mediaSource.Path) { return extension is ".mks" or ".mkv" or ".webm" ? "matroska" : null; }
        var containers = (mediaSource.Container ?? string.Empty).ToLowerInvariant().Split(',');
        if (containers.Any(container => container is "mov" or "mp4" or "m4v")) { return "mov"; }
        if (containers.Contains("avi")) { return "avi"; }
        if (containers.Any(container => container is "matroska" or "mkv" or "webm") || extension is ".mks" or ".mkv" or ".webm") { return "matroska"; }
        if (containers.Any(container => container is "mpegts" or "ts")) { return "mpegts"; }
        if (containers.Any(container => container is "mpeg" or "mpg")) { return "mpeg"; }
        return null;
    }

    private static string Normalize(string codec) => codec.ToLowerInvariant() switch
    {
        "subrip" or "mov_text" => "srt",
        "webvtt" => "vtt",
        "hdmv_pgs_subtitle" or "pgssub" => "sup",
        "dvd_subtitle" or "dvdsub" => "mks",
        var format => format
    };
    private static bool IsRemote(string path) => path.StartsWith("http://", StringComparison.Ordinal) || path.StartsWith("https://", StringComparison.Ordinal);

    public void Dispose()
    {
        _remote.Dispose();
        _gate.Dispose();
    }
}