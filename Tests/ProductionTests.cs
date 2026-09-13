using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.MediaEncoding.Subtitles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SubtitleGuard;

internal static class ProductionTests
{
    private const UnixFileMode PrivateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const string Token = "production-policy-secret-sentinel";
    private const string InitialOrigin = "https://production-source.invalid:8443";
    private const string RedirectOrigin = "http://production-redirect.invalid:8080";
    private static readonly string[] EnvironmentNames = ["SUBTITLE_GUARD_SANDBOX", "SUBTITLE_GUARD_TEST_ORIGIN",
        "SUBTITLE_GUARD_SOURCE_POLICY", "SUBTITLE_GUARD_NATIVE_PILOT", "SUBTITLE_GUARD_PRODUCTION_POLICY"];

    public static async Task<(int Passed, int Failed)> Run(string root, string ffmpeg)
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException(); }
        Check(Path.IsPathFullyQualified(root) && root == Path.GetFullPath(root) && Path.GetDirectoryName(root) == "/tmp"
            && File.GetUnixFileMode(root) == PrivateDirectory, "Production tests require a private direct /tmp parent.");
        Check(Path.IsPathFullyQualified(ffmpeg) && File.Exists(ffmpeg), "Production tests require the local FFmpeg fixture.");
        var cacheRoot = Path.Combine(root, "subtitle-guard");
        Check(!Path.Exists(cacheRoot), "Production fixture already exists.");
        Directory.CreateDirectory(cacheRoot, PrivateDirectory);
        var marker = Path.Combine(cacheRoot, ".subtitle-guard-sandbox");
        var policyPath = Path.Combine(cacheRoot, "production-policy.json");
        var proofPath = Path.Combine(cacheRoot, "registration.json");
        var previous = EnvironmentNames.Select(Environment.GetEnvironmentVariable).ToArray();
        var passed = 0;
        var failed = 0;
        using var network = new NetworkEvents();
        var loader = typeof(Registrator).GetMethod("LoadProductionPolicy", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Production policy loader is absent.");

        string Policy() => JsonSerializer.Serialize(new { CacheRoot = cacheRoot,
            InitialOrigins = new[] { InitialOrigin }, RedirectOrigins = new[] { RedirectOrigin } });
        string Changed(string field, JsonNode? value, bool remove = false)
        {
            var json = JsonNode.Parse(Policy())!.AsObject();
            if (remove) { json.Remove(field); } else { json[field] = value; }
            return json.ToJsonString();
        }
        Sandbox Load(string? candidate = null, string[]? sourceRoots = null)
        {
            try { return (Sandbox)loader.Invoke(null, [candidate ?? policyPath, null, sourceRoots])!; }
            catch (TargetInvocationException exception) { throw exception.InnerException!; }
        }
        void SelectProduction()
        {
            foreach (var name in EnvironmentNames) { Environment.SetEnvironmentVariable(name, null); }
            Environment.SetEnvironmentVariable(EnvironmentNames[4], policyPath);
        }
        void RejectRegistration()
        {
            var original = new Mock<ISubtitleEncoder>(MockBehavior.Strict);
            var host = new Mock<IServerApplicationHost>(MockBehavior.Strict);
            var services = new ServiceCollection();
            services.AddSingleton(original.Object);
            var before = services.ToArray();
            Reject(() => new Registrator().RegisterServices(services, host.Object));
            Check(before.SequenceEqual(services), "Rejected production registration mutated DI.");
            using var provider = services.BuildServiceProvider();
            Check(ReferenceEquals(original.Object, provider.GetRequiredService<ISubtitleEncoder>()), "Rejection removed the original encoder.");
            original.VerifyNoOtherCalls();
            host.VerifyNoOtherCalls();
        }
        async Task Case(string name, Func<Task> action)
        {
            SelectProduction();
            WritePrivate(marker, "");
            WritePrivate(policyPath, Policy());
            File.Delete(proofPath);
            try
            {
                await action();
                Check(network.Requests == 0, "Production policy tests attempted network access.");
                Console.WriteLine("PASS: Production: " + name);
                passed++;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("FAIL: Production: " + name + ": "
                    + (exception.GetType() == typeof(Exception) ? exception.Message : exception.GetType().Name));
                failed++;
            }
        }
        void CheckProof()
        {
            using var json = JsonDocument.Parse(File.ReadAllText(proofPath));
            var proof = json.RootElement;
            using var process = Process.GetCurrentProcess();
            var assembly = typeof(GuardedEncoder).Assembly;
            using var binary = File.OpenRead(assembly.Location);
            Check(proof.GetProperty("ProcessId").GetInt32() == Environment.ProcessId, "Proof PID is not the current process.");
            Check(proof.GetProperty("ProcessStartUtc").GetDateTime() == process.StartTime.ToUniversalTime(), "Proof start time is not the current process.");
            Check(proof.GetProperty("EncoderFullName").GetString() == typeof(GuardedEncoder).FullName, "Proof does not name the guarded encoder.");
            Check(proof.GetProperty("AssemblyVersion").GetString() == "0.1.1.0"
                && assembly.GetName().Version!.ToString() == "0.1.1.0", "Proof assembly version differs from the candidate contract.");
            Check(proof.GetProperty("AssemblySha256").GetString() == Convert.ToHexStringLower(SHA256.HashData(binary)), "Proof hash does not match the loaded assembly.");
            Check(proof.GetProperty("CacheRoot").GetString() == cacheRoot && proof.EnumerateObject().Count() == 6, "Proof cache root or schema differs.");
            CheckSecretless(File.ReadAllText(proofPath));
            if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException(); }
            Check(File.GetUnixFileMode(proofPath) == PrivateFile && new FileInfo(proofPath).LinkTarget is null, "Proof is not a regular mode-600 file.");
            Check(Directory.GetFiles(cacheRoot, ".registration-*.tmp").Length == 0, "Proof left temporary output.");
        }
        GuardedEncoder Encoder(Sandbox sandbox)
        {
            var mediaEncoder = new Mock<IMediaEncoder>(MockBehavior.Strict);
            mediaEncoder.SetupGet(value => value.EncoderPath).Returns(ffmpeg);
            return new GuardedEncoder(sandbox, mediaEncoder.Object, new Mock<IMediaSourceManager>(MockBehavior.Strict).Object,
                new SubtitleEditParser(NullLogger<SubtitleEditParser>.Instance));
        }

        try
        {
            await Case("defaults, real DI singleton and current-process proof", async () =>
            {
                var original = new Mock<ISubtitleEncoder>(MockBehavior.Strict);
                var host = new Mock<IServerApplicationHost>(MockBehavior.Strict);
                var mediaEncoder = new Mock<IMediaEncoder>(MockBehavior.Strict);
                mediaEncoder.SetupGet(value => value.EncoderPath).Returns(ffmpeg);
                var sources = new Mock<IMediaSourceManager>(MockBehavior.Strict);
                var services = new ServiceCollection();
                services.AddSingleton(original.Object);
                services.AddSingleton(mediaEncoder.Object);
                services.AddSingleton(sources.Object);
                services.AddSingleton<ISubtitleParser>(new SubtitleEditParser(NullLogger<SubtitleEditParser>.Instance));
                services.AddSingleton<ILogger<RegistrationCheck>>(NullLogger<RegistrationCheck>.Instance);
                new Registrator().RegisterServices(services, host.Object);
                using var provider = services.BuildServiceProvider();
                var sandbox = provider.GetRequiredService<Sandbox>();
                var encoder = provider.GetRequiredService<ISubtitleEncoder>();
                Check(encoder is GuardedEncoder && ReferenceEquals(encoder, provider.GetRequiredService<ISubtitleEncoder>())
                    && provider.GetServices<ISubtitleEncoder>().Count() == 1, "Production did not register only the guarded singleton.");
                Check(sandbox.Root == cacheRoot && sandbox.InitialOrigins.SequenceEqual(new[] { InitialOrigin })
                    && sandbox.PilotRedirectOrigins.SequenceEqual(new[] { RedirectOrigin }) && sandbox.PilotUrls.Length == 0
                    && sandbox.HttpTestOrigin is null, "Production source grants differ from configuration.");
                Check(sandbox.TransferLimitBytes == 4L * 1024 * 1024 * 1024 && sandbox.Timeout == TimeSpan.FromSeconds(900)
                    && sandbox.PersistRemoteState && sandbox.RevalidationInterval == TimeSpan.FromDays(1)
                    && sandbox.FailureCooldown == TimeSpan.FromMinutes(5), "Production defaults differ.");
                Check(typeof(Sandbox).GetProperty("RequireLoopbackNetwork", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.GetValue(sandbox) is false, "Production retained the isolated-network requirement.");
                Check(sandbox.ValidateInput(InitialOrigin + "/episode.mkv?token=" + Token).StartsWith(InitialOrigin, StringComparison.Ordinal),
                    "Production refused an approved origin.");
                Check(sandbox.ValidateRedirect(InitialOrigin + "/episode.mkv", RedirectOrigin + "/storage.mkv") == RedirectOrigin + "/storage.mkv",
                    "Production refused the approved HTTPS-to-HTTP redirect.");
                Check(!File.Exists(proofPath), "Registration proof appeared before startup validation.");
                var startup = provider.GetServices<IHostedService>().Single();
                Check(startup is RegistrationCheck, "Production startup check is absent.");
                await startup.StartAsync(CancellationToken.None);
                CheckProof();
                await startup.StopAsync(CancellationToken.None);
                original.VerifyNoOtherCalls();
                host.VerifyNoOtherCalls();
                sources.VerifyNoOtherCalls();
                mediaEncoder.VerifyNoOtherCalls();
            });

            await Case("explicit minimum and maximum budgets and eight-origin boundary", () =>
            {
                foreach (var (bytes, seconds) in new[] { (1L, 1), (8L * 1024 * 1024 * 1024, 900) })
                {
                    var json = JsonNode.Parse(Policy())!;
                    json["MaxBytes"] = bytes;
                    json["TimeoutSeconds"] = seconds;
                    WritePrivate(policyPath, json.ToJsonString().PadRight(16384));
                    var sandbox = Load();
                    Check(sandbox.TransferLimitBytes == bytes && sandbox.Timeout == TimeSpan.FromSeconds(seconds), "Explicit production budgets were not retained.");
                }
                var boundary = JsonNode.Parse(Policy())!;
                var initial = Enumerable.Range(0, 8).Select(index => $"https://initial-{index}.invalid").ToArray();
                var redirects = Enumerable.Range(0, 8).Select(index => $"http://redirect-{index}.invalid").ToArray();
                boundary["InitialOrigins"] = JsonSerializer.SerializeToNode(initial);
                boundary["RedirectOrigins"] = JsonSerializer.SerializeToNode(redirects);
                WritePrivate(policyPath, boundary.ToJsonString());
                var accepted = Load();
                Check(accepted.InitialOrigins.SequenceEqual(initial) && accepted.PilotRedirectOrigins.SequenceEqual(redirects), "Eight-origin policy was not retained.");
                Check(accepted.ValidateInput(initial[7] + "/episode.mkv") == initial[7] + "/episode.mkv"
                    && accepted.ValidateRedirect(initial[7] + "/episode.mkv", redirects[7] + "/storage.mkv") == redirects[7] + "/storage.mkv",
                    "Accepted eight-origin policy is unusable.");
                WritePrivate(policyPath, Changed("RedirectOrigins", new JsonArray()));
                Check(Load().PilotRedirectOrigins.Length == 0, "An empty redirect list should be valid.");
                return Task.CompletedTask;
            });

            await Case("strict JSON, required fields and duplicate properties reject before DI", () =>
            {
                var invalid = new List<string> { "{", "null", "[]", "{}", Policy() + "{}", Policy().PadRight(16385),
                    Changed("Unexpected", Token), Policy().Insert(1, "\"CacheRoot\":" + JsonSerializer.Serialize(cacheRoot) + ",") };
                invalid.AddRange(new[] { "CacheRoot", "InitialOrigins", "RedirectOrigins" }.Select(field => Changed(field, null, true)));
                foreach (var text in invalid) { WritePrivate(policyPath, text); RejectRegistration(); }
                return Task.CompletedTask;
            });

            await Case("array and resource limits reject before DI", () =>
            {
                foreach (var (field, value) in new (string, JsonNode?)[] {
                    ("InitialOrigins", new JsonArray()), ("InitialOrigins", null), ("RedirectOrigins", null),
                    ("InitialOrigins", JsonSerializer.SerializeToNode(Enumerable.Repeat(InitialOrigin, 9))),
                    ("RedirectOrigins", JsonSerializer.SerializeToNode(Enumerable.Repeat(RedirectOrigin, 9))),
                    ("MaxBytes", JsonValue.Create(0)), ("MaxBytes", JsonValue.Create(-1)),
                    ("MaxBytes", JsonValue.Create(8L * 1024 * 1024 * 1024 + 1)),
                    ("TimeoutSeconds", JsonValue.Create(0)), ("TimeoutSeconds", JsonValue.Create(-1)),
                    ("TimeoutSeconds", JsonValue.Create(901)) })
                { WritePrivate(policyPath, Changed(field, value)); RejectRegistration(); }
                return Task.CompletedTask;
            });

            await Case("non-HTTP and noncanonical origins reject without secret leakage", () =>
            {
                foreach (var field in new[] { "InitialOrigins", "RedirectOrigins" })
                foreach (var value in new string?[] { null, "file:///tmp/" + Token, "ftp://source.invalid", "https://user:" + Token + "@source.invalid",
                    InitialOrigin + "/" + Token, InitialOrigin + "?token=" + Token, InitialOrigin + "#" + Token, InitialOrigin + "/" })
                { WritePrivate(policyPath, Changed(field, new JsonArray(JsonValue.Create(value)))); RejectRegistration(); }
                return Task.CompletedTask;
            });

            await Case("canonical cache root, policy location and media-root separation", () =>
            {
                foreach (var value in new[] { "relative", root, cacheRoot + "/child", cacheRoot + "-wrong" })
                { WritePrivate(policyPath, Changed("CacheRoot", value)); RejectRegistration(); }
                WritePrivate(policyPath, Policy());
                foreach (var candidate in new[] { "production-policy.json", Path.Combine(cacheRoot, "other.json"), cacheRoot + "/../subtitle-guard/production-policy.json" })
                {
                    Environment.SetEnvironmentVariable(EnvironmentNames[4], candidate);
                    RejectRegistration();
                }
                SelectProduction();
                foreach (var sourceRoot in new[] { root, cacheRoot, Path.Combine(cacheRoot, "media") })
                { Reject(() => Load(sourceRoots: [sourceRoot])); }
                Check(Load(sourceRoots: [cacheRoot + "-media"]).Root == cacheRoot, "Sibling prefix was incorrectly treated as overlapping media.");
                var renamed = Path.Combine(root, "wrong-cache-name");
                Directory.Move(cacheRoot, renamed);
                try
                {
                    var json = JsonNode.Parse(Policy())!;
                    json["CacheRoot"] = renamed;
                    WritePrivate(Path.Combine(renamed, "production-policy.json"), json.ToJsonString());
                    Environment.SetEnvironmentVariable(EnvironmentNames[4], Path.Combine(renamed, "production-policy.json"));
                    RejectRegistration();
                }
                finally { Directory.Move(renamed, cacheRoot); SelectProduction(); }
                return Task.CompletedTask;
            });

            await Case("missing marker and unsafe root, marker and policy permissions", () =>
            {
                if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException(); }
                File.Delete(marker);
                RejectRegistration();
                WritePrivate(marker, "");
                foreach (var (path, mode) in new[] { (cacheRoot, PrivateDirectory), (policyPath, PrivateFile), (marker, PrivateFile) })
                foreach (var extra in new[] { UnixFileMode.GroupRead, UnixFileMode.OtherWrite })
                {
                    File.SetUnixFileMode(path, mode | extra);
                    try { RejectRegistration(); }
                    finally { File.SetUnixFileMode(path, mode); }
                }
                return Task.CompletedTask;
            });

            await Case("symlinked parent, root, marker and policy paths", () =>
            {
                foreach (var path in new[] { marker, policyPath, cacheRoot })
                {
                    var target = path + ".saved";
                    var directory = path == cacheRoot;
                    if (directory) { Directory.Move(path, target); Directory.CreateSymbolicLink(path, target); }
                    else { File.Move(path, target); File.CreateSymbolicLink(path, target); }
                    try { RejectRegistration(); }
                    finally
                    {
                        if (directory) { Directory.Delete(path); Directory.Move(target, path); }
                        else { File.Delete(path); File.Move(target, path); }
                    }
                }
                var alias = root + "-production-link-" + Guid.NewGuid().ToString("N");
                Directory.CreateSymbolicLink(alias, root);
                try
                {
                    Environment.SetEnvironmentVariable(EnvironmentNames[4], Path.Combine(alias, "subtitle-guard", "production-policy.json"));
                    RejectRegistration();
                }
                finally { Directory.Delete(alias); SelectProduction(); }
                return Task.CompletedTask;
            });

            await Case("all four sandbox opt-ins conflict, including whitespace; absent production is not enabled", () =>
            {
                foreach (var name in EnvironmentNames.Take(4))
                foreach (var value in new[] { "selected-" + Token, " \t " })
                {
                    Environment.SetEnvironmentVariable(name, value);
                    try { RejectRegistration(); }
                    finally { Environment.SetEnvironmentVariable(name, null); }
                }
                foreach (var value in new string?[] { null, " \t " })
                {
                    Environment.SetEnvironmentVariable(EnvironmentNames[4], value);
                    RejectRegistration();
                }
                return Task.CompletedTask;
            });

            await Case("original encoder and cancelled startup cannot create proof", async () =>
            {
                var sandbox = Load();
                var original = new Mock<ISubtitleEncoder>(MockBehavior.Strict);
                var check = new RegistrationCheck(original.Object, NullLogger<RegistrationCheck>.Instance, sandbox);
                await RejectAsync(() => check.StartAsync(CancellationToken.None));
                Check(!File.Exists(proofPath), "Original encoder wrote a registration proof.");
                original.VerifyNoOtherCalls();
                using var encoder = Encoder(sandbox);
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                try
                {
                    await new RegistrationCheck(encoder, NullLogger<RegistrationCheck>.Instance, sandbox).StartAsync(cancelled.Token);
                    throw new Exception("Cancelled startup was accepted.");
                }
                catch (OperationCanceledException) { }
                Check(!File.Exists(proofPath), "Cancelled startup wrote a registration proof.");
            });

            await Case("existing and dangling proof symlinks are refused without changing targets", async () =>
            {
                var sandbox = Load();
                using var encoder = Encoder(sandbox);
                var check = new RegistrationCheck(encoder, NullLogger<RegistrationCheck>.Instance, sandbox);
                var target = Path.Combine(root, "proof-target.json");
                foreach (var dangling in new[] { false, true })
                {
                    if (!dangling) { WritePrivate(target, Token); }
                    File.CreateSymbolicLink(proofPath, target);
                    try
                    {
                        await RejectAsync(() => check.StartAsync(CancellationToken.None));
                        Check(new FileInfo(proofPath).LinkTarget == target, "Rejected proof replaced the symlink.");
                        Check(dangling ? !File.Exists(target) : File.ReadAllText(target) == Token, "Rejected proof changed its target.");
                        Check(Directory.GetFiles(cacheRoot, ".registration-*.tmp").Length == 0, "Rejected proof left temporary output.");
                    }
                    finally { File.Delete(proofPath); File.Delete(target); }
                }
            });

            await Case("stale filename proof is replaced with every current identity field", async () =>
            {
                var sandbox = Load();
                using var encoder = Encoder(sandbox);
                var check = new RegistrationCheck(encoder, NullLogger<RegistrationCheck>.Instance, sandbox);
                await check.StartAsync(CancellationToken.None);
                var valid = File.ReadAllText(proofPath);
                foreach (var (field, value) in new (string, JsonNode?)[] {
                    ("ProcessId", JsonValue.Create(-1)), ("ProcessStartUtc", JsonValue.Create(DateTime.UnixEpoch)),
                    ("EncoderFullName", JsonValue.Create("OriginalEncoder")), ("AssemblyVersion", JsonValue.Create("0.0.0.0")),
                    ("AssemblySha256", JsonValue.Create(new string('0', 64))), ("CacheRoot", JsonValue.Create(root)) })
                {
                    var stale = JsonNode.Parse(valid)!;
                    stale[field] = value;
                    var text = stale.ToJsonString();
                    WritePrivate(proofPath, text);
                    await check.StartAsync(CancellationToken.None);
                    Check(File.ReadAllText(proofPath) != text, "Startup trusted a stale proof filename.");
                    CheckProof();
                }
            });
        }
        finally
        {
            for (var index = 0; index < EnvironmentNames.Length; index++)
            { Environment.SetEnvironmentVariable(EnvironmentNames[index], previous[index]); }
        }
        return (passed, failed);
    }

    private static void WritePrivate(string path, string text)
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException(); }
        using var stream = new FileStream(path, new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write,
            Share = FileShare.None, UnixCreateMode = PrivateFile });
        File.SetUnixFileMode(stream.SafeFileHandle, PrivateFile);
        using var writer = new StreamWriter(stream);
        writer.Write(text);
    }

    private static void Reject(Action action) => RejectAsync(() => { action(); return Task.CompletedTask; }).GetAwaiter().GetResult();

    private static async Task RejectAsync(Func<Task> action)
    {
        try { await action(); }
        catch (InvalidOperationException exception)
        {
            Check(exception.InnerException is null, "Production rejection retained an inner exception.");
            CheckSecretless(exception.ToString());
            return;
        }
        throw new Exception("Invalid production configuration or proof was accepted.");
    }

    private static void CheckSecretless(string text) => Check(new[] { Token, InitialOrigin, RedirectOrigin, "token=", "source.invalid", "redirect.invalid" }
        .All(value => !text.Contains(value, StringComparison.Ordinal)), "Production output exposed a URL or secret.");

    private static void Check(bool condition, string message)
    {
        if (!condition) { throw new Exception(message); }
    }

    private sealed class NetworkEvents : EventListener
    {
        private int _requests;
        public int Requests => Volatile.Read(ref _requests);
        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name is "System.Net.Http" or "System.Net.Sockets" or "System.Net.NameResolution")
            { EnableEvents(eventSource, EventLevel.Informational); }
        }
        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventName is "RequestStart" or "ConnectStart" or "ResolutionStart")
            { Interlocked.Increment(ref _requests); }
        }
    }
}