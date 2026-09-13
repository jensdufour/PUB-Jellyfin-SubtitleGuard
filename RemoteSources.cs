using System.Net;
using System.ComponentModel;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SubtitleGuard;

public sealed class RemoteSourceFailure(string stage, int? statusCode) : IOException(
    $"Remote source failed validation or transfer; retry deferred. Stage={((stage is "request" or "response" or "redirect" or "transfer") ? stage : "unknown")}; HTTP={((statusCode is >= 100 and <= 599) ? statusCode.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unavailable")}.")
{
    public string Stage { get; } = stage;
    public int? StatusCode { get; } = statusCode;
}

public sealed class RemoteSources : IDisposable
{
    public const long MaximumBodyBytes = 256L * 1024 * 1024;
    private const int MaximumStateBytes = 1024 * 1024;
    private readonly Sandbox sandbox;
    private IOException? _stateFailure;
    private static readonly JsonSerializerOptions StateOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false
    };
    public delegate Task ExtractStream(Func<Stream, CancellationToken, Task> writeBody, CancellationToken cancellationToken);
    private readonly HttpClient _client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None
    }) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _failures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _originFailures = new(StringComparer.Ordinal);

    private sealed record Entry(string Digest, string FinalUrl, string? ETag, DateTimeOffset? Modified, DateTimeOffset CheckedAt);
    private sealed record State(int Version, Dictionary<string, Entry> Entries,
        Dictionary<string, DateTimeOffset> Failures, Dictionary<string, DateTimeOffset> OriginFailures);

    public RemoteSources(Sandbox sandbox)
    {
        this.sandbox = sandbox;
        try
        {
            if (sandbox.PersistRemoteState)
            {
                LoadState();
            }
        }
        catch
        {
            _client.Dispose();
            throw;
        }
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private string StatePath(string name)
    {
        var path = sandbox.ValidatePath(Path.Combine(sandbox.Root, name));
        if (new FileInfo(path).LinkTarget is not null)
        {
            throw new IOException("Remote state paths must not be symbolic links.");
        }

        return path;
    }

    private void ValidateState(State state)
    {
        static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
        var latestMetadata = sandbox.Clock.GetUtcNow() + TimeSpan.FromMinutes(5);
        var latestCooldown = sandbox.Clock.GetUtcNow() + TimeSpan.FromDays(30);
        if (state.Version != 1 || state.Entries is null || state.Failures is null || state.OriginFailures is null
            || state.Entries.Count > 4096 || state.Failures.Count > 4096 || state.OriginFailures.Count > 4096
            || state.Entries.Any(pair => !IsHash(pair.Key) || pair.Value is not { } entry
                || !IsHash(entry.Digest) || !IsHash(entry.FinalUrl)
                || entry.CheckedAt < DateTimeOffset.UnixEpoch || entry.CheckedAt > latestMetadata
                || (entry.Modified is { } modified && (modified < DateTimeOffset.UnixEpoch || modified > latestMetadata))
                || (entry.ETag is { } tag && (tag.Length > 1024 || !EntityTagHeaderValue.TryParse(tag, out var parsed) || parsed.IsWeak)))
            || state.Failures.Concat(state.OriginFailures).Any(pair => !IsHash(pair.Key)
                || pair.Value < DateTimeOffset.UnixEpoch || pair.Value > latestCooldown))
        {
            throw new IOException("Invalid remote state version, hashes, validators, counts or timestamps.");
        }
    }

    private void LoadState()
    {
        try
        {
            FileStream stream;
            try { stream = new FileStream(StatePath("remote-state.json"), FileMode.Open, FileAccess.Read, FileShare.Read); }
            catch (FileNotFoundException) { return; }
            using (stream)
            {
                if (stream.Length > MaximumStateBytes)
                {
                    throw new IOException("Remote state exceeds one MiB.");
                }

                var bytes = new byte[MaximumStateBytes + 1];
                var count = stream.ReadAtLeast(bytes, bytes.Length, false);
                if (count > MaximumStateBytes)
                {
                    throw new IOException("Remote state exceeds one MiB.");
                }

                var state = JsonSerializer.Deserialize<State>(bytes.AsSpan(0, count), StateOptions)
                    ?? throw new IOException("Remote state is empty.");
                ValidateState(state);
                foreach (var pair in state.Entries) { _entries.Add(pair.Key, pair.Value); }
                foreach (var pair in state.Failures) { _failures.Add(pair.Key, pair.Value); }
                foreach (var pair in state.OriginFailures) { _originFailures.Add(pair.Key, pair.Value); }
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new IOException("Remote state could not be loaded; refusing to reset revalidation or cooldowns.", exception);
        }
    }

    private void SaveState()
    {
        if (!sandbox.PersistRemoteState) { return; }
        if (_stateFailure is not null) { throw _stateFailure; }
        string? pending = null;
        try
        {
            var entries = sandbox.PilotUrls.Length == 0 && sandbox.InitialOrigins.Length == 0 ? _entries
                : _entries.ToDictionary(pair => pair.Key, pair => pair.Value with { ETag = null }, StringComparer.Ordinal);
            var state = new State(1, entries, _failures, _originFailures);
            ValidateState(state);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(state, StateOptions);
            if (bytes.Length > MaximumStateBytes)
            {
                throw new IOException("Remote state exceeds one MiB.");
            }

            var destination = StatePath("remote-state.json");
            pending = StatePath($".{Guid.NewGuid():N}.pending");
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (OperatingSystem.IsLinux())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using (var stream = new FileStream(pending, options))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }

            File.Move(StatePath(Path.GetFileName(pending)), StatePath(Path.GetFileName(destination)), true);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _stateFailure = new IOException("Remote state could not be saved; further source requests are blocked.", exception);
            throw _stateFailure;
        }
        finally
        {
            if (pending is not null) { File.Delete(pending); }
        }
    }

    private void Commit(string key, Entry entry)
    {
        _entries[key] = entry;
        SaveState();
    }

    public sealed class Download
    {
        public required string Digest { get; init; }
        public bool Extracted { get; init; }
        public required Action Commit { get; init; }
    }

    public void Failed(string url)
    {
        var key = Hash(url);
        var until = sandbox.Clock.GetUtcNow() + sandbox.FailureCooldown;
        if (!_failures.TryGetValue(key, out var previous) || previous < until)
        {
            _failures[key] = until;
        }

        SaveState();
    }

    public async Task<Download> Get(string input, ExtractStream extract, CancellationToken cancellationToken, bool forceBody = false)
    {
        if (_stateFailure is not null) { throw _stateFailure; }
        var url = sandbox.ValidateInput(input);
        var key = Hash(url);
        var origin = Hash(new Uri(url).GetLeftPart(UriPartial.Authority));
        var now = sandbox.Clock.GetUtcNow();
        if ((_failures.TryGetValue(key, out var until) && until > now)
            || (_originFailures.TryGetValue(origin, out until) && until > now))
        {
            throw new IOException("Remote subtitle source is in cooldown; no request sent.");
        }

        _entries.TryGetValue(key, out var known);
        if (!forceBody && known is not null && now - known.CheckedAt < sandbox.RevalidationInterval)
        {
            return new Download { Digest = known.Digest, Commit = () => { } };
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(sandbox.Timeout);
        var stage = "request";
        int? statusCode = null;
        try
        {
            var current = url;
            for (var hops = 0; hops <= 3; hops++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                var conditional = !forceBody && known is not null && Hash(current) == known.FinalUrl;
                if (conditional && known!.ETag is not null)
                {
                    request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(known.ETag));
                }
                else if (conditional && known!.Modified.HasValue)
                {
                    request.Headers.IfModifiedSince = known.Modified;
                }
                else
                {
                    conditional = false;
                }

                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                statusCode = (int)response.StatusCode;
                stage = "response";
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect
                    or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    if (hops == 3 || response.Headers.Location is null)
                    {
                        throw new IOException("Remote redirect limit or invalid redirect.");
                    }

                    stage = "redirect";
                    current = sandbox.ValidateRedirect(current, new Uri(new Uri(current), response.Headers.Location).AbsoluteUri);
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.NotModified)
                {
                    if (!conditional || known is null
                        || (response.Headers.ETag is not null && known.ETag is not null && response.Headers.ETag.ToString() != known.ETag))
                    {
                        throw new IOException("Unexpected remote cache validation response.");
                    }

                    var refreshed = known with { CheckedAt = sandbox.Clock.GetUtcNow() };
                    return new Download { Digest = known.Digest, Commit = () => Commit(key, refreshed) };
                }

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    var retry = response.Headers.RetryAfter;
                    var retryUntil = sandbox.Clock.GetUtcNow() + sandbox.FailureCooldown;
                    var requested = retry?.Date ?? (retry?.Delta is { } delta ? sandbox.Clock.GetUtcNow() + delta : retryUntil);
                    if (requested > retryUntil)
                    {
                        retryUntil = requested;
                    }

                    _failures[key] = retryUntil;
                    if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                    {
                        _originFailures[origin] = retryUntil;
                        _originFailures[Hash(new Uri(current).GetLeftPart(UriPartial.Authority))] = retryUntil;
                    }

                    throw new IOException($"Remote subtitle source returned HTTP {(int)response.StatusCode}; retry deferred.");
                }

                if (response.Content.Headers.ContentEncoding.Count != 0
                    || response.Content.Headers.ContentLength == 0 || response.Content.Headers.ContentLength > sandbox.TransferLimitBytes)
                {
                    throw new IOException("Remote fixture size or encoding is unsupported.");
                }

                long total = 0;
                stage = "transfer";
                var bodyComplete = false;
                using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await extract(async (destination, transferToken) =>
                {
                    await using var source = await response.Content.ReadAsStreamAsync(transferToken).ConfigureAwait(false);
                    var buffer = new byte[65536];
                    int count;
                    while ((count = await source.ReadAsync(buffer, transferToken).ConfigureAwait(false)) != 0)
                    {
                        total += count;
                        if (total > sandbox.TransferLimitBytes)
                        {
                            throw new IOException("Remote source exceeds the configured transfer limit.");
                        }

                        digest.AppendData(buffer, 0, count);
                        await destination.WriteAsync(buffer.AsMemory(0, count), transferToken).ConfigureAwait(false);
                    }

                    if (total == 0 || (response.Content.Headers.ContentLength is { } length && total != length))
                    {
                        throw new IOException("Incomplete remote fixture body.");
                    }

                    bodyComplete = true;
                }, timeout.Token).ConfigureAwait(false);

                if (!bodyComplete)
                {
                    throw new IOException("Remote extraction did not consume the full body.");
                }

                var etag = response.Headers.ETag is { IsWeak: false } tag ? tag.ToString() : null;
                var entry = new Entry(Convert.ToHexString(digest.GetHashAndReset()), Hash(current), etag,
                    response.Content.Headers.LastModified, sandbox.Clock.GetUtcNow());
                return new Download { Digest = entry.Digest, Extracted = true, Commit = () => Commit(key, entry) };
            }

            throw new IOException("Remote redirect limit exceeded.");
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException or OperationCanceledException or NotSupportedException or UriFormatException or Win32Exception)
        {
            try { Failed(url); }
            catch (IOException persistenceFailure)
            {
                throw new IOException("Remote source validation or transfer failed; cooldown persistence also failed.", persistenceFailure);
            }
            if (exception is OperationCanceledException)
            {
                throw;
            }

            throw new RemoteSourceFailure(stage, statusCode);
        }
    }

    public void Dispose() => _client.Dispose();
}