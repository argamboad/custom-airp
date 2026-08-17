namespace Airp.Domain.Conversations;

/// <summary>A per-conversation dial the site exposes.</summary>
public enum ChatSetting
{
    /// <summary>How forward the chat is.</summary>
    Lust,

    /// <summary>How much the chat writes per reply.</summary>
    ResponseLength,

    /// <summary>How varied and surprising the replies are.</summary>
    Creativity,
}

/// <summary>
/// The three levels that shape a conversation's replies.
/// </summary>
/// <remarks>
/// Each is an integer level, or <see langword="null"/> when the conversation has never been
/// given one and the site is applying its own default. Null is deliberately distinct from
/// the middle level: writing a value the user never chose would change their chat.
/// </remarks>
public sealed record ChatSettings
{
    /// <summary>How forward the chat is.</summary>
    public int? Lust { get; init; }

    /// <summary>How much the chat writes per reply.</summary>
    public int? ResponseLength { get; init; }

    /// <summary>How varied and surprising the replies are.</summary>
    public int? Creativity { get; init; }

    /// <summary>Whether each character adds a line of what they did not say.</summary>
    /// <remarks>
    /// A toggle rather than a dial, but the same contract otherwise: null means "not stated
    /// here", so a partial update leaves it alone.
    /// </remarks>
    public bool? InnerThoughts { get; init; }

    /// <summary>Whether any level is set.</summary>
    public bool IsEmpty
        => Lust is null && ResponseLength is null && Creativity is null && InnerThoughts is null;

    /// <summary>Reads one setting's level.</summary>
    /// <param name="setting">The setting to read.</param>
    /// <returns>The level, or <see langword="null"/> when it is unset.</returns>
    public int? Level(ChatSetting setting) => setting switch
    {
        ChatSetting.Lust => Lust,
        ChatSetting.ResponseLength => ResponseLength,
        ChatSetting.Creativity => Creativity,
        _ => null,
    };

    /// <summary>Returns a copy with one setting changed.</summary>
    /// <param name="setting">The setting to change.</param>
    /// <param name="level">The new level.</param>
    /// <returns>The updated settings.</returns>
    public ChatSettings With(ChatSetting setting, int? level) => setting switch
    {
        ChatSetting.Lust => this with { Lust = level },
        ChatSetting.ResponseLength => this with { ResponseLength = level },
        ChatSetting.Creativity => this with { Creativity = level },
        _ => this,
    };

    /// <summary>The settings that differ from another set, with everything else null.</summary>
    /// <param name="other">The settings to compare against.</param>
    /// <returns>Only the changed levels.</returns>
    public ChatSettings ChangesFrom(ChatSettings other) => new()
    {
        Lust = Lust == other.Lust ? null : Lust,
        ResponseLength = ResponseLength == other.ResponseLength ? null : ResponseLength,
        Creativity = Creativity == other.Creativity ? null : Creativity,
        InnerThoughts = InnerThoughts == other.InnerThoughts ? null : InnerThoughts,
    };

    /// <summary>The settings carrying a value, in display order.</summary>
    /// <returns>The settings that are set.</returns>
    public IEnumerable<ChatSetting> Assigned()
    {
        foreach (var setting in ChatSettingScale.All)
        {
            if (Level(setting) is not null)
            {
                yield return setting;
            }
        }
    }
}

/// <summary>One step of a setting's scale.</summary>
/// <param name="Label">The site's name for this level.</param>
/// <param name="Description">The site's explanation of what it does.</param>
public readonly record struct ChatSettingLevel(string Label, string Description);

/// <summary>
/// What each level of each setting means, in the site's own words.
/// </summary>
/// <remarks>
/// These labels are read from the site's own interface rather than invented here, so the
/// terminal describes a level exactly as the web app does. A bare number would make the
/// difference between "Flirty" and "Unhinged" a guess, which is not a guess anyone should
/// have to make about their own conversation.
/// </remarks>
public static class ChatSettingScale
{
    /// <summary>Every setting, in the order the site presents them.</summary>
    public static readonly IReadOnlyList<ChatSetting> All =
        [ChatSetting.Creativity, ChatSetting.Lust, ChatSetting.ResponseLength];

    private static readonly IReadOnlyList<ChatSettingLevel> LustLevels =
    [
        new("Cold", "detached, deflects all romantic or sexual cues"),
        new("Friendly", "warm but platonic, open to slow natural connection"),
        new("Flirty", "playful and suggestive, follows the scenario pace"),
        new("Explicit", "sexually forward, fast escalation, anatomically detailed"),
        new("Unhinged", "no limits, no pacing, maximum intensity every message"),
    ];

    private static readonly IReadOnlyList<ChatSettingLevel> LengthLevels =
    [
        new("Minimal", "a very brief response, straight to the point"),
        new("Concise", "a short response with essential details"),
        new("Detailed", "balanced response length"),
        new("Verbose", "a longer response with significant detail"),
        new("Extensive", "extensive response with comprehensive details"),
    ];

    private static readonly IReadOnlyList<ChatSettingLevel> CreativityLevels =
    [
        new("Precise", "very consistent and on-chat, minimal surprises"),
        new("Focused", "mostly predictable with a little variation"),
        new("Balanced", "the model's recommended default"),
        new("Creative", "more varied wording and unexpected turns"),
        new("Wild", "maximum variation"),
    ];

    /// <summary>The scale for a setting, from lowest level to highest.</summary>
    /// <param name="setting">The setting.</param>
    /// <returns>The levels.</returns>
    public static IReadOnlyList<ChatSettingLevel> Levels(ChatSetting setting) => setting switch
    {
        ChatSetting.Lust => LustLevels,
        ChatSetting.ResponseLength => LengthLevels,
        ChatSetting.Creativity => CreativityLevels,
        _ => [],
    };

    /// <summary>The site's name for a setting.</summary>
    /// <param name="setting">The setting.</param>
    /// <returns>The title.</returns>
    public static string Title(ChatSetting setting) => setting switch
    {
        ChatSetting.Lust => "Lust Level",
        ChatSetting.ResponseLength => "Response Length",
        ChatSetting.Creativity => "Creativity",
        _ => setting.ToString(),
    };

    /// <summary>The site's explanation of a setting.</summary>
    /// <param name="setting">The setting.</param>
    /// <returns>The description.</returns>
    public static string Description(ChatSetting setting) => setting switch
    {
        ChatSetting.Lust => "Set the chat's current arousal level",
        ChatSetting.ResponseLength => "Set the response length for this chat",
        ChatSetting.Creativity => "How varied and surprising the replies are. "
                                  + "The middle is each model's recommended setting.",
        _ => string.Empty,
    };

    /// <summary>Describes one level of a setting.</summary>
    /// <param name="setting">The setting.</param>
    /// <param name="level">The level, or <see langword="null"/> when unset.</param>
    /// <returns>The level's label and description, or a placeholder when unset.</returns>
    public static ChatSettingLevel Describe(ChatSetting setting, int? level)
    {
        var levels = Levels(setting);

        return level is { } value && value >= 0 && value < levels.Count
            ? levels[value]
            // Worded without naming a site: the same scale is read by an adapter that talks to
            // one and by an adapter that builds its own prompts, and only one of them has a
            // site to blame a default on.
            : new ChatSettingLevel("Not set", "no level chosen, so whatever answers uses its own default");
    }
}
