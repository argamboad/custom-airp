using System.Text.Json;
using System.Text.Json.Nodes;

namespace Airp.Application.Dials;

/// <summary>What shape of control a dial is.</summary>
public enum DialKind
{
    /// <summary>Five steps, read by index. Never fewer: a short scale would make the top of the dial quietly mean the bottom.</summary>
    Scale = 0,

    /// <summary>On or off. On injects the dial's text; off injects nothing.</summary>
    Toggle,

    /// <summary>Named options, one active.</summary>
    Choice,

    /// <summary>Reader-supplied items rendered through a template.</summary>
    List,

    /// <summary>One free value rendered through a template.</summary>
    Text,
}

/// <summary>Which lever a dial pulls.</summary>
public enum DialLever
{
    /// <summary>The chosen text is injected into the prompt's directives layer, framed by the application.</summary>
    Prompt = 0,

    /// <summary>The chosen value becomes an API sampling parameter. Nothing is injected: a number the model can see is a number it performs.</summary>
    Sampler,

    /// <summary>Both at once — one value and one text per level.</summary>
    Both,
}

/// <summary>One step of a scale dial.</summary>
/// <param name="Label">The name shown on screen, and sent when the level's text is.</param>
/// <param name="Text">What is injected for prompt levers, or null for sampler-only levels.</param>
/// <param name="Value">The sampler value for sampler levers, or null for prompt-only levels.</param>
/// <param name="Description">Screen-only explanation, for sampler levers whose text never travels.</param>
public sealed record DialLevel(string Label, string? Text, double? Value, string? Description);

/// <summary>One option of a choice dial.</summary>
/// <param name="Key">The stored value.</param>
/// <param name="Label">The name shown on screen.</param>
/// <param name="Text">What is injected when this option is active.</param>
public sealed record DialOption(string Key, string Label, string Text);

/// <summary>A dial the pack could not use, and why — surfaced rather than swallowed.</summary>
/// <param name="Key">The dial's key in the file.</param>
/// <param name="Reason">What was wrong with it.</param>
public sealed record SkippedDial(string Key, string Reason);

/// <summary>
/// One configurable control over how replies are written.
/// </summary>
/// <remarks>
/// The definition carries its own documentation — <see cref="Title"/>, <see cref="Help"/>,
/// <see cref="Accepts"/>, <see cref="Examples"/> — as data rather than comments, because the
/// settings screen renders them and because comments do not survive programmatic rewriting.
/// </remarks>
public sealed class DialDefinition
{
    /// <summary>The dial's identity: the key in the pack file and in the per-conversation store.</summary>
    public required string Key { get; init; }

    /// <summary>What shape of control it is.</summary>
    public DialKind Kind { get; init; }

    /// <summary>Which lever it pulls.</summary>
    public DialLever Lever { get; init; }

    /// <summary>The sampler parameter a sampler lever sets: <c>temperature</c>, <c>max_tokens</c> or <c>frequency_penalty</c>.</summary>
    public string? Maps { get; init; }

    /// <summary>
    /// Whether the settings screen offers this dial.
    /// </summary>
    /// <remarks>
    /// Disabled is pinned, not off: the <see cref="Default"/> still applies on every prompt.
    /// A stored per-conversation value survives disablement and resurfaces on re-enable.
    /// </remarks>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// What applies when no per-conversation choice has been made, in stored form.
    /// </summary>
    /// <remarks>
    /// Stored form is one string per kind: a level index for a scale, <c>true</c>/<c>false</c>
    /// for a toggle, an option key for a choice, a JSON array for a list, the raw value for a
    /// text. Null is a legal default and means "inject nothing" — the model's own behaviour.
    /// </remarks>
    public string? Default { get; init; }

    /// <summary>What the settings screen calls it.</summary>
    public required string Title { get; init; }

    /// <summary>What it does, shown in the settings screen.</summary>
    public string Help { get; init; } = string.Empty;

    /// <summary>The five steps of a scale, lowest first. Empty for other kinds.</summary>
    public IReadOnlyList<DialLevel> Levels { get; init; } = [];

    /// <summary>The options of a choice, in file order. Empty for other kinds.</summary>
    public IReadOnlyList<DialOption> Options { get; init; } = [];

    /// <summary>The text a toggle injects when on.</summary>
    public string? OnText { get; init; }

    /// <summary>The template a list or text dial renders through, with <c>{items}</c> or <c>{value}</c>.</summary>
    public string? Template { get; init; }

    /// <summary>What values the free-text kinds accept, in words, for the settings screen.</summary>
    public string? Accepts { get; init; }

    /// <summary>Example values for the free-text kinds, for the settings screen.</summary>
    public IReadOnlyList<string> Examples { get; init; } = [];
}

/// <summary>
/// The set of dials in force: the embedded defaults, or the reader's own file.
/// </summary>
public sealed class DialPack
{
    /// <summary>The usable dials, in file order — which is the order their text enters the prompt.</summary>
    public required IReadOnlyList<DialDefinition> Dials { get; init; }

    /// <summary>Dials the file declared that could not be used, with the reason each was refused.</summary>
    public IReadOnlyList<SkippedDial> Skipped { get; init; } = [];

    /// <summary>Finds a dial by key.</summary>
    /// <param name="key">The dial's key, matched without case.</param>
    /// <returns>The dial, or null when the pack has no such key.</returns>
    public DialDefinition? Find(string key)
        => Dials.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Reads a dial pack out of JSON.
/// </summary>
/// <remarks>
/// <para>
/// Tolerant of comments and trailing commas, like the configuration file — the shipped pack
/// documents itself with comments, so a parser that refused them would refuse its own default.
/// </para>
/// <para>
/// A dial that cannot be used — a scale without exactly five levels, a sampler lever with no
/// parameter to set, a template missing its placeholder — is skipped whole with a recorded
/// reason, never used in part. Levels are read by index, so a partial scale would silently
/// make the top of the dial mean whatever the bottom did.
/// </para>
/// </remarks>
public static class DialPackParser
{
    private static readonly JsonDocumentOptions Tolerant = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>How many steps every scale has.</summary>
    public const int ScaleSteps = 5;

    /// <summary>Parses a pack.</summary>
    /// <param name="json">The pack file's text.</param>
    /// <returns>The pack, with anything unusable listed in <see cref="DialPack.Skipped"/>.</returns>
    /// <exception cref="JsonException">The text is not JSON at all.</exception>
    public static DialPack Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        var root = JsonNode.Parse(json, documentOptions: Tolerant);
        var dials = new List<DialDefinition>();
        var skipped = new List<SkippedDial>();

        if (root?["dials"] is not JsonObject declared)
        {
            return new DialPack { Dials = [], Skipped = [new SkippedDial("(file)", "no \"dials\" object")] };
        }

        foreach (var (key, node) in declared)
        {
            if (node is not JsonObject dial)
            {
                skipped.Add(new SkippedDial(key, "not an object"));
                continue;
            }

            try
            {
                if (Read(key, dial) is { } definition)
                {
                    dials.Add(definition);
                }
                else
                {
                    skipped.Add(new SkippedDial(key, Why(key, dial)));
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException or JsonException)
            {
                skipped.Add(new SkippedDial(key, ex.Message));
            }
        }

        return new DialPack { Dials = dials, Skipped = skipped };
    }

    private static DialDefinition? Read(string key, JsonObject dial)
    {
        if (!Enum.TryParse<DialKind>(dial["kind"]?.GetValue<string>(), ignoreCase: true, out var kind)
            || !Enum.TryParse<DialLever>(dial["lever"]?.GetValue<string>(), ignoreCase: true, out var lever))
        {
            return null;
        }

        var maps = dial["maps"]?.GetValue<string>();

        if (lever is DialLever.Sampler or DialLever.Both
            && maps is not ("temperature" or "max_tokens" or "frequency_penalty"))
        {
            return null;
        }

        var levels = ReadLevels(dial, lever);
        var options = ReadOptions(dial);
        var template = dial["template"]?.GetValue<string>();
        var on = dial["on"]?.GetValue<string>();

        var usable = kind switch
        {
            DialKind.Scale => levels is { Count: ScaleSteps },
            DialKind.Toggle => !string.IsNullOrWhiteSpace(on),
            DialKind.Choice => options is { Count: >= 2 },
            DialKind.List => template?.Contains("{items}", StringComparison.Ordinal) == true,
            DialKind.Text => template?.Contains("{value}", StringComparison.Ordinal) == true,
            _ => false,
        };

        if (!usable)
        {
            return null;
        }

        return new DialDefinition
        {
            Key = key,
            Kind = kind,
            Lever = lever,
            Maps = maps,
            Enabled = dial["enabled"]?.GetValue<bool>() ?? true,
            Default = StoredDefault(kind, dial["default"]),
            Title = dial["title"]?.GetValue<string>() ?? key,
            Help = dial["help"]?.GetValue<string>() ?? string.Empty,
            Levels = levels ?? [],
            Options = options ?? [],
            OnText = on,
            Template = template,
            Accepts = dial["accepts"]?.GetValue<string>(),
            Examples = dial["examples"] is JsonArray examples
                ? [.. examples.Select(static e => e switch
                    {
                        JsonArray list => string.Join(", ", list.Select(static i => i?.GetValue<string>() ?? string.Empty)),
                        _ => e?.GetValue<string>() ?? string.Empty,
                    })]
                : [],
        };
    }

    private static List<DialLevel>? ReadLevels(JsonObject dial, DialLever lever)
    {
        if (dial["levels"] is not JsonArray declared)
        {
            return null;
        }

        var levels = new List<DialLevel>();

        foreach (var node in declared)
        {
            if (node is not JsonObject level || level["label"]?.GetValue<string>() is not { Length: > 0 } label)
            {
                return null;
            }

            var text = level["text"]?.GetValue<string>();
            var value = level["value"]?.GetValue<double>();

            // Each level must carry what its lever spends: text for the prompt, a value for
            // the sampler, both for both. A level missing its half breaks the whole scale.
            var complete = lever switch
            {
                DialLever.Prompt => !string.IsNullOrWhiteSpace(text),
                DialLever.Sampler => value is not null,
                DialLever.Both => !string.IsNullOrWhiteSpace(text) && value is not null,
                _ => false,
            };

            if (!complete)
            {
                return null;
            }

            levels.Add(new DialLevel(label, text, value, level["description"]?.GetValue<string>()));
        }

        return levels;
    }

    private static List<DialOption>? ReadOptions(JsonObject dial)
    {
        if (dial["options"] is not JsonObject declared)
        {
            return null;
        }

        var options = new List<DialOption>();

        foreach (var (key, node) in declared)
        {
            if (node is not JsonObject option
                || option["text"]?.GetValue<string>() is not { Length: > 0 } text)
            {
                return null;
            }

            options.Add(new DialOption(key, option["label"]?.GetValue<string>() ?? key, text));
        }

        return options;
    }

    /// <summary>Converts a JSON default into the stored form the value store uses.</summary>
    private static string? StoredDefault(DialKind kind, JsonNode? node) => node switch
    {
        null => null,
        JsonArray items when kind == DialKind.List => items.ToJsonString(),
        JsonValue value => value.GetValueKind() switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => value.GetValue<double>() is var n && n == Math.Floor(n)
                ? ((long)n).ToString(System.Globalization.CultureInfo.InvariantCulture)
                : n.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => value.GetValue<string>(),
        },
        _ => null,
    };

    private static string Why(string key, JsonObject dial)
    {
        _ = key;

        if (!Enum.TryParse<DialKind>(dial["kind"]?.GetValue<string>(), ignoreCase: true, out var kind))
        {
            return $"unknown kind '{dial["kind"]?.GetValue<string>()}'";
        }

        if (!Enum.TryParse<DialLever>(dial["lever"]?.GetValue<string>(), ignoreCase: true, out var lever))
        {
            return $"unknown lever '{dial["lever"]?.GetValue<string>()}'";
        }

        if (lever is DialLever.Sampler or DialLever.Both
            && dial["maps"]?.GetValue<string>() is not ("temperature" or "max_tokens" or "frequency_penalty"))
        {
            return "a sampler lever needs \"maps\": temperature, max_tokens or frequency_penalty";
        }

        return kind switch
        {
            DialKind.Scale => $"a scale needs exactly {ScaleSteps} complete levels",
            DialKind.Toggle => "a toggle needs \"on\" text",
            DialKind.Choice => "a choice needs at least two options, each with text",
            DialKind.List => "a list needs a template containing {items}",
            DialKind.Text => "a text needs a template containing {value}",
            _ => "unusable",
        };
    }
}
