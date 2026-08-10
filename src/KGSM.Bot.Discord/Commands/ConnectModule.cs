using Discord;
using Discord.Interactions;

using KGSM.Bot.Core.Common;
using KGSM.Bot.Core.Interfaces;
using KGSM.Bot.Core.Models;
using KGSM.Bot.Discord.Autocomplete;

using Microsoft.Extensions.Logging;

using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Core.Models;

namespace KGSM.Bot.Discord.Commands;

/// <summary>
/// Answers the question a game Discord asks more than any other: how do I join.
/// </summary>
/// <remarks>
/// This host can answer it twice over — the address and ports a server is served on, and whether
/// those ports are actually reachable — and the second half is the one nothing else answers. Both
/// halves are separately unknown-able and each says so on its own, so a host with no firewall
/// authority still hands out an address, and a host that could not read its external IP still hands
/// out the ports.
/// </remarks>
[RequireTier(KgsmTier.Viewer)]
public class ConnectModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly IServerConnectionService _connections;
    private readonly ILogger<ConnectModule> _logger;

    public ConnectModule(IServerConnectionService connections, ILogger<ConnectModule> logger)
    {
        _connections = connections;
        _logger = logger;
    }

    [SlashCommand("connect", "How to join a game server — address, ports, and whether they are reachable")]
    public async Task ConnectAsync(
        [Summary(description: "Game server instance")]
        [Autocomplete(typeof(InstancesAutocompleteHandler))]
        string instance)
    {
        try
        {
            // Reading the instance, the host's addresses and the firewall authority takes longer than
            // Discord's three seconds allows an interaction to sit unanswered.
            await DeferAsync();

            Result<ServerConnection> result = await _connections.DescribeAsync(instance);

            if (result.IsFailure)
            {
                await FollowupAsync($"⚠️ {result.Error}");
                return;
            }

            await FollowupAsync(embed: Render(result.Value!));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling connect command for instance {InstanceName}", instance);
            await FollowupAsync($"An error occurred: {ex.Message}");
        }
    }

    private static Embed Render(ServerConnection connection)
    {
        var embed = new EmbedBuilder()
            .WithTitle($"How to join {connection.Instance}")
            .WithColor(connection.IsRunning ? Color.Green : Color.LightGrey)
            .WithCurrentTimestamp();

        embed.AddField("Address", AddressField(connection));

        if (connection.Ports.Count > 0)
            embed.AddField("Ports", $"`{connection.Ports.ToUfwSpec()}`", inline: true);

        embed.AddField("Reachable", ReachabilityField(connection.Firewall), inline: true);

        if (connection.LocalAddresses.Count > 0)
        {
            embed.AddField("On the same network",
                string.Join("\n", connection.LocalAddresses.Select(ip => $"`{Join(ip, connection)}`")));
        }

        if (!connection.IsRunning)
        {
            embed.WithFooter(
                "This server is not running, so nothing is listening on those ports right now.");
        }

        return embed.Build();
    }

    /// <summary>
    /// The line to copy, and where the address came from. An operator-set address is stated plainly; a
    /// measured one is qualified, because it is true at the moment it was read and can change without
    /// anybody being told.
    /// </summary>
    private static string AddressField(ServerConnection connection) => connection.AddressSource switch
    {
        AddressSource.Configured => $"`{Join(connection.Address!, connection)}`",

        AddressSource.Measured =>
            $"`{Join(connection.Address!, connection)}`\n" +
            "This is the host's current external IP — it can change without notice.",

        _ => "This host could not determine its own external address, so I can't tell you what to " +
             "type. The ports below are still right.",
    };

    /// <summary>
    /// What the firewall authority said, in the words that keep it honest.
    /// </summary>
    /// <remarks>
    /// The three ways of not knowing are all phrased as not knowing. In particular a backend that is
    /// installed but not enforcing is reported as reachable-because-nothing-is-filtering, which is
    /// the opposite of what its empty rule set naively reads as.
    /// </remarks>
    private static string ReachabilityField(FirewallExposure firewall)
    {
        string backend = firewall.Backend is null ? "the firewall" : $"`{firewall.Backend}`";

        return firewall.Exposure switch
        {
            PortExposure.Open => $"✅ Yes — {backend} is holding every port open.",
            PortExposure.Partial => $"⚠️ Partly — {backend} is holding some of these ports open, not all.",
            PortExposure.Closed => $"⛔ No — {backend} is enforcing and holds none of these ports open.",
            PortExposure.Unfiltered => $"✅ Yes — {backend} is installed but not enforcing, so nothing is filtered.",
            PortExposure.Unknown => "❔ The firewall could not say.",
            _ => "❔ No firewall authority is running here, so I can't tell you.",
        };
    }

    /// <summary>
    /// An address joined to the first port the server declares, which is the one a player types. A
    /// server that declares none is given the bare address rather than an invented default — a
    /// guessed port is a wrong answer that looks like a right one.
    /// </summary>
    private static string Join(string address, ServerConnection connection)
    {
        PortMapping? first = connection.Ports.Count > 0 ? connection.Ports[0] : null;
        return first is null ? address : $"{address}:{first.Start}";
    }
}
