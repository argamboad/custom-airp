using Airp.Domain.Conversations;

namespace Airp.Application.Options;

/// <summary>One step of a scale, as configured.</summary>
public sealed class ScaleLevel
{
    /// <summary>The name of this level, shown on screen.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>What it means, shown on screen and sent to the model verbatim.</summary>
    public string Description { get; set; } = string.Empty;
}

/// <summary>A dial's own wording, replacing the one shipped with the client.</summary>
public sealed class ScaleOptions
{
    /// <summary>What to call the dial, or null to keep the shipped name.</summary>
    public string? Title { get; set; }

    /// <summary>The five levels, lowest first.</summary>
    public List<ScaleLevel> Levels { get; set; } = [];
}

/// <summary>
/// Resolves what each level of each dial means, preferring the reader's own wording.
/// </summary>
/// <remarks>
/// <para>
/// The shipped descriptions came from a site's interface, and they were kept because they were
/// reusable and already on screen. They are not neutral, though: "Explicit" meant whatever that
/// site meant by it. A client that builds its own prompts has no reason to inherit somebody
/// else's vocabulary for how a scene should go.
/// </para>
/// <para>
/// The same text is shown and sent. That is the whole constraint: a reader picks a level after
/// reading what it means, and sending the model a different form of words would make the dial
/// mean two things at once.
/// </para>
/// </remarks>
public static class SettingScales
{
    /// <summary>How many steps a dial has. A replacement scale must supply exactly this many.</summary>
    /// <remarks>
    /// Fixed rather than free, because the terminal's own arithmetic — which level is selected,
    /// what the arrow keys clamp to — is written against it. A scale of a different length would
    /// be a UI change wearing a configuration change's clothes.
    /// </remarks>
    public const int Steps = 5;

    /// <summary>The levels in force for a dial.</summary>
    /// <param name="setting">The dial.</param>
    /// <param name="options">Application options.</param>
    /// <returns>The levels, lowest first.</returns>
    public static IReadOnlyList<ChatSettingLevel> Levels(ChatSetting setting, AirpOptions? options)
        => Configured(setting, options) is { Levels.Count: Steps } custom
            ? [.. custom.Levels.Select(static l => new ChatSettingLevel(l.Label, l.Description))]
            : ChatSettingScale.Levels(setting);

    /// <summary>The name in force for a dial.</summary>
    /// <param name="setting">The dial.</param>
    /// <param name="options">Application options.</param>
    /// <returns>The title.</returns>
    public static string Title(ChatSetting setting, AirpOptions? options)
        => Configured(setting, options)?.Title is { Length: > 0 } title
            ? title
            : ChatSettingScale.Title(setting);

    /// <summary>Describes one level of a dial.</summary>
    /// <param name="setting">The dial.</param>
    /// <param name="level">The level, or null when unset.</param>
    /// <param name="options">Application options.</param>
    /// <returns>The level's label and description, or a placeholder when unset.</returns>
    public static ChatSettingLevel Describe(ChatSetting setting, int? level, AirpOptions? options)
    {
        var levels = Levels(setting, options);

        return level is { } value && value >= 0 && value < levels.Count
            ? levels[value]
            : new ChatSettingLevel("Not set", "no level chosen, so whatever answers uses its own default");
    }

    /// <summary>
    /// Renders the dials that belong in the prompt, in the reader's own wording.
    /// </summary>
    /// <remarks>
    /// Creativity is left out deliberately: it is spent on the sampler's temperature, which is
    /// a far stronger lever over how varied a reply is than asking for variety in words.
    /// </remarks>
    /// <param name="settings">The conversation's levels.</param>
    /// <param name="options">Application options.</param>
    /// <returns>The directives, or null when no dial is set.</returns>
    public static string? Directives(ChatSettings settings, AirpOptions? options)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.IsEmpty)
        {
            return null;
        }

        var lines = settings.Assigned()
            .Where(static s => s != ChatSetting.Creativity)
            .Select(s =>
            {
                var described = Describe(s, settings.Level(s), options);
                return $"{Title(s, options)}: {described.Label} — {described.Description}.";
            })
            .ToArray();

        return lines.Length == 0 ? null : string.Join("\n", lines);
    }

    /// <summary>
    /// Finds a configured scale, whatever state it is in.
    /// </summary>
    /// <remarks>
    /// The caller decides what it is good for. A title alone is a complete and useful thing to
    /// configure — renaming a dial without rewriting all five of its descriptions is a small
    /// ask — whereas a partial list of levels is not, because levels are read by index and a
    /// short list would silently make the top of the dial mean whatever the bottom of it meant.
    /// </remarks>
    private static ScaleOptions? Configured(ChatSetting setting, AirpOptions? options)
        => options is not null && options.Scales.TryGetValue(setting.ToString(), out var scale)
            ? scale
            : null;
}
