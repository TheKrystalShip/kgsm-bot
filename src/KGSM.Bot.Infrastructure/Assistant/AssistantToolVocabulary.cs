using System.Text.Json;

namespace KGSM.Bot.Infrastructure.Assistant;

/// <summary>
/// The assistant's tools in words, and the one safe sentence each result is worth.
/// </summary>
/// <remarks>
/// <para>
/// A tool name is the catalog's vocabulary, not a reader's: <c>run_health_check</c> and
/// <c>trace_root_cause</c> mean something to whoever wrote the catalog and nothing to somebody who
/// opened a crash thread. The label is what a surface shows instead.
/// </para>
/// <para>
/// <b>An unknown tool is described, never hidden.</b> The assistant's catalog grows without this
/// repo being rebuilt, and a step dropped because its name is unrecognised would make the account of
/// a turn quietly incomplete — the one thing a transcript must not be. A name nobody has written
/// prose for is turned back into words (<c>find_files</c> → "Find files"), which reads correctly for
/// every tool the catalog is likely to gain.
/// </para>
/// </remarks>
public static class AssistantToolVocabulary
{
    private static readonly IReadOnlyDictionary<string, string> Labels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["server_info"] = "Checked the server",
            ["host_info"] = "Checked the host",
            ["blueprint_info"] = "Looked up the game",
            ["events"] = "Read recent events",
            ["run_health_check"] = "Ran a health check",
            ["trace_root_cause"] = "Traced the root cause",
            ["read_console"] = "Read the console",
            ["read_file"] = "Read a file",
            ["list_files"] = "Listed files",
            ["find_files"] = "Searched for a file",
            ["search_files"] = "Searched inside the files",
            ["get_performance"] = "Checked performance",
            ["get_network"] = "Checked the network",
            ["search"] = "Searched the guides",
            ["fetch_url"] = "Read a page",
            ["server_command"] = "Proposed a server action",
            ["set_config_value"] = "Proposed a config change",
            ["backup_command"] = "Proposed a backup action",
            ["player_command"] = "Proposed a player action",
            ["install_server"] = "Proposed an install",
        };

    /// <summary>
    /// <paramref name="tool"/> in words. A tool this build has no prose for is turned back into
    /// words from its own name rather than shown raw or dropped.
    /// </summary>
    public static string Label(string tool)
    {
        if (string.IsNullOrWhiteSpace(tool))
            return "Worked on it";

        if (Labels.TryGetValue(tool, out var label))
            return label;

        var words = tool.Replace('_', ' ').Replace('-', ' ').Trim();
        return words.Length == 0
            ? "Worked on it"
            : char.ToUpperInvariant(words[0]) + words[1..];
    }

    /// <summary>
    /// What the step is about, taken from the arguments the assistant called the tool with — an
    /// instance name where there is one, else whatever names the target.
    /// </summary>
    /// <remarks>
    /// Read from the arguments rather than the result, because it is known when the tool STARTS. A
    /// surface that could only name the subject on completion would show a row of anonymous "working"
    /// lines for the whole turn, which is the part people actually watch.
    /// </remarks>
    public static string? SubjectOf(IReadOnlyDictionary<string, string?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
            return null;

        foreach (var key in new[] { "instance_name", "blueprint_name", "query", "path", "pattern", "target" })
        {
            if (arguments.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }

    /// <summary>
    /// The one line a structured result is worth, or <see langword="null"/> when it carries nothing
    /// briefly sayable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Read from the card, never from the summary.</b> The frame's <c>summary</c> is the model's
    /// grounding text — for a console read, the log itself — and this surface does not publish it.
    /// The card is the structured projection the Control Panel renders, and only its describing
    /// fields are read here: what the result is about, and how far it is trusted.
    /// </para>
    /// <para>
    /// A card whose shape this build does not recognise yields null rather than a guess. The
    /// assistant owns this vocabulary, and a surface inventing a reading of an unfamiliar payload is
    /// how a tool that found nothing comes to be described as one that found something.
    /// </para>
    /// </remarks>
    /// <param name="card">The structured result, or null for a tool that produces none.</param>
    /// <param name="knownSubject">
    /// What the row already says this step is about. A card naming the same thing adds nothing, and
    /// printing it anyway reads as two facts where there is one.
    /// </param>
    public static string? DescribeCard(JsonElement? card, string? knownSubject = null)
    {
        if (card is not { ValueKind: JsonValueKind.Object } element)
            return null;

        var parts = new List<string>(2);

        if (element.TryGetProperty("subject", out var subject)
            && subject.ValueKind == JsonValueKind.Object
            && subject.TryGetProperty("id", out var id)
            && id.ValueKind == JsonValueKind.String
            && id.GetString() is { Length: > 0 } subjectId
            && !string.Equals(subjectId, knownSubject, StringComparison.OrdinalIgnoreCase))
        {
            parts.Add(subjectId);
        }

        // Said only when it qualifies the result. `confirmed` is the assistant's word for a measured
        // fact — the ordinary case — and printing it on every row trains a reader to skip the one
        // place that says a conclusion was inferred rather than observed.
        if (element.TryGetProperty("confidence", out var confidence)
            && confidence.ValueKind == JsonValueKind.String
            && confidence.GetString() is { Length: > 0 } word
            && !string.Equals(word, "confirmed", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add($"confidence: {word.ToLowerInvariant()}");
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }
}
