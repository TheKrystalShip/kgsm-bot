using System.Globalization;

using KGSM.Bot.Core.Common;
using KGSM.Bot.Core.Models;
using KGSM.Bot.Infrastructure.Configuration;
using KGSM.Bot.Infrastructure.Guilds;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace KGSM.Bot.Discord;

/// <summary>
/// Moves a host that was configured for one Discord server — a guild id in the environment and a
/// hand-maintained <c>KGSM:Instances</c> channel map in the settings file — into the guild store.
/// </summary>
/// <remarks>
/// <para>
/// <b>Dry-run by default.</b> Every binding it would write is printed and nothing is touched until
/// <c>--apply</c>. The channels it names carry years of history, and adoption is the one step that
/// can strand them.
/// </para>
/// <para>
/// <b>It refuses a guild that already has a row</b>, rather than merging. A second run that silently
/// re-pointed bindings is how a guild ends up with its channels orphaned and a fresh set created
/// beside them, splitting every server's history in two.
/// </para>
/// <para>
/// The old map's <c>Blueprint</c> field is not carried across: nothing ever read it.
/// </para>
/// </remarks>
internal static class GuildConfigAdoption
{
    /// <summary>The keys this reads. They no longer bind to anything, which is the point.</summary>
    private const string GuildKey = "Discord:GuildId";
    private const string AnnounceKey = "Discord:AnnouncementChannelId";
    private const string CategoryKey = "Discord:InstancesCategoryId";
    private const string InstancesKey = "KGSM:Instances";

    /// <summary>
    /// Reads the old configuration and reports (or writes) the guild store rows it becomes.
    /// </summary>
    /// <param name="settingsPath">
    /// The settings file to read the channel map from. This is deliberately a parameter: the map has
    /// already left the shipped settings file, so adoption is run against the copy the host was
    /// actually running.
    /// </param>
    /// <param name="announceChannelOverride">
    /// The guild's announcement channel, when the old configuration carries none. A host whose every
    /// server had a channel of its own never needed the fallback and left it at zero — but a guild
    /// row requires one, because it is where a server with no channel of its own reports, so it has
    /// to be named rather than guessed at.
    /// </param>
    /// <param name="apply">Write it. Without this nothing is touched.</param>
    /// <returns>0 when there was nothing to do or it was done; 1 when it refused.</returns>
    public static int Run(string settingsPath, ulong announceChannelOverride, bool apply)
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddJsonFile(settingsPath, optional: true, reloadOnChange: false)
            // The same order the bot itself uses, so adoption sees the guild the running bot saw:
            // the id and the category are host identity and live in the env file, never the JSON.
            .AddEnvironmentVariables()
            .Build();

        ulong guildId = Snowflake(configuration[GuildKey]);
        ulong announceChannelId = announceChannelOverride != 0
            ? announceChannelOverride
            : Snowflake(configuration[AnnounceKey]);
        ulong categoryId = Snowflake(configuration[CategoryKey]);

        if (guildId == 0)
        {
            Console.Error.WriteLine(
                $"No {GuildKey} found in '{settingsPath}' or the environment — there is no " +
                "single-guild configuration here to adopt. Nothing to do.");
            return 0;
        }

        List<(string Instance, ulong ChannelId)> bindings = [];
        foreach (IConfigurationSection instance in configuration.GetSection(InstancesKey).GetChildren())
        {
            ulong channelId = Snowflake(instance["ChannelId"]);
            if (channelId != 0)
                bindings.Add((instance.Key, channelId));
        }
        bindings.Sort((a, b) => string.CompareOrdinal(a.Instance, b.Instance));

        Console.WriteLine($"Reading  {settingsPath}");
        Console.WriteLine($"Guild    {guildId}");
        Console.WriteLine($"Announce {(announceChannelId != 0 ? announceChannelId.ToString(CultureInfo.InvariantCulture) : "(none configured)")}");
        Console.WriteLine($"Board    {(categoryId != 0 ? $"on, under category {categoryId}" : "off (no category configured)")}");
        Console.WriteLine($"Channels {bindings.Count}");
        foreach ((string instance, ulong channelId) in bindings)
            Console.WriteLine($"           {instance,-24} → {channelId}");
        Console.WriteLine();

        if (announceChannelId == 0)
        {
            // The announcement channel is the one required piece: it is where every server without a
            // channel of its own reports, and a guild row cannot be written without one.
            Console.Error.WriteLine(
                $"REFUSED: {AnnounceKey} is not set, and a guild's announcement channel is required — " +
                "it is where a server with no channel of its own reports, and there is nothing here to " +
                "infer it from. Name it with --announce-channel <id>, or set the guild up from Discord " +
                "with /setup announce instead of adopting.");
            return 1;
        }

        SqliteGuildStore store = Store(configuration);

        if (!store.Available)
        {
            Console.Error.WriteLine($"REFUSED: {store.UnavailableReason}");
            return 1;
        }

        if (store.Find(guildId) is GuildTopology existing)
        {
            Console.Error.WriteLine(
                $"REFUSED: guild {guildId} is already in the store (set up by {existing.ConfiguredBy} " +
                $"on {existing.ConfiguredUtc:yyyy-MM-dd}, {store.ChannelsIn(guildId).Count} channel(s)). " +
                "Adoption does not merge — re-pointing bindings is how channels get orphaned and " +
                "duplicated. Change it with /setup, or delete the row deliberately first.");
            return 1;
        }

        if (!apply)
        {
            Console.WriteLine("Dry run — nothing was written. Re-run with --apply to write it.");
            return 0;
        }

        Result guild = store.SetAnnounceChannel(guildId, announceChannelId, "adopted");
        if (guild.IsFailure)
        {
            Console.Error.WriteLine($"FAILED writing the guild row: {guild.Error}");
            return 1;
        }

        if (categoryId != 0)
        {
            Result board = store.SetBoard(guildId, categoryId);
            if (board.IsFailure)
            {
                Console.Error.WriteLine($"FAILED writing the board category: {board.Error}");
                return 1;
            }
        }

        int written = 0;
        foreach ((string instance, ulong channelId) in bindings)
        {
            Result binding = store.BindChannel(guildId, instance, channelId);
            if (binding.IsFailure)
            {
                Console.Error.WriteLine($"FAILED binding {instance}: {binding.Error}");
                return 1;
            }
            written++;
        }

        Console.WriteLine($"Written: 1 guild, {written} channel binding(s).");
        return 0;
    }

    private static SqliteGuildStore Store(IConfiguration configuration)
    {
        GuildOptions options = new();
        configuration.GetSection(GuildOptions.Section).Bind(options);
        return new SqliteGuildStore(
            Options.Create(options),
            NullLogger<SqliteGuildStore>.Instance);
    }

    private static ulong Snowflake(string? value) =>
        ulong.TryParse(value, CultureInfo.InvariantCulture, out ulong id) ? id : 0;
}
