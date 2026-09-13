using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;

namespace SubtitleGuard;

public sealed class Plugin(IApplicationPaths paths, IXmlSerializer serializer)
    : BasePlugin<BasePluginConfiguration>(paths, serializer)
{
    public override Guid Id => new("a7c9c612-4b77-46ed-9b92-b32b83d2f771");
    public override string Name => "Subtitle Guard";
}

public sealed class Registrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(applicationHost);
        var root = Environment.GetEnvironmentVariable("SUBTITLE_GUARD_SANDBOX");
        var origin = Environment.GetEnvironmentVariable("SUBTITLE_GUARD_TEST_ORIGIN");
        var policyPath = Environment.GetEnvironmentVariable("SUBTITLE_GUARD_SOURCE_POLICY");
        var nativePilot = Environment.GetEnvironmentVariable("SUBTITLE_GUARD_NATIVE_PILOT");
        var productionPath = Environment.GetEnvironmentVariable("SUBTITLE_GUARD_PRODUCTION_POLICY");
        Sandbox sandbox;
        if (productionPath is not null)
        {
            if (root is not null || origin is not null || policyPath is not null || nativePilot is not null)
            {
                throw new InvalidOperationException("Production subtitle policy cannot be combined with sandbox or pilot modes.");
            }
            sandbox = LoadProductionPolicy(productionPath);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                throw new InvalidOperationException("Prototype requires an explicit isolated sandbox; not for production installation.");
            }

            if (nativePilot is not null && (string.IsNullOrWhiteSpace(nativePilot)
                || string.IsNullOrWhiteSpace(policyPath) || !string.IsNullOrWhiteSpace(origin)))
            {
                throw new InvalidOperationException("Native subtitle-only pilot requires an exclusive private source policy.");
            }

            if (!string.IsNullOrWhiteSpace(origin) && !string.IsNullOrWhiteSpace(policyPath))
            {
                throw new InvalidOperationException("Select one isolated source-policy mode.");
            }

            sandbox = string.IsNullOrWhiteSpace(policyPath) ? new Sandbox(root)
            {
                HttpTestOrigin = string.IsNullOrWhiteSpace(origin) ? null : new Uri(origin, UriKind.Absolute)
            } : LoadSourcePolicy(root, policyPath, nativePilot);
        }
        if (sandbox.HttpTestOrigin is not null)
        {
            sandbox.ValidateInput(sandbox.HttpTestOrigin.AbsoluteUri);
        }
        services.AddSingleton(sandbox);
        services.RemoveAll<ISubtitleEncoder>();
        services.AddSingleton<ISubtitleEncoder, GuardedEncoder>();
        services.AddHostedService<RegistrationCheck>();
    }

    internal static Sandbox LoadProductionPolicy(string policyPath, string? allowedRoot = null, string[]? sourceRoots = null)
    {
        try
        {
            if (!OperatingSystem.IsLinux() || !Path.IsPathFullyQualified(policyPath)
                || policyPath != Path.GetFullPath(policyPath) || Path.GetFileName(policyPath) != "production-policy.json")
            {
                throw new InvalidOperationException();
            }

            var root = Path.GetDirectoryName(policyPath)!;
            var parent = Path.GetDirectoryName(root);
            allowedRoot ??= parent is not null && Path.GetDirectoryName(parent) == "/tmp"
                ? root : "/var/lib/jellyfin/data/subtitle-guard";
            if (root != allowedRoot || Path.GetFileName(root) != "subtitle-guard"
                || (sourceRoots ?? ["/data/media/xtream", "/var/lib/jellyfin/metadata"])
                    .Select(sourceRoot => Path.GetFullPath(sourceRoot).TrimEnd('/')).Any(sourceRoot =>
                        root == sourceRoot || root.StartsWith(sourceRoot + "/", StringComparison.Ordinal)
                        || sourceRoot.StartsWith(root + "/", StringComparison.Ordinal)))
            {
                throw new InvalidOperationException();
            }

            var sandbox = new Sandbox(root);
            sandbox.ValidatePath(Path.Combine(root, ".subtitle-guard-sandbox"));
            var path = sandbox.ValidatePath(policyPath);
            if (File.GetUnixFileMode(root) != (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute)
                || File.GetUnixFileMode(Path.Combine(root, ".subtitle-guard-sandbox")) != (UnixFileMode.UserRead | UnixFileMode.UserWrite)
                || File.GetUnixFileMode(path) != (UnixFileMode.UserRead | UnixFileMode.UserWrite))
            {
                throw new InvalidOperationException();
            }

            using var file = File.OpenRead(path);
            var bytes = new byte[16385];
            var count = file.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            if (count == bytes.Length) { throw new InvalidOperationException(); }
            var policy = JsonSerializer.Deserialize<ProductionPolicy>(bytes.AsSpan(0, count), new JsonSerializerOptions
            {
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
                AllowDuplicateProperties = false,
                MaxDepth = 4
            });
            if (policy is null || policy.CacheRoot != root || policy.InitialOrigins is not { Length: >= 1 and <= 8 }
                || policy.RedirectOrigins is not { Length: <= 8 }
                || policy.MaxBytes is <= 0 or > 8L * 1024 * 1024 * 1024 || policy.TimeoutSeconds is <= 0 or > 900)
            {
                throw new InvalidOperationException();
            }

            foreach (var value in policy.InitialOrigins.Concat(policy.RedirectOrigins))
            {
                if (!Uri.TryCreate(value, UriKind.Absolute, out var origin) || origin.Scheme is not ("http" or "https")
                    || origin.UserInfo.Length != 0 || origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0
                    || value != origin.GetLeftPart(UriPartial.Authority))
                {
                    throw new InvalidOperationException();
                }
            }

            return new Sandbox(root)
            {
                InitialOrigins = policy.InitialOrigins,
                PilotRedirectOrigins = policy.RedirectOrigins,
                TransferLimitBytes = policy.MaxBytes,
                Timeout = TimeSpan.FromSeconds(policy.TimeoutSeconds),
                PersistRemoteState = true,
                RevalidationInterval = TimeSpan.FromDays(1),
                FailureCooldown = TimeSpan.FromMinutes(5),
                RequireLoopbackNetwork = false
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or NotSupportedException or JsonException)
        {
            throw new InvalidOperationException("Production subtitle policy rejected; requires a bounded private policy in a dedicated marked cache root.");
        }
    }

    private static Sandbox LoadSourcePolicy(string root, string policyPath, string? nativePilot)
    {
        try
        {
            if (!OperatingSystem.IsLinux())
            {
                throw new InvalidOperationException();
            }

            if (nativePilot is not null)
            {
                if (!string.Equals(nativePilot, root, StringComparison.Ordinal)) { throw new InvalidOperationException(); }
                ValidateNativePilot(root, Environment.GetCommandLineArgs(), Environment.CurrentDirectory);
            }
            else if (NetworkInterface.GetAllNetworkInterfaces().Any(network => network.NetworkInterfaceType != NetworkInterfaceType.Loopback))
            {
                throw new InvalidOperationException();
            }

            var sandbox = new Sandbox(root);
            var path = sandbox.ValidatePath(policyPath);
            if (File.GetUnixFileMode(sandbox.Root) != (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute)
                || File.GetUnixFileMode(path) != (UnixFileMode.UserRead | UnixFileMode.UserWrite))
            {
                throw new InvalidOperationException();
            }

            using var file = File.OpenRead(path);
            var bytes = new byte[16385];
            var count = file.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            if (count == bytes.Length)
            {
                throw new InvalidOperationException();
            }

            var policy = JsonSerializer.Deserialize<SourcePolicy>(bytes.AsSpan(0, count), new JsonSerializerOptions
            {
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
                AllowDuplicateProperties = false,
                MaxDepth = 4
            });
            if (policy is null || policy.AllowedUrls is not { Length: 1 } || policy.AllowedRedirectOrigins is null
                || policy.MaxBytes is <= 0 or > 4L * 1024 * 1024 * 1024 || policy.TimeoutSeconds is <= 0 or > 900)
            {
                throw new InvalidOperationException();
            }

            if (!Uri.TryCreate(policy.AllowedUrls[0], UriKind.Absolute, out var source) || source.Scheme is not ("http" or "https"))
            {
                throw new InvalidOperationException();
            }

            sandbox = new Sandbox(root)
            {
                PilotUrls = policy.AllowedUrls,
                PilotRedirectOrigins = policy.AllowedRedirectOrigins,
                TransferLimitBytes = policy.MaxBytes,
                Timeout = TimeSpan.FromSeconds(policy.TimeoutSeconds),
                PersistRemoteState = true,
                RevalidationInterval = TimeSpan.FromDays(1),
                FailureCooldown = TimeSpan.FromMinutes(5),
                RequireLoopbackNetwork = nativePilot is null
            };
            if (!string.Equals(sandbox.ValidateInput(policy.AllowedUrls[0]), policy.AllowedUrls[0], StringComparison.Ordinal))
            {
                throw new InvalidOperationException();
            }

            return sandbox;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or NotSupportedException or JsonException)
        {
            throw new InvalidOperationException("Private source policy rejected; requires a bounded policy inside a private isolated sandbox.");
        }
    }

    internal static void ValidateNativePilot(string root, string[] arguments, string workingDirectory)
    {
        try
        {
            if (!OperatingSystem.IsLinux() || !Path.IsPathFullyQualified(root) || root != Path.GetFullPath(root)
                || Path.GetDirectoryName(root) != "/tmp" || !Path.GetFileName(root).StartsWith("subtitle-guard-native-", StringComparison.Ordinal)
                || Path.GetFileName(root).Length == "subtitle-guard-native-".Length || workingDirectory != root)
            {
                throw new InvalidOperationException();
            }

            var sandbox = new Sandbox(root);
            sandbox.ValidatePath(Path.Combine(root, ".subtitle-guard-sandbox"));
            if (File.GetUnixFileMode(root) != (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute))
            {
                throw new InvalidOperationException();
            }

            foreach (var (flag, directory) in new[] { ("--datadir", "data"), ("--configdir", "config"), ("--cachedir", "cache"), ("--logdir", "logs"), ("--nowebclient", "") })
            {
                var positions = Enumerable.Range(0, arguments.Length).Where(index => string.Equals(arguments[index], flag, StringComparison.OrdinalIgnoreCase)
                    || arguments[index].StartsWith(flag + "=", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (positions.Length != 1 || arguments[positions[0]] != flag) { throw new InvalidOperationException(); }
                var next = positions[0] + 1;
                if (directory.Length == 0)
                {
                    if (next < arguments.Length && !arguments[next].StartsWith("--", StringComparison.Ordinal)) { throw new InvalidOperationException(); }
                    continue;
                }

                var expected = sandbox.ValidatePath(Path.Combine(root, directory));
                if (!Directory.Exists(expected) || next >= arguments.Length || arguments[next] != expected
                    || arguments.Count(argument => argument == expected) != 1)
                {
                    throw new InvalidOperationException();
                }
            }

            if (arguments.Any(argument => argument == "--" || (argument.StartsWith('-') && !argument.StartsWith("--", StringComparison.Ordinal))))
            {
                throw new InvalidOperationException();
            }

            var path = sandbox.ValidatePath(Path.Combine(root, "config", "network.xml"));
            if (!File.Exists(path) || File.GetUnixFileMode(path) != (UnixFileMode.UserRead | UnixFileMode.UserWrite))
            {
                throw new InvalidOperationException();
            }

            using var reader = XmlReader.Create(path, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 16384
            });
            var network = XDocument.Load(reader).Root;
            if (network?.Name != "NetworkConfiguration" || network.Attributes().Any(attribute => !attribute.IsNamespaceDeclaration))
            {
                throw new InvalidOperationException();
            }
            foreach (var (name, value) in new[] { ("InternalHttpPort", "18097"), ("EnableIPv4", "true"), ("EnableIPv6", "false"),
                ("EnableRemoteAccess", "false"), ("EnableAutoDiscovery", "false") })
            {
                var setting = network.Elements(name).Single();
                if (setting.HasElements || setting.HasAttributes || setting.Value != value) { throw new InvalidOperationException(); }
            }

            var addresses = network.Elements("LocalNetworkAddresses").Single();
            var address = addresses.Elements().Single();
            if (addresses.HasAttributes || address.Name != "string" || address.HasElements || address.HasAttributes || address.Value != "127.0.0.1")
            {
                throw new InvalidOperationException();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or NotSupportedException or XmlException)
        {
            throw new InvalidOperationException("Native subtitle-only pilot host rejected.");
        }
    }

    private sealed record SourcePolicy(string[] AllowedUrls, string[] AllowedRedirectOrigins, long MaxBytes, int TimeoutSeconds);
}

public sealed record ProductionPolicy(string CacheRoot, string[] InitialOrigins, string[] RedirectOrigins,
    long MaxBytes = 4L * 1024 * 1024 * 1024, int TimeoutSeconds = 900);

public sealed class RegistrationCheck(ISubtitleEncoder encoder, ILogger<RegistrationCheck> logger, Sandbox sandbox) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (encoder is not GuardedEncoder)
        {
            throw new InvalidOperationException(sandbox.InitialOrigins.Length > 0
                ? "Guarded subtitle encoder registration was replaced." : "Prototype encoder registration was replaced.");
        }

        if (sandbox.InitialOrigins.Length > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteRegistrationProof();
            logger.LogInformation("Guarded subtitle encoder registration verified.");
        }
        else
        {
            logger.LogInformation("Isolated Subtitle Guard encoder registration verified.");
        }

        return Task.CompletedTask;
    }

    private void WriteRegistrationProof()
    {
        try
        {
            if (!OperatingSystem.IsLinux()
                || File.GetUnixFileMode(sandbox.Root) != (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute))
            {
                throw new InvalidOperationException();
            }
            if (!File.Exists(sandbox.ValidatePath(Path.Combine(sandbox.Root, ".subtitle-guard-sandbox"))))
            {
                throw new InvalidOperationException();
            }
            var path = sandbox.ValidatePath(Path.Combine(sandbox.Root, "registration.json"));
            var temporary = sandbox.ValidatePath(Path.Combine(sandbox.Root, ".registration-" + Guid.NewGuid().ToString("N") + ".tmp"));
            var assembly = encoder.GetType().Assembly;
            using var assemblyFile = File.OpenRead(assembly.Location);
            using var process = Process.GetCurrentProcess();
            var proof = new RegistrationProof(Environment.ProcessId, process.StartTime.ToUniversalTime(),
                encoder.GetType().FullName!, assembly.GetName().Version!.ToString(),
                Convert.ToHexStringLower(SHA256.HashData(assemblyFile)), sandbox.Root);
            var created = false;
            try
            {
                using (var output = new FileStream(temporary, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
                }))
                {
                    created = true;
                    File.SetUnixFileMode(output.SafeFileHandle, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    JsonSerializer.Serialize(output, proof);
                    output.Flush(true);
                }
                File.Move(temporary, sandbox.ValidatePath(path), overwrite: true);
            }
            finally
            {
                if (created) { File.Delete(sandbox.ValidatePath(temporary)); }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or NotSupportedException or JsonException or System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException("Guarded subtitle encoder registration proof rejected.");
        }
    }

    private sealed record RegistrationProof(int ProcessId, DateTime ProcessStartUtc, string EncoderFullName,
        string AssemblyVersion, string AssemblySha256, string CacheRoot);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}