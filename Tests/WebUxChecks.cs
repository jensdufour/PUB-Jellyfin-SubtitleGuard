using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace SubtitleGuard.Tests
{
    internal static class WebUxChecks
    {
        public static async Task Run(string stockPath, string candidatePath)
        {
            var stock = await File.ReadAllTextAsync(stockPath);
            var transformed = WebSubtitleUx.Transform(new WebSubtitlePayload { Contents = stock });
            Require(transformed != stock, "known stock asset is transformed");
            Require(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(transformed))).Equals(
                "8a43027e9b9c39c6998f8106fc96ef31e2f9c111130fd2482df66043f3c1ef6a", StringComparison.OrdinalIgnoreCase), "callback preserves exact verified player bytes");
            Require(WebSubtitleUx.Transform(new WebSubtitlePayload { Contents = transformed }) == transformed, "already-transformed asset is unchanged");
            Require(WebSubtitleUx.Transform(new WebSubtitlePayload { Contents = stock + "\n" }) == stock + "\n", "unknown asset is not partially patched");
            Require(WebSubtitleUx.Transform(new WebSubtitlePayload { Contents = string.Empty }) == string.Empty, "empty asset is unchanged");
            await File.WriteAllTextAsync(candidatePath, transformed, new UTF8Encoding(false));

            var host = new WebSubtitleUx(NullLogger<WebSubtitleUx>.Instance);
            await host.StartAsync(CancellationToken.None);
            var registration = Jellyfin.Plugin.FileTransformation.PluginInterface.LastRegistration
                ?? throw new InvalidOperationException("Missing runtime registration.");
            using (var document = JsonDocument.Parse(registration))
            {
                var payload = document.RootElement;
                Require(payload.GetProperty("callbackAssembly").GetString() == typeof(WebSubtitleUx).Assembly.FullName, "callback belongs to Subtitle Guard assembly");
                Require(payload.GetProperty("callbackClass").GetString() == typeof(WebSubtitleUx).FullName, "public callback type is registered");
                Require(payload.GetProperty("callbackMethod").GetString() == nameof(WebSubtitleUx.Transform), "public transform method is registered");
                Require(payload.GetProperty("id").GetGuid() == new Guid("08203b64-cae7-48aa-a287-00e481ea7056"), "runtime registration has a distinct stable ID");
                Require(!payload.TryGetProperty("transformationEndpoint", out _), "registration uses no HTTP callback or saved rule");
            }
            await host.StopAsync(CancellationToken.None);
            Require(Jellyfin.Plugin.FileTransformation.PluginInterface.Removed == new Guid("08203b64-cae7-48aa-a287-00e481ea7056"), "shutdown unregisters only the plugin callback");

            Jellyfin.Plugin.FileTransformation.PluginInterface.RejectRegistration = true;
            Jellyfin.Plugin.FileTransformation.PluginInterface.Removed = null;
            await host.StartAsync(CancellationToken.None);
            await host.StopAsync(CancellationToken.None);
            Require(Jellyfin.Plugin.FileTransformation.PluginInterface.Removed is null, "unavailable optional integration does not break startup or remove unrelated rules");
            Console.WriteLine("PASS plugin-owned Web callback: byte parity, fail-closed asset gate, idempotence, runtime registration, shutdown cleanup, unavailable integration");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}

namespace Jellyfin.Plugin.FileTransformation
{
    public sealed record WebUxTestPayload(string Json)
    {
        public static WebUxTestPayload Parse(string json) => new(json);
    }

    public static class PluginInterface
    {
        public static string? LastRegistration { get; private set; }
        public static Guid? Removed { get; set; }
        public static bool RejectRegistration { get; set; }

        public static void RegisterTransformation(WebUxTestPayload payload)
        {
            if (RejectRegistration) throw new InvalidOperationException("Synthetic unavailable integration.");
            LastRegistration = payload.Json;
        }

        public static void RemoveTransformation(Guid id) => Removed = id;
    }
}