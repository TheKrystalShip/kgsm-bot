using Discord;
using Discord.Interactions;
using Discord.WebSocket;

using KGSM.Bot.Application.Commands;
using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Discord.Llm;
using KGSM.Bot.Infrastructure.Configuration;

using MediatR;

using TheKrystalShip.Kgsm.Assistant;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KGSM.Bot.Discord.Commands;

/// <summary>
/// Handles the Confirm/Cancel buttons posted for staged destructive ops. This is
/// the model-independent execution gate: the LLM only ever STAGES install/uninstall;
/// the actual command runs here, after a human clicks Confirm. The click is
/// re-authorized (the clicker must hold the action role) and the target is
/// re-validated against the live list — neither is trusted from the staging turn.
/// </summary>
public class ConfirmationModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly IMediator _mediator;
    private readonly IKgsmStateCache _stateCache;
    private readonly DiscordOptions _options;
    private readonly ILogger<ConfirmationModule> _logger;

    public ConfirmationModule(
        IMediator mediator,
        IKgsmStateCache stateCache,
        IOptions<DiscordOptions> options,
        ILogger<ConfirmationModule> logger)
    {
        _mediator = mediator;
        _stateCache = stateCache;
        _options = options.Value;
        _logger = logger;
    }

    // customId: "kgsmcf~<payload>" — the wildcard captures everything after the prefix.
    [ComponentInteraction("kgsmcf~*")]
    public async Task ConfirmAsync(string data)
    {
        if (!IsAuthorized(Context.User))
        {
            // Leave the prompt intact so a permitted user can still confirm.
            await RespondAsync("⛔ You don't have permission to confirm server actions.", ephemeral: true);
            return;
        }

        if (!ConfirmationIds.TryParse(data, out var confirmation))
        {
            await RespondAsync("⚠️ That confirmation is malformed and can't be processed.", ephemeral: true);
            return;
        }

        var component = (SocketMessageComponent)Context.Interaction;

        // Ack within Discord's ~3s window and clear the buttons so it can't be double-clicked,
        // THEN do the (potentially long) kgsm work, then edit in the final result.
        await component.UpdateAsync(m =>
        {
            m.Content = "⏳ Working on it…";
            m.Components = new ComponentBuilder().Build();
        });

        var outcome = confirmation.Kind switch
        {
            ConfirmationKind.Uninstall => await RunUninstallAsync(confirmation.Target),
            ConfirmationKind.Install => await RunInstallAsync(confirmation.Target, confirmation.InstanceName),
            _ => "⚠️ Unknown action."
        };

        await component.ModifyOriginalResponseAsync(m => m.Content = outcome);
    }

    [ComponentInteraction(ConfirmationIds.Cancel)]
    public async Task CancelAsync()
    {
        var component = (SocketMessageComponent)Context.Interaction;
        await component.UpdateAsync(m =>
        {
            m.Content = "❌ Cancelled — nothing was changed.";
            m.Components = new ComponentBuilder().Build();
        });
    }

    private async Task<string> RunUninstallAsync(string instanceName)
    {
        // Re-validate the target still exists (it was resolved at staging time).
        var instances = await _stateCache.GetInstancesAsync();
        var match = instances.Keys.FirstOrDefault(
            k => string.Equals(k, instanceName, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return $"⚠️ `{instanceName}` no longer exists — nothing to uninstall.";

        _logger.LogInformation("Confirmed uninstall of {Instance} by {User}", match, Context.User.Username);

        var result = await _mediator.Send(new UninstallServerCommand(match));
        return result.IsSuccess
            ? $"🗑️ Uninstalled **{match}**."
            : $"⚠️ Could not uninstall **{match}**: {result.ErrorMessage ?? "unknown error"}.";
    }

    private async Task<string> RunInstallAsync(string blueprint, string? instanceName)
    {
        // Re-validate the blueprint still exists.
        var blueprints = await _stateCache.GetBlueprintsAsync();
        var match = blueprints.Keys.FirstOrDefault(
            k => string.Equals(k, blueprint, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return $"⚠️ Blueprint `{blueprint}` is no longer available.";

        // Re-check the requested name doesn't now collide.
        if (!string.IsNullOrWhiteSpace(instanceName))
        {
            var instances = await _stateCache.GetInstancesAsync();
            if (instances.Keys.Any(k => string.Equals(k, instanceName, StringComparison.OrdinalIgnoreCase)))
                return $"⚠️ An instance named `{instanceName}` already exists — pick another name.";
        }

        _logger.LogInformation(
            "Confirmed install of {Blueprint} (name={Name}) by {User}",
            match, instanceName ?? "(default)", Context.User.Username);

        var result = await _mediator.Send(new InstallServerCommand(match, null, null, instanceName));
        return result.IsSuccess
            ? $"📦 Installed a new **{match}** server{(instanceName is null ? "" : $" (`{instanceName}`)")}."
            : $"⚠️ Could not install **{match}**: {result.ErrorMessage ?? "unknown error"}.";
    }

    private bool IsAuthorized(SocketUser user) =>
        _options.ActionRoleId != 0 &&
        user is SocketGuildUser guildUser &&
        guildUser.Roles.Any(r => r.Id == _options.ActionRoleId);
}
