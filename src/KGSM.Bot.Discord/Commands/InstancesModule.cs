using Discord;
using Discord.Interactions;

using KGSM.Bot.Application;
using KGSM.Bot.Core.Common;
using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Discord.Autocomplete;
using KGSM.Bot.Infrastructure.Configuration;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TheKrystalShip.KGSM.Auth;

namespace KGSM.Bot.Discord.Commands;

/// <summary>
/// Discord module for managing game server instances
/// </summary>
[RequireTier(KgsmTier.Viewer)]
public class InstancesModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly IServerService _server;
    private readonly IGuildStore _guilds;
    private readonly IInvocationContext _invocation;
    private readonly DiscordOptions _options;
    private readonly ILogger<InstancesModule> _logger;
    private const string SUMMARY = "Game server instance";

    public InstancesModule(
        IServerService server,
        IGuildStore guilds,
        IInvocationContext invocation,
        IOptions<DiscordOptions> options,
        ILogger<InstancesModule> logger)
    {
        _server = server;
        _guilds = guilds;
        _invocation = invocation;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Whether an answer to a read command is shown only to the person who asked.
    /// </summary>
    /// <remarks>
    /// <b>Only for the commands that answer one person's question.</b> "Is it up", "what's installed"
    /// and "what is the watchdog doing" are asked to find something out, not to tell the channel — and
    /// a busy channel fills with everyone else's. <c>/connect</c> is deliberately not one of these:
    /// its entire purpose is to be read by somebody other than the person who typed it.
    /// </remarks>
    private bool Quietly => _options.EphemeralReads;

    [SlashCommand("start", "Start up a game server")]
    [Mutating]
    public async Task StartAsync(
        [Summary(description: SUMMARY)]
        [Autocomplete(typeof(InstancesAutocompleteHandler))]
        string instance)
    {
        using var provenance = _invocation.Begin(Invocation.ForDiscordUser(Context.User.Username));
        try
        {
            _logger.LogInformation("Handling start command for instance {InstanceName}", instance);

            // Check if the instance is already running
            var statusResult = await _server.IsActiveAsync(instance);
            if (statusResult.IsSuccess && statusResult.IsActive)
            {
                await RespondAsync($"Instance {instance} is already running");
                return;
            }

            await RespondAsync($"Starting {instance}...");

            var result = await _server.StartAsync(instance);
            if (!result.IsSuccess)
            {
                _logger.LogWarning("Failed to start instance {InstanceName}: {Error}",
                    instance, result.ErrorMessage);
                await FollowupAsync($"Error starting {instance}: {result.ErrorMessage}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling start command for instance {InstanceName}", instance);
            await RespondAsync($"An error occurred: {ex.Message}");
        }
    }

    [SlashCommand("stop", "Shut down a game server")]
    [Mutating]
    public async Task StopAsync(
        [Summary(description: SUMMARY)]
        [Autocomplete(typeof(InstancesAutocompleteHandler))]
        string instance)
    {
        using var provenance = _invocation.Begin(Invocation.ForDiscordUser(Context.User.Username));
        try
        {
            _logger.LogInformation("Handling stop command for instance {InstanceName}", instance);

            await RespondAsync($"Stopping {instance}...");

            var result = await _server.StopAsync(instance);
            if (!result.IsSuccess)
            {
                _logger.LogWarning("Failed to stop instance {InstanceName}: {Error}",
                    instance, result.ErrorMessage);
                await FollowupAsync($"Error stopping {instance}: {result.ErrorMessage}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling stop command for instance {InstanceName}", instance);
            await RespondAsync($"An error occurred: {ex.Message}");
        }
    }

    [SlashCommand("restart", "Restart a game server")]
    [Mutating]
    public async Task RestartAsync(
        [Summary(description: SUMMARY)]
        [Autocomplete(typeof(InstancesAutocompleteHandler))]
        string instance)
    {
        using var provenance = _invocation.Begin(Invocation.ForDiscordUser(Context.User.Username));
        try
        {
            _logger.LogInformation("Handling restart command for instance {InstanceName}", instance);

            await RespondAsync($"Restarting {instance}...");

            var result = await _server.RestartAsync(instance);
            if (!result.IsSuccess)
            {
                _logger.LogWarning("Failed to restart instance {InstanceName}: {Error}",
                    instance, result.ErrorMessage);
                await FollowupAsync($"Error restarting {instance}: {result.ErrorMessage}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling restart command for instance {InstanceName}", instance);
            await RespondAsync($"An error occurred: {ex.Message}");
        }
    }

    [SlashCommand("status", "Get a detailed status of a game server")]
    public async Task StatusAsync(
        [Summary(description: SUMMARY)]
        [Autocomplete(typeof(InstancesAutocompleteHandler))]
        string instance)
    {
        try
        {
            _logger.LogInformation("Handling status command for instance {InstanceName}", instance);

            var result = await _server.GetStatusAsync(instance);

            if (!result.IsSuccess)
            {
                _logger.LogWarning("Failed to get status for instance {InstanceName}: {Error}",
                    instance, result.ErrorMessage);
                await RespondAsync($"Error getting status for {instance}: {result.ErrorMessage}", ephemeral: Quietly);
                return;
            }

            await RespondAsync(result.Status ?? "No status information available", ephemeral: Quietly);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling status command for instance {InstanceName}", instance);
            await RespondAsync($"An error occurred: {ex.Message}", ephemeral: Quietly);
        }
    }

    [SlashCommand("supervision", "Show the watchdog supervision state of a game server")]
    public async Task SupervisionAsync(
        [Summary(description: SUMMARY)]
        [Autocomplete(typeof(InstancesAutocompleteHandler))]
        string instance)
    {
        try
        {
            _logger.LogInformation("Handling supervision command for instance {InstanceName}", instance);

            var result = await _server.GetWatchdogStatusAsync(instance);

            if (!result.IsSuccess)
            {
                // The only failure here is an unreachable daemon — report it plainly
                // rather than as a server error; native lifecycle still works without it.
                _logger.LogWarning("Failed to get supervision state for instance {InstanceName}: {Error}",
                    instance, result.ErrorMessage);
                await RespondAsync($"⚠️ {result.ErrorMessage} Native start/stop still works; only live supervision details are unavailable.", ephemeral: Quietly);
                return;
            }

            var status = result.Status!;
            if (!status.IsSupervised || status.State is null)
            {
                await RespondAsync($"{instance} is not currently supervised by the watchdog " +
                    "(it was started outside the daemon, or is not a native standalone instance).", ephemeral: Quietly);
                return;
            }

            var state = status.State;
            var liveness = state.Populated ? "🟢 Online" : "🔴 Offline";
            var embed = new EmbedBuilder()
                .WithTitle($"Watchdog supervision — {state.Name}")
                .WithColor(state.Phase == "failed" ? Color.Red : Color.Green)
                .AddField("Liveness", liveness, inline: true)
                .AddField("Desired", string.IsNullOrEmpty(state.Desired) ? "—" : state.Desired, inline: true)
                .AddField("Phase", string.IsNullOrEmpty(state.Phase) ? "—" : state.Phase, inline: true)
                .AddField("Restarts", state.Restarts.ToString(), inline: true)
                .AddField("PID", state.Pid?.ToString() ?? "—", inline: true)
                .WithCurrentTimestamp();

            if (!string.IsNullOrWhiteSpace(state.Reason))
                embed.AddField("Reason", state.Reason);

            await RespondAsync(embed: embed.Build(), ephemeral: Quietly);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling supervision command for instance {InstanceName}", instance);
            await RespondAsync($"An error occurred: {ex.Message}", ephemeral: Quietly);
        }
    }

    [SlashCommand("is-active", "Check if an instance is currently running")]
    public async Task IsActiveAsync(
        [Summary(description: SUMMARY)]
        [Autocomplete(typeof(InstancesAutocompleteHandler))]
        string instance)
    {
        try
        {
            _logger.LogInformation("Handling is-active command for instance {InstanceName}", instance);

            var result = await _server.IsActiveAsync(instance);

            if (!result.IsSuccess)
            {
                _logger.LogWarning("Failed to check active status for instance {InstanceName}: {Error}",
                    instance, result.ErrorMessage);
                await RespondAsync($"Error checking active status for {instance}: {result.ErrorMessage}", ephemeral: Quietly);
                return;
            }

            string outputMessage = $"{instance} is {(result.IsActive ? "active" : "inactive")}";
            await RespondAsync(outputMessage, ephemeral: Quietly);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling is-active command for instance {InstanceName}", instance);
            await RespondAsync($"An error occurred: {ex.Message}", ephemeral: Quietly);
        }
    }

    [SlashCommand("list", "List all game server instances")]
    public async Task ListAsync()
    {
        try
        {
            _logger.LogInformation("Handling list command for instances");

            var result = await _server.GetAllInstancesAsync();

            if (!result.IsSuccess || result.Instances == null)
            {
                _logger.LogWarning("Failed to list instances: {Error}", result.ErrorMessage);
                await RespondAsync($"Error listing instances: {result.ErrorMessage}", ephemeral: Quietly);
                return;
            }

            if (result.Instances.Count == 0)
            {
                await RespondAsync("No instances found", ephemeral: Quietly);
                return;
            }

            var embedBuilder = new EmbedBuilder()
                .WithTitle("Game Server Instances")
                .WithColor(Color.Blue)
                .WithCurrentTimestamp();

            // Each instance needs a live status check (IsActiveAsync spawns a
            // kgsm subprocess). Run them concurrently rather than sequentially so /list
            // scales with the slowest single check instead of their sum. Fields are
            // added afterwards in the original order — Discord renders by add order.
            var fields = await Task.WhenAll(result.Instances.Select(async pair =>
            {
                var (name, instance) = pair;

                var isActive = await _server.IsActiveAsync(name);
                string status = isActive.IsSuccess && isActive.IsActive ? "🟢 Online" : "🔴 Offline";

                // The channel THIS Discord server reports it in. A server has one only where a board
                // is on, and the answer is per-guild — the same instance reports in a different
                // channel in each guild that runs one, and in none at all in a guild that doesn't.
                string channelInfo = Context.Guild is not null
                    && _guilds.ChannelFor(Context.Guild.Id, name) is ulong channelId
                        ? $"<#{channelId}>"
                        : "No channel";

                return new EmbedFieldBuilder()
                    .WithName(name)
                    .WithValue($"Blueprint: {instance.Blueprint}\n" +
                               $"Status: {status}\n" +
                               $"Channel: {channelInfo}\n" +
                               $"Directory: {instance.WorkingDir}")
                    .WithIsInline(true);
            }));

            foreach (var field in fields)
                embedBuilder.AddField(field);

            await RespondAsync(embed: embedBuilder.Build(), ephemeral: Quietly);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling list command");
            await RespondAsync($"An error occurred: {ex.Message}", ephemeral: Quietly);
        }
    }
}
