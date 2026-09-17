using System.Runtime.Loader;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SubtitleGuard;

public sealed class Plugin(IApplicationPaths paths, IXmlSerializer serializer) : BasePlugin<BasePluginConfiguration>(paths, serializer)
{
    public override Guid Id => new("a7c9c612-4b77-46ed-9b92-b32b83d2f771");
    public override string Name => "Subtitle Guard";
    public override string Description => "Prevents failed native subtitle extraction from becoming a reusable cache.";
}

public sealed class Registrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        var assembly = typeof(Registrator).Assembly;
        if (assembly.IsCollectible)
        {
            AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(Path.GetDirectoryName(assembly.Location)!, "0Harmony.dll"));
            var runtime = AssemblyLoadContext.Default.LoadFromAssemblyPath(assembly.Location);
            var registrator = (IPluginServiceRegistrator)Activator.CreateInstance(runtime.GetType(typeof(Registrator).FullName!)!)!;
            registrator.RegisterServices(services, applicationHost);
            return;
        }

        var guard = new NativeExtractionGuard();
        guard.Install();
        services.AddSingleton(_ => guard);
        services.AddHostedService<GuardStatus>();
    }
}

internal sealed class GuardStatus(NativeExtractionGuard guard, ILogger<GuardStatus> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = guard;
        logger.LogInformation("Subtitle Guard {Version}: native extraction/cache guards installed; no background extraction or playback scheduling.", typeof(Plugin).Assembly.GetName().Version);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}