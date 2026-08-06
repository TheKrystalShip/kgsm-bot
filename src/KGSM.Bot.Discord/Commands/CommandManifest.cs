using Discord.Interactions;

using TheKrystalShip.KGSM.Auth;

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KGSM.Bot.Discord.Commands;

/// <summary>
/// Marks a slash command that changes something — a server's run state, or what is installed — and
/// requires <see cref="KgsmTier.Operator"/> to run it. The manifest below carries the mark so the
/// Control Panel can separate the commands that read from the commands that act, which is the
/// distinction an operator is actually looking for when they open the list.
/// </summary>
/// <remarks>
/// One attribute is both the mark and the gate on purpose: the thing that puts a command in the "acts"
/// column is exactly the thing that decides who may run it, so a new mutating command cannot be added
/// and left open by forgetting a second attribute. A command needing a different tier carries
/// <see cref="RequireTierAttribute"/> directly.
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class MutatingAttribute() : RequireTierAttribute(KgsmTier.Operator);

/// <summary>
/// One option of a slash command, named and typed the way Discord presents it. <c>Autocomplete</c>
/// means the option suggests values as you type rather than taking free text.
/// </summary>
internal sealed record CommandOption(
    string Name,
    string? Description,
    string Type,
    bool Required,
    bool Autocomplete);

/// <summary>One slash command this build registers.</summary>
internal sealed record BotCommand(
    string Name,
    string Description,
    bool Mutates,
    IReadOnlyList<CommandOption> Options);

/// <summary>
/// The command catalog the Control Panel lists for this leaf: what a person can type at the bot, in
/// the bot's own words.
/// <para>
/// It is a <strong>file this repo's deploy ships</strong>, for the same reasons the leaf config
/// descriptor is one: this bot has no listening surface to ask, and the list has to be readable when
/// the unit is stopped. It is <strong>generated from the built assembly</strong> — the build runs the
/// binary with <c>--emit-commands</c> — so what the panel shows is what this build registers, and a
/// command cannot be listed that does not exist or renamed without the list following.
/// </para>
/// <para>
/// <c>Gate</c> is what this bot itself requires of whoever runs a <see cref="BotCommand.Mutates"/>
/// command — the tier from the ecosystem's shared role map, so the panel prints the same word that
/// decides the answer everywhere else.
/// </para>
/// </summary>
internal sealed record CommandManifest(
    int SchemaVersion,
    string Leaf,
    string Surface,
    string Gate,
    IReadOnlyList<BotCommand> Commands)
{
    /// <summary>The schema readers match on. A reader that does not know the version skips the file.</summary>
    public const int Version = 1;

    /// <summary>
    /// The leaf id this manifest belongs to — the same id the config descriptor declares and the
    /// filename stem it is installed under.
    /// </summary>
    public const string LeafId = "bot";

    /// <summary>
    /// A mutating slash command requires <see cref="KgsmTier.Operator"/>, enforced by
    /// <see cref="MutatingAttribute"/> before the command body runs. Read commands require guild
    /// membership, which the shared role map floors at <see cref="KgsmTier.Viewer"/>.
    /// </summary>
    public const string SlashCommandGate = KgsmTiers.Operator;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    /// <summary>Read the catalog out of the assembly that carries the modules.</summary>
    public static CommandManifest Build(Assembly assembly) => new(
        SchemaVersion: Version,
        Leaf: LeafId,
        Surface: "discord",
        Gate: SlashCommandGate,
        Commands: [.. assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IInteractionModuleBase).IsAssignableFrom(t))
            .SelectMany(CommandsIn)
            // Reflection makes no promise about the order it returns types or methods in, and this
            // file is committed — an unordered write would show up as a diff on an unrelated build.
            .OrderBy(c => c.Name, StringComparer.Ordinal)]);

    /// <summary>Generate the manifest and write it where the deploy expects to find it.</summary>
    public static void WriteTo(string path)
    {
        string json = JsonSerializer.Serialize(Build(Assembly.GetExecutingAssembly()), JsonOptions);
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, json + Environment.NewLine);
    }

    private static IEnumerable<BotCommand> CommandsIn(Type module)
    {
        // A module may nest its commands under a group word, which becomes part of what a user types.
        // None does today; reading it is what keeps the manifest correct if one starts to.
        string prefix = module.GetCustomAttribute<GroupAttribute>() is { Name: string g } && g.Length > 0
            ? g + " "
            : string.Empty;

        foreach (MethodInfo method in module.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (method.GetCustomAttribute<SlashCommandAttribute>() is not { } slash)
                continue;

            yield return new BotCommand(
                Name: prefix + slash.Name,
                Description: slash.Description,
                Mutates: method.GetCustomAttribute<MutatingAttribute>() is not null,
                Options: [.. method.GetParameters().Select(OptionOf)]);
        }
    }

    private static CommandOption OptionOf(ParameterInfo p)
    {
        SummaryAttribute? summary = p.GetCustomAttribute<SummaryAttribute>();
        return new CommandOption(
            Name: (summary?.Name ?? p.Name ?? "?").ToLowerInvariant(),
            Description: summary?.Description,
            Type: TypeNameOf(p.ParameterType),
            // Discord calls an option optional when the method gives it a default, and required when
            // it does not — the nullability of the type says nothing about it either way.
            Required: !p.HasDefaultValue,
            Autocomplete: p.GetCustomAttribute<AutocompleteAttribute>() is not null);
    }

    // Discord's own option-type vocabulary. Anything the table does not cover falls through under its
    // CLR name rather than being labelled as a type it is not; the manifest tests fail on one, so an
    // unmapped type is caught where it can be added rather than shipped as a wrong label.
    private static string TypeNameOf(Type t) => (Nullable.GetUnderlyingType(t) ?? t) switch
    {
        var x when x == typeof(string) => "string",
        var x when x == typeof(bool) => "boolean",
        var x when x == typeof(int) || x == typeof(long) => "integer",
        var x when x == typeof(double) || x == typeof(decimal) => "number",
        var x when x.IsEnum => "string",
        var x => x.Name.ToLowerInvariant(),
    };
}
