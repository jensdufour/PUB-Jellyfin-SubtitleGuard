using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubtitleGuard;

internal static class PersistentStateTests
{
    private const string SyntheticKey = "persistent-state-synthetic-api-key";

    public static async Task Run(string root, Func<string, Func<Task>, Task> test)
    {
        Check(OperatingSystem.IsLinux()
            && NetworkInterface.GetAllNetworkInterfaces().All(network => network.NetworkInterfaceType == NetworkInterfaceType.Loopback),
            "Persistent state checks require Linux unshare --net with only loopback enabled.");
        Check(new[] { "http_proxy", "https_proxy", "all_proxy", "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY" }
            .All(name => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name))), "Clear inherited proxy variables.");

        var payload = await File.ReadAllBytesAsync(Path.Combine(root, "bilingual.mkv"));
        var expectedDigest = Convert.ToHexString(SHA256.HashData(payload));
        var endpoints = new ConcurrentDictionary<string, Endpoint>(StringComparer.Ordinal);
        var requests = 0;
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = root });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        app.Run(async context =>
        {
            Interlocked.Increment(ref requests);
            if (!endpoints.TryGetValue(context.Request.Path.Value!, out var endpoint))
            {
                context.Response.StatusCode = 404;
                return;
            }

            Interlocked.Increment(ref endpoint.Requests);
            if (context.Request.Headers.ContainsKey("If-None-Match") || context.Request.Headers.ContainsKey("If-Modified-Since"))
            {
                Interlocked.Increment(ref endpoint.ConditionalRequests);
            }

            if (endpoint.RedirectTo is not null)
            {
                context.Response.StatusCode = 302;
                context.Response.Headers.Location = endpoint.RedirectTo;
                return;
            }

            if (endpoint.Status != 200)
            {
                context.Response.StatusCode = endpoint.Status;
                context.Response.Headers.RetryAfter = endpoint.RetryAfter;
                return;
            }

            context.Response.Headers.ETag = endpoint.ETag;
            context.Response.Headers.LastModified = endpoint.Modified?.ToString("R");
            if ((endpoint.ETag is not null && context.Request.Headers.IfNoneMatch == endpoint.ETag)
                || (endpoint.ETag is null && endpoint.Modified is not null
                    && context.Request.Headers.IfModifiedSince == endpoint.Modified.Value.ToString("R")))
            {
                context.Response.StatusCode = 304;
                return;
            }

            Interlocked.Increment(ref endpoint.Bodies);
            context.Response.ContentLength = payload.Length;
            await context.Response.Body.WriteAsync(payload, context.RequestAborted);
        });

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await app.StartAsync(deadline.Token);
        try
        {
            var origin = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            Task Case(string name, Func<Task> action) => test("Persistent metadata: " + name, action);
            (Sandbox Sandbox, string Url, Endpoint Endpoint) Create(string name, bool persist = true, TimeSpan? interval = null)
            {
                var scenarioRoot = Path.Combine(root, "persistent-" + name);
                Check(!Path.Exists(scenarioRoot), "Scenario root already exists.");
                if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException(); }
                Directory.CreateDirectory(scenarioRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                using (File.Open(Path.Combine(scenarioRoot, ".subtitle-guard-sandbox"), FileMode.CreateNew)) { }
                var sandbox = new Sandbox(scenarioRoot)
                {
                    HttpTestOrigin = origin, PersistRemoteState = persist, Clock = new ManualClock(),
                    RevalidationInterval = interval ?? TimeSpan.FromMinutes(5)
                };
                var endpoint = new Endpoint();
                Check(endpoints.TryAdd("/" + name + ".mkv", endpoint), "Duplicate endpoint.");
                return (sandbox, new Uri(origin, "/" + name + ".mkv?api_key=" + SyntheticKey).AbsoluteUri, endpoint);
            }

            Task<RemoteSources.Download> Get(RemoteSources remote, string url) => remote.Get(url,
                (producer, token) => producer(Stream.Null, token), deadline.Token);

            await Case("default opt-out never reads or writes state", async () =>
            {
                var fixture = Create("disabled", false);
                Check(!new Sandbox(fixture.Sandbox.Root).PersistRemoteState, "Persistence must default to false.");
                using (var first = new RemoteSources(fixture.Sandbox)) { (await Get(first, fixture.Url)).Commit(); }
                var path = StatePath(fixture.Sandbox);
                Check(!File.Exists(path), "Opt-out wrote state.");
                await File.WriteAllTextAsync(path, "invalid ignored opt-out state");
                using var second = new RemoteSources(fixture.Sandbox);
                (await Get(second, fixture.Url)).Commit();
                Check(fixture.Endpoint.Requests == 2 && await File.ReadAllTextAsync(path) == "invalid ignored opt-out state",
                    "Opt-out read or replaced state instead of fetching.");
            });

            await Case("committed metadata survives recreation for a 24-hour window", async () =>
            {
                var fixture = Create("warm", interval: TimeSpan.FromHours(24));
                using (var first = new RemoteSources(fixture.Sandbox))
                {
                    var cold = await Get(first, fixture.Url);
                    Check(cold.Extracted && cold.Digest == expectedDigest && fixture.Endpoint.Requests == 1, "Missing state did not fetch the full fixture.");
                    cold.Commit();
                }

                Advance(fixture.Sandbox, TimeSpan.FromHours(23));
                using var second = new RemoteSources(fixture.Sandbox);
                var warm = await Get(second, fixture.Url);
                Check(!warm.Extracted && warm.Digest == expectedDigest && fixture.Endpoint.Requests == 1,
                    "Recreated metadata fetched within the revalidation window.");
            });

            await Case("uncommitted metadata cannot be reused by a new instance", async () =>
            {
                var fixture = Create("uncommitted");
                using var first = new RemoteSources(fixture.Sandbox);
                _ = await Get(first, fixture.Url);
                Check(!File.Exists(StatePath(fixture.Sandbox)), "State was saved before Commit.");
                using var second = new RemoteSources(fixture.Sandbox);
                var repeated = await Get(second, fixture.Url);
                Check(repeated.Extracted && repeated.Digest == expectedDigest && fixture.Endpoint.Requests == 2,
                    "Uncommitted metadata suppressed a source fetch.");
            });

            foreach (var modifiedOnly in new[] { false, true })
            {
                await Case((modifiedOnly ? "Last-Modified" : "ETag") + " revalidation and 304 commit survive recreation", async () =>
                {
                    var fixture = Create(modifiedOnly ? "modified" : "etag");
                    if (modifiedOnly) { fixture.Endpoint.ETag = null; }
                    using (var first = new RemoteSources(fixture.Sandbox)) { (await Get(first, fixture.Url)).Commit(); }
                    Advance(fixture.Sandbox, TimeSpan.FromMinutes(6));
                    using (var second = new RemoteSources(fixture.Sandbox))
                    {
                        var validated = await Get(second, fixture.Url);
                        Check(!validated.Extracted && validated.Digest == expectedDigest && fixture.Endpoint.ConditionalRequests == 1,
                            "Recreation lost a conditional validator.");
                        validated.Commit();
                    }

                    using var third = new RemoteSources(fixture.Sandbox);
                    _ = await Get(third, fixture.Url);
                    Check(fixture.Endpoint.Requests == 2 && fixture.Endpoint.Bodies == 1, "304 freshness was not persisted.");
                });
            }

            await Case("expired metadata without validators requires a full body", async () =>
            {
                var fixture = Create("no-validator");
                fixture.Endpoint.ETag = null;
                fixture.Endpoint.Modified = null;
                using (var first = new RemoteSources(fixture.Sandbox)) { (await Get(first, fixture.Url)).Commit(); }
                Advance(fixture.Sandbox, TimeSpan.FromMinutes(6));
                using var second = new RemoteSources(fixture.Sandbox);
                var refreshed = await Get(second, fixture.Url);
                Check(refreshed.Extracted && refreshed.Digest == expectedDigest && fixture.Endpoint.Bodies == 2
                    && fixture.Endpoint.ConditionalRequests == 0, "Missing validators permitted expired reuse.");
            });

            await Case("explicit failure cooldown survives recreation and expires", async () =>
            {
                var fixture = Create("failed");
                using (var first = new RemoteSources(fixture.Sandbox)) { first.Failed(fixture.Url); }
                using var second = new RemoteSources(fixture.Sandbox);
                await ThrowsIo(() => Get(second, fixture.Url));
                Check(fixture.Endpoint.Requests == 0, "Recreation lost the explicit cooldown.");
                Advance(fixture.Sandbox, TimeSpan.FromSeconds(31));
                _ = await Get(second, fixture.Url);
                Check(fixture.Endpoint.Requests == 1, "Expired cooldown did not allow a single explicit request.");
            });

            foreach (var status in new[] { 429, 503 })
            {
                await Case($"HTTP {status} persists exact URL and origin Retry-After deadlines", async () =>
                {
                    var fixture = Create("retry-" + status);
                    fixture.Endpoint.Status = status;
                    var retryUntil = fixture.Sandbox.Clock.GetUtcNow() + TimeSpan.FromSeconds(90);
                    fixture.Endpoint.RetryAfter = status == 429 ? "90" : retryUntil.ToString("R");
                    using (var first = new RemoteSources(fixture.Sandbox)) { await ThrowsIo(() => Get(first, fixture.Url)); }
                    using var stored = JsonDocument.Parse(await File.ReadAllTextAsync(StatePath(fixture.Sandbox)));
                    Check(stored.RootElement.GetProperty("Failures").GetProperty(Hash(fixture.Url)).GetDateTimeOffset() == retryUntil
                        && stored.RootElement.GetProperty("OriginFailures").GetProperty(Hash(origin.GetLeftPart(UriPartial.Authority))).GetDateTimeOffset() == retryUntil,
                        "Retry-After deadlines were not retained exactly.");
                    Advance(fixture.Sandbox, TimeSpan.FromSeconds(89));
                    var before = requests;
                    using var second = new RemoteSources(fixture.Sandbox);
                    await ThrowsIo(() => Get(second, fixture.Url));
                    await ThrowsIo(() => Get(second, new Uri(origin, "/different-source.mkv").AbsoluteUri));
                    Check(requests == before, "Persisted URL or origin cooldown allowed HTTP.");
                    Advance(fixture.Sandbox, TimeSpan.FromSeconds(2));
                    fixture.Endpoint.Status = 200;
                    _ = await Get(second, fixture.Url);
                    Check(fixture.Endpoint.Requests == 2, "Retry did not occur once after the exact deadline.");
                });
            }

            await Case("consumer failure persists cooldown without successful metadata", async () =>
            {
                var fixture = Create("consumer-failure");
                using (var first = new RemoteSources(fixture.Sandbox))
                {
                    await ThrowsIo(() => first.Get(fixture.Url, (_, _) => throw new IOException("Synthetic consumer failure."), deadline.Token));
                }

                using var second = new RemoteSources(fixture.Sandbox);
                await ThrowsIo(() => Get(second, fixture.Url));
                using var stored = JsonDocument.Parse(await File.ReadAllTextAsync(StatePath(fixture.Sandbox)));
                Check(fixture.Endpoint.Requests == 1 && stored.RootElement.GetProperty("Entries").EnumerateObject().Count() == 0,
                    "Consumer failure committed success or lost cooldown.");
            });

            await Case("hashed redirect target receives validators only on the final hop", async () =>
            {
                var fixture = Create("redirect");
                var target = new Endpoint();
                Check(endpoints.TryAdd("/redirect-target.mkv", target), "Duplicate redirect target.");
                fixture.Endpoint.RedirectTo = "/redirect-target.mkv?api_key=" + SyntheticKey;
                using (var first = new RemoteSources(fixture.Sandbox)) { (await Get(first, fixture.Url)).Commit(); }
                Advance(fixture.Sandbox, TimeSpan.FromMinutes(6));
                using var second = new RemoteSources(fixture.Sandbox);
                var validated = await Get(second, fixture.Url);
                Check(!validated.Extracted && fixture.Endpoint.ConditionalRequests == 0 && target.ConditionalRequests == 1
                    && target.Bodies == 1, "Stored target hash lost or forwarded validators to the wrong hop.");
            });

            await Case("atomic state is secretless, mode 600, and retains exact metadata", async () =>
            {
                var fixture = Create("private");
                using var remote = new RemoteSources(fixture.Sandbox);
                (await Get(remote, fixture.Url)).Commit();
                var path = StatePath(fixture.Sandbox);
                using var before = JsonDocument.Parse(await File.ReadAllTextAsync(path));
                remote.Failed(new Uri(origin, "/other.mkv?api_key=" + SyntheticKey).AbsoluteUri);
                var text = await File.ReadAllTextAsync(path);
                using var after = JsonDocument.Parse(text);
                Check(before.RootElement.GetProperty("Entries").GetRawText() == after.RootElement.GetProperty("Entries").GetRawText(),
                    "Saving cooldowns rewrote successful metadata.");
                var entry = after.RootElement.GetProperty("Entries").GetProperty(Hash(fixture.Url));
                Check(entry.GetProperty("FinalUrl").GetString() == Hash(fixture.Url) && entry.GetProperty("Digest").GetString() == expectedDigest,
                    "Persisted URL or digest was not hashed.");
                Check(!text.Contains("http", StringComparison.OrdinalIgnoreCase) && !text.Contains("127.0.0.1", StringComparison.Ordinal)
                    && !text.Contains("api_key", StringComparison.Ordinal) && !text.Contains(SyntheticKey, StringComparison.Ordinal), "State exposed a source URL or API key.");
                if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException(); }
                Check(File.GetUnixFileMode(path) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "State permissions are not 600.");
                Check(Directory.GetFiles(fixture.Sandbox.Root, "*.pending").Length == 0, "Atomic write left temporary state.");
            });

            await Case("invalid state fails closed before HTTP and remains untouched", async () =>
            {
                var fixture = Create("invalid-state");
                var now = fixture.Sandbox.Clock.GetUtcNow();
                var entry = new { Digest = expectedDigest, FinalUrl = Hash(fixture.Url), ETag = "\"v1\"", Modified = now.AddDays(-1), CheckedAt = now };
                var valid = JsonSerializer.Serialize(new
                {
                    Version = 1, Entries = new Dictionary<string, object> { [Hash(fixture.Url)] = entry },
                    Failures = new Dictionary<string, DateTimeOffset>(), OriginFailures = new Dictionary<string, DateTimeOffset>()
                });
                string Mutate(Action<JsonNode> action)
                {
                    var node = JsonNode.Parse(valid)!;
                    action(node);
                    return node.ToJsonString();
                }

                var invalid = new[]
                {
                    "{", "null", "{}", new string(' ', 1024 * 1024 + 1),
                    Mutate(node => node["Version"] = 2),
                    Mutate(node => node["Entries"] = null),
                    Mutate(node => node["Unexpected"] = true),
                    Mutate(node => node["Entries"]![Hash(fixture.Url)] = null),
                    Mutate(node => node["Entries"]![Hash(fixture.Url)]!["FinalUrl"] = fixture.Url),
                    Mutate(node => node["Entries"]![Hash(fixture.Url)]!["Digest"] = "not-a-hash"),
                    Mutate(node => node["Entries"]![Hash(fixture.Url)]!["CheckedAt"] = "not-a-date"),
                    Mutate(node => node["Entries"]![Hash(fixture.Url)]!["CheckedAt"] = now.AddDays(1)),
                    Mutate(node => node["Entries"]![Hash(fixture.Url)]!["ETag"] = "W/\"weak\""),
                    Mutate(node => node["Failures"]![Hash(fixture.Url)] = now.AddDays(31)),
                    Mutate(node => node["OriginFailures"]!["raw-origin"] = now.AddMinutes(1)),
                    Mutate(node => node["Failures"] = JsonSerializer.SerializeToNode(Enumerable.Range(0, 4097)
                        .ToDictionary(index => Hash(index.ToString()), _ => now.AddMinutes(1)))),
                    valid.Replace("\"Version\":1", "\"Version\":1,\"Version\":1", StringComparison.Ordinal)
                };
                var before = requests;
                foreach (var text in invalid)
                {
                    await File.WriteAllTextAsync(StatePath(fixture.Sandbox), text);
                    await ThrowsIo(() => { using var rejected = new RemoteSources(fixture.Sandbox); return Task.CompletedTask; });
                    Check(await File.ReadAllTextAsync(StatePath(fixture.Sandbox)) == text, "Rejected state was deleted or rewritten.");
                }

                Check(requests == before, "Invalid persisted state caused HTTP.");
            });

            await Case("existing and dangling state symlinks are rejected on load and commit", async () =>
            {
                var fixture = Create("symlink");
                var path = StatePath(fixture.Sandbox);
                var target = Path.Combine(fixture.Sandbox.Root, "sentinel.json");
                await File.WriteAllTextAsync(target, "untouched");
                using var remote = new RemoteSources(fixture.Sandbox);
                var downloaded = await Get(remote, fixture.Url);
                File.CreateSymbolicLink(path, target);
                await ThrowsIo(() => { using var rejected = new RemoteSources(fixture.Sandbox); return Task.CompletedTask; });
                await ThrowsIo(() => { downloaded.Commit(); return Task.CompletedTask; });
                Check(await File.ReadAllTextAsync(target) == "untouched", "Commit followed or replaced the state symlink target.");
                File.Delete(path);
                File.CreateSymbolicLink(path, target + ".missing");
                await ThrowsIo(() => { using var rejected = new RemoteSources(fixture.Sandbox); return Task.CompletedTask; });
                Check(fixture.Endpoint.Requests == 1, "Symlink rejection caused extra HTTP.");
            });

            await Case("failed atomic replacement removes temporary state and blocks warm reuse", async () =>
            {
                var fixture = Create("write-failure");
                using var remote = new RemoteSources(fixture.Sandbox);
                var downloaded = await Get(remote, fixture.Url);
                Directory.CreateDirectory(StatePath(fixture.Sandbox));
                await ThrowsIo(() => { downloaded.Commit(); return Task.CompletedTask; });
                await ThrowsIo(() => Get(remote, fixture.Url));
                Check(fixture.Endpoint.Requests == 1 && Directory.GetFiles(fixture.Sandbox.Root, "*.pending").Length == 0,
                    "Persistence failure left temporary files or allowed reuse.");
            });

            await Case("pilot allowlist requires exact URLs including path and query without HTTP", () =>
            {
                const string allowed = "https://pilot.example.invalid/episode.mkv?api_key=synthetic";
                var sandbox = new Sandbox(root) { PilotUrls = [allowed] };
                var before = requests;
                Check(sandbox.ValidateInput(allowed) == allowed, "Exact HTTPS pilot URL was rejected.");
                foreach (var rejected in new[]
                {
                    "https://pilot.example.invalid/other.mkv?api_key=synthetic",
                    "https://pilot.example.invalid/episode.mkv?api_key=changed",
                    "https://pilot.example.invalid/Episode.mkv?api_key=synthetic"
                })
                {
                    try { sandbox.ValidateInput(rejected); }
                    catch (NotSupportedException) { continue; }
                    throw new InvalidOperationException("Pilot allowlist accepted a different path or query.");
                }

                Check(requests == before, "Pure URL validation caused HTTP.");
                return Task.CompletedTask;
            });

            await Case("pilot rejects hostname-only grants and explicitly listed unsafe URLs without HTTP", () =>
            {
                var before = requests;
                foreach (var (allowed, input) in new[]
                {
                    ("pilot.example.invalid", "https://pilot.example.invalid/episode.mkv"),
                    ("https://pilot.example.invalid/", "https://pilot.example.invalid/episode.mkv"),
                    ("ftp://pilot.example.invalid/episode.mkv", "ftp://pilot.example.invalid/episode.mkv"),
                    ("https://user:secret@pilot.example.invalid/episode.mkv", "https://user:secret@pilot.example.invalid/episode.mkv"),
                    ("https://pilot.example.invalid/episode.mkv#secret", "https://pilot.example.invalid/episode.mkv#secret")
                })
                {
                    var sandbox = new Sandbox(root) { PilotUrls = [allowed] };
                    try { sandbox.ValidateInput(input); }
                    catch (NotSupportedException) { continue; }
                    throw new InvalidOperationException("Pilot accepted a broad grant, unsupported scheme, userinfo or fragment.");
                }

                Check(requests == before, "Pure unsafe URL validation caused HTTP.");
                return Task.CompletedTask;
            });

            await Case("pure pilot redirect helper permits listed HTTPS downgrade and validates both URLs", () =>
            {
                const string secure = "https://pilot.example.invalid/episode.mkv";
                const string target = "https://pilot.example.invalid/final.mkv";
                const string insecure = "http://pilot.example.invalid/episode.mkv";
                const string unlisted = "https://pilot.example.invalid/unlisted.mkv";
                var sandbox = new Sandbox(root) { PilotUrls = [secure, target, insecure] };
                var before = requests;
                Check(sandbox.ValidateInput(secure) == secure && sandbox.ValidateInput(insecure) == insecure,
                    "Both downgrade URLs must be individually allowed.");
                Check(sandbox.ValidateRedirect(secure, secure) == secure && sandbox.ValidateRedirect(secure, target) == target
                    && sandbox.ValidateRedirect(secure, insecure) == insecure,
                    "Exact listed redirects were rejected.");
                foreach (var (source, destination) in new[] { (unlisted, target), (secure, unlisted) })
                {
                    try { sandbox.ValidateRedirect(source, destination); }
                    catch (NotSupportedException) { continue; }
                    throw new InvalidOperationException("Redirect helper accepted an unlisted source/target.");
                }

                Check(requests == before, "Pure redirect validation caused HTTP.");
                return Task.CompletedTask;
            });

            await Case("pilot resource bounds include eight GiB and fifteen minutes but reject excess", () =>
            {
                const string allowed = "https://pilot.example.invalid/episode.mkv";
                const long maximum = 8L * 1024 * 1024 * 1024;
                var sandbox = new Sandbox(root)
                {
                    PilotUrls = [allowed], TransferLimitBytes = maximum, Timeout = TimeSpan.FromMinutes(15)
                };
                var before = requests;
                Check(sandbox.ValidateInput(allowed) == allowed, "Inclusive pilot resource limits were rejected.");
                foreach (var (limit, timeout) in new[]
                {
                    (maximum + 1, TimeSpan.FromMinutes(15)), (maximum, TimeSpan.FromMinutes(16)),
                    (0L, TimeSpan.FromMinutes(15)), (-1L, TimeSpan.FromMinutes(15)),
                    (maximum, TimeSpan.Zero), (maximum, TimeSpan.FromSeconds(-1))
                })
                {
                    var rejected = new Sandbox(root) { PilotUrls = [allowed], TransferLimitBytes = limit, Timeout = timeout };
                    try { rejected.ValidateInput(allowed); }
                    catch (NotSupportedException) { continue; }
                    throw new InvalidOperationException("Pilot accepted out-of-bounds transfer size or timeout.");
                }

                Check(requests == before, "Pure pilot budget validation caused HTTP.");
                return Task.CompletedTask;
            });

            await Case("pilot persistence redacts URL-valued ETags but retains memory validators, TTL and cooldowns", async () =>
            {
                var fixture = Create("pilot-private", interval: TimeSpan.FromHours(24));
                const string failurePath = "/pilot-private-failure.mkv";
                var failureUrl = new Uri(origin, failurePath + "?api_key=" + SyntheticKey).AbsoluteUri;
                var failure = new Endpoint { Status = 503, RetryAfter = "90" };
                Check(endpoints.TryAdd(failurePath, failure), "Duplicate pilot failure endpoint.");
                var sandbox = new Sandbox(fixture.Sandbox.Root)
                {
                    PilotUrls = [fixture.Url, failureUrl], HttpTestOrigin = origin, PersistRemoteState = true,
                    Clock = fixture.Sandbox.Clock, RevalidationInterval = fixture.Sandbox.RevalidationInterval
                };
                fixture.Endpoint.ETag = "\"" + new Uri(origin, "/privatekey").AbsoluteUri + "\"";
                fixture.Endpoint.Modified = null;
                Check(System.Net.Http.Headers.EntityTagHeaderValue.TryParse(fixture.Endpoint.ETag, out var parsed) && !parsed.IsWeak,
                    "URL-valued fixture ETag must be syntactically valid and strong.");
                var before = requests;
                using (var first = new RemoteSources(sandbox))
                {
                    var cold = await Get(first, fixture.Url);
                    Check(cold.Extracted && cold.Digest == expectedDigest && fixture.Endpoint.Bodies == 1,
                        "Exact loopback pilot URL did not consume the full fixture.");
                    cold.Commit();
                    Advance(sandbox, TimeSpan.FromHours(24) + TimeSpan.FromMinutes(1));
                    var validated = await Get(first, fixture.Url);
                    Check(!validated.Extracted && validated.Digest == expectedDigest && fixture.Endpoint.Requests == 2
                        && fixture.Endpoint.ConditionalRequests == 1 && fixture.Endpoint.Bodies == 1,
                        "Saving pilot state discarded the in-memory ETag or lost its exact If-None-Match value.");
                    validated.Commit();
                    await ThrowsIo(() => Get(first, failureUrl));
                }

                var text = await File.ReadAllTextAsync(StatePath(sandbox));
                using var stored = JsonDocument.Parse(text);
                var entries = stored.RootElement.GetProperty("Entries");
                var entry = entries.GetProperty(Hash(fixture.Url));
                Check(entries.EnumerateObject().Count() == 1 && entry.GetProperty("FinalUrl").GetString() == Hash(fixture.Url)
                    && entry.GetProperty("Digest").GetString() == expectedDigest
                    && entry.GetProperty("ETag").ValueKind == JsonValueKind.Null
                    && entry.GetProperty("CheckedAt").GetDateTimeOffset() == sandbox.Clock.GetUtcNow(),
                    "Pilot persistence lost hashed metadata/freshness or retained an ETag.");
                Check(!text.Contains("http", StringComparison.OrdinalIgnoreCase) && !text.Contains("127.0.0.1", StringComparison.Ordinal)
                    && !text.Contains("privatekey", StringComparison.Ordinal) && !text.Contains("api_key", StringComparison.Ordinal)
                    && !text.Contains(SyntheticKey, StringComparison.Ordinal), "Pilot state exposed a source URL, ETag secret or API key.");
                var retryUntil = sandbox.Clock.GetUtcNow() + TimeSpan.FromSeconds(90);
                Check(stored.RootElement.GetProperty("Failures").GetProperty(Hash(failureUrl)).GetDateTimeOffset() == retryUntil
                    && stored.RootElement.GetProperty("OriginFailures").GetProperty(Hash(origin.GetLeftPart(UriPartial.Authority))).GetDateTimeOffset() == retryUntil,
                    "Pilot redaction lost exact URL or origin cooldowns.");
                Check(requests == before + 3 && failure.Requests == 1, "Pilot fixture made unexpected source requests.");
                using var recreated = new RemoteSources(sandbox);
                await ThrowsIo(() => Get(recreated, failureUrl));
                await ThrowsIo(() => Get(recreated, fixture.Url));
                Check(requests == before + 3, "Recreated pilot cooldown allowed HTTP.");
                Advance(sandbox, TimeSpan.FromHours(23));
                var warm = await Get(recreated, fixture.Url);
                Check(!warm.Extracted && warm.Digest == expectedDigest && requests == before + 3,
                    "Recreated pilot metadata fetched within the 24-hour TTL after its cooldown expired.");
            });

            await Case("pilot redirect origins allow rotating paths and queries but not initial sources", () =>
            {
                const string source = "https://store.example.invalid/source.mkv?key=initial";
                const string current = "https://store.example.invalid/token-one/episode.mkv?key=one";
                const string target = "https://store.example.invalid/token-two/episode.mkv?key=two";
                var defaults = new Sandbox(root);
                Check(defaults.PilotRedirectOrigins.Length == 0 && defaults.HttpTestOrigin is null
                    && defaults.Timeout > TimeSpan.Zero && defaults.Timeout <= TimeSpan.FromSeconds(900)
                    && defaults.TransferLimitBytes > 0 && defaults.TransferLimitBytes <= 8L * 1024 * 1024 * 1024,
                    "Optional origins changed default network opt-in or valid resource limits.");
                var sandbox = new Sandbox(root)
                {
                    PilotUrls = [source], PilotRedirectOrigins = ["https://store.example.invalid"]
                };
                var before = requests;
                Check(sandbox.ValidateInput(source) == source && sandbox.ValidateRedirect(source, current) == current
                    && sandbox.ValidateRedirect(current, target) == target,
                    "Origin approval did not permit rotating redirect paths and queries.");
                foreach (var rejected in new[] { current, target, source.Replace("key=initial", "key=changed", StringComparison.Ordinal) })
                {
                    try { sandbox.ValidateInput(rejected); }
                    catch (NotSupportedException) { continue; }
                    throw new InvalidOperationException("Redirect origin approval broadened the exact initial source allowlist.");
                }

                var originsOnly = new Sandbox(root) { PilotRedirectOrigins = sandbox.PilotRedirectOrigins };
                foreach (var validate in new Func<string>[]
                {
                    () => originsOnly.ValidateInput(source), () => originsOnly.ValidateRedirect(current, target)
                })
                {
                    try { validate(); }
                    catch (NotSupportedException) { continue; }
                    throw new InvalidOperationException("Origin-only settings enabled pilot access without an exact source grant.");
                }

                Check(requests == before, "Pure origin/source validation caused HTTP.");
                return Task.CompletedTask;
            });

            await Case("pilot redirect origins normalize default ports but bind host scheme and other ports", () =>
            {
                const string source = "http://source.example.invalid/episode.mkv";
                const string target = "http://store.example.invalid/rotated.mkv?key=two";
                var before = requests;
                foreach (var grant in new[] { "http://store.example.invalid", "http://store.example.invalid:80/" })
                {
                    var sandbox = new Sandbox(root) { PilotUrls = [source], PilotRedirectOrigins = [grant] };
                    foreach (var destination in new[] { target, "http://store.example.invalid:80/rotated.mkv?key=two" })
                    {
                        Check(sandbox.ValidateRedirect(source, destination) == target
                            && sandbox.ValidateRedirect(destination, target) == target,
                            "Default HTTP port normalization rejected an approved current or target origin.");
                    }

                    foreach (var rejected in new[]
                    {
                        "http://store.example.invalid:81/rotated.mkv?key=two",
                        "http://other.example.invalid/rotated.mkv?key=two",
                        "https://store.example.invalid/rotated.mkv?key=two"
                    })
                    {
                        foreach (var (current, destination) in new[] { (source, rejected), (rejected, target) })
                        {
                            try { sandbox.ValidateRedirect(current, destination); }
                            catch (NotSupportedException) { continue; }
                            throw new InvalidOperationException("Origin policy accepted a different host, scheme or nondefault port.");
                        }
                    }
                }

                Check(requests == before, "Pure port/origin validation caused HTTP.");
                return Task.CompletedTask;
            });

            await Case("pilot redirect origins reject unsafe URLs and malformed or broad grants", () =>
            {
                const string source = "https://source.example.invalid/episode.mkv";
                const string target = "https://store.example.invalid/rotated.mkv?key=two";
                var sandbox = new Sandbox(root)
                {
                    PilotUrls = [source], PilotRedirectOrigins = ["https://store.example.invalid"]
                };
                var before = requests;
                foreach (var rejected in new[]
                {
                    "https://user:secret@store.example.invalid/rotated.mkv?key=two",
                    target + "#fragment"
                })
                {
                    foreach (var (current, destination) in new[] { (source, rejected), (rejected, target) })
                    {
                        try { sandbox.ValidateRedirect(current, destination); }
                        catch (NotSupportedException) { continue; }
                        throw new InvalidOperationException("Approved redirect origin accepted userinfo or a fragment.");
                    }
                }

                foreach (var grant in new[]
                {
                    "store.example.invalid", "https://", "https://*.store.example.invalid",
                    "https://store.example.invalid/rotated.mkv", "https://store.example.invalid/?key=two",
                    "https://store.example.invalid/#fragment", "https://user:secret@store.example.invalid",
                    "ftp://store.example.invalid"
                })
                {
                    var rejected = new Sandbox(root) { PilotUrls = [source], PilotRedirectOrigins = [grant] };
                    foreach (var validate in new Func<string>[]
                    {
                        () => rejected.ValidateInput(source), () => rejected.ValidateRedirect(source, target)
                    })
                    {
                        try { validate(); }
                        catch (NotSupportedException) { continue; }
                        throw new InvalidOperationException("Malformed redirect origin grant did not fail closed.");
                    }
                }

                Check(requests == before, "Pure unsafe origin validation caused HTTP.");
                return Task.CompletedTask;
            });

            await Case("pilot redirect origins permit approved HTTPS downgrade on initial and subsequent hops", () =>
            {
                const string source = "https://source.example.invalid/episode.mkv";
                const string current = "https://store.example.invalid/token-one/episode.mkv";
                const string insecure = "http://store.example.invalid/token-two/episode.mkv?key=two";
                var sandbox = new Sandbox(root)
                {
                    PilotUrls = [source],
                    PilotRedirectOrigins = ["https://store.example.invalid", "http://store.example.invalid"],
                    TransferLimitBytes = 8L * 1024 * 1024 * 1024, Timeout = TimeSpan.FromSeconds(900)
                };
                var before = requests;
                Check(sandbox.ValidateInput(source) == source && sandbox.ValidateRedirect(source, current) == current
                    && sandbox.ValidateRedirect(insecure, insecure) == insecure,
                    "Downgrade test must individually approve both origins within inclusive resource limits.");
                foreach (var secure in new[] { source, current })
                {
                    Check(sandbox.ValidateRedirect(secure, insecure) == insecure,
                        "An approved HTTP origin was rejected solely because of HTTPS downgrade.");
                    foreach (var rejected in new[] { "http://other.example.invalid/file", "http://store.example.invalid:81/file" })
                    {
                        try { sandbox.ValidateRedirect(secure, rejected); }
                        catch (NotSupportedException) { continue; }
                        throw new InvalidOperationException("Downgrade policy bypassed destination origin/port restrictions.");
                    }
                }

                Check(requests == before, "Pure downgrade validation caused HTTP.");
                return Task.CompletedTask;
            });

            await Case("pilot redirect origins stream three unlisted hops and commit the final digest", async () =>
            {
                var fixture = Create("pilot-origin-chain");
                var hopOne = new Endpoint { RedirectTo = "/pilot-origin-chain-two.mkv?key=two" };
                var hopTwo = new Endpoint { RedirectTo = "/pilot-origin-chain-final.mkv?key=three" };
                var final = new Endpoint();
                Check(endpoints.TryAdd("/pilot-origin-chain-one.mkv", hopOne)
                    && endpoints.TryAdd("/pilot-origin-chain-two.mkv", hopTwo)
                    && endpoints.TryAdd("/pilot-origin-chain-final.mkv", final), "Duplicate origin chain endpoints.");
                fixture.Endpoint.RedirectTo = "/pilot-origin-chain-one.mkv?key=one";
                var finalUrl = new Uri(origin, hopTwo.RedirectTo).AbsoluteUri;
                var sandbox = new Sandbox(fixture.Sandbox.Root)
                {
                    PilotUrls = [fixture.Url], PilotRedirectOrigins = [origin.GetLeftPart(UriPartial.Authority)],
                    PersistRemoteState = true, Clock = fixture.Sandbox.Clock
                };
                var before = requests;
                Check(sandbox.HttpTestOrigin is null && sandbox.PilotUrls.Length == 1
                    && sandbox.ValidateInput(fixture.Url) == fixture.Url, "Chain must use only the pilot policy and exact initial source.");
                using (var remote = new RemoteSources(sandbox))
                {
                    try
                    {
                        _ = await Get(remote, finalUrl);
                        throw new InvalidOperationException("Origin-approved redirect was accepted as a direct initial GET.");
                    }
                    catch (NotSupportedException) { }
                    Check(requests == before, "Unlisted initial URL reached HTTP.");
                    var downloaded = await Get(remote, fixture.Url);
                    Check(downloaded.Extracted && downloaded.Digest == expectedDigest && requests == before + 4
                        && fixture.Endpoint.Requests == 1 && hopOne.Requests == 1 && hopTwo.Requests == 1 && final.Requests == 1
                        && fixture.Endpoint.Bodies == 0 && hopOne.Bodies == 0 && hopTwo.Bodies == 0 && final.Bodies == 1,
                        "Three origin-approved, exact-unlisted hops did not stream one complete synthetic body.");
                    Check(!File.Exists(StatePath(sandbox)), "Redirect metadata was persisted before Commit.");
                    downloaded.Commit();
                }

                using var stored = JsonDocument.Parse(await File.ReadAllTextAsync(StatePath(sandbox)));
                var entries = stored.RootElement.GetProperty("Entries");
                var entry = entries.GetProperty(Hash(fixture.Url));
                Check(entries.EnumerateObject().Count() == 1 && entry.GetProperty("FinalUrl").GetString() == Hash(finalUrl)
                    && entry.GetProperty("Digest").GetString() == expectedDigest,
                    "Commit lost the exact initial key, final target hash or full-body digest.");
                using var recreated = new RemoteSources(sandbox);
                var warm = await Get(recreated, fixture.Url);
                Check(!warm.Extracted && warm.Digest == expectedDigest && requests == before + 4,
                    "Committed origin-chain metadata did not survive recreation without HTTP.");
            });

            await Case("pilot redirect origins revalidate rotated paths and queries without forwarding old ETags", async () =>
            {
                var fixture = Create("pilot-origin-validators");
                var oldTarget = new Endpoint { Modified = null };
                var newTarget = new Endpoint { Modified = null };
                Check(endpoints.TryAdd("/pilot-origin-old.mkv", oldTarget)
                    && endpoints.TryAdd("/pilot-origin-new.mkv", newTarget), "Duplicate origin validator endpoints.");
                fixture.Endpoint.RedirectTo = "/pilot-origin-old.mkv?key=one";
                var sandbox = new Sandbox(fixture.Sandbox.Root)
                {
                    PilotUrls = [fixture.Url], PilotRedirectOrigins = [origin.GetLeftPart(UriPartial.Authority)],
                    PersistRemoteState = true, Clock = fixture.Sandbox.Clock
                };
                var before = requests;
                using var remote = new RemoteSources(sandbox);
                var cold = await Get(remote, fixture.Url);
                Check(cold.Extracted && cold.Digest == expectedDigest && oldTarget.Bodies == 1,
                    "Origin-approved old target did not establish a full-body validator.");
                cold.Commit();
                Check(oldTarget.ETag == newTarget.ETag && oldTarget.ETag is not null,
                    "Both targets must share a strong ETag so accidental forwarding would produce 304.");
                foreach (var redirect in new[] { "/pilot-origin-new.mkv?key=one", "/pilot-origin-new.mkv?key=two" })
                {
                    fixture.Endpoint.RedirectTo = redirect;
                    Advance(sandbox, TimeSpan.FromMinutes(6));
                    var bodiesBefore = newTarget.Bodies;
                    var refreshed = await Get(remote, fixture.Url);
                    Check(refreshed.Extracted && refreshed.Digest == expectedDigest && newTarget.Bodies == bodiesBefore + 1
                        && newTarget.ConditionalRequests == 0 && fixture.Endpoint.ConditionalRequests == 0
                        && oldTarget.Requests == 1 && oldTarget.ConditionalRequests == 0,
                        "Path or query rotation forwarded an old validator or skipped the required new body.");
                    refreshed.Commit();
                    using var stored = JsonDocument.Parse(await File.ReadAllTextAsync(StatePath(sandbox)));
                    Check(stored.RootElement.GetProperty("Entries").GetProperty(Hash(fixture.Url))
                        .GetProperty("FinalUrl").GetString() == Hash(new Uri(origin, redirect).AbsoluteUri),
                        "Rotation did not commit the new exact final URL hash.");
                }

                Advance(sandbox, TimeSpan.FromMinutes(6));
                var validated = await Get(remote, fixture.Url);
                Check(!validated.Extracted && validated.Digest == expectedDigest && newTarget.ConditionalRequests == 1
                    && newTarget.Bodies == 2 && newTarget.Requests == 3 && oldTarget.Requests == 1
                    && fixture.Endpoint.ConditionalRequests == 0 && fixture.Endpoint.Requests == 4 && requests == before + 8,
                    "Unchanged final target did not retain its own ETag or made unexpected requests.");
                validated.Commit();
            });
        }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await app.StopAsync(stop.Token);
        }
    }

    private static string StatePath(Sandbox sandbox) => Path.Combine(sandbox.Root, "remote-state.json");
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static void Advance(Sandbox sandbox, TimeSpan elapsed) => ((ManualClock)sandbox.Clock).Now += elapsed;

    private static void Check(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }

    private static async Task ThrowsIo(Func<Task> action)
    {
        try { await action(); }
        catch (IOException) { return; }
        throw new InvalidOperationException("Expected a fail-closed IOException.");
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Endpoint
    {
        public int Requests;
        public int ConditionalRequests;
        public int Bodies;
        public int Status = 200;
        public string? RetryAfter;
        public string? RedirectTo;
        public string? ETag = "\"persistent-v1\"";
        public DateTimeOffset? Modified = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
    }
}