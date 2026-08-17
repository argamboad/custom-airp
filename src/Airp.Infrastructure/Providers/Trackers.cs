using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Airp.Infrastructure.Storage.Local;

namespace Airp.Infrastructure.Providers;

/// <summary>
/// Renders the story's meters into the prompt, and reads back what the model did with them.
/// </summary>
/// <remarks>
/// <para>
/// The loop is: inject the stored value, ask the model to render the meter having moved it,
/// then read the rendered line and store the new value. That round trip is what keeps a meter
/// honest three hundred turns later — a card that only instructs the format relies on the model
/// still being able to see the previous value, and it cannot once that turn is compressed away.
/// </para>
/// <para>
/// The format is the one that came out of a card with two million messages of use behind it:
/// a bar, a value, a delta and a reason. The delta is the part that earns its place. An absolute
/// number tells a reader where they stand; the delta tells them what the thing they just did was
/// worth, which is the only part they can act on.
/// </para>
/// </remarks>
internal static partial class Trackers
{
    /// <summary>
    /// Reads a rendered meter line back out of a reply.
    /// </summary>
    /// <remarks>
    /// Deliberately loose about the bar: models render hearts, blocks, or nothing at all, and
    /// which glyph they chose is not worth failing a parse over. The value, the delta and the
    /// name are what matter.
    /// </remarks>
    [GeneratedRegex(
        @"\[\s*(?<name>[^\]\n]{1,120}?)\s*\]\s*(?<bar>[^\n\d]*?)\s*(?<value>-?\d+(?:\.\d+)?)\s*/\s*(?<max>\d+(?:\.\d+)?)\s*\|\s*(?:Δ|delta)?\s*(?<delta>[+\-]?\d+(?:\.\d+)?)\s*\|\s*(?<note>[^\n|]{0,200})",
        RegexOptions.IgnoreCase)]
    private static partial Regex Rendered { get; }

    /// <summary>Builds the block of meters that goes into the prompt.</summary>
    /// <param name="trackers">The conversation's meters.</param>
    /// <returns>The text, or <see langword="null"/> when there are none.</returns>
    public static string? Render(IReadOnlyList<TrackerRecord> trackers)
    {
        ArgumentNullException.ThrowIfNull(trackers);

        if (trackers.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder();

        builder.AppendLine(
            "These meters belong to this story. End every reply with all of them, each on its "
            + "own line, in exactly this shape, and nothing else after them:");
        builder.AppendLine();
        builder.AppendLine("[NAME] {bar} {value}/{max} | Δ {change} | {reason in three words}");
        builder.AppendLine();
        builder.AppendLine(
            "The values below are where the meters stand right now — start from them. Move one "
            + "only when this turn earned it, and by a little: one to three points for an "
            + "ordinary beat, more only for something the scene treats as a turning point. A "
            + "turn that earned nothing shows Δ 0. The reason is what the reader actually reads, "
            + "so say what moved it, not how it feels.");
        builder.AppendLine();

        foreach (var tracker in trackers)
        {
            builder.Append(CultureInfo.InvariantCulture, $"[{tracker.Name}] {Bar(tracker)} ");
            builder.Append(CultureInfo.InvariantCulture, $"{Trim(tracker.Value)}/{Trim(tracker.Max)} ");
            builder.Append(CultureInfo.InvariantCulture, $"| Δ {Trim(tracker.Delta):+0.#;-0.#;0} ");
            builder.AppendLine(string.IsNullOrWhiteSpace(tracker.Note) ? "| —" : $"| {tracker.Note}");

            // What it measures, what the numbers mean, and what constrains it. Without these a
            // meter is a word and a number, and the model re-invents both every turn.
            if (!string.IsNullOrWhiteSpace(tracker.Means))
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"    measures: {tracker.Means}");
            }

            if (!string.IsNullOrWhiteSpace(tracker.Anchors))
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"    scale: {tracker.Anchors}");
            }

            if (!string.IsNullOrWhiteSpace(tracker.Rule))
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"    rule: {tracker.Rule}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Updates the stored meters from what the model actually rendered.
    /// </summary>
    /// <remarks>
    /// Only meters that already exist are touched. A model that invents a new one is writing
    /// fiction, not configuration — the reader decides what this story measures.
    /// </remarks>
    /// <param name="trackers">The conversation's meters, tracked by the store.</param>
    /// <param name="reply">What the model wrote.</param>
    /// <param name="sequence">Sequence of the turn being stored.</param>
    /// <returns>How many meters moved.</returns>
    public static int Absorb(IReadOnlyList<TrackerRecord> trackers, string reply, long sequence)
    {
        ArgumentNullException.ThrowIfNull(trackers);

        if (trackers.Count == 0 || string.IsNullOrWhiteSpace(reply))
        {
            return 0;
        }

        var moved = 0;

        foreach (Match match in Rendered.Matches(reply))
        {
            var name = match.Groups["name"].Value.Trim();

            var tracker = trackers.FirstOrDefault(
                t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

            if (tracker is null)
            {
                continue;
            }

            if (!double.TryParse(
                    match.Groups["value"].Value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var value))
            {
                continue;
            }

            // Clamped rather than trusted: a model that renders 250/100 has lost the plot, and
            // storing that would poison every later turn with an impossible starting point.
            value = Math.Clamp(value, 0, tracker.Max);

            if (Math.Abs(value - tracker.Value) < 0.001)
            {
                continue;
            }

            tracker.Delta = Math.Round(value - tracker.Value, 2);
            tracker.Value = value;
            tracker.UpdatedAtSequence = sequence;

            var note = match.Groups["note"].Value.Trim().Trim('—', '-', ' ');
            tracker.Note = string.IsNullOrWhiteSpace(note) ? tracker.Note : note[..Math.Min(200, note.Length)];

            moved++;
        }

        return moved;
    }

    /// <summary>Draws a ten-step bar for a meter.</summary>
    private static string Bar(TrackerRecord tracker)
    {
        if (tracker.Max <= 0)
        {
            return string.Empty;
        }

        var filled = (int)Math.Round(10 * Math.Clamp(tracker.Value / tracker.Max, 0, 1));
        return new string('#', filled) + new string('.', 10 - filled);
    }

    private static string Trim(double value)
        => value == Math.Floor(value)
            ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.#", CultureInfo.InvariantCulture);
}
