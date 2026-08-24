using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Airp.Application.Dials;

/// <summary>The sampler parameters the dials ask for, each null when no dial speaks to it.</summary>
/// <param name="Temperature">Sampling temperature, from a dial mapping <c>temperature</c>.</param>
/// <param name="MaxTokens">Reply ceiling, from a dial mapping <c>max_tokens</c>.</param>
/// <param name="FrequencyPenalty">Repetition penalty, from a dial mapping <c>frequency_penalty</c>.</param>
public readonly record struct SamplerOverrides(double? Temperature, int? MaxTokens, double? FrequencyPenalty);

/// <summary>
/// Turns a pack and a conversation's choices into the two things the model actually receives:
/// a directives text and a set of sampler parameters.
/// </summary>
/// <remarks>
/// <para>
/// The effective value of a dial is: the conversation's stored choice while the dial is
/// enabled, and the pack's default otherwise. Disabling a dial pins it to its default rather
/// than switching it off — the stored choice is kept, merely overridden, and comes back the
/// day the dial is re-enabled.
/// </para>
/// <para>
/// Prompt levers render in pack order into the directives layer — cache-stable until a dial
/// moves (the layer-order contract). Sampler levers never render: a number the model can see
/// is a number it performs, which is the tracker lesson applied here.
/// </para>
/// </remarks>
public static class DialEngine
{
    /// <summary>
    /// The value in force for a dial, in stored form.
    /// </summary>
    /// <param name="dial">The dial.</param>
    /// <param name="values">The conversation's stored choices, keyed by dial key.</param>
    /// <returns>The stored-form value, or null when nothing applies.</returns>
    public static string? Effective(DialDefinition dial, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(dial);
        ArgumentNullException.ThrowIfNull(values);

        return dial.Enabled && values.TryGetValue(dial.Key, out var chosen)
            ? chosen
            : dial.Default;
    }

    /// <summary>
    /// Renders every prompt-lever dial that is in force into the directives layer's text.
    /// </summary>
    /// <param name="pack">The pack in force.</param>
    /// <param name="values">The conversation's stored choices.</param>
    /// <returns>The directives text, or null when no dial says anything.</returns>
    public static string? Directives(DialPack pack, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(values);

        var lines = new List<string>();
        var blocks = new List<string>();

        foreach (var dial in pack.Dials)
        {
            if (dial.Lever == DialLever.Sampler || Effective(dial, values) is not { } value)
            {
                continue;
            }

            switch (dial.Kind)
            {
                case DialKind.Scale when LevelIndex(dial, value) is { } index:
                    lines.Add($"{dial.Title}: {dial.Levels[index].Label} — {dial.Levels[index].Text}.");
                    break;

                case DialKind.Choice when dial.Options.FirstOrDefault(
                    o => string.Equals(o.Key, value, StringComparison.OrdinalIgnoreCase)) is { } option:
                    lines.Add($"{dial.Title}: {option.Text}.");
                    break;

                case DialKind.Toggle when IsOn(value) && !string.IsNullOrWhiteSpace(dial.OnText):
                    blocks.Add(dial.OnText.Trim());
                    break;

                case DialKind.List when Items(value) is { Count: > 0 } items
                                        && dial.Template is not null:
                    blocks.Add(dial.Template
                        .Replace("{items}", string.Join(", ", items), StringComparison.Ordinal)
                        .Trim());
                    break;

                case DialKind.Text when !string.IsNullOrWhiteSpace(value) && dial.Template is not null:
                    blocks.Add(dial.Template
                        .Replace("{value}", value.Trim(), StringComparison.Ordinal)
                        .Trim());
                    break;

                default:
                    // A stored value the pack cannot read — a level index out of range, an
                    // option key that was renamed — says nothing rather than something wrong.
                    break;
            }
        }

        // The one-line dials read as a block of their own, ahead of the paragraph-shaped ones,
        // which is the shape the prompt has always had: dial lines first, directives after.
        var parts = new List<string>();

        if (lines.Count > 0)
        {
            parts.Add(string.Join("\n", lines));
        }

        parts.AddRange(blocks);

        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }

    /// <summary>
    /// Resolves the sampler parameters the dials ask for.
    /// </summary>
    /// <param name="pack">The pack in force.</param>
    /// <param name="values">The conversation's stored choices.</param>
    /// <returns>The overrides, each null where no dial is set.</returns>
    public static SamplerOverrides Sampler(DialPack pack, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(values);

        double? temperature = null;
        int? maxTokens = null;
        double? frequency = null;

        foreach (var dial in pack.Dials)
        {
            if (dial.Lever == DialLever.Prompt
                || dial.Kind != DialKind.Scale
                || Effective(dial, values) is not { } value
                || LevelIndex(dial, value) is not { } index
                || dial.Levels[index].Value is not { } chosen)
            {
                continue;
            }

            switch (dial.Maps)
            {
                case "temperature":
                    temperature = chosen;
                    break;
                case "max_tokens":
                    maxTokens = (int)chosen;
                    break;
                case "frequency_penalty":
                    frequency = chosen;
                    break;
                default:
                    break;
            }
        }

        return new SamplerOverrides(temperature, maxTokens, frequency);
    }

    /// <summary>Parses a stored scale value into a level index, or null when it is not one.</summary>
    /// <param name="dial">The scale.</param>
    /// <param name="value">The stored value.</param>
    /// <returns>The index, or null when out of range or unreadable.</returns>
    public static int? LevelIndex(DialDefinition dial, string? value)
    {
        ArgumentNullException.ThrowIfNull(dial);

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
               && index >= 0
               && index < dial.Levels.Count
            ? index
            : null;
    }

    /// <summary>Whether a stored toggle value means on.</summary>
    /// <param name="value">The stored value.</param>
    /// <returns>True when it reads as true.</returns>
    public static bool IsOn(string? value) => bool.TryParse(value, out var on) && on;

    /// <summary>Reads a stored list value back into its items.</summary>
    /// <param name="value">The stored value: a JSON array of strings.</param>
    /// <returns>The items, or empty when the value is not a list.</returns>
    public static IReadOnlyList<string> Items(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        try
        {
            return JsonNode.Parse(value) is JsonArray items
                ? [.. items
                    .Select(static i => i?.GetValue<string>())
                    .Where(static i => !string.IsNullOrWhiteSpace(i))
                    .Select(static i => i!.Trim())]
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Serialises items into the stored form of a list value.</summary>
    /// <param name="items">The items.</param>
    /// <returns>The stored value, or null when there are none.</returns>
    public static string? StoreItems(IReadOnlyList<string> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var kept = items
            .Where(static i => !string.IsNullOrWhiteSpace(i))
            .Select(static i => i.Trim())
            .ToArray();

        return kept.Length == 0
            ? null
            : new JsonArray([.. kept.Select(static i => (JsonNode)i)]).ToJsonString();
    }
}
