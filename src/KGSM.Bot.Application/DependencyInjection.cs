using KGSM.Bot.Application.Services;

using Microsoft.Extensions.DependencyInjection;

namespace KGSM.Bot.Application;

/// <summary>
/// Extension methods for registering application services
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        // Register the consolidated server operations service (replaces MediatR dispatcher)
        services.AddSingleton<IServerService, ServerService>();

        // Register application services. The coordinator is a singleton because initializing it
        // APPENDS callbacks to the singleton IServerEventHandler's lists — a second instance would
        // register a second full set and double every announcement, which its own guard cannot
        // catch across instances. Every dependency it takes is a singleton too, so there is no
        // captive-dependency concern.
        services.AddSingleton<ServerEventCoordinatorService>();

        return services;
    }
}
