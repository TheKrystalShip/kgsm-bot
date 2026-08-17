using System.Text.Json;

namespace KGSM.Bot.Infrastructure.Assistant;

/// <summary>
/// The assistant's tool results in words — what a step's card is worth saying about, and who it was
/// about.
/// </summary>
/// <remarks>
/// <b>The label is not here.</b> It comes off the wire: the assistant sends each step's prose with
/// the step, because the tool catalog is a file on the assistant's host and this repo learns of a
/// rename only by being rebuilt. A table here would go stale the moment a tool is renamed and keep
/// showing a name nothing is called any more — which is exactly what it did.
/// </remarks>
public static class AssistantToolVocabulary
{
    /// <summary>
    /// A step in words, from the label the assistant sent with it.
    /// <para>
    /// A frame carrying no label — an older assistant, or a tool whose catalog entry has none — is
    /// turned back into words from the tool's own name (<c>find_instance_file</c> → "Find instance
    /// file"). <b>An unknown step is described, never hidden</b>: a step dropped because nothing
    /// here recognised it would make the account of a turn quietly incomplete, which is the one
    /// thing a transcript must not be.
    /// </para>
    /// </summary>
    public static string Label(string? tool, string? label)
    {
        if (!string.IsNullOrWhiteSpace(label))
            return label;

        if (string.IsNullOrWhiteSpace(tool))
            return "Worked on it";

        var words = tool.Replace('_', ' ').Replace('-', ' ').Trim();
        return words.Length == 0 ? "Worked on it" : char.ToUpperInvariant(words[0]) + words[1..];
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
