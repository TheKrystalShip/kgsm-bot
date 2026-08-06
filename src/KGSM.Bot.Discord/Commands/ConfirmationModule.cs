using Discord;
using Discord.Interactions;
using Discord.WebSocket;

using KGSM.Bot.Application;
using KGSM.Bot.Core.Common;
using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Discord.Llm;
using KGSM.Bot.Infrastructure.Configuration;

using TheKrystalShip.Kgsm.Assistant;
using TheKrystalShip.KGSM.Auth;

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
    private readonly IServerService _server;
    private readonly IKgsmStateCache _stateCache;
    private readonly PendingEditStore _pendingEdits;
    private readonly DiscordOptions _options;
    private readonly KgsmRoleMap _roleMap;
    private readonly IInvocationContext _invocation;
    private readonly ILogger<ConfirmationModule> _logger;

    public ConfirmationModule(
        IServerService server,
        IKgsmStateCache stateCache,
        PendingEditStore pendingEdits,
        IOptions<DiscordOptions> options,
        KgsmRoleMap roleMap,
        IInvocationContext invocation,
        ILogger<ConfirmationModule> logger)
    {
        _server = server;
        _stateCache = stateCache;
        _pendingEdits = pendingEdits;
        _options = options.Value;
        _roleMap = roleMap;
        _invocation = invocation;
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

        if (!TryResolve(data, out var confirmation))
        {
            await RespondAsync(
                "⚠️ That confirmation is malformed or has expired and can't be processed.", ephemeral: true);
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

        // The clicker is the authority for the action they just confirmed (origin=discord); flows to the
        // kgsm chokepoint through the awaited Run* dispatches below.
        using var provenance = _invocation.Begin(Invocation.ForDiscordUser(Context.User.Username));

        var outcome = confirmation.Kind switch
        {
            ConfirmationKind.Uninstall => await RunUninstallAsync(confirmation.Target),
            ConfirmationKind.Install => await RunInstallAsync(confirmation.Target, confirmation.InstanceName),
            ConfirmationKind.SetConfig => await RunSetConfigAsync(
                confirmation.Target, confirmation.ConfigKey, confirmation.ConfigValue),
            ConfirmationKind.Start or ConfirmationKind.Stop or ConfirmationKind.Restart
                or ConfirmationKind.Update or ConfirmationKind.Backup
                => await RunCommandAsync(confirmation.Kind, confirmation.Target),
            _ => "⚠️ Unknown action."
        };

        await component.ModifyOriginalResponseAsync(m => m.Content = outcome);
    }

    /// <summary>
    /// Resolves the clicked customId remainder into a staged op. Store-backed (overflow)
    /// confirmations carry only a single-use lookup id (a long SetConfig value didn't fit
    /// the customId); every other kind is self-describing in the id.
    /// </summary>
    private bool TryResolve(string data, out PendingConfirmation confirmation)
    {
        if (ConfirmationIds.TryParseStored(data, out var storeId))
            return _pendingEdits.TryTake(storeId, out confirmation);
        return ConfirmationIds.TryParse(data, out confirmation);
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

    /// <summary>
    /// Runs a confirmed single-instance command (start/stop/restart/update/backup) on the
    /// exact same path as the Discord slash commands. Re-validates the target still
    /// exists (it was resolved at staging time); the click is already re-authorized above.
    /// </summary>
    private async Task<string> RunCommandAsync(ConfirmationKind kind, string instanceName)
    {
        var instances = await _stateCache.GetInstancesAsync();
        var match = instances.Keys.FirstOrDefault(
            k => string.Equals(k, instanceName, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return $"⚠️ `{instanceName}` no longer exists — nothing to {ConfirmationKinds.Verb(kind)}.";

        _logger.LogInformation(
            "Confirmed {Verb} of {Instance} by {User}", ConfirmationKinds.Verb(kind), match, Context.User.Username);

        var result = await RunCommandDirectAsync(kind, match);
        return result.IsSuccess
            ? $"✅ **{match}** has been {ConfirmationKinds.PastTense(kind)}."
            : $"⚠️ Could not {ConfirmationKinds.Verb(kind)} **{match}**: {result.ErrorMessage ?? "unknown error"}.";
    }

    private Task<OperationResult> RunCommandDirectAsync(ConfirmationKind kind, string instance) => kind switch
    {
        ConfirmationKind.Start => _server.StartAsync(instance),
        ConfirmationKind.Stop => _server.StopAsync(instance),
        ConfirmationKind.Restart => _server.RestartAsync(instance),
        ConfirmationKind.Update => _server.UpdateAsync(instance),
        ConfirmationKind.Backup => _server.CreateBackupAsync(instance),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "not a single-instance command"),
    };

    private async Task<string> RunUninstallAsync(string instanceName)
    {
        // Re-validate the target still exists (it was resolved at staging time).
        var instances = await _stateCache.GetInstancesAsync();
        var match = instances.Keys.FirstOrDefault(
            k => string.Equals(k, instanceName, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return $"⚠️ `{instanceName}` no longer exists — nothing to uninstall.";

        _logger.LogInformation("Confirmed uninstall of {Instance} by {User}", match, Context.User.Username);

        var result = await _server.UninstallAsync(match);
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

        var result = await _server.InstallAsync(match, null, null, instanceName);
        return result.IsSuccess
            ? $"📦 Installed a new **{match}** server{(instanceName is null ? "" : $" (`{instanceName}`)")}."
            : $"⚠️ Could not install **{match}**: {result.ErrorMessage ?? "unknown error"}.";
    }

    /// <summary>
    /// Runs a confirmed config edit on the same path as everything else. Re-validates
    /// the target still exists; kgsm owns the key-safety policy, so a refused (denylisted/
    /// invalid) key comes back as a failed result reported to the user.
    /// </summary>
    private async Task<string> RunSetConfigAsync(string instanceName, string? key, string? value)
    {
        if (string.IsNullOrWhiteSpace(key))
            return "⚠️ No configuration key was given — nothing to set.";

        var instances = await _stateCache.GetInstancesAsync();
        var match = instances.Keys.FirstOrDefault(
            k => string.Equals(k, instanceName, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return $"⚠️ `{instanceName}` no longer exists — nothing to configure.";

        var newValue = value ?? string.Empty;

        _logger.LogInformation(
            "Confirmed set-config of {Instance} ({Key}) by {User}", match, key, Context.User.Username);

        var result = await _server.SetConfigAsync(match, key, newValue);
        var shown = newValue.Length == 0 ? "(empty)" : newValue;
        return result.IsSuccess
            ? $"⚙️ Set `{key}` = `{shown}` on **{match}**."
            : $"⚠️ Could not set `{key}` on **{match}**: {result.ErrorMessage ?? "unknown error"}.";
    }

    // Re-resolved at the click rather than trusted from the staging turn: the roles a person held
    // when the button was posted are not the roles they hold now. A non-member has no member object,
    // which the map reads as "not a member" and denies.
    private bool IsAuthorized(SocketUser user) =>
        _roleMap.ResolveSnowflakes((user as SocketGuildUser)?.Roles.Select(r => r.Id))
            >= KgsmTier.Operator;
}
