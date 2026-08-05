using Discord.Interactions;

using KGSM.Bot.Application;
using KGSM.Bot.Core.Common;
using KGSM.Bot.Discord.Autocomplete;

using Microsoft.Extensions.Logging;

namespace KGSM.Bot.Discord.Commands;

/// <summary>
/// Discord module for managing blueprints and installing servers
/// </summary>
public class BlueprintsModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly IServerService _server;
    private readonly IInvocationContext _invocation;
    private readonly ILogger<BlueprintsModule> _logger;

    public BlueprintsModule(IServerService server, IInvocationContext invocation, ILogger<BlueprintsModule> logger)
    {
        _server = server;
        _invocation = invocation;
        _logger = logger;
    }

    [SlashCommand("install", "Install a new game server")]
    [Mutating]
    public async Task InstallAsync(
        [Summary(description: "Blueprint to install")]
        [Autocomplete(typeof(BlueprintAutocompleteHandler))]
        string blueprint,

        [Summary(description: "Installation path (optional)")]
        string? path = null,

        [Summary(description: "Version to install (optional)")]
        string? version = null,

        [Summary(description: "Custom name for the instance (optional)")]
        string? name = null)
    {
        using var provenance = _invocation.Begin(Invocation.ForDiscordUser(Context.User.Username));
        try
        {
            _logger.LogInformation("Handling install command for blueprint {BlueprintName} at {Path} with version {Version} and name {Name}",
                blueprint, path ?? "default", version ?? "default", name ?? "auto-generated");

            var installMessage = $"Installing {blueprint}";
            if (!string.IsNullOrEmpty(path)) installMessage += $" at {path}";
            if (!string.IsNullOrEmpty(version)) installMessage += $" version {version}";
            if (!string.IsNullOrEmpty(name)) installMessage += $" with name {name}";
            installMessage += "...";

            await RespondAsync(installMessage);

            var result = await _server.InstallAsync(blueprint, path, version, name);
            if (!result.IsSuccess)
            {
                _logger.LogWarning("Failed to install blueprint {BlueprintName}: {Error}",
                    blueprint, result.ErrorMessage);
                await FollowupAsync($"Error installing {blueprint}: {result.ErrorMessage}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling install command for blueprint {BlueprintName}", blueprint);
            await RespondAsync($"An error occurred: {ex.Message}");
        }
    }

    [SlashCommand("uninstall", "Uninstall a game server")]
    [Mutating]
    public async Task UninstallAsync(
        [Summary(description: "Game server instance")]
        [Autocomplete(typeof(InstancesAutocompleteHandler))]
        string instance)
    {
        using var provenance = _invocation.Begin(Invocation.ForDiscordUser(Context.User.Username));
        try
        {
            _logger.LogInformation("Handling uninstall command for instance {InstanceName}", instance);

            await RespondAsync($"Uninstalling {instance}...");

            var result = await _server.UninstallAsync(instance);
            if (!result.IsSuccess)
            {
                _logger.LogWarning("Failed to uninstall instance {InstanceName}: {Error}",
                    instance, result.ErrorMessage);
                await FollowupAsync($"Error uninstalling {instance}: {result.ErrorMessage}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling uninstall command for instance {InstanceName}", instance);
            await RespondAsync($"An error occurred: {ex.Message}");
        }
    }
}
