namespace Airp.Domain.Conversations;

/// <summary>
/// Why a reply is being asked for again.
/// </summary>
/// <remarks>
/// These are the site's own options, not this client's invention: it validates the value
/// against a fixed list and shows the reader the same choices. Sending a reason it does not
/// recognise would be rejected, so the set is kept in step with the site rather than
/// extended locally.
/// </remarks>
public enum RegenerateReason
{
    /// <summary>No reason given — simply produce another reply.</summary>
    None = 0,

    /// <summary>Steer the next reply, usually with instructions alongside.</summary>
    Steer,

    /// <summary>The reply contradicted what the chat should remember.</summary>
    BadMemory,

    /// <summary>The reply repeated itself or the conversation went in circles.</summary>
    Looping,

    /// <summary>The reply wrote the reader's own actions or words for them.</summary>
    ActingForUser,

    /// <summary>The reply was too short.</summary>
    TooShort,

    /// <summary>The reply was too long.</summary>
    TooLong,

    /// <summary>The reply's formatting was wrong.</summary>
    WrongFormat,

    /// <summary>The chat refused to answer.</summary>
    Refusing,
}

/// <summary>
/// The site's wording for each regenerate reason, and the value it expects.
/// </summary>
/// <remarks>
/// The label is not decoration: the reason is chosen by pressing the site's own button, and
/// that button is found by the text on it. The value is what the site records against the
/// request.
/// </remarks>
public static class RegenerateReasons
{
    /// <summary>Every reason a reader can pick, in the order the site lists them.</summary>
    public static readonly IReadOnlyList<RegenerateReason> All =
    [
        RegenerateReason.None,
        RegenerateReason.Steer,
        RegenerateReason.BadMemory,
        RegenerateReason.Looping,
        RegenerateReason.ActingForUser,
        RegenerateReason.TooShort,
        RegenerateReason.TooLong,
        RegenerateReason.WrongFormat,
        RegenerateReason.Refusing,
    ];

    /// <summary>The value the site expects for a reason.</summary>
    /// <param name="reason">The reason.</param>
    /// <returns>The site's identifier, or an empty string for <see cref="RegenerateReason.None"/>.</returns>
    public static string Value(RegenerateReason reason) => reason switch
    {
        RegenerateReason.Steer => "steer",
        RegenerateReason.BadMemory => "bad-memory",
        RegenerateReason.Looping => "looping",
        RegenerateReason.ActingForUser => "acting-on-behalf-of-user",
        RegenerateReason.TooShort => "message-too-short",
        RegenerateReason.TooLong => "message-too-long",
        RegenerateReason.WrongFormat => "formatting-wrong",
        RegenerateReason.Refusing => "refusal",
        _ => string.Empty,
    };

    /// <summary>The label the site puts on the button for a reason.</summary>
    /// <param name="reason">The reason.</param>
    /// <returns>The label.</returns>
    public static string Label(RegenerateReason reason) => reason switch
    {
        RegenerateReason.Steer => "Guide the reply",
        RegenerateReason.BadMemory => "Bad memory",
        RegenerateReason.Looping => "Looping",
        RegenerateReason.ActingForUser => "Writing my actions",
        RegenerateReason.TooShort => "Too short",
        RegenerateReason.TooLong => "Too long",
        RegenerateReason.WrongFormat => "Wrong format",
        RegenerateReason.Refusing => "AI refusing",
        _ => "No reason",
    };

    /// <summary>What picking this reason tells the character.</summary>
    /// <param name="reason">The reason.</param>
    /// <returns>A short explanation for the reader.</returns>
    public static string Describe(RegenerateReason reason) => reason switch
    {
        RegenerateReason.Steer => "just write it differently — say how in the instructions",
        RegenerateReason.BadMemory => "it contradicted something established earlier",
        RegenerateReason.Looping => "it repeated itself, or the scene stopped moving",
        RegenerateReason.ActingForUser => "it wrote your actions or words for you",
        RegenerateReason.TooShort => "there was not enough of it",
        RegenerateReason.TooLong => "there was too much of it",
        RegenerateReason.WrongFormat => "the prose, dialogue or emphasis came out wrong",
        RegenerateReason.Refusing => "it declined to answer",
        _ => "ask for another reply without saying why",
    };
}
