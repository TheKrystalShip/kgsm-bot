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

        // Register application services
        services.AddTransient<ServerEventCoordinatorService>();

        return services;
    }
}
