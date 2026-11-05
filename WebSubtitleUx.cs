using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SubtitleGuard;

public sealed class WebSubtitleUx(ILogger<WebSubtitleUx> logger) : IHostedService
{
    private static readonly Guid TransformationId = new("08203b64-cae7-48aa-a287-00e481ea7056");
    private static readonly JsonElement Definition = ReadDefinition();
    private MethodInfo? _remove;

    private static JsonElement ReadDefinition()
    {
        using var resource = typeof(WebSubtitleUx).Assembly.GetManifestResourceStream("SubtitleGuard.WebSubtitleUx.json")
            ?? throw new InvalidOperationException("Missing embedded subtitle UX definition.");
        using var document = JsonDocument.Parse(resource);
        return document.RootElement.Clone();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var api = AssemblyLoadContext.All.SelectMany(context => context.Assemblies)
                .Select(assembly => assembly.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface"))
                .FirstOrDefault(type => type is not null);
            var register = api?.GetMethod("RegisterTransformation", BindingFlags.Public | BindingFlags.Static);
            var remove = api?.GetMethod("RemoveTransformation", BindingFlags.Public | BindingFlags.Static, [typeof(Guid)]);
            var parameters = register?.GetParameters();
            var parse = parameters is { Length: 1 }
                ? parameters[0].ParameterType.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, [typeof(string)]) : null;
            if (register is null || remove is null || parse is null)
            {
                logger.LogInformation("Subtitle Guard Web UX inactive: compatible File Transformation API is unavailable; extraction guards are unchanged.");
                return Task.CompletedTask;
            }

            var payload = JsonSerializer.Serialize(new
            {
                id = TransformationId,
                fileNamePattern = Definition.GetProperty("filenamePattern").GetString(),
                callbackAssembly = typeof(WebSubtitleUx).Assembly.FullName,
                callbackClass = typeof(WebSubtitleUx).FullName,
                callbackMethod = nameof(Transform)
            });
            register.Invoke(null, [parse.Invoke(null, [payload])]);
            _remove = remove;
            logger.LogInformation("Subtitle Guard Web UX registered through File Transformation; no saved transformation rules or playback changes.");
        }
        catch (Exception exception) when (exception is TargetInvocationException or ArgumentException or InvalidOperationException or TypeLoadException)
        {
            logger.LogWarning("Subtitle Guard Web UX registration unavailable ({Failure}); extraction guards are unchanged.", exception.GetType().Name);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        try { _remove?.Invoke(null, [TransformationId]); }
        catch (Exception exception) when (exception is TargetInvocationException or ArgumentException)
        {
            logger.LogWarning("Subtitle Guard Web UX unregistration failed ({Failure}).", exception.GetType().Name);
        }
        _remove = null;
        return Task.CompletedTask;
    }

    public static string Transform(WebSubtitlePayload payload)
    {
        var source = payload.Contents;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
        if (!hash.Equals(Definition.GetProperty("originalSha256").GetString(), StringComparison.OrdinalIgnoreCase)) return source;
        foreach (var rule in Definition.GetProperty("transformations").EnumerateArray())
            source = source.Replace(rule.GetProperty("SearchText").GetString()!, rule.GetProperty("ReplaceText").GetString()!, StringComparison.Ordinal);
        var transformedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
        return transformedHash.Equals(Definition.GetProperty("patchedSha256").GetString(), StringComparison.OrdinalIgnoreCase)
            ? source : payload.Contents;
    }
}

public sealed class WebSubtitlePayload
{
    public string Contents { get; set; } = string.Empty;
}